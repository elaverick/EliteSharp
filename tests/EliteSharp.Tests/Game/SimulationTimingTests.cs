using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;
using Silk.NET.Input;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The game runs at its simulation rate however often the renderer draws. Each
/// test plays the real game on a <see cref="VirtualClock"/>, with a renderer
/// that takes the game's frames (and moves the world smoothly between them)
/// at a given rate in the game's time, and checks that the game does exactly
/// the same things, at the same times, at every rate.
/// </summary>
public sealed class SimulationTimingTests
{
    private static readonly long Second = Stopwatch.Frequency;

    /// <summary>What happened in one run of the game.</summary>
    private sealed class Run
    {
        /// <summary>The game's state each time it waited, and how long it waited for.</summary>
        public List<string> Trace { get; } = [];

        /// <summary>The 3D world that was drawn at each time, for the frames drawn at multiples of <see cref="CommonDrawInterval"/>.</summary>
        public Dictionary<long, string> Drawn { get; } = [];

        /// <summary>The number of frames drawn that were part of the way between two of the game's frames.</summary>
        public int InBetweenFrames { get; set; }

        /// <summary>The number of main loop iterations (waits of one main loop period).</summary>
        public int MainLoopSteps { get; set; }

        public bool Launched { get; set; }

        public int MostShips { get; set; }

        public bool FiredLasers { get; set; }
    }

    /// <summary>An interval that the frames of all the renderers that draw at least 50 times a second have in common.</summary>
    private static readonly long CommonDrawInterval = Second / 50;

    /// <summary>
    /// Something done to the game at a time: pressing and releasing keys, or
    /// a test command (see EliteGame.DebugCommand).
    /// </summary>
    private sealed record Input(double Seconds, Action<PrivateGame, BbcKeyboard> Act);

    private static Input Press(double seconds, Key key) => new(seconds, (_, k) => k.OnKeyDown(key));

    private static Input Release(double seconds, Key key) => new(seconds, (_, k) => k.OnKeyUp(key));

    private static IEnumerable<Input> Tap(double seconds, Key key, double hold = 0.08) => [Press(seconds, key), Release(seconds + hold, key)];

    private static Input Command(double seconds, string command) => new(seconds, (g, _) => g.Game.DebugCommand(command));

    /// <summary>
    /// Start a new game, launch, and fly into a fight: speed up, meet some
    /// hostile ships, fire the lasers, roll and climb, use the E.C.M., fire a
    /// missile and look out of the back.
    /// </summary>
    private static readonly Input[] Flight =
    [
        .. Tap(0.5, Key.N),                 // Load New Commander? No
        .. Tap(1.5, Key.Space),             // Press Space, Commander
        .. Tap(2.5, Key.F1),                // f0: launch
        Command(7.0, "equip"),
        Press(7.0, Key.Space),              // speed up
        Release(8.5, Key.Space),
        Command(9.0, "spawn 20 12 0 0 ff 04"),
        Command(9.0, "spawn 17 16 600 200 ff 04"),
        Command(9.0, "spawn 7 30 -900 0 0 0"),
        Press(10.0, Key.A),                 // fire
        Release(12.0, Key.A),
        Press(12.5, Key.Comma),             // roll
        Release(13.5, Key.Comma),
        Press(14.0, Key.X),                 // climb
        Release(14.8, Key.X),
        .. Tap(15.5, Key.E),                // E.C.M.
        .. Tap(17.0, Key.T),                // target a missile
        .. Tap(18.0, Key.M),                // and fire it
        .. Tap(20.0, Key.F2),               // f1: rear view
        Press(21.0, Key.A),
        Release(22.0, Key.A),
        .. Tap(23.0, Key.F1),               // f0: front view
        Press(23.5, Key.Period),            // roll the other way
        Release(24.5, Key.Period),
    ];

    /// <summary>
    /// Play the game for a while on a virtual clock, at the given simulation
    /// rate, with a renderer drawing the given number of frames a second (or
    /// none), with the given inputs, and with the space view widened by the
    /// given margin (as the renderer sets for a window wider than 4:3).
    /// </summary>
    private static Run Play(int simulationRate, int renderRate, double seconds, IReadOnlyList<Input> inputs, bool interpolate = true, float sideMargin = 0)
    {
        var clock = new VirtualClock();
        var keyboard = new BbcKeyboard();
        var exchange = new FrameExchange();
        var game = new PrivateGame(clock: clock, keyboard: keyboard, exchange: exchange, mainLoopRate: simulationRate);
        var seeds = game.Get<int[]>("_randomSeeds");
        int[] fixedSeeds = [0x4A, 0x91, 0x2C, 0xE3];
        fixedSeeds.CopyTo(seeds, 0);
        game.Get<Hud>("_hud").SideMargin = sideMargin;

        var interpolator = new FrameInterpolator { Enabled = interpolate };
        var run = new Run();
        long end = (long)(seconds * Second);
        long mainLoopPeriod = Second / simulationRate;
        long renderPeriod = renderRate > 0 ? Second / renderRate : long.MaxValue;
        long nextDraw = renderRate > 0 ? renderPeriod : long.MaxValue;
        var pending = new Queue<Input>(inputs.OrderBy(i => i.Seconds));

        clock.Sleeping = (from, to) =>
        {
            if (from > end + 10 * Second)
            {
                throw new InvalidOperationException("The game didn't stop");
            }

            run.Trace.Add($"{to - from}: {Snapshot(game)}");
            if (to - from == mainLoopPeriod)
            {
                run.MainLoopSteps++;
            }

            run.Launched |= game.Get<int>("_docked") == 0 && game.Get<int>("_speed") > 0;
            run.MostShips = Math.Max(run.MostShips, game.Slots.Count(s => s is { Type: < 128 }));
            run.FiredLasers |= game.Get<int>("_laserTemperature") > 0;

            // The renderer draws while the game sleeps
            while (nextDraw <= to)
            {
                if (exchange.TakeLatest() is { } frame)
                {
                    var world = interpolator.World(frame, nextDraw);
                    if (world != frame.World)
                    {
                        run.InBetweenFrames++;
                    }

                    if (nextDraw % CommonDrawInterval == 0)
                    {
                        run.Drawn[nextDraw] = Describe(world);
                    }
                }

                nextDraw += renderPeriod;
            }

            // Keys are pressed while the game sleeps
            while (pending.TryPeek(out var input) && (long)(input.Seconds * Second) <= to)
            {
                pending.Dequeue().Act(game, keyboard);
            }

            if (to >= end)
            {
                game.Game.Quit();
            }
        };

        game.Game.Run();
        return run;
    }

    /// <summary>Everything about the game that the simulation rate governs.</summary>
    private static string Snapshot(PrivateGame game)
    {
        var text = new StringBuilder();
        foreach (string field in new[]
        {
            "_mainLoopCounter", "_speed", "_roll", "_pitch", "_energy", "_forwardShield", "_aftShield",
            "_cabinTemperature", "_laserTemperature", "_laserPulseCounter", "_altitude", "_view", "_viewType",
            "_docked", "_missileTarget", "_ecmCounter", "_extraVesselsDelay", "_messageDelay",
            "_hyperspaceCountdown", "_junkCount", "_fuel", "_legalStatus", "_killTally", "_missiles",
        })
        {
            text.Append(game.Get<int>(field)).Append(' ');
        }

        text.Append(game.Get<uint>("_cash")).Append(" seeds ").AppendJoin(',', game.Get<int[]>("_randomSeeds"));
        foreach (var ship in game.Slots)
        {
            if (ship != null)
            {
                text.Append(CultureInfo.InvariantCulture, $" | {ship.Type} {Vector(ship.Position)} {Vector(ship.Nose)} {Vector(ship.Roof)} {ship.Speed} {ship.Flags} {ship.Ai} {ship.Energy} {ship.Behaviour}");
            }
        }

        text.Append(" | dust ").AppendJoin(',', game.Get<float[]>("_dustZ").Select(z => z.ToString("R", CultureInfo.InvariantCulture)));
        return text.ToString();
    }

    private static string Vector(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:R},{v.Y:R},{v.Z:R})");

    /// <summary>The 3D world as drawn.</summary>
    private static string Describe(SceneFrame world)
    {
        var text = new StringBuilder($"view {world.Camera.View}");
        foreach (var ship in world.Ships)
        {
            text.Append(CultureInfo.InvariantCulture, $" | {ship.Id} {ship.Model} {ship.Transform}");
        }

        foreach (var planet in world.Planets)
        {
            text.Append(CultureInfo.InvariantCulture, $" | planet {Vector(planet.Centre)} {Vector(planet.Nose)}");
        }

        foreach (var sun in world.Suns)
        {
            text.Append(CultureInfo.InvariantCulture, $" | sun {Vector(sun.Centre)}");
        }

        foreach (var particle in world.Particles)
        {
            text.Append(CultureInfo.InvariantCulture, $" | {Vector(particle.Position)}");
        }

        return text.ToString();
    }

    [Fact]
    public void TheFlightScenarioPlaysTheGame()
    {
        // Make sure the scenario that the other tests compare really does
        // exercise the game: launching, flying, meeting ships and fighting
        var run = Play(16, 0, 26, Flight);
        Assert.True(run.Launched);
        Assert.True(run.MostShips >= 3);
        Assert.True(run.FiredLasers);
    }

    [Fact]
    public void TheGameIsTheSameHoweverOftenTheRendererDraws()
    {
        // No renderer at all, slow ones, the display's usual rates, and one
        // much faster than any display (and one that doesn't move smoothly)
        var expected = Play(16, 0, 26, Flight).Trace;
        foreach (int renderRate in new[] { 1, 24, 30, 50, 60, 100, 144, 165, 240, 1000 })
        {
            var trace = Play(16, renderRate, 26, Flight).Trace;
            AssertSameTrace(expected, trace, $"rendering at {renderRate} frames a second");
        }

        AssertSameTrace(expected, Play(16, 144, 26, Flight, interpolate: false).Trace, "rendering without interpolation");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(50)]
    public void TheGameIsTheSameHoweverOftenTheRendererDrawsAtOtherSimulationRates(int simulationRate)
    {
        var expected = Play(simulationRate, 0, 26, Flight).Trace;
        foreach (int renderRate in new[] { 30, 144, 1000 })
        {
            AssertSameTrace(expected, Play(simulationRate, renderRate, 26, Flight).Trace, $"rendering at {renderRate} frames a second");
        }
    }

    [Fact]
    public void TheGameIsTheSameHoweverWideTheWindowIs()
    {
        // A wider window shows more stardust (and wider tunnels), but the game
        // itself doesn't change
        var expected = Play(16, 60, 26, Flight).Trace;
        foreach (float margin in new[] { 50f, 92.7f, 250f })
        {
            AssertSameTrace(expected, Play(16, 60, 26, Flight, sideMargin: margin).Trace, $"with a side margin of {margin}");
        }
    }

    private static void AssertSameTrace(List<string> expected, List<string> actual, string because)
    {
        int count = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(expected[i] == actual[i], $"{because}, the game differed at wait {i}:\nexpected {expected[i]}\nactual   {actual[i]}");
        }

        Assert.True(expected.Count == actual.Count, $"{because}, the game waited {actual.Count} times rather than {expected.Count}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(50)]
    public void TheMainLoopRunsAtTheSimulationRateHoweverOftenTheRendererDraws(int simulationRate)
    {
        // The title screen is nothing but main loop iterations, each of which
        // moves the ship and counts down MCNT
        const int seconds = 10;
        foreach (int renderRate in new[] { 0, 30, 144, 1000 })
        {
            var run = Play(simulationRate, renderRate, seconds, []);
            Assert.Equal(simulationRate * seconds, run.MainLoopSteps);
            Assert.Equal(run.MainLoopSteps, run.Trace.Count);
        }
    }

    [Fact]
    public void TheWorldDrawnAtEachMomentIsTheSameHoweverOftenTheRendererDraws()
    {
        // The renderers that draw at least as often as the game's vertical
        // sync draw the same picture at the times they have in common, so
        // drawing faster only adds in-between pictures
        var expected = Play(16, 50, 26, Flight);
        Assert.NotEmpty(expected.Drawn);
        foreach (int renderRate in new[] { 100, 200, 1000 })
        {
            var run = Play(16, renderRate, 26, Flight);
            Assert.True(run.InBetweenFrames > expected.InBetweenFrames);
            foreach (var (time, world) in expected.Drawn)
            {
                Assert.True(run.Drawn.TryGetValue(time, out string? drawn), $"at {renderRate} frames a second, nothing was drawn at {time}");
                Assert.True(world == drawn, $"at {renderRate} frames a second, the world drawn at {time} differed:\nexpected {world}\nactual   {drawn}");
            }
        }
    }

    [Fact]
    public void ShipsMoveSmoothlyBetweenTheGamesFrames()
    {
        // In flight, the renderer draws in-between pictures, in which the
        // ships are part of the way between where they were in the game's
        // last two frames
        var run = Play(16, 1000, 26, Flight);
        Assert.True(run.InBetweenFrames > 1000, $"only {run.InBetweenFrames} in-between frames were drawn");
        Assert.Equal(0, Play(16, 1000, 26, Flight, interpolate: false).InBetweenFrames);
    }
}

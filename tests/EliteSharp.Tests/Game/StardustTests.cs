using System.Numerics;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Tests.Game;

/// <summary>The stardust in the 3D world, which the renderer moves smoothly between frames.</summary>
public sealed class StardustTests
{
    /// <summary>
    /// Put two particles in the front view, one that is about to go off the
    /// screen and one in the middle, move them twice at the given speed and
    /// pitch, and return the frame that the renderer would get for the second
    /// move.
    /// </summary>
    private static FrameData Fly(int speed, int pitch, (float X, float Y, float Z) leaving)
    {
        var game = new PrivateGame();
        game.Set("_viewType", 0);
        game.Set("_view", 0);
        game.Set("_stardustCount", 2);
        game.Set("_speed", speed);
        game.Set("_pitch", pitch);
        var (x, y, z) = (game.Get<float[]>("_dustX"), game.Get<float[]>("_dustY"), game.Get<float[]>("_dustZ"));
        (x[1], y[1], z[1]) = leaving;
        (x[2], y[2], z[2]) = (10, 10, 200);

        var world = game.Get<World>("_world");
        var frame = new FrameData { HasWorld = true, HasPreviousWorld = true, Time = 0, NextTime = 100 };
        game.Call("MoveStardust");
        world.CopyTo(frame.PreviousWorld);
        game.Call("MoveStardust");
        world.CopyTo(frame.World);
        return frame;
    }

    /// <summary>Flying forwards, the first particle goes off the side of the screen in the second move.</summary>
    private static FrameData FlyForwards() => Fly(40, 0, (110, 10, 200));

    /// <summary>Pulling up hard, the first particle goes off the bottom of the screen in the second move.</summary>
    private static FrameData PullUp() => Fly(0, 8, (10, -105, 200));

    private static float ScreenY(Vector3 position) => 256 * position.Y / position.Z;

    [Fact]
    public void AParticleThatIsRecycledGetsANewId()
    {
        var frame = FlyForwards();
        var before = frame.PreviousWorld.Particles;
        var after = frame.World.Particles;
        Assert.DoesNotContain(after[0].Id, before.Select(p => p.Id));
        Assert.Equal(before[1].Id, after[1].Id);
        Assert.NotNull(after[0].Entry);
        Assert.Null(after[1].Entry);
    }

    [Fact]
    public void ARecycledParticleMovesInFromWhereItWouldHaveBeenRatherThanFromWhereItWas()
    {
        // It doesn't glide across the screen from where it left, and it
        // doesn't sit still for a frame either
        var frame = FlyForwards();
        var interpolator = new FrameInterpolator();
        var recycled = frame.World.Particles[0];
        var entry = recycled.Entry!.Value;
        Assert.Equal(entry, interpolator.World(frame, 0).Particles[0].Position);
        Assert.Equal(Vector3.Lerp(entry, recycled.Position, 0.5f), interpolator.World(frame, 50).Particles[0].Position);
        Assert.Equal(recycled.Position, interpolator.World(frame, 100).Particles[0].Position);
        Assert.NotEqual(frame.World.Particles[1].Position, interpolator.World(frame, 50).Particles[1].Position);
    }

    [Fact]
    public void WhenPullingUpARecycledParticleMovesDownTheScreenLikeTheRest()
    {
        // Pulling up moves the stardust down the screen by the pitch (8
        // pixels) each move, so a recycled particle comes in from 8 pixels
        // further up, and moves just as far in a frame as the other particle
        var frame = PullUp();
        var recycled = frame.World.Particles[0];
        Assert.Equal(8, ScreenY(recycled.Entry!.Value) - ScreenY(recycled.Position), 3);

        var other = frame.World.Particles[1];
        var otherBefore = frame.PreviousWorld.Particles[1];
        Assert.Equal(ScreenY(otherBefore.Position) - ScreenY(other.Position), ScreenY(recycled.Entry!.Value) - ScreenY(recycled.Position), 3);
    }

    [Fact]
    public void ARecycledParticleIsMovedInOnlyInTheFrameItWasRecycled()
    {
        // If the game sends the same stardust again (say while waiting), the
        // particle is where it was in the frame before, so it stays put
        var frame = FlyForwards();
        frame.PreviousWorld.CopyFrom(frame.World);
        var particle = frame.World.Particles[0];
        Assert.Equal(particle.Position, new FrameInterpolator().World(frame, 50).Particles[0].Position);
    }
}

/// <summary>
/// The wide stardust, which fills a space view that is wider than the
/// original's in place of the game's own stardust.
/// </summary>
public sealed class WideStardustTests
{
    /// <summary>A game in flight in the given view, with the space view widened by the given margin, and the stardust scattered.</summary>
    private static PrivateGame InFlight(float margin, int view = 0, int speed = 0, int roll = 0, int pitch = 0)
    {
        var game = new PrivateGame();
        game.Get<Hud>("_hud").SideMargin = margin;
        game.Set("_viewType", 0);
        game.Set("_view", view);
        game.Set("_stardustCount", 20);
        game.Set("_speed", speed);
        game.Set("_roll", roll);
        game.Set("_pitch", pitch);
        game.Call("CreateStardust");
        return game;
    }

    private static List<Particle> Shown(PrivateGame game)
    {
        var frame = new SceneFrame();
        game.Get<World>("_world").CopyTo(frame);
        return frame.Particles;
    }

    /// <summary>A particle's position on the front view's screen (in the original's pixels from the centre) and its distance.</summary>
    private static (float X, float Y, float Z) OnScreen(Particle particle) =>
        (256 * particle.Position.X / particle.Position.Z, 256 * particle.Position.Y / particle.Position.Z, particle.Position.Z);

    [Fact]
    public void AtTheOriginalsWidthTheGamesOwnStardustIsShown()
    {
        var game = InFlight(0);
        game.Call("MoveStardust");
        var shown = Shown(game);
        var x = game.Get<float[]>("_dustX");
        var z = game.Get<float[]>("_dustZ");
        Assert.Equal(20, shown.Count);
        for (int particle = 1; particle <= 20; particle++)
        {
            var (screenX, _, distance) = OnScreen(shown[particle - 1]);
            Assert.Equal(x[particle], screenX, 2);
            Assert.Equal(MathF.Max(z[particle], 4), distance, 2);
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    public void AWidenedViewIsFilledWithStardustAsDenselyAsTheOriginal(float margin)
    {
        var game = InFlight(margin);
        game.Call("MoveStardust");
        var shown = Shown(game);
        float halfWidth = 120 + margin;
        Assert.Equal((int)MathF.Round(20 * halfWidth / 120), shown.Count);
        Assert.All(shown, p => Assert.True(MathF.Abs(OnScreen(p).X) < halfWidth + 7));
        Assert.Contains(shown, p => MathF.Abs(OnScreen(p).X) > 128);
    }

    [Theory]
    [InlineData(0, 0, 31, 0)]     // rolling hard
    [InlineData(0, 0, 0, 8)]      // pulling up hard
    [InlineData(0, 40, 20, -5)]   // flying, rolling and diving
    [InlineData(1, 40, 31, 8)]
    [InlineData(2, 40, 31, 8)]
    [InlineData(3, 40, -31, -8)]
    public void TheWideStardustMovesExactlyAsTheGamesStardustWould(int view, int speed, int roll, int pitch)
    {
        // Every particle that isn't recycled moves just as the game moves its
        // own stardust, however far out it is, so rolling turns it in circles
        // right across the view rather than in squashed ellipses
        var game = InFlight(150, view, speed, roll, pitch);
        game.Call("MoveStardust");
        var before = Shown(game).ToDictionary(p => p.Id);
        game.Call("MoveStardust");
        var moved = 0;
        foreach (var particle in Shown(game))
        {
            if (!before.TryGetValue(particle.Id, out var old))
            {
                continue;
            }

            var from = InView(old.Position, view);
            var expected = view switch
            {
                0 => ((float, float, float))game.Call("StepStardustFront", from.X, from.Y, from.Z, roll / 256f)!,
                1 => ((float, float, float))game.Call("StepStardustRear", from.X, from.Y, from.Z, roll / 256f)!,
                _ => StepSide(game, from, view, roll, pitch),
            };

            var actual = InView(particle.Position, view);
            Assert.Equal(expected.Item1, actual.X, 2);
            Assert.Equal(expected.Item2, actual.Y, 2);
            moved++;
        }

        Assert.True(moved >= 20, $"only {moved} particles moved without being recycled");
    }

    /// <summary>A particle's position in a view's own screen coordinates.</summary>
    private static (float X, float Y, float Z) InView(Vector3 world, int view)
    {
        var v = Vector3.TransformNormal(world, Camera.ViewRotation(view));
        return (256 * v.X / v.Z, 256 * v.Y / v.Z, v.Z);
    }

    private static (float, float, float) StepSide(PrivateGame game, (float X, float Y, float Z) from, int view, int roll, int pitch)
    {
        int direction = view == 3 ? -1 : 1;
        var method = typeof(EliteSharp.Game.EliteGame).GetMethod("StepStardustSide", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object[] args = [from.X, from.Y, from.Z, direction, (float)(direction * pitch), (float)(direction * roll), 0f];
        return ((float, float, float))method.Invoke(game.Game, args)!;
    }

    [Fact]
    public void RecycledWideStardustMovesInFromWhereItWouldHaveBeen()
    {
        var game = InFlight(150, speed: 40, roll: 10);
        var seen = new HashSet<int>();
        int recycled = 0;
        for (int move = 0; move < 30; move++)
        {
            game.Call("MoveStardust");
            foreach (var particle in Shown(game))
            {
                if (seen.Add(particle.Id) && move > 0)
                {
                    Assert.NotNull(particle.Entry);
                    recycled++;
                }
            }
        }

        Assert.True(recycled > 10, $"only {recycled} particles were recycled");
    }

    [Fact]
    public void NarrowingTheViewBringsBackTheGamesOwnStardust()
    {
        var game = InFlight(150);
        game.Call("MoveStardust");
        Assert.True(Shown(game).Count > 20);
        game.Get<Hud>("_hud").SideMargin = 0;
        game.Call("MoveStardust");
        Assert.Equal(20, Shown(game).Count);
        Assert.All(Shown(game), p => Assert.True(p.Id > 0));
    }
}

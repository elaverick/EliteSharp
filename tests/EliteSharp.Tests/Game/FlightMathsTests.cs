using System.Numerics;
using EliteSharp.Game.Ships;

namespace EliteSharp.Tests.Game;

/// <summary>
/// Moving and turning ships (MVEIT, MVS4, MVS5 and TIDY), with the game's
/// floating-point maths.
/// </summary>
public sealed class FlightMathsTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>
    /// Set up INWK as a Cobra Mk III at the given position, pointing along the
    /// given nose with its roof as close to up as possible, with our roll,
    /// pitch and speed, and on an iteration of the main loop that doesn't
    /// tidy its orientation.
    /// </summary>
    private static (PrivateGame Game, Ship Ship) SetUp(Vector3 position, Vector3 nose, int roll = 0, int pitch = 0, int speed = 0)
    {
        var game = new PrivateGame();
        var ship = game.SetUpShip(ShipType.CobraMkIII);
        nose = Vector3.Normalize(nose);
        var roof = Vector3.Normalize(Vector3.UnitY - nose * Vector3.Dot(Vector3.UnitY, nose));
        ship.Position = position;
        ship.Nose = nose;
        ship.Roof = roof;
        ship.Side = Vector3.Cross(roof, nose);
        game.Set("_roll", roll);
        game.Set("_pitch", pitch);
        game.Set("_speed", speed);
        game.Set("_currentSlot", 0);
        game.Set("_mainLoopCounter", 1);
        return (game, ship);
    }

    private static float OrthonormalityError(Ship ship) => new[]
    {
        MathF.Abs(ship.Nose.Length() - 1), MathF.Abs(ship.Roof.Length() - 1), MathF.Abs(ship.Side.Length() - 1),
        MathF.Abs(Vector3.Dot(ship.Nose, ship.Roof)), MathF.Abs(Vector3.Dot(ship.Nose, ship.Side)), MathF.Abs(Vector3.Dot(ship.Roof, ship.Side)),
    }.Max();

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = Tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, got {actual}");

    [Fact]
    public void ShipsMoveAlongTheirNoseByOneAndAHalfForEachUnitOfSpeed()
    {
        var (game, ship) = SetUp(new Vector3(0, 0, 1000), new Vector3(0.6f, 0, 0.8f));
        ship.Speed = 20;

        game.Call("MoveShip");

        AssertClose(new Vector3(18, 0, 1024), game.CurrentShip.Position, 1e-3f);
    }

    [Fact]
    public void OurSpeedMovesEverythingTowardsUs()
    {
        var (game, _) = SetUp(new Vector3(100, 200, 5000), Vector3.UnitZ, speed: 12);

        game.Call("MoveShip");

        AssertClose(new Vector3(100, 200, 4988), game.CurrentShip.Position, 1e-3f);
    }

    [Fact]
    public void RollingTurnsTheWorldAboutTheZAxis()
    {
        // MVEIT part 5 approximates y = y - alpha * x and x = x + alpha * y,
        // where alpha is the roll in radians (the steps are 1/256 radian)
        var (game, _) = SetUp(new Vector3(1000, 0, 0), Vector3.UnitZ, roll: 16);

        game.Call("MoveShip");

        float angle = 16 / 256f;
        AssertClose(new Vector3(1000 * MathF.Cos(angle), -1000 * MathF.Sin(angle), 0), game.CurrentShip.Position, 1e-2f);
    }

    [Fact]
    public void PitchingTurnsTheWorldAboutTheXAxis()
    {
        // MVEIT part 5 approximates z = z + beta * y and y = y - beta * z
        var (game, _) = SetUp(new Vector3(0, 1000, 0), Vector3.UnitZ, pitch: 8);

        game.Call("MoveShip");

        float angle = 8 / 256f;
        AssertClose(new Vector3(0, 1000 * MathF.Cos(angle), 1000 * MathF.Sin(angle)), game.CurrentShip.Position, 1e-2f);
    }

    [Fact]
    public void TurningKeepsDistancesAndOrientationsExact()
    {
        var (game, ship) = SetUp(new Vector3(2000, -1500, 15000), new Vector3(3, 1, 4), roll: -7, pitch: 4);
        ship.PitchCounter = 0x80 | 127;
        ship.RollCounter = 127;
        float distance = ship.Position.Length();

        for (int i = 0; i < 5000; i++)
        {
            game.Call("MoveShip");
            game.Set("_mainLoopCounter", (game.Get<int>("_mainLoopCounter") - 1) & 0xFF);
        }

        Assert.Equal(distance, game.CurrentShip.Position.Length(), distance * 1e-4f);
        Assert.True(OrthonormalityError(game.CurrentShip) < 1e-5f, $"Orthonormality error {OrthonormalityError(game.CurrentShip)}");
    }

    [Theory]
    [InlineData(127, 1)]
    [InlineData(0x80 | 127, -1)]
    public void ShipsPitchBySixteenthOfARadian(int pitchCounter, int direction)
    {
        // MVS5 rotates roofv towards nosev (or away from it, if the pitch
        // counter is negative) by 1/16 radian
        var (game, ship) = SetUp(Vector3.Zero, Vector3.UnitZ);
        ship.PitchCounter = pitchCounter;

        game.Call("MoveShip");

        var (sin, cos) = MathF.SinCos(direction / 16f);
        AssertClose(new Vector3(0, -sin, cos), game.CurrentShip.Nose);
        AssertClose(new Vector3(0, cos, sin), game.CurrentShip.Roof);
        Assert.Equal(pitchCounter, game.CurrentShip.PitchCounter);
    }

    [Fact]
    public void ThePitchCounterDampsDown()
    {
        var (game, ship) = SetUp(Vector3.Zero, Vector3.UnitZ);
        ship.PitchCounter = 0x80 | 5;

        game.Call("MoveShip");

        Assert.Equal(0x80 | 4, game.CurrentShip.PitchCounter);
    }

    [Fact]
    public void TidyingMakesTheOrientationOrthonormalWithTheOriginalsHandedness()
    {
        var game = new PrivateGame();
        var ship = game.SetUpShip(ShipType.CobraMkIII);
        ship.Nose = new Vector3(0.1f, 0.05f, 1.04f);
        ship.Roof = new Vector3(0.03f, 0.97f, -0.08f);
        ship.Side = new Vector3(0.5f, 0.5f, 0.5f);

        game.Call("OrthonormaliseOrientation");

        ship = game.CurrentShip;
        Assert.True(OrthonormalityError(ship) < 1e-6f);
        AssertClose(Vector3.Normalize(new Vector3(0.1f, 0.05f, 1.04f)), ship.Nose, 1e-6f);

        // TIDY sets sidev to roofv x nosev
        AssertClose(Vector3.Cross(ship.Roof, ship.Nose), ship.Side, 1e-6f);
    }
}

using System.Numerics;
using EliteSharp.Game.Ships;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The docking checks (ISDK), the altimeter (MA93) and hyperspace distances,
/// whose thresholds come from the original.
/// </summary>
public sealed class DockingAndAltitudeTests
{
    private static Vector3 Degrees(float angle, Func<float, Vector3> direction) => direction(angle * MathF.PI / 180);

    /// <summary>
    /// Set up the planet and a friendly station just in front of us, and check
    /// whether we can dock with the station facing us at the given angles.
    /// </summary>
    /// <param name="noseTilt">How far the station's nose is tilted away from pointing straight at us, in degrees.</param>
    /// <param name="slotRoll">How far the slot is rolled from horizontal, in degrees.</param>
    /// <param name="planetAngle">The angle between straight ahead and the direction of the planet, in degrees.</param>
    private static bool CanDock(float noseTilt, float slotRoll, float planetAngle, bool hostile = false)
    {
        var game = new PrivateGame();
        game.Call("ResetBubble");
        game.Call("ResetWorkspace");
        game.CurrentShip.Position = Degrees(planetAngle, a => new Vector3(MathF.Sin(a), 0, MathF.Cos(a))) * 80000;
        game.Call("AddShip", ShipType.Planet);
        game.Call("ResetWorkspace");
        game.CurrentShip.Position = new Vector3(0, 0, 200);
        game.Call("AddShip", ShipType.SpaceStation);
        game.Slots[1]!.Behaviour = hostile ? 0b00000100 : 0;

        game.Call("ResetWorkspace");
        var station = game.CurrentShip;
        station.Position = new Vector3(0, 0, 100);
        station.Nose = Degrees(noseTilt, a => new Vector3(0, -MathF.Sin(a), -MathF.Cos(a)));
        var roof = Degrees(slotRoll, a => new Vector3(MathF.Cos(a), MathF.Sin(a), 0));
        station.Roof = Vector3.Normalize(roof - station.Nose * Vector3.Dot(roof, station.Nose));
        station.Side = Vector3.Cross(station.Roof, station.Nose);
        return (bool)game.Call("CheckDocking")!;
    }

    [Fact]
    public void WeCanDockStraightIn() => Assert.True(CanDock(0, 0, 0));

    [Fact]
    public void WeCantDockWithAHostileStation() => Assert.False(CanDock(0, 0, 0, hostile: true));

    [Theory]
    [InlineData(26, true)]
    [InlineData(27, false)]
    public void TheAngleOfApproachMustBeLessThan26Degrees(float noseTilt, bool docks)
    {
        // nosev_z must be -86/96 or less, which is 26.4 degrees
        Assert.Equal(docks, CanDock(noseTilt, 0, 0));
    }

    [Theory]
    [InlineData(33, true)]
    [InlineData(34, false)]
    public void TheSlotMustBeCloseToHorizontal(float slotRoll, bool docks)
    {
        // |roofv_x| must be at least 80/96, which is 33.6 degrees
        Assert.Equal(docks, CanDock(0, slotRoll, 0));
    }

    [Theory]
    [InlineData(21, true)]
    [InlineData(23, false)]
    [InlineData(120, true)]
    public void WeMustBeInTheSafeConeOfApproach(float planetAngle, bool docks)
    {
        // The z-coordinate of the unit vector to the planet must be at least
        // 89/96 (22 degrees), but the original doesn't check its sign, so it
        // also passes if the planet is behind us
        Assert.Equal(docks, CanDock(0, 0, planetAngle));
    }

    /// <summary>The altimeter reading with the planet at the given distance, or null if we crash.</summary>
    private static int? Altitude(Vector3 planet)
    {
        var game = new PrivateGame();
        game.Call("ResetBubble");
        game.Call("ResetWorkspace");
        game.CurrentShip.Position = planet;
        game.Call("AddShip", ShipType.Planet);
        game.Set("InSafeZone", 0);
        game.Set("_energy", 200);
        return game.Survives(() => game.Call("AltitudeChecks", 10)) ? game.Get<int>("_altitude") : null;
    }

    [Theory]
    [InlineData(40000)]
    [InlineData(30000)]
    [InlineData(60000)]
    public void TheAltimeterShowsOurHeightAboveTheSurface(float distance)
    {
        // The altimeter measures in 256ths, and the surface is at the square
        // root of 37 * 256 (so about 24,915 from the planet's centre)
        int expected = (int)MathF.Sqrt(distance * distance / 65536 - 37 * 256);
        Assert.Equal(expected, Altitude(Vector3.Normalize(new Vector3(1, -2, 3)) * distance));
    }

    [Fact]
    public void WeCrashIntoThePlanetAtItsSurface()
    {
        Assert.Null(Altitude(new Vector3(0, 0, 24_900)));
        Assert.NotNull(Altitude(new Vector3(0, 0, 24_950)));

        // The surface is the same distance away in every direction
        Assert.Null(Altitude(Vector3.Normalize(new Vector3(1, 1, 1)) * 24_900));
        Assert.NotNull(Altitude(Vector3.Normalize(new Vector3(1, 1, 1)) * 24_950));
    }

    [Fact]
    public void TheAltimeterIsFullOutOfRange() => Assert.Equal(255, Altitude(new Vector3(0, 0, 70000)));

    /// <summary>The distance in tenths of a light year between two systems at the given galactic coordinates.</summary>
    private static int Distance(int fromX, int fromY, int toX, int toY)
    {
        var game = new PrivateGame();
        game.Set("_currentSystemX", fromX);
        game.Set("_currentSystemY", fromY);
        var seeds = game.Get<int[]>("_selectedSeeds");
        seeds[1] = toY;
        seeds[3] = toX;
        game.Call("CalculateDistance", toX);
        return game.Get<int>("_selectedDistance");
    }

    [Theory]
    [InlineData(20, 173, 11, 174, 36)]  // Lave to Diso
    [InlineData(20, 173, 13, 186, 38)]  // Lave to Leesti
    [InlineData(20, 173, 20, 173, 0)]
    [InlineData(0, 0, 255, 255, 1140)]
    public void HyperspaceDistancesAreInTenthsOfALightYear(int fromX, int fromY, int toX, int toY, int expected)
    {
        // Each unit of x is 0.4 light years, and each unit of y is 0.2
        Assert.Equal(expected, Distance(fromX, fromY, toX, toY));
        Assert.Equal(expected, Distance(toX, toY, fromX, fromY));
    }
}

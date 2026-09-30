using System.Numerics;
using System.Reflection;
using EliteSharp.Game;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Tests.Rendering;

/// <summary>
/// Checks what the game hands to the 3D world when it draws a ship: the ship
/// itself wherever it is (the renderer decides what can be seen), and an
/// exploding ship's cloud. This drives a real game (without a window) through
/// its ship drawing routine, LL9, and reads the 3D world back through the HUD.
/// </summary>
public sealed class GameShipDrawingTests
{
    private sealed class Harness
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Hud _hud = new(new FrameExchange());
        private readonly EliteGame _game;

        public Harness(int shipType, int x, int y, int z)
        {
            _game = new EliteGame(_hud, new BbcKeyboard(), null, new GameOptions { Sound = false, Gamepad = false });
            Call("ResetWorkspace");
            Set("_shipType", shipType);
            Set("_blueprint", Call("BlueprintFor", shipType));
            Ship.Position = new Vector3(x, y, z);
        }

        public Ship Ship => (Ship)typeof(EliteGame).GetField("_currentShip", Private)!.GetValue(_game)!;

        public SceneFrame Draw()
        {
            Call("DrawShip");
            var frame = new SceneFrame();
            _hud.CopyWorld!(frame);
            return frame;
        }

        private object? Call(string method, params object[] args) =>
            typeof(EliteGame).GetMethod(method, Private)!.Invoke(_game, args);

        private void Set(string field, object? value) => typeof(EliteGame).GetField(field, Private)!.SetValue(_game, value);
    }

    public static TheoryData<int, int, int, int> Positions() => new()
    {
        // Close, straight ahead
        { ShipType.Constrictor, 0, 0, 600 },

        // Far away, where the original draws a dot (and beyond, where it doesn't draw it at all)
        { ShipType.Constrictor, 0, 0, 30_000 },
        { ShipType.Sidewinder, 500, -300, 60_000 },
        { ShipType.CobraMkIII, 0, 0, 0x7F_0000 / 2 },

        // Outside the original's 45-degree field of view (but in a wide one)
        { ShipType.Constrictor, 1_300, 0, 1_000 },
        { ShipType.Viper, -4_000, 2_000, 3_000 },

        // Behind us
        { ShipType.Thargoid, 200, 100, -2_000 },
    };

    [Theory]
    [MemberData(nameof(Positions))]
    public void ShipsAreAlwaysGivenToTheWorldAsShips(int shipType, int x, int y, int z)
    {
        var frame = new Harness(shipType, x, y, z).Draw();

        // The ship is its model, wherever it is, and never a dot
        var ship = Assert.Single(frame.Ships);
        Assert.Equal(ShipCatalogue.Get(shipType).Model.Name, ship.Model);
        Assert.Empty(frame.Particles);
        Assert.Equal(new System.Numerics.Vector3(x, y, z), ship.Transform.Translation);
    }

    [Theory]
    [InlineData(1_000, 0, 1_000)]
    [InlineData(0, 0, 60_000)]
    [InlineData(0, 0, -1_000)]
    public void AFiringShipShowsItsLaserWhereverItIs(int x, int y, int z)
    {
        var harness = new Harness(ShipType.Sidewinder, x, y, z);
        harness.Ship.Flags |= Ship.FlagFiring;
        var frame = harness.Draw();
        Assert.Single(frame.Ships);
        Assert.Single(frame.Lines);
        Assert.Equal(0, harness.Ship.Flags & Ship.FlagFiring);
    }

    [Theory]
    [InlineData(0, 0, 1_000, true)]
    [InlineData(3_000, 0, 1_000, false)]
    [InlineData(0, 0, -1_000, false)]
    public void AnExplodingShipBecomesACloud(int x, int y, int z, bool inOriginalView)
    {
        var harness = new Harness(ShipType.Constrictor, x, y, z);
        harness.Ship.Flags |= Ship.FlagKilled;
        var frame = harness.Draw();

        // The ship becomes its explosion cloud (in front of the camera it has
        // particles; behind it, the particles are left out)
        Assert.Empty(frame.Ships);
        Assert.True((harness.Ship.Flags & Ship.FlagExploding) != 0);
        Assert.Equal(z > 0, frame.Particles.Count > 0);

        // As in the original, the cloud only counts as drawn on the screen
        // (which decides whether it takes random numbers next time) when the
        // ship is in the original's field of view
        Assert.Equal(inOriginalView, (harness.Ship.Flags & Ship.FlagOnScreenCloud) != 0);
    }
}

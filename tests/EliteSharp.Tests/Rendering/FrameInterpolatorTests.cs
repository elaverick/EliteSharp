using System.Numerics;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Tests.Rendering;

/// <summary>
/// Moving the 3D world smoothly between the game's frames, which only changes
/// what is drawn, and only for the ships, planet, sun and stardust.
/// </summary>
public sealed class FrameInterpolatorTests
{
    private const long Published = 1000;
    private const long Next = 2000;

    private static Matrix4x4 Placed(Vector3 position, float roll = 0)
    {
        // The original's starting orientation (nosev pointing at us), rolled
        var rotation = Matrix4x4.CreateRotationZ(roll);
        var side = Vector3.TransformNormal(Vector3.UnitX, rotation);
        var roof = Vector3.TransformNormal(Vector3.UnitY, rotation);
        var nose = -Vector3.UnitZ;
        return new Matrix4x4(
            side.X, side.Y, side.Z, 0,
            roof.X, roof.Y, roof.Z, 0,
            nose.X, nose.Y, nose.Z, 0,
            position.X, position.Y, position.Z, 1);
    }

    private static ShipInstance Ship(Vector3 position, int id = 1, string model = "cobra-mk3", float roll = 0) =>
        new(model, Placed(position, roll), Ink.Cyan, id);

    /// <summary>A frame with the given previous and current worlds.</summary>
    private static FrameData Frame(Action<SceneFrame> previous, Action<SceneFrame> current, int previousView = 0, int previousGeneration = 0)
    {
        var frame = new FrameData { HasWorld = true, HasPreviousWorld = true, Time = Published, NextTime = Next };
        frame.PreviousWorld.Camera = new Camera(previousView);
        frame.PreviousWorld.Generation = previousGeneration;
        frame.World.Camera = new Camera(0);
        previous(frame.PreviousWorld);
        current(frame.World);
        return frame;
    }

    private static FrameData MovingShip(Vector3 from, Vector3 to) =>
        Frame(w => w.Ships.Add(Ship(from)), w => w.Ships.Add(Ship(to)));

    private static long At(float t) => Published + (long)((Next - Published) * t);

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(Published, 0f)]
    [InlineData(1250, 0.25f)]
    [InlineData(1500, 0.5f)]
    [InlineData(Next, 1f)]
    [InlineData(5000, 1f)]
    public void ProgressGoesFromPublishedToNextFrame(long now, float expected) =>
        Assert.Equal(expected, FrameInterpolator.Progress(new FrameData { Time = Published, NextTime = Next }, now), 5);

    [Fact]
    public void AFrameWithNoNextTimeIsShownAsItIs() =>
        Assert.Equal(1f, FrameInterpolator.Progress(new FrameData { Time = Published, NextTime = Published }, Published));

    [Theory]
    [InlineData(0f, 1000f)]
    [InlineData(0.25f, 975f)]
    [InlineData(0.5f, 950f)]
    public void ShipsMoveInAStraightLineFromThePreviousFrame(float t, float z)
    {
        var frame = MovingShip(new Vector3(0, 0, 1000), new Vector3(0, 0, 900));
        var world = new FrameInterpolator().World(frame, At(t));
        Assert.Equal(z, world.Ships.Single().Transform.Translation.Z, 3);
    }

    [Fact]
    public void WhenTheNextFrameIsDueTheLatestFrameIsShownAsItIs()
    {
        var frame = MovingShip(new Vector3(0, 0, 1000), new Vector3(0, 0, 900));
        Assert.Same(frame.World, new FrameInterpolator().World(frame, Next));
        Assert.Same(frame.World, new FrameInterpolator().World(frame, Next + 12345));
    }

    [Fact]
    public void TurningShipsKeepTheirAxesAtRightAnglesAndTheSameWayRound()
    {
        // Our fastest roll turns things by 31/256 radians a frame
        var frame = Frame(w => w.Ships.Add(Ship(new Vector3(100, 0, 1000), roll: 0)), w => w.Ships.Add(Ship(new Vector3(100, 0, 1000), roll: 31 / 256f)));
        var expectedHandedness = MathF.Sign(Handedness(frame.World.Ships[0].Transform));
        for (float t = 0; t < 1; t += 0.125f)
        {
            var transform = new FrameInterpolator().World(frame, At(t)).Ships.Single().Transform;
            var (side, roof, nose) = Axes(transform);
            Assert.Equal(1, side.Length(), 4);
            Assert.Equal(1, roof.Length(), 4);
            Assert.Equal(1, nose.Length(), 4);
            Assert.Equal(0, Vector3.Dot(side, roof), 4);
            Assert.Equal(0, Vector3.Dot(roof, nose), 4);
            Assert.Equal(0, Vector3.Dot(side, nose), 4);
            Assert.Equal(expectedHandedness, MathF.Sign(Handedness(transform)));
        }

        // Half way, the ship has rolled half as far
        var halfway = Axes(new FrameInterpolator().World(frame, At(0.5f)).Ships.Single().Transform);
        Assert.Equal(31 / 512f, MathF.Atan2(halfway.Side.Y, halfway.Side.X), 4);
    }

    private static (Vector3 Side, Vector3 Roof, Vector3 Nose) Axes(Matrix4x4 m) =>
        (new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33));

    private static float Handedness(Matrix4x4 m)
    {
        var (side, roof, nose) = Axes(m);
        return Vector3.Dot(Vector3.Cross(roof, nose), side);
    }

    [Fact]
    public void NewShipsAppearAtOnceAndShipsThatHaveGoneAreNotDrawn()
    {
        var frame = Frame(
            w => w.Ships.Add(Ship(new Vector3(0, 0, 1000), id: 1)),
            w => w.Ships.Add(Ship(new Vector3(0, 0, 500), id: 2)));
        var ship = new FrameInterpolator().World(frame, At(0.5f)).Ships.Single();
        Assert.Equal(2, ship.Id);
        Assert.Equal(500, ship.Transform.Translation.Z);
    }

    [Fact]
    public void AShipThatHasBecomeAnotherModelIsNotMovedSmoothly()
    {
        var frame = Frame(
            w => w.Ships.Add(Ship(new Vector3(0, 0, 1000), model: "cobra-mk3")),
            w => w.Ships.Add(Ship(new Vector3(0, 0, 900), model: "python")));
        Assert.Equal(900, new FrameInterpolator().World(frame, At(0.5f)).Ships.Single().Transform.Translation.Z);
    }

    [Fact]
    public void NothingMovesSmoothlyWhenTheViewChanges()
    {
        var frame = Frame(w => w.Ships.Add(Ship(new Vector3(0, 0, 1000))), w => w.Ships.Add(Ship(new Vector3(0, 0, 900))), previousView: 1);
        Assert.Same(frame.World, new FrameInterpolator().World(frame, At(0.5f)));
    }

    [Fact]
    public void NothingMovesSmoothlyAfterTheScreenIsCleared()
    {
        var frame = Frame(w => w.Ships.Add(Ship(new Vector3(0, 0, 1000))), w => w.Ships.Add(Ship(new Vector3(0, 0, 900))), previousGeneration: -1);
        Assert.Same(frame.World, new FrameInterpolator().World(frame, At(0.5f)));
    }

    [Fact]
    public void NothingMovesSmoothlyWhenThePreviousFrameHadNoWorld()
    {
        var frame = MovingShip(new Vector3(0, 0, 1000), new Vector3(0, 0, 900));
        frame.HasPreviousWorld = false;
        Assert.Same(frame.World, new FrameInterpolator().World(frame, At(0.5f)));
    }

    [Fact]
    public void NothingMovesSmoothlyWhenItIsTurnedOff()
    {
        var frame = MovingShip(new Vector3(0, 0, 1000), new Vector3(0, 0, 900));
        Assert.Same(frame.World, new FrameInterpolator { Enabled = false }.World(frame, At(0.5f)));
    }

    [Theory]
    [InlineData(0, 0, 1000, 0, 0, 2000, true)]     // flying away
    [InlineData(0, 0, 1000, 0, 0, 4000, false)]    // too far in one frame
    [InlineData(0, 0, 1000, 120, 0, 1000, true)]   // turning by 7 degrees
    [InlineData(0, 0, 1000, 1000, 0, 0, false)]    // turning by 90 degrees
    [InlineData(10, 10, 4, -10, 5, 50, false)]     // stardust that has gone off the edge and come back
    public void ThingsThatJumpALongWayAreNotMovedSmoothly(float x1, float y1, float z1, float x2, float y2, float z2, bool smooth) =>
        Assert.Equal(smooth, FrameInterpolator.CanMoveSmoothly(new Vector3(x1, y1, z1), new Vector3(x2, y2, z2)));

    [Fact]
    public void TheStardustMovesSmoothlyButExplosionsAndLasersAreAsInTheLatestFrame()
    {
        var oldDust = new Particle(new Vector3(0, 0, 100), 2, 2, Ink.White, Stardust: true, Id: 1);
        var newDust = oldDust with { Position = new Vector3(0, 0, 80) };
        var oldCloud = new Particle(new Vector3(5, 5, 500), 2, 2, Ink.Yellow);
        var newCloud = oldCloud with { Position = new Vector3(-5, 3, 480) };
        var beam = new LineSegment(new Vector3(0, -10, 8), new Vector3(0, 0, 4096), Ink.Red);
        var frame = Frame(
            w => w.Particles.AddRange([oldDust, oldCloud]),
            w =>
            {
                w.Particles.AddRange([newDust, newCloud]);
                w.Lines.Add(beam);
            });

        var world = new FrameInterpolator().World(frame, At(0.5f));
        Assert.Equal(90, world.Particles.Single(p => p.Stardust).Position.Z, 3);
        Assert.Equal(newCloud, world.Particles.Single(p => !p.Stardust));
        Assert.Equal(beam, world.Lines.Single());
    }

    [Fact]
    public void ThePlanetAndSunMoveSmoothly()
    {
        var frame = Frame(
            w =>
            {
                w.Planets.Add(new PlanetInstance(new Vector3(0, 0, 100000), 24576, -Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX, false, true, Ink.Green, Id: 3));
                w.Suns.Add(new SunInstance(new Vector3(0, 300000, 0), 24576, 1, 7, Id: 4));
            },
            w =>
            {
                w.Planets.Add(new PlanetInstance(new Vector3(0, 0, 90000), 24576, -Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX, false, false, Ink.Green, Id: 3));
                w.Suns.Add(new SunInstance(new Vector3(0, 290000, 0), 24576, 2, 8, Id: 4));
            });

        var world = new FrameInterpolator().World(frame, At(0.5f));
        var planet = world.Planets.Single();
        var sun = world.Suns.Single();
        Assert.Equal(95000, planet.Centre.Z, 1);
        Assert.False(planet.ShowFeatures);
        Assert.Equal(295000, sun.Centre.Y, 1);
        Assert.Equal((2, 8), (sun.FringeMask, sun.Seed));
    }

    [Fact]
    public void WhatIsDrawnDependsOnlyOnTheTimeNotOnHowOftenItIsDrawn()
    {
        // Drawing the same frame at every tick, or only at every hundredth,
        // gives the same picture at the times they have in common
        var frame = MovingShip(new Vector3(0, 0, 1000), new Vector3(30, 0, 900));
        float At(FrameInterpolator interpolator, long now) => interpolator.World(frame, now).Ships.Single().Transform.Translation.Z;
        var slow = new FrameInterpolator();
        var fast = new FrameInterpolator();
        for (long now = Published; now <= Next; now++)
        {
            float z = At(fast, now);
            if (now % 100 == 0)
            {
                Assert.Equal(At(slow, now), z);
            }
        }
    }
}

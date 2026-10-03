using System.Runtime.CompilerServices;

namespace EliteSharp.Rendering.Scene;

/// <summary>
/// The 3D world as maintained by the game: each thing in it (a ship, the
/// planet, the sun, the stardust, our laser beams and so on) is an object
/// identified by its owner, which the game replaces whenever it redraws that
/// thing and removes when the thing leaves the screen (where the original
/// would erase it from screen memory). Each frame, the world is copied into
/// the frame that is handed to the renderer.
///
/// The objects' storage is kept and reused, so updating the world doesn't
/// allocate anything once it has warmed up.
///
/// Each owner has an id that stays the same for as long as the owner exists
/// (even if it is removed and put back, as the sun is each time it is
/// redrawn), which goes into the frames so the renderer can match up the
/// objects in one frame with those in the next.
/// </summary>
public sealed class World
{
    /// <summary>Everything that one owner contributes to the world.</summary>
    private sealed class WorldObject
    {
        public int Id;
        public bool HasShip, HasPlanet, HasSun;
        public ShipInstance Ship;
        public PlanetInstance Planet;
        public SunInstance Sun;
        public readonly List<Particle> Particles = [];
        public readonly List<LineSegment> Lines = [];

        public void Reset()
        {
            HasShip = HasPlanet = HasSun = false;
            Particles.Clear();
            Lines.Clear();
        }
    }

    private readonly Dictionary<object, WorldObject> _objects = [];
    private readonly Stack<WorldObject> _spare = [];
    private readonly ConditionalWeakTable<object, StrongBox<int>> _ids = [];
    private int _lastId;

    /// <summary>The space view that the camera is looking out of.</summary>
    public int CameraView { get; set; }

    /// <summary>The number of times the world has been cleared (see <see cref="SceneFrame.Generation"/>).</summary>
    public int Generation { get; private set; }

    /// <summary>The object for an owner, emptied ready to be set.</summary>
    private WorldObject Replace(object owner)
    {
        if (!_objects.TryGetValue(owner, out var worldObject))
        {
            worldObject = _spare.TryPop(out var spare) ? spare : new WorldObject();
            worldObject.Id = _ids.GetValue(owner, _ => new StrongBox<int>(++_lastId)).Value;
            _objects[owner] = worldObject;
        }

        worldObject.Reset();
        return worldObject;
    }

    /// <summary>Set a ship, along with any lines that go with it (such as its laser firing at us).</summary>
    public void SetShip(object owner, in ShipInstance ship, ReadOnlySpan<LineSegment> lines = default)
    {
        var worldObject = Replace(owner);
        worldObject.HasShip = true;
        worldObject.Ship = ship;
        worldObject.Lines.AddRange(lines);
    }

    public void SetPlanet(object owner, in PlanetInstance planet)
    {
        var worldObject = Replace(owner);
        worldObject.HasPlanet = true;
        worldObject.Planet = planet;
    }

    public void SetSun(object owner, in SunInstance sun)
    {
        var worldObject = Replace(owner);
        worldObject.HasSun = true;
        worldObject.Sun = sun;
    }

    public void SetParticles(object owner, ReadOnlySpan<Particle> particles) => Replace(owner).Particles.AddRange(particles);

    public void SetLines(object owner, ReadOnlySpan<LineSegment> lines) => Replace(owner).Lines.AddRange(lines);

    public void Remove(object owner)
    {
        if (_objects.Remove(owner, out var worldObject))
        {
            _spare.Push(worldObject);
        }
    }

    public bool Contains(object owner) => _objects.ContainsKey(owner);

    /// <summary>Remove everything (when the screen is cleared).</summary>
    public void Clear()
    {
        foreach (var worldObject in _objects.Values)
        {
            _spare.Push(worldObject);
        }

        _objects.Clear();
        Generation++;
    }

    /// <summary>Copy the world into a frame for the renderer.</summary>
    public void CopyTo(SceneFrame frame)
    {
        frame.Clear();
        frame.Camera = new Camera(CameraView);
        frame.Generation = Generation;
        foreach (var worldObject in _objects.Values)
        {
            if (worldObject.HasShip)
            {
                frame.Ships.Add(worldObject.Ship with { Id = worldObject.Id });
            }

            if (worldObject.HasPlanet)
            {
                frame.Planets.Add(worldObject.Planet with { Id = worldObject.Id });
            }

            if (worldObject.HasSun)
            {
                frame.Suns.Add(worldObject.Sun with { Id = worldObject.Id });
            }

            frame.Particles.AddRange(worldObject.Particles);
            frame.Lines.AddRange(worldObject.Lines);
        }
    }
}

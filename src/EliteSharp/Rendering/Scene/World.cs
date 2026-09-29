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
/// </summary>
public sealed class World
{
    /// <summary>Everything that one owner contributes to the world.</summary>
    private sealed class WorldObject
    {
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

    /// <summary>The space view that the camera is looking out of.</summary>
    public int CameraView { get; set; }

    /// <summary>The object for an owner, emptied ready to be set.</summary>
    private WorldObject Replace(object owner)
    {
        if (!_objects.TryGetValue(owner, out var worldObject))
        {
            worldObject = _spare.TryPop(out var spare) ? spare : new WorldObject();
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
    }

    /// <summary>Copy the world into a frame for the renderer.</summary>
    public void CopyTo(SceneFrame frame)
    {
        frame.Clear();
        frame.Camera = new Camera(CameraView);
        foreach (var worldObject in _objects.Values)
        {
            if (worldObject.HasShip)
            {
                frame.Ships.Add(worldObject.Ship);
            }

            if (worldObject.HasPlanet)
            {
                frame.Planets.Add(worldObject.Planet);
            }

            if (worldObject.HasSun)
            {
                frame.Suns.Add(worldObject.Sun);
            }

            frame.Particles.AddRange(worldObject.Particles);
            frame.Lines.AddRange(worldObject.Lines);
        }
    }
}

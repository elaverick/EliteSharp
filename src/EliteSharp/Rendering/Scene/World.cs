namespace EliteSharp.Rendering.Scene;

/// <summary>
/// The 3D world as maintained by the game: each drawable thing (a ship, the
/// planet, the sun, the stardust, our laser beams and so on) is an object
/// identified by its owner, which the game replaces whenever it redraws that
/// thing and removes when the thing leaves the screen, just as it does with
/// the thing's image in the 2D screen model. Each frame, a snapshot of the
/// world is handed to the renderer.
/// </summary>
public sealed class World
{
    /// <summary>Everything that one owner contributes to the world.</summary>
    private sealed record WorldObject(
        ShipInstance? Ship = null,
        PlanetInstance? Planet = null,
        SunInstance? Sun = null,
        IReadOnlyList<Particle>? Particles = null,
        IReadOnlyList<LineSegment>? Lines = null);

    private readonly Dictionary<object, WorldObject> _objects = [];

    /// <summary>The space view that the camera is looking out of.</summary>
    public int CameraView { get; set; }

    /// <summary>Set a ship, along with any lines that go with it (such as its laser firing at us).</summary>
    public void SetShip(object owner, ShipInstance ship, IReadOnlyList<LineSegment>? lines = null) =>
        _objects[owner] = new WorldObject(Ship: ship, Lines: lines);

    public void SetPlanet(object owner, PlanetInstance planet) => _objects[owner] = new WorldObject(Planet: planet);

    public void SetSun(object owner, SunInstance sun) => _objects[owner] = new WorldObject(Sun: sun);

    public void SetParticles(object owner, IReadOnlyList<Particle> particles) => _objects[owner] = new WorldObject(Particles: particles);

    public void SetLines(object owner, IReadOnlyList<LineSegment> lines) => _objects[owner] = new WorldObject(Lines: lines);

    public void Remove(object owner) => _objects.Remove(owner);

    public bool Contains(object owner) => _objects.ContainsKey(owner);

    /// <summary>Remove everything (when the screen is cleared).</summary>
    public void Clear() => _objects.Clear();

    /// <summary>Take a snapshot of the world for the renderer.</summary>
    public SceneFrame Snapshot()
    {
        var ships = new List<ShipInstance>();
        var planets = new List<PlanetInstance>();
        var suns = new List<SunInstance>();
        var particles = new List<Particle>();
        var lines = new List<LineSegment>();
        foreach (var worldObject in _objects.Values)
        {
            if (worldObject.Ship != null)
            {
                ships.Add(worldObject.Ship);
            }

            if (worldObject.Planet != null)
            {
                planets.Add(worldObject.Planet);
            }

            if (worldObject.Sun != null)
            {
                suns.Add(worldObject.Sun);
            }

            if (worldObject.Particles != null)
            {
                particles.AddRange(worldObject.Particles);
            }

            if (worldObject.Lines != null)
            {
                lines.AddRange(worldObject.Lines);
            }
        }

        return new SceneFrame
        {
            Camera = new Camera(CameraView),
            Ships = ships,
            Planets = planets,
            Suns = suns,
            Particles = particles,
            Lines = lines,
        };
    }
}

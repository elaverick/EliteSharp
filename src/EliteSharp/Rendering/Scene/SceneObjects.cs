using System.Numerics;

namespace EliteSharp.Rendering.Scene;

// The objects in the 3D world. Positions and orientations are in world space
// (our ship's frame of reference, in the original's units), and colours are
// inks, which look however the current palette says (see Palette). Ships,
// planets and suns have an id, which World gives them, that stays the same
// from frame to frame (0 means none), so the renderer can tell which object
// is which when it moves smoothly between frames (see FrameInterpolator).

/// <summary>
/// A ship (or other object with a blueprint), which the renderer draws as a
/// wireframe over a solid surface, at a level of detail that suits its
/// distance.
/// </summary>
/// <param name="Model">The name of the ship's model (its file name in the ship models folder, without the extension).</param>
/// <param name="Transform">The model-to-world transform (rows: sidev, roofv and nosev as unit vectors, then the position).</param>
/// <param name="Colour">The ship's colour.</param>
/// <param name="Id">The ship's id, which is the same in every frame.</param>
public readonly record struct ShipInstance(string Model, Matrix4x4 Transform, Ink Colour, int Id = 0);

/// <summary>A planet: a sphere with an outline, and either a meridian and equator or a crater.</summary>
/// <param name="Centre">The centre of the planet.</param>
/// <param name="Radius">The planet's radius.</param>
/// <param name="Nose">The planet's nosev (a unit vector).</param>
/// <param name="Roof">The planet's roofv (a unit vector).</param>
/// <param name="Side">The planet's sidev (a unit vector).</param>
/// <param name="HasCrater">True for a planet with a crater, false for one with a meridian and equator.</param>
/// <param name="ShowFeatures">Whether to draw the meridian and equator or the crater (the original leaves them out when the planet is very small or very large on-screen).</param>
/// <param name="Colour">The colour of the lines.</param>
/// <param name="Id">The planet's id, which is the same in every frame.</param>
public readonly record struct PlanetInstance(Vector3 Centre, float Radius, Vector3 Nose, Vector3 Roof, Vector3 Side, bool HasCrater, bool ShowFeatures, Ink Colour, int Id = 0);

/// <summary>The sun: a filled disc with a flickering fringe.</summary>
/// <param name="Centre">The centre of the sun.</param>
/// <param name="Radius">The sun's radius.</param>
/// <param name="FringeMask">The mask applied to the random width added to each row of the sun's fringe, in pixels (0-7).</param>
/// <param name="Seed">The random seed for the fringe, which changes each time the game redraws the sun.</param>
/// <param name="Id">The sun's id, which is the same in every frame.</param>
public readonly record struct SunInstance(Vector3 Centre, float Radius, int FringeMask, int Seed, int Id = 0);

/// <summary>
/// A particle (stardust or a fragment of an explosion), which is drawn as a small rectangle of fixed on-screen size.
/// </summary>
/// <param name="Position">The particle's position.</param>
/// <param name="Width">The width in original screen pixels (1/192 of the view's height).</param>
/// <param name="Height">The height in original screen pixels.</param>
/// <param name="Colour">The particle's colour.</param>
/// <param name="Stardust">
/// True for stardust (which fills the whole width of a view that is wider
/// than the original's: see the game's wide stardust).
/// </param>
/// <param name="Id">
/// For stardust, the particle's number, which is the same in every frame (0
/// for other particles, such as the fragments of an explosion, which are
/// scattered afresh each frame).
/// </param>
/// <param name="Entry">
/// For stardust that has just been given a new place to start from, where it
/// would have been a frame earlier had it been there all along, which the
/// renderer moves it in from (so it moves like the rest of the stardust,
/// rather than sitting still for a frame); otherwise null.
/// </param>
public readonly record struct Particle(Vector3 Position, float Width, float Height, Ink Colour, bool Stardust = false, int Id = 0, Vector3? Entry = null);

/// <summary>A straight line in space, such as a laser beam.</summary>
public readonly record struct LineSegment(Vector3 Start, Vector3 End, Ink Colour);

/// <summary>
/// A snapshot of the 3D world for one frame, handed from the game thread to
/// the renderer. Each frame's storage is reused for later frames (see
/// FrameExchange), so the lists are cleared and refilled rather than replaced.
/// </summary>
public sealed class SceneFrame
{
    public Camera Camera { get; set; }

    /// <summary>
    /// The number of times the world had been cleared when this frame was
    /// taken. Two frames with different generations show different scenes (the
    /// screen was cleared in between), so the renderer doesn't move smoothly
    /// from one to the other.
    /// </summary>
    public int Generation { get; set; }

    public List<ShipInstance> Ships { get; } = [];

    public List<PlanetInstance> Planets { get; } = [];

    public List<SunInstance> Suns { get; } = [];

    public List<Particle> Particles { get; } = [];

    public List<LineSegment> Lines { get; } = [];

    public void Clear()
    {
        Ships.Clear();
        Planets.Clear();
        Suns.Clear();
        Particles.Clear();
        Lines.Clear();
    }

    /// <summary>Make this frame a copy of another.</summary>
    public void CopyFrom(SceneFrame other)
    {
        Clear();
        Camera = other.Camera;
        Generation = other.Generation;
        Ships.AddRange(other.Ships);
        Planets.AddRange(other.Planets);
        Suns.AddRange(other.Suns);
        Particles.AddRange(other.Particles);
        Lines.AddRange(other.Lines);
    }
}

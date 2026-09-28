using System.Numerics;
using EliteSharp.Game.Ships;

namespace EliteSharp.Rendering.Scene;

// The objects in the 3D world. Positions and orientations are in world space
// (our ship's frame of reference, in the original's units), and colours are
// BBC screen bytes, which the renderer turns into colour patterns using the
// current palette (see ColourPattern).

/// <summary>
/// A ship (or other object with a blueprint) drawn as a wireframe with Elite's
/// hidden line removal, which the GPU applies using the model's face normals.
/// </summary>
/// <param name="Model">The ship's model.</param>
/// <param name="Transform">The model-to-world transform (rows: sidev, roofv and nosev as unit vectors, then the position).</param>
/// <param name="Colour">The ship's colour (a mode 1 screen byte).</param>
/// <param name="LodDistance">
/// XX4, the ship's distance reduced to 0-31: faces whose visibility distance
/// is less than this are always visible, and edges whose visibility distance
/// is less than this are hidden.
/// </param>
/// <param name="NormalOffsetScale">
/// The scale that turns a face normal into a point on the face (2^-S for the
/// blueprint's normal scale S), or 0 where the original ignores the normal
/// because the ship is far enough away (see LL9 part 5).
/// </param>
public sealed record ShipInstance(ShipModel Model, Matrix4x4 Transform, int Colour, float LodDistance, float NormalOffsetScale);

/// <summary>A planet: a sphere with an outline, and either a meridian and equator or a crater.</summary>
/// <param name="Centre">The centre of the planet.</param>
/// <param name="Radius">The planet's radius.</param>
/// <param name="Nose">The planet's nosev (a unit vector).</param>
/// <param name="Roof">The planet's roofv (a unit vector).</param>
/// <param name="Side">The planet's sidev (a unit vector).</param>
/// <param name="HasCrater">True for a planet with a crater, false for one with a meridian and equator.</param>
/// <param name="ShowFeatures">Whether to draw the meridian and equator or the crater (the original leaves them out when the planet is very small or very large on-screen).</param>
/// <param name="Colour">The colour of the lines (a mode 1 screen byte).</param>
public sealed record PlanetInstance(Vector3 Centre, float Radius, Vector3 Nose, Vector3 Roof, Vector3 Side, bool HasCrater, bool ShowFeatures, int Colour);

/// <summary>The sun: a filled disc with a flickering fringe.</summary>
/// <param name="Centre">The centre of the sun.</param>
/// <param name="Radius">The sun's radius.</param>
/// <param name="FringeMask">The mask applied to the random width added to each row of the sun's fringe, in pixels (0-7).</param>
/// <param name="Seed">The random seed for the fringe, which changes each time the game redraws the sun.</param>
public sealed record SunInstance(Vector3 Centre, float Radius, int FringeMask, int Seed);

/// <summary>
/// A particle (stardust, a fragment of an explosion, or a distant ship shown
/// as a dot), which is drawn as a small rectangle of fixed on-screen size.
/// </summary>
/// <param name="Position">The particle's position.</param>
/// <param name="Width">The width in original screen pixels (1/192 of the view's height).</param>
/// <param name="Height">The height in original screen pixels.</param>
/// <param name="Colour">The particle's colour (a mode 1 screen byte).</param>
/// <param name="Stardust">
/// True for stardust. The game simulates the stardust within the original's
/// 4:3 field of view, so the renderer spreads it horizontally to fill wider
/// views (which doesn't affect the simulation).
/// </param>
public readonly record struct Particle(Vector3 Position, float Width, float Height, int Colour, bool Stardust = false);

/// <summary>A straight line in space, such as a laser beam.</summary>
public readonly record struct LineSegment(Vector3 Start, Vector3 End, int Colour);

/// <summary>A snapshot of the 3D world for one frame, handed from the game thread to the renderer.</summary>
public sealed class SceneFrame
{
    public required Camera Camera { get; init; }

    public required IReadOnlyList<ShipInstance> Ships { get; init; }

    public required IReadOnlyList<PlanetInstance> Planets { get; init; }

    public required IReadOnlyList<SunInstance> Suns { get; init; }

    public required IReadOnlyList<Particle> Particles { get; init; }

    public required IReadOnlyList<LineSegment> Lines { get; init; }
}

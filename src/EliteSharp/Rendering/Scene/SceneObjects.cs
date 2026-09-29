using System.Numerics;
using EliteSharp.Game.Ships;

namespace EliteSharp.Rendering.Scene;

// The objects in the 3D world. Positions and orientations are in world space
// (our ship's frame of reference, in the original's units), and colours are
// inks, which look however the current palette says (see Palette).

/// <summary>
/// A ship (or other object with a blueprint), which the renderer draws as a
/// wireframe over a solid surface, at a level of detail that suits its
/// distance.
/// </summary>
/// <param name="Model">The ship's model.</param>
/// <param name="Transform">The model-to-world transform (rows: sidev, roofv and nosev as unit vectors, then the position).</param>
/// <param name="Colour">The ship's colour.</param>
public sealed record ShipInstance(ShipModel Model, Matrix4x4 Transform, Ink Colour);

/// <summary>A planet: a sphere with an outline, and either a meridian and equator or a crater.</summary>
/// <param name="Centre">The centre of the planet.</param>
/// <param name="Radius">The planet's radius.</param>
/// <param name="Nose">The planet's nosev (a unit vector).</param>
/// <param name="Roof">The planet's roofv (a unit vector).</param>
/// <param name="Side">The planet's sidev (a unit vector).</param>
/// <param name="HasCrater">True for a planet with a crater, false for one with a meridian and equator.</param>
/// <param name="ShowFeatures">Whether to draw the meridian and equator or the crater (the original leaves them out when the planet is very small or very large on-screen).</param>
/// <param name="Colour">The colour of the lines.</param>
public sealed record PlanetInstance(Vector3 Centre, float Radius, Vector3 Nose, Vector3 Roof, Vector3 Side, bool HasCrater, bool ShowFeatures, Ink Colour);

/// <summary>The sun: a filled disc with a flickering fringe.</summary>
/// <param name="Centre">The centre of the sun.</param>
/// <param name="Radius">The sun's radius.</param>
/// <param name="FringeMask">The mask applied to the random width added to each row of the sun's fringe, in pixels (0-7).</param>
/// <param name="Seed">The random seed for the fringe, which changes each time the game redraws the sun.</param>
public sealed record SunInstance(Vector3 Centre, float Radius, int FringeMask, int Seed);

/// <summary>
/// A particle (stardust or a fragment of an explosion), which is drawn as a small rectangle of fixed on-screen size.
/// </summary>
/// <param name="Position">The particle's position.</param>
/// <param name="Width">The width in original screen pixels (1/192 of the view's height).</param>
/// <param name="Height">The height in original screen pixels.</param>
/// <param name="Colour">The particle's colour.</param>
/// <param name="Stardust">
/// True for stardust. The game simulates the stardust within the original's
/// 4:3 field of view, so the renderer spreads it horizontally to fill wider
/// views (which doesn't affect the simulation).
/// </param>
public readonly record struct Particle(Vector3 Position, float Width, float Height, Ink Colour, bool Stardust = false);

/// <summary>A straight line in space, such as a laser beam.</summary>
public readonly record struct LineSegment(Vector3 Start, Vector3 End, Ink Colour);

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

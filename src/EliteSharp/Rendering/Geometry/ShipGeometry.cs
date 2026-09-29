using System.Numerics;
using EliteSharp.Game.Ships;

namespace EliteSharp.Rendering.Geometry;

/// <summary>
/// One level of detail of a ship: a closed surface, which establishes the
/// ship's depth so that the GPU can hide whatever is behind it (including the
/// ship's own far side), and the edges that are drawn as its wireframe.
/// </summary>
/// <param name="Points">The points that the surface and edges index into.</param>
/// <param name="Surface">The triangles of the surface.</param>
/// <param name="Edges">The wireframe's edges, as pairs of indices into the points.</param>
public sealed record ShipLevel(IReadOnlyList<Vector3> Points, IReadOnlyList<Triangle> Surface, IReadOnlyList<(int A, int B)> Edges);

/// <summary>
/// The 3D geometry of a ship model at each level of detail, from the full
/// model for ships that are close by, down to a simple solid for ships that
/// are far away.
///
/// Elite's ships are all convex, or very nearly (the missile's fins and a few
/// other parts stick out by a few units at most), so the surface of each
/// level is the convex hull of its points. The levels are:
///
/// 0. The full model: every edge, including the surface details (such as
///    vents and windows).
/// 1. The model's structural edges, leaving out the details that are drawn
///    on a flat part of the surface.
/// 2. A simplified solid: the hull of half of the hull's corners, chosen to
///    keep as much of the ship's volume as possible, with an edge wherever
///    its surface bends.
/// 3. A minimal solid, made the same way from six corners.
/// </summary>
public sealed class ShipGeometry
{
    /// <summary>The number of levels of detail.</summary>
    public const int LevelCount = 4;

    /// <summary>
    /// Two faces of a hull that meet at less than this angle (in degrees) are
    /// treated as one flat face, as the models' whole-number coordinates make
    /// faces that are meant to be flat slightly bent.
    /// </summary>
    private const float FlatAngle = 4;

    /// <summary>How close a point must be to a face of the hull to be on it.</summary>
    private const float OnSurface = 0.5f;

    /// <summary>The number of hull corners in the minimal solid (level 3).</summary>
    private const int MinimalCorners = 6;

    /// <summary>The fewest hull corners in the simplified solid (level 2).</summary>
    private const int SimplifiedCorners = 8;

    private ShipGeometry(ShipLevel[] levels, float radius)
    {
        Levels = levels;
        Radius = radius;
    }

    /// <summary>The levels of detail, from the full model (0) to the minimal solid.</summary>
    public IReadOnlyList<ShipLevel> Levels { get; }

    /// <summary>The distance from the model's origin to its furthest vertex.</summary>
    public float Radius { get; }

    /// <summary>Build the levels of detail for a model.</summary>
    public static ShipGeometry Build(ShipModel model)
    {
        var points = model.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToList();
        var edges = model.Edges.Select(e => (e.Vertex1, e.Vertex2)).Where(e => e.Vertex1 != e.Vertex2).ToList();
        var hull = ConvexHull.Build(points);
        float radius = points.Count == 0 ? 0 : points.Max(p => p.Length());

        var levels = new ShipLevel[LevelCount];
        levels[0] = new ShipLevel(points, hull.Triangles, edges);
        // A flat model (such as a plate of alloy) has no bends, so it keeps all its edges
        var structural = edges.Where(e => IsStructural(hull, points[e.Vertex1], points[e.Vertex2])).ToList();
        levels[1] = new ShipLevel(points, hull.Triangles, structural.Count > 0 ? structural : edges);

        var corners = hull.Corners.ToList();
        levels[2] = Simplify(points, corners, Math.Max(SimplifiedCorners, (corners.Count + 1) / 2)) ?? levels[1];
        levels[3] = Simplify(points, corners, MinimalCorners) ?? levels[2];
        return new ShipGeometry(levels, radius);
    }

    /// <summary>
    /// Whether an edge of the model is part of its structure, rather than a
    /// detail drawn on a flat part of its surface. Edges on a bend in the
    /// hull's surface (whose midpoint is on two faces of the hull that aren't
    /// in the same plane) are structural, as are edges that aren't on the
    /// hull's surface at all (which belong to the parts that aren't quite
    /// convex, such as the missile's body inside its fins).
    /// </summary>
    private static bool IsStructural(ConvexHull hull, Vector3 a, Vector3 b)
    {
        var middle = (a + b) / 2;
        var normals = new List<Vector3>();
        foreach (var triangle in hull.Triangles)
        {
            var normal = hull.Normal(triangle);
            if (MathF.Abs(Vector3.Dot(normal, middle - hull.Points[triangle.A])) <= OnSurface)
            {
                normals.Add(normal);
            }
        }

        float flat = MathF.Cos(FlatAngle * MathF.PI / 180);
        return normals.Count == 0 || normals.Any(n => normals.Any(m => Vector3.Dot(n, m) < flat));
    }

    /// <summary>
    /// A simplified solid with the given number of corners, made by removing
    /// the hull's corners one at a time, each time taking the one whose loss
    /// shrinks the hull the least. Returns null if the hull already has no
    /// more corners than that.
    /// </summary>
    private static ShipLevel? Simplify(IReadOnlyList<Vector3> points, List<int> corners, int count)
    {
        if (corners.Count <= count)
        {
            return null;
        }

        var kept = new List<int>(corners);
        while (kept.Count > count)
        {
            int best = -1;
            double bestVolume = double.NegativeInfinity;
            for (int i = 0; i < kept.Count; i++)
            {
                double volume = ConvexHull.Build(kept.Where((_, j) => j != i).Select(k => points[k]).ToList()).Volume;
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    best = i;
                }
            }

            kept.RemoveAt(best);
        }

        var keptPoints = kept.Select(k => points[k]).ToList();
        var hull = ConvexHull.Build(keptPoints);
        return new ShipLevel(keptPoints, hull.Triangles, Creases(hull));
    }

    /// <summary>The edges of a hull where its surface bends (where the two faces that meet aren't in the same plane).</summary>
    private static List<(int A, int B)> Creases(ConvexHull hull)
    {
        var faces = new Dictionary<(int, int), List<Triangle>>();
        foreach (var t in hull.Triangles)
        {
            foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            {
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!faces.TryGetValue(key, out var list))
                {
                    list = [];
                    faces[key] = list;
                }

                list.Add(t);
            }
        }

        float flat = MathF.Cos(FlatAngle * MathF.PI / 180);
        return faces
            .Where(f => f.Value.Count != 2 || Vector3.Dot(hull.Normal(f.Value[0]), hull.Normal(f.Value[1])) < flat)
            .Select(f => f.Key)
            .ToList();
    }
}

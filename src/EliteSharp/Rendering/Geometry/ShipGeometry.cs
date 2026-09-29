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

    /// <summary>
    /// How far inside the hull a point can be and still be moved out onto its
    /// surface (see <see cref="SnapToSurface"/>).
    /// </summary>
    private const float SnapDepth = 8;

    /// <summary>
    /// The number of pieces that an edge is split into when it dips inside the
    /// hull (see <see cref="FollowSurface"/>).
    /// </summary>
    private const int EdgePieces = 8;

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

        // The full model and its structural edges are drawn from the vertices
        // moved onto the hull's surface, so the surface hides only what is
        // really behind it
        var planes = hull.Triangles.Select(t => new Plane(hull.Normal(t), -Vector3.Dot(hull.Normal(t), hull.Points[t.A]))).ToList();
        var surfacePoints = points.Select(p => SnapToSurface(planes, p)).ToList();
        var levels = new ShipLevel[LevelCount];
        levels[0] = FollowSurface(planes, surfacePoints, hull.Triangles, edges);

        // A flat model (such as a plate of alloy) has no bends, so it keeps all its edges
        var structural = edges.Where(e => IsStructural(hull, points[e.Vertex1], points[e.Vertex2])).ToList();
        levels[1] = FollowSurface(planes, surfacePoints, hull.Triangles, structural.Count > 0 ? structural : edges);

        var corners = hull.Corners.ToList();
        levels[2] = Simplify(points, corners, Math.Max(SimplifiedCorners, (corners.Count + 1) / 2)) ?? levels[1];
        levels[3] = Simplify(points, corners, MinimalCorners) ?? levels[2];
        return new ShipGeometry(levels, radius);
    }

    /// <summary>
    /// A point, moved out onto the hull's surface if it is a little inside it.
    /// The surface details (vents, windows and so on) are drawn on the ships'
    /// faces, but the models' whole-number coordinates put some of them a few
    /// units inside the hull (and the parts that aren't quite convex, such as
    /// the missile's body inside its fins, are inside it too). Moving them onto
    /// the surface keeps them in front of it, so the surface only has to be
    /// pushed back a little to keep the lines on it visible, and hidden edges
    /// don't show through near the corners.
    /// </summary>
    private static Vector3 SnapToSurface(List<Plane> planes, Vector3 point)
    {
        // The nearest face is the one whose plane the point is least far behind
        var nearest = planes[0];
        float nearestDistance = float.NegativeInfinity;
        foreach (var plane in planes)
        {
            float distance = Plane.DotCoordinate(plane, point);
            if (distance > nearestDistance)
            {
                (nearest, nearestDistance) = (plane, distance);
            }
        }

        bool inside = nearestDistance < 0 && nearestDistance >= -SnapDepth;
        return inside ? point - nearest.Normal * nearestDistance : point;
    }

    /// <summary>
    /// A level of detail whose edges follow the hull's surface. Some of the
    /// models' faces aren't quite flat, so an edge between two corners can dip
    /// inside the hull in the middle, where the surface would hide it; these
    /// edges are split into pieces whose ends are moved onto the surface.
    /// </summary>
    private static ShipLevel FollowSurface(List<Plane> planes, List<Vector3> points, IReadOnlyList<Triangle> surface, List<(int A, int B)> edges)
    {
        if (planes.Count == 0)
        {
            return new ShipLevel(points, surface, edges);
        }

        var allPoints = new List<Vector3>(points);
        var allEdges = new List<(int A, int B)>();
        foreach (var (a, b) in edges)
        {
            bool dips = false;
            for (int i = 1; i < EdgePieces && !dips; i++)
            {
                var point = Vector3.Lerp(points[a], points[b], i / (float)EdgePieces);
                dips = planes.Max(p => Plane.DotCoordinate(p, point)) < -OnSurface;
            }

            if (!dips)
            {
                allEdges.Add((a, b));
                continue;
            }

            int previous = a;
            for (int i = 1; i < EdgePieces; i++)
            {
                allPoints.Add(SnapToSurface(planes, Vector3.Lerp(points[a], points[b], i / (float)EdgePieces)));
                allEdges.Add((previous, allPoints.Count - 1));
                previous = allPoints.Count - 1;
            }

            allEdges.Add((previous, b));
        }

        return new ShipLevel(allPoints, surface, allEdges);
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

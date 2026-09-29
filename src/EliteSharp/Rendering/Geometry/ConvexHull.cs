using System.Numerics;

namespace EliteSharp.Rendering.Geometry;

/// <summary>A triangle of a mesh, as indices into its points, wound anticlockwise when seen from outside.</summary>
public readonly record struct Triangle(int A, int B, int C);

/// <summary>
/// The convex hull of a set of points: the smallest convex solid that contains
/// them all, as a closed surface of triangles. Built with the incremental
/// algorithm, which is plenty fast enough for Elite's models (which have at
/// most 64 vertices). Points that lie on the surface without being corners of
/// it (within <see cref="Tolerance"/>) are not included.
/// </summary>
public sealed class ConvexHull
{
    /// <summary>How far outside the hull a point must be to change it, relative to the size of the point set.</summary>
    private const double Tolerance = 1e-9;

    private ConvexHull(IReadOnlyList<Vector3> points, List<Triangle> triangles)
    {
        Points = points;
        Triangles = triangles;
    }

    /// <summary>The points the hull was built from (the triangles index into these).</summary>
    public IReadOnlyList<Vector3> Points { get; }

    /// <summary>The hull's surface.</summary>
    public IReadOnlyList<Triangle> Triangles { get; }

    /// <summary>The indices of the points that are corners of the hull.</summary>
    public IEnumerable<int> Corners => Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Order();

    /// <summary>The volume enclosed by the hull.</summary>
    public double Volume
    {
        get
        {
            double volume = 0;
            foreach (var t in Triangles)
            {
                volume += Vector3D.Dot(Point(t.A), Vector3D.Cross(Point(t.B), Point(t.C)));
            }

            return volume / 6;
        }
    }

    /// <summary>The outward normal of a triangle (a unit vector).</summary>
    public Vector3 Normal(Triangle t) => Vector3.Normalize(Vector3.Cross(Points[t.B] - Points[t.A], Points[t.C] - Points[t.A]));

    private Vector3D Point(int index) => new(Points[index]);

    /// <summary>
    /// Build the convex hull of the given points. If they are all (nearly) in
    /// a plane, the hull is flat: the outline of the points in that plane, as
    /// triangles facing one way.
    /// </summary>
    public static ConvexHull Build(IReadOnlyList<Vector3> points)
    {
        var p = points.Select(v => new Vector3D(v)).ToArray();
        if (p.Length < 3)
        {
            return new ConvexHull(points, []);
        }

        double size = 0;
        foreach (var point in p)
        {
            size = Math.Max(size, (point - p[0]).Length);
        }

        double epsilon = Tolerance * Math.Max(size * size * size, 1);

        // Start with a tetrahedron of four points that are as spread out as possible
        int i0 = 0;
        int i1 = Farthest(p, q => (q - p[i0]).Length);
        int i2 = Farthest(p, q => Vector3D.Cross(p[i1] - p[i0], q - p[i0]).Length);
        var baseNormal = Vector3D.Cross(p[i1] - p[i0], p[i2] - p[i0]);
        int i3 = Farthest(p, q => Math.Abs(Vector3D.Dot(baseNormal, q - p[i0])));
        if (baseNormal.Length <= epsilon / Math.Max(size, 1) || Math.Abs(Vector3D.Dot(baseNormal, p[i3] - p[i0])) <= epsilon)
        {
            return new ConvexHull(points, Flat(p, baseNormal));
        }

        var inside = (p[i0] + p[i1] + p[i2] + p[i3]) * 0.25;
        var faces = new List<Triangle>();

        void AddFace(int a, int b, int c)
        {
            // Wind the face so its normal points away from the inside
            var normal = Vector3D.Cross(p[b] - p[a], p[c] - p[a]);
            faces.Add(Vector3D.Dot(normal, p[a] - inside) >= 0 ? new Triangle(a, b, c) : new Triangle(a, c, b));
        }

        AddFace(i0, i1, i2);
        AddFace(i0, i1, i3);
        AddFace(i0, i2, i3);
        AddFace(i1, i2, i3);

        double Distance(Triangle t, Vector3D q) => Vector3D.Dot(Vector3D.Cross(p[t.B] - p[t.A], p[t.C] - p[t.A]), q - p[t.A]);

        for (int i = 0; i < p.Length; i++)
        {
            if (i == i0 || i == i1 || i == i2 || i == i3)
            {
                continue;
            }

            // The faces that the point can see need replacing
            var visible = faces.Where(t => Distance(t, p[i]) > epsilon).ToList();
            if (visible.Count == 0)
            {
                continue;
            }

            // The horizon is the boundary of the visible faces: the edges whose
            // reverse is not in a visible face
            var visibleEdges = new HashSet<(int, int)>();
            foreach (var t in visible)
            {
                visibleEdges.Add((t.A, t.B));
                visibleEdges.Add((t.B, t.C));
                visibleEdges.Add((t.C, t.A));
            }

            faces.RemoveAll(visible.Contains);
            foreach (var (a, b) in visibleEdges)
            {
                if (!visibleEdges.Contains((b, a)))
                {
                    faces.Add(new Triangle(a, b, i));
                }
            }
        }

        return new ConvexHull(points, faces);
    }

    /// <summary>The index of the point with the largest value of the given measure.</summary>
    private static int Farthest(Vector3D[] points, Func<Vector3D, double> measure)
    {
        int best = 0;
        double bestValue = double.NegativeInfinity;
        for (int i = 0; i < points.Length; i++)
        {
            double value = measure(points[i]);
            if (value > bestValue)
            {
                bestValue = value;
                best = i;
            }
        }

        return best;
    }

    /// <summary>The outline of a set of points that lie in a plane, as a fan of triangles.</summary>
    private static List<Triangle> Flat(Vector3D[] points, Vector3D normal)
    {
        if (normal.Length == 0)
        {
            // The points are all in a line, so there is no surface
            return [];
        }

        // Work in 2D, in a pair of axes in the plane
        normal = normal.Normalized;
        var axisU = Vector3D.Cross(Math.Abs(normal.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0), normal).Normalized;
        var axisV = Vector3D.Cross(normal, axisU);
        var order = Enumerable.Range(0, points.Length)
            .Select(i => (Index: i, U: Vector3D.Dot(points[i], axisU), V: Vector3D.Dot(points[i], axisV)))
            .OrderBy(q => q.U).ThenBy(q => q.V)
            .ToList();

        // Andrew's monotone chain
        double Turn((int Index, double U, double V) o, (int Index, double U, double V) a, (int Index, double U, double V) b) =>
            (a.U - o.U) * (b.V - o.V) - (a.V - o.V) * (b.U - o.U);

        var outline = new List<(int Index, double U, double V)>();
        foreach (var pass in new[] { order, Enumerable.Reverse(order).ToList() })
        {
            int start = outline.Count;
            foreach (var q in pass)
            {
                while (outline.Count >= start + 2 && Turn(outline[^2], outline[^1], q) <= 0)
                {
                    outline.RemoveAt(outline.Count - 1);
                }

                outline.Add(q);
            }

            outline.RemoveAt(outline.Count - 1);
        }

        var triangles = new List<Triangle>();
        for (int i = 1; i + 1 < outline.Count; i++)
        {
            triangles.Add(new Triangle(outline[0].Index, outline[i].Index, outline[i + 1].Index));
        }

        return triangles;
    }

    /// <summary>A vector in double precision, so the hull's tests are exact for Elite's whole-number coordinates.</summary>
    private readonly record struct Vector3D(double X, double Y, double Z)
    {
        public Vector3D(Vector3 v)
            : this(v.X, v.Y, v.Z)
        {
        }

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vector3D Normalized => this * (1 / Length);

        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3D Cross(Vector3D a, Vector3D b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}

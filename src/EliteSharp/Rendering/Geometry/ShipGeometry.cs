using System.Numerics;

namespace EliteSharp.Rendering.Geometry;

/// <summary>
/// One level of detail of a ship: a closed surface, which establishes the
/// ship's depth so that the GPU can hide whatever is behind it (including the
/// ship's own far side), and the edges that are drawn as its wireframe.
/// </summary>
/// <param name="Points">The points that the surface and edges index into.</param>
/// <param name="Surface">The triangles of the surface, wound anticlockwise when seen from outside.</param>
/// <param name="Edges">The wireframe's edges, as pairs of indices into the points.</param>
public sealed record ShipLevel(IReadOnlyList<Vector3> Points, IReadOnlyList<Triangle> Surface, IReadOnlyList<(int A, int B)> Edges);

/// <summary>What a ship's line is, which decides how long it survives as the ship gets smaller.</summary>
public enum ShipEdgeKind
{
    /// <summary>A detail drawn on a face, such as a vent or a window.</summary>
    Detail,

    /// <summary>A line where two faces of the surface meet.</summary>
    Crease,

    /// <summary>A part that sticks out of the surface, such as a gun barrel, a fin or the Krait's prongs.</summary>
    Protrusion,
}

/// <summary>A line of a ship's wireframe, with what kind of line it is.</summary>
/// <param name="A">The index of one end.</param>
/// <param name="B">The index of the other end.</param>
/// <param name="Kind">What the line is.</param>
/// <param name="CreaseAngle">For a crease, the angle between the two faces' normals, in degrees (0 where the faces are in the same plane).</param>
public readonly record struct ShipEdge(int A, int B, ShipEdgeKind Kind, float CreaseAngle);

/// <summary>
/// The 3D geometry of a ship model at each level of detail, all built from the
/// model's real surface and lines (see <see cref="ShipMeshAsset"/>). Every level
/// keeps the whole surface, which is only a few dozen triangles and is only
/// drawn into the depth buffer, so the lines of every level lie on (or stick
/// out of) the same surface that hides them. The levels differ in which lines
/// they draw, dropping the least visible first:
///
/// 0. Every line: the structure and the details on the faces (such as vents
///    and windows).
/// 1. The structure: the creases where faces meet, and the parts that stick out.
/// 2. The structure without the gentle creases between faces that are almost
///    in the same plane (panel lines).
/// 3. Only the sharp creases, which make the ship's outline and main shape,
///    and the parts that stick out, which make ships such as the Krait and the
///    missile recognisable.
///
/// A level that would lose every line keeps the lines of the level before it.
/// </summary>
public sealed class ShipGeometry
{
    /// <summary>The number of levels of detail.</summary>
    public const int LevelCount = 4;

    /// <summary>The smallest crease angle (in degrees) that level 2 keeps.</summary>
    public const float GentleCreaseAngle = 20;

    /// <summary>The smallest crease angle (in degrees) that level 3 keeps.</summary>
    public const float SharpCreaseAngle = 45;

    private ShipGeometry(ShipLevel[] levels, IReadOnlyList<ShipEdge> edges, float radius)
    {
        Levels = levels;
        Edges = edges;
        Radius = radius;
    }

    /// <summary>The levels of detail, from the full model (0) to the simplest.</summary>
    public IReadOnlyList<ShipLevel> Levels { get; }

    /// <summary>All the model's lines, with what each is.</summary>
    public IReadOnlyList<ShipEdge> Edges { get; }

    /// <summary>The distance from the model's origin to its furthest point.</summary>
    public float Radius { get; }

    /// <summary>Build the levels of detail for a model.</summary>
    public static ShipGeometry Build(ShipMeshAsset asset)
    {
        var points = asset.Points;
        float radius = points.Count == 0 ? 0 : points.Max(p => p.Length());
        var edges = ClassifyEdges(asset);

        bool InLevel(ShipEdge edge, int level) => level switch
        {
            0 => true,
            1 => edge.Kind != ShipEdgeKind.Detail,
            2 => edge.Kind == ShipEdgeKind.Protrusion || (edge.Kind == ShipEdgeKind.Crease && edge.CreaseAngle >= GentleCreaseAngle),
            _ => edge.Kind == ShipEdgeKind.Protrusion || (edge.Kind == ShipEdgeKind.Crease && edge.CreaseAngle >= SharpCreaseAngle),
        };

        var levels = new ShipLevel[LevelCount];
        for (int level = 0; level < LevelCount; level++)
        {
            var levelEdges = edges.Where(e => InLevel(e, level)).Select(e => (e.A, e.B)).ToList();
            if (levelEdges.Count == 0 && level > 0)
            {
                levelEdges = [.. levels[level - 1].Edges];
            }

            levels[level] = new ShipLevel(points, asset.Surface, levelEdges);
        }

        return new ShipGeometry(levels, edges, radius);
    }

    /// <summary>
    /// Work out what each of the model's lines is: the details are as the
    /// model says; a structure line is a crease if it is an edge of the
    /// surface (with the angle between the faces on either side), and a
    /// protrusion if not.
    /// </summary>
    private static List<ShipEdge> ClassifyEdges(ShipMeshAsset asset)
    {
        var points = asset.Points;
        var normals = asset.Surface.Select(t =>
            Vector3.Normalize(Vector3.Cross(points[t.B] - points[t.A], points[t.C] - points[t.A]))).ToList();

        var adjacent = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < asset.Surface.Count; i++)
        {
            var t = asset.Surface[i];
            foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            {
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!adjacent.TryGetValue(key, out var list))
                {
                    list = [];
                    adjacent[key] = list;
                }

                list.Add(i);
            }
        }

        var edges = new List<ShipEdge>();
        foreach (var (a, b) in asset.Structure)
        {
            if (adjacent.TryGetValue((Math.Min(a, b), Math.Max(a, b)), out var faces) && faces.Count == 2)
            {
                float cosine = Math.Clamp(Vector3.Dot(normals[faces[0]], normals[faces[1]]), -1, 1);
                edges.Add(new ShipEdge(a, b, ShipEdgeKind.Crease, MathF.Acos(cosine) * 180 / MathF.PI));
            }
            else if (faces != null)
            {
                // An edge of the surface that isn't between exactly two
                // triangles (which none of the models have) counts as sharp
                edges.Add(new ShipEdge(a, b, ShipEdgeKind.Crease, 180));
            }
            else
            {
                edges.Add(new ShipEdge(a, b, ShipEdgeKind.Protrusion, 0));
            }
        }

        edges.AddRange(asset.Details.Select(d => new ShipEdge(d.A, d.B, ShipEdgeKind.Detail, 0)));
        return edges;
    }
}

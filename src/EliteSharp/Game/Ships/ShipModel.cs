using System.Text.Json;

namespace EliteSharp.Game.Ships;

/// <summary>
/// A ship's wireframe model, loaded from a glTF 2.0 file (see
/// tools/export_ships.py for the layout). As well as the vertices and edges,
/// the model holds the data the original uses for hidden line removal: the
/// faces that each vertex and edge belong to, the face normals, and the
/// distances beyond which vertices, edges and faces are no longer drawn. The
/// game's port of LL9 still uses these (the explosions, the ships' lasers and
/// the hangar depend on its results), but the renderer doesn't: it draws the
/// ships' actual geometry and lets the depth buffer hide what's out of sight
/// (see Rendering/Geometry/ShipGeometry.cs).
///
/// The coordinates are in the original's left-handed space (x right, y up,
/// z forward); the glTF node mirrors x for other viewers, but the mesh data is
/// used as it is.
/// </summary>
public sealed class ShipModel
{
    /// <summary>The largest coordinate or normal component that fits in a blueprint (a sign-magnitude byte).</summary>
    private const int MaxMagnitude = 255;

    /// <summary>Faces are referenced by 4-bit numbers.</summary>
    private const int MaxFaces = 16;

    /// <summary>
    /// The most vertices a model can have (the size of the XX3 heap of projected
    /// vertices).
    /// </summary>
    private const int MaxVertices = 64;

    /// <summary>
    /// The explosion count can exceed the number of vertices (the rock hermit's
    /// does, in the original), in which case the extra explosion origins come
    /// from whatever was last left in the XX3 heap, as in the original.
    /// </summary>
    private const int MaxExplosionVertices = MaxVertices;

    /// <summary>Visibility distances are 5-bit numbers.</summary>
    private const int MaxVisibility = 31;

    private ShipModel(
        string name,
        ShipVertex[] vertices,
        ShipEdge[] edges,
        ShipFace[] faces,
        int normalScale,
        int dotDistance,
        int maxVisibleEdges,
        int gunVertex,
        int explosionVertices)
    {
        Name = name;
        Vertices = vertices;
        Edges = edges;
        Faces = faces;
        NormalScale = normalScale;
        DotDistance = dotDistance;
        MaxVisibleEdges = maxVisibleEdges;
        GunVertex = gunVertex;
        ExplosionVertices = explosionVertices;
    }

    public string Name { get; }

    public IReadOnlyList<ShipVertex> Vertices { get; }

    public IReadOnlyList<ShipEdge> Edges { get; }

    public IReadOnlyList<ShipFace> Faces { get; }

    /// <summary>The face normals are scaled by 2^NormalScale.</summary>
    public int NormalScale { get; }

    /// <summary>The distance (z_hi) beyond which the original draws the ship as a dot.</summary>
    public int DotDistance { get; }

    /// <summary>The most edges that can be visible at once (which sizes the ship line heap).</summary>
    public int MaxVisibleEdges { get; }

    /// <summary>The vertex from which the ship fires its lasers.</summary>
    public int GunVertex { get; }

    /// <summary>
    /// The number of vertices used as the origins of the explosion cloud (which
    /// can be more than the model has; see <see cref="MaxExplosionVertices"/>).
    /// </summary>
    public int ExplosionVertices { get; }

    /// <summary>Load a model from a glTF file.</summary>
    public static ShipModel Load(string path)
    {
        try
        {
            return Read(GltfReader.Load(path));
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException or FormatException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    private static ShipModel Read(GltfReader gltf)
    {
        var mesh = gltf.Array("meshes").FirstOrDefault();
        if (mesh.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The model has no mesh");
        }

        string name = mesh.TryGetProperty("name", out var meshName) ? meshName.GetString() ?? "" : "";
        var primitive = mesh.GetProperty("primitives")[0];
        if (!primitive.TryGetProperty("mode", out var mode) || mode.GetInt32() != 1)
        {
            throw new InvalidDataException("The mesh must be made of lines (primitive mode 1)");
        }

        var attributes = primitive.GetProperty("attributes");
        double[][] positions = gltf.ReadAccessor(attributes.GetProperty("POSITION").GetInt32());
        double[][] vertexFaces = gltf.ReadAccessor(attributes.GetProperty("_FACES").GetInt32());
        double[][] vertexVisibility = gltf.ReadAccessor(attributes.GetProperty("_VISIBILITY").GetInt32());
        double[][] indices = gltf.ReadAccessor(primitive.GetProperty("indices").GetInt32());
        var elite = mesh.GetProperty("extras").GetProperty("elite");

        // The faces
        var faces = new List<ShipFace>();
        foreach (var face in elite.GetProperty("faces").EnumerateArray())
        {
            var normal = face.GetProperty("normal");
            faces.Add(new ShipFace(
                Whole(normal[0].GetDouble(), MaxMagnitude, "Face normal"),
                Whole(normal[1].GetDouble(), MaxMagnitude, "Face normal"),
                Whole(normal[2].GetDouble(), MaxMagnitude, "Face normal"),
                Range(face.GetProperty("visibility").GetInt32(), 0, MaxVisibility, "Face visibility")));
        }

        if (faces.Count > MaxFaces)
        {
            throw new InvalidDataException($"The model has {faces.Count} faces, but at most {MaxFaces} are allowed");
        }

        // The vertices
        if (vertexFaces.Length != positions.Length || vertexVisibility.Length != positions.Length)
        {
            throw new InvalidDataException("The vertex attributes have different lengths");
        }

        if (positions.Length > MaxVertices)
        {
            throw new InvalidDataException($"The model has {positions.Length} vertices, but at most {MaxVertices} are allowed");
        }

        var vertices = new ShipVertex[positions.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var p = positions[i];
            var f = vertexFaces[i];
            vertices[i] = new ShipVertex(
                Whole(p[0], MaxMagnitude, "Vertex coordinate"),
                Whole(p[1], MaxMagnitude, "Vertex coordinate"),
                Whole(p[2], MaxMagnitude, "Vertex coordinate"),
                FaceNumber(f[0]),
                FaceNumber(f[1]),
                FaceNumber(f[2]),
                FaceNumber(f[3]),
                Range(Whole(vertexVisibility[i][0], MaxVisibility, "Vertex visibility"), 0, MaxVisibility, "Vertex visibility"));
        }

        // The edges, one per line in the mesh
        var edgeData = elite.GetProperty("edges");
        if (indices.Length != 2 * edgeData.GetArrayLength())
        {
            throw new InvalidDataException("The number of edges doesn't match the number of lines in the mesh");
        }

        var edges = new ShipEdge[edgeData.GetArrayLength()];
        for (int i = 0; i < edges.Length; i++)
        {
            var edge = edgeData[i];
            var edgeFaces = edge.GetProperty("faces");
            edges[i] = new ShipEdge(
                Range((int)indices[2 * i][0], 0, vertices.Length - 1, "Edge vertex"),
                Range((int)indices[2 * i + 1][0], 0, vertices.Length - 1, "Edge vertex"),
                FaceNumber(edgeFaces[0].GetInt32()),
                FaceNumber(edgeFaces[1].GetInt32()),
                Range(edge.GetProperty("visibility").GetInt32(), 0, MaxVisibility, "Edge visibility"));
        }

        return new ShipModel(
            name,
            vertices,
            edges,
            [.. faces],
            Range(elite.GetProperty("normalScale").GetInt32(), 0, 7, "normalScale"),
            Range(elite.GetProperty("dotDistance").GetInt32(), 0, 255, "dotDistance"),
            Range(elite.GetProperty("maxVisibleEdges").GetInt32(), 0, 63, "maxVisibleEdges"),
            Range(elite.GetProperty("gunVertex").GetInt32(), 0, Math.Max(0, vertices.Length - 1), "gunVertex"),
            Range(elite.GetProperty("explosionVertices").GetInt32(), 0, MaxExplosionVertices, "explosionVertices"));
    }

    private static int FaceNumber(double value) => Range(Whole(value, MaxFaces - 1, "Face number"), 0, MaxFaces - 1, "Face number");

    /// <summary>Check that a value is a whole number no larger than the limit in magnitude.</summary>
    private static int Whole(double value, int limit, string what)
    {
        int whole = (int)Math.Round(value);
        if (Math.Abs(value - whole) > 1e-6 || Math.Abs(whole) > limit)
        {
            throw new InvalidDataException($"{what} {value} must be a whole number between -{limit} and {limit}");
        }

        return whole;
    }

    private static int Range(int value, int min, int max, string what) =>
        value >= min && value <= max ? value : throw new InvalidDataException($"{what} {value} must be between {min} and {max}");
}

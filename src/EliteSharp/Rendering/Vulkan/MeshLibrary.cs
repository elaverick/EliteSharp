using System.Numerics;
using System.Runtime.InteropServices;
using EliteSharp.Game.Ships;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// A vertex of the world's geometry. Wireframe models are line lists, with
/// two vertices per edge, and each vertex carries the normals of the faces on
/// either side of its edge so the vertex shader can do Elite's hidden line
/// removal. Solid geometry uses the same format, with no faces.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex(Vector3 position, Vector4 faceA, Vector4 faceB, float edgeVisibility)
{
    /// <summary>The position in model space.</summary>
    public Vector3 Position = position;

    /// <summary>The normal of the face on one side of the edge (xyz), and its visibility distance (w), or w = -1 if there is no face.</summary>
    public Vector4 FaceA = faceA;

    /// <summary>The normal of the face on the other side of the edge, as for <see cref="FaceA"/>.</summary>
    public Vector4 FaceB = faceB;

    /// <summary>The visibility distance of the edge.</summary>
    public float EdgeVisibility = edgeVisibility;

    /// <summary>A face that is always visible (for geometry with no faces).</summary>
    public static readonly Vector4 NoFace = new(0, 0, 0, -1);

    /// <summary>A vertex of geometry that has no faces.</summary>
    public static MeshVertex Plain(Vector3 position) => new(position, NoFace, NoFace, 31);

    public static readonly uint SizeInBytes = (uint)Marshal.SizeOf<MeshVertex>();

    public static VertexInputAttributeDescription[] Attributes(uint binding) =>
    [
        new(0, binding, Format.R32G32B32Sfloat, 0),
        new(1, binding, Format.R32G32B32A32Sfloat, 12),
        new(2, binding, Format.R32G32B32A32Sfloat, 28),
        new(3, binding, Format.R32Sfloat, 44),
    ];
}

/// <summary>A range of vertices in the mesh library's vertex buffer.</summary>
public readonly record struct Mesh(uint FirstVertex, uint VertexCount);

/// <summary>
/// The geometry of the 3D world, uploaded to the GPU once at startup: a
/// wireframe mesh for each ship model, plus the unit shapes used for the
/// planet and sun (a circle, a sphere and a disc), all in one vertex buffer.
/// </summary>
public sealed class MeshLibrary : IDisposable
{
    /// <summary>The number of segments in the circles and the disc.</summary>
    private const int CircleSegments = 64;

    private readonly Dictionary<ShipModel, Mesh> _ships = [];
    private readonly GpuBuffer _vertexBuffer;

    public MeshLibrary(GpuDevice gpu)
    {
        var vertices = new List<MeshVertex>();

        foreach (var model in ShipCatalogue.All.Select(b => b.Model).Distinct())
        {
            _ships[model] = Add(vertices, BuildWireframe(model));
        }

        UnitCircle = Add(vertices, BuildCircle());
        UnitSphere = Add(vertices, BuildSphere(48, 24));
        UnitDisc = Add(vertices, BuildDisc());

        _vertexBuffer = gpu.CreateDeviceBuffer<MeshVertex>(CollectionsMarshal.AsSpan(vertices), BufferUsageFlags.VertexBufferBit);
    }

    public Silk.NET.Vulkan.Buffer VertexBuffer => _vertexBuffer.Buffer;

    /// <summary>A circle of radius 1 in the xy plane, as a line list.</summary>
    public Mesh UnitCircle { get; }

    /// <summary>A sphere of radius 1, as a triangle list.</summary>
    public Mesh UnitSphere { get; }

    /// <summary>A disc of radius 1 in the xy plane, as a triangle list.</summary>
    public Mesh UnitDisc { get; }

    /// <summary>The wireframe mesh for a ship model.</summary>
    public Mesh Ship(ShipModel model) => _ships[model];

    private static Mesh Add(List<MeshVertex> vertices, List<MeshVertex> mesh)
    {
        var range = new Mesh((uint)vertices.Count, (uint)mesh.Count);
        vertices.AddRange(mesh);
        return range;
    }

    /// <summary>Build the line list for a ship's wireframe, with the face data for each edge.</summary>
    private static List<MeshVertex> BuildWireframe(ShipModel model)
    {
        Vector4 Face(int face)
        {
            // Edges can refer to faces that the blueprint doesn't have (face
            // 15 is used for this), and the original treats those as visible
            if (face >= model.Faces.Count)
            {
                return MeshVertex.NoFace;
            }

            var f = model.Faces[face];
            return new Vector4(f.NormalX, f.NormalY, f.NormalZ, f.Visibility);
        }

        var vertices = new List<MeshVertex>();
        foreach (var edge in model.Edges)
        {
            var faceA = Face(edge.Face1);
            var faceB = Face(edge.Face2);
            foreach (int index in (ReadOnlySpan<int>)[edge.Vertex1, edge.Vertex2])
            {
                var vertex = model.Vertices[index];
                vertices.Add(new MeshVertex(new Vector3(vertex.X, vertex.Y, vertex.Z), faceA, faceB, edge.Visibility));
            }
        }

        return vertices;
    }

    private static Vector3 OnCircle(int segment) =>
        new(MathF.Cos(segment * MathF.Tau / CircleSegments), MathF.Sin(segment * MathF.Tau / CircleSegments), 0);

    private static List<MeshVertex> BuildCircle()
    {
        var vertices = new List<MeshVertex>();
        for (int i = 0; i < CircleSegments; i++)
        {
            vertices.Add(MeshVertex.Plain(OnCircle(i)));
            vertices.Add(MeshVertex.Plain(OnCircle(i + 1)));
        }

        return vertices;
    }

    private static List<MeshVertex> BuildDisc()
    {
        var vertices = new List<MeshVertex>();
        for (int i = 0; i < CircleSegments; i++)
        {
            vertices.Add(MeshVertex.Plain(Vector3.Zero));
            vertices.Add(MeshVertex.Plain(OnCircle(i)));
            vertices.Add(MeshVertex.Plain(OnCircle(i + 1)));
        }

        return vertices;
    }

    private static List<MeshVertex> BuildSphere(int slices, int stacks)
    {
        Vector3 Point(int slice, int stack)
        {
            float latitude = MathF.PI * stack / stacks - MathF.PI / 2;
            float longitude = MathF.Tau * slice / slices;
            return new Vector3(MathF.Cos(latitude) * MathF.Cos(longitude), MathF.Sin(latitude), MathF.Cos(latitude) * MathF.Sin(longitude));
        }

        var vertices = new List<MeshVertex>();
        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                var a = Point(slice, stack);
                var b = Point(slice + 1, stack);
                var c = Point(slice, stack + 1);
                var d = Point(slice + 1, stack + 1);
                vertices.Add(MeshVertex.Plain(a));
                vertices.Add(MeshVertex.Plain(b));
                vertices.Add(MeshVertex.Plain(c));
                vertices.Add(MeshVertex.Plain(b));
                vertices.Add(MeshVertex.Plain(d));
                vertices.Add(MeshVertex.Plain(c));
            }
        }

        return vertices;
    }

    public void Dispose() => _vertexBuffer.Dispose();
}

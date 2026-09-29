using System.Numerics;
using System.Runtime.InteropServices;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering.Geometry;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>
/// A vertex of the world's geometry: a position in model space. Wireframes
/// are line lists, with two vertices per edge, and surfaces are triangle
/// lists.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex(Vector3 position)
{
    /// <summary>The position in model space.</summary>
    public Vector3 Position = position;

    public static readonly uint SizeInBytes = (uint)Marshal.SizeOf<MeshVertex>();

    public static VertexInputAttributeDescription[] Attributes(uint binding) =>
    [
        new(0, binding, Format.R32G32B32Sfloat, 0),
    ];
}

/// <summary>A range of vertices in the mesh library's vertex buffer.</summary>
public readonly record struct Mesh(uint FirstVertex, uint VertexCount);

/// <summary>
/// The meshes for a ship model: for each level of detail (see
/// <see cref="ShipGeometry"/>), its surface and its wireframe.
/// </summary>
/// <param name="Surfaces">The surface at each level of detail (a triangle list).</param>
/// <param name="Wireframes">The wireframe at each level of detail (a line list).</param>
/// <param name="Radius">The distance from the model's origin to its furthest vertex.</param>
public sealed record ShipMeshes(Mesh[] Surfaces, Mesh[] Wireframes, float Radius);

/// <summary>
/// The geometry of the 3D world, uploaded to the GPU once at startup: the
/// surfaces and wireframes of each ship model at each level of detail, plus the unit shapes used for the
/// planet and sun (a circle, a sphere and a disc), all in one vertex buffer.
/// </summary>
public sealed class MeshLibrary : IDisposable
{
    /// <summary>The number of segments in the circles and the disc.</summary>
    private const int CircleSegments = 64;

    private readonly Dictionary<ShipModel, ShipMeshes> _ships = [];
    private readonly GpuBuffer _vertexBuffer;

    public MeshLibrary(GpuDevice gpu)
    {
        var vertices = new List<MeshVertex>();

        foreach (var model in ShipCatalogue.All.Select(b => b.Model).Distinct())
        {
            var geometry = ShipGeometry.Build(model);
            _ships[model] = new ShipMeshes(
                [.. geometry.Levels.Select(level => Add(vertices, BuildSurface(level)))],
                [.. geometry.Levels.Select(level => Add(vertices, BuildWireframe(level)))],
                geometry.Radius);
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

    /// <summary>The meshes for a ship model.</summary>
    public ShipMeshes Ship(ShipModel model) => _ships[model];

    private static Mesh Add(List<MeshVertex> vertices, List<MeshVertex> mesh)
    {
        var range = new Mesh((uint)vertices.Count, (uint)mesh.Count);
        vertices.AddRange(mesh);
        return range;
    }

    /// <summary>Build the triangle list for a level of a ship's surface.</summary>
    private static List<MeshVertex> BuildSurface(ShipLevel level)
    {
        var vertices = new List<MeshVertex>();
        foreach (var triangle in level.Surface)
        {
            vertices.Add(new MeshVertex(level.Points[triangle.A]));
            vertices.Add(new MeshVertex(level.Points[triangle.B]));
            vertices.Add(new MeshVertex(level.Points[triangle.C]));
        }

        return vertices;
    }

    /// <summary>Build the line list for a level of a ship's wireframe.</summary>
    private static List<MeshVertex> BuildWireframe(ShipLevel level)
    {
        var vertices = new List<MeshVertex>();
        foreach (var (a, b) in level.Edges)
        {
            vertices.Add(new MeshVertex(level.Points[a]));
            vertices.Add(new MeshVertex(level.Points[b]));
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
            vertices.Add(new MeshVertex(OnCircle(i)));
            vertices.Add(new MeshVertex(OnCircle(i + 1)));
        }

        return vertices;
    }

    private static List<MeshVertex> BuildDisc()
    {
        var vertices = new List<MeshVertex>();
        for (int i = 0; i < CircleSegments; i++)
        {
            vertices.Add(new MeshVertex(Vector3.Zero));
            vertices.Add(new MeshVertex(OnCircle(i)));
            vertices.Add(new MeshVertex(OnCircle(i + 1)));
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
                vertices.Add(new MeshVertex(a));
                vertices.Add(new MeshVertex(b));
                vertices.Add(new MeshVertex(c));
                vertices.Add(new MeshVertex(b));
                vertices.Add(new MeshVertex(d));
                vertices.Add(new MeshVertex(c));
            }
        }

        return vertices;
    }

    public void Dispose() => _vertexBuffer.Dispose();
}

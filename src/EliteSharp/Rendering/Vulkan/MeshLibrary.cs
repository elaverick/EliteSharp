using System.Numerics;
using System.Runtime.InteropServices;
using EliteSharp.Rendering.Geometry;
using Silk.NET.Vulkan;

namespace EliteSharp.Rendering.Vulkan;

/// <summary>A vertex of the world's geometry: a position in model space.</summary>
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

/// <summary>
/// A range of the mesh library's index buffer (a triangle list or a line
/// list), whose indices are relative to the given vertex in the vertex buffer.
/// </summary>
public readonly record struct Mesh(uint FirstIndex, uint IndexCount, int VertexOffset);

/// <summary>
/// The meshes for a ship model: for each level of detail (see
/// <see cref="ShipGeometry"/>), its surface and its wireframe.
/// </summary>
/// <param name="Surfaces">The surface at each level of detail (a triangle list).</param>
/// <param name="Wireframes">The wireframe at each level of detail (a line list).</param>
/// <param name="Radius">The distance from the model's origin to its furthest vertex.</param>
public sealed record ShipMeshes(Mesh[] Surfaces, Mesh[] Wireframes, float Radius);

/// <summary>
/// The geometry of the 3D world, uploaded to the GPU once at startup, as one
/// vertex buffer and one index buffer: the surfaces and wireframes of each
/// ship model at each level of detail (the ship models are all the models in
/// the ship models folder, known by their file names), plus the unit shapes
/// used for the planet and sun (a circle, a sphere and a disc).
/// </summary>
public sealed class MeshLibrary : IDisposable
{
    /// <summary>The number of segments in the circles and the disc.</summary>
    private const int CircleSegments = 64;

    private readonly Dictionary<string, ShipMeshes> _ships = [];
    private readonly List<MeshVertex> _vertices = [];
    private readonly List<uint> _indices = [];
    private readonly GpuBuffer _vertexBuffer;
    private readonly GpuBuffer _indexBuffer;

    public MeshLibrary(GpuDevice gpu, string shipModelFolder)
    {
        foreach (string path in Directory.EnumerateFiles(shipModelFolder, "*.gltf").Order())
        {
            var geometry = ShipGeometry.Build(ShipMeshAsset.Load(path));

            // Levels that share their points (the full model and its structure)
            // share their vertices
            var vertexOffsets = new Dictionary<IReadOnlyList<Vector3>, int>(ReferenceEqualityComparer.Instance);
            int VertexOffset(IReadOnlyList<Vector3> points)
            {
                if (!vertexOffsets.TryGetValue(points, out int offset))
                {
                    offset = AddVertices(points);
                    vertexOffsets[points] = offset;
                }

                return offset;
            }

            var surfaces = new Mesh[ShipGeometry.LevelCount];
            var wireframes = new Mesh[ShipGeometry.LevelCount];
            for (int level = 0; level < ShipGeometry.LevelCount; level++)
            {
                var geometryLevel = geometry.Levels[level];
                int offset = VertexOffset(geometryLevel.Points);
                surfaces[level] = AddIndices(offset, geometryLevel.Surface.SelectMany(t => new[] { t.A, t.B, t.C }));
                wireframes[level] = AddIndices(offset, geometryLevel.Edges.SelectMany(e => new[] { e.A, e.B }));
            }

            _ships[Path.GetFileNameWithoutExtension(path)] = new ShipMeshes(surfaces, wireframes, geometry.Radius);
        }

        UnitCircle = AddCircle();
        UnitSphere = AddSphere(48, 24);
        UnitDisc = AddDisc();

        _vertexBuffer = gpu.CreateDeviceBuffer<MeshVertex>(CollectionsMarshal.AsSpan(_vertices), BufferUsageFlags.VertexBufferBit);
        _indexBuffer = gpu.CreateDeviceBuffer<uint>(CollectionsMarshal.AsSpan(_indices), BufferUsageFlags.IndexBufferBit);
    }

    public Silk.NET.Vulkan.Buffer VertexBuffer => _vertexBuffer.Buffer;

    public Silk.NET.Vulkan.Buffer IndexBuffer => _indexBuffer.Buffer;

    /// <summary>A circle of radius 1 in the xy plane, as a line list.</summary>
    public Mesh UnitCircle { get; }

    /// <summary>A sphere of radius 1, as a triangle list wound anticlockwise when seen from outside.</summary>
    public Mesh UnitSphere { get; }

    /// <summary>A disc of radius 1 in the xy plane, as a triangle list.</summary>
    public Mesh UnitDisc { get; }

    /// <summary>The meshes for the ship model with the given name.</summary>
    public ShipMeshes Ship(string model) => _ships[model];

    private int AddVertices(IEnumerable<Vector3> points)
    {
        int offset = _vertices.Count;
        _vertices.AddRange(points.Select(p => new MeshVertex(p)));
        return offset;
    }

    private Mesh AddIndices(int vertexOffset, IEnumerable<int> indices)
    {
        uint first = (uint)_indices.Count;
        _indices.AddRange(indices.Select(i => (uint)i));
        return new Mesh(first, (uint)_indices.Count - first, vertexOffset);
    }

    private static Vector3 OnCircle(int segment) =>
        new(MathF.Cos(segment * MathF.Tau / CircleSegments), MathF.Sin(segment * MathF.Tau / CircleSegments), 0);

    private Mesh AddCircle()
    {
        int offset = AddVertices(Enumerable.Range(0, CircleSegments).Select(OnCircle));
        return AddIndices(offset, Enumerable.Range(0, CircleSegments).SelectMany(i => new[] { i, (i + 1) % CircleSegments }));
    }

    private Mesh AddDisc()
    {
        // The centre, then the rim
        int offset = AddVertices([Vector3.Zero, .. Enumerable.Range(0, CircleSegments).Select(OnCircle)]);
        return AddIndices(offset, Enumerable.Range(0, CircleSegments).SelectMany(i => new[] { 0, 1 + i, 1 + (i + 1) % CircleSegments }));
    }

    private Mesh AddSphere(int slices, int stacks)
    {
        // A grid of points from pole to pole (the points at the poles are
        // repeated, which keeps the indexing simple)
        var points = new List<Vector3>();
        for (int stack = 0; stack <= stacks; stack++)
        {
            float latitude = MathF.PI * stack / stacks - MathF.PI / 2;
            for (int slice = 0; slice <= slices; slice++)
            {
                float longitude = MathF.Tau * slice / slices;
                points.Add(new Vector3(MathF.Cos(latitude) * MathF.Cos(longitude), MathF.Sin(latitude), MathF.Cos(latitude) * MathF.Sin(longitude)));
            }
        }

        int Index(int slice, int stack) => stack * (slices + 1) + slice;
        var indices = new List<int>();
        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                int a = Index(slice, stack), b = Index(slice + 1, stack), c = Index(slice, stack + 1), d = Index(slice + 1, stack + 1);
                indices.AddRange([a, c, b, b, c, d]);
            }
        }

        return AddIndices(AddVertices(points), indices);
    }

    public void Dispose()
    {
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
    }
}

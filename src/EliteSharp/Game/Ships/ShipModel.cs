using System.Numerics;
using System.Text.Json;
using EliteSharp.Formats;

namespace EliteSharp.Game.Ships;

/// <summary>
/// What the game needs from a ship's model (a glTF 2.0 file, see
/// tools/export_ships.py for the layout): the positions of its vertices (for
/// its explosion cloud and its laser beam), and the original's data about the
/// model that the game uses. The model's surface and lines are for the
/// renderer, which loads them itself; the game refers to the model by name.
///
/// The coordinates are in the original's left-handed space (x right, y up,
/// z forward); the glTF node mirrors x for other viewers, but the mesh data is
/// used as it is.
/// </summary>
public sealed class ShipModel
{
    /// <summary>
    /// The most vertices a model can have (the size of the original's heap of
    /// projected vertices, XX3).
    /// </summary>
    private const int MaxVertices = 64;

    private ShipModel(string name, Vector3[] vertices, int gunVertex, int explosionVertices, int dotDistance)
    {
        Name = name;
        Vertices = vertices;
        GunVertex = gunVertex;
        ExplosionVertices = explosionVertices;
        DotDistance = dotDistance;
    }

    /// <summary>The model's name (its file name without the extension), which the renderer knows it by.</summary>
    public string Name { get; }

    /// <summary>The positions of the model's vertices.</summary>
    public IReadOnlyList<Vector3> Vertices { get; }

    /// <summary>The vertex from which the ship fires its lasers.</summary>
    public int GunVertex { get; }

    /// <summary>
    /// The number of vertices used as the origins of the explosion cloud (which
    /// can be more than the model has, as it is for the rock hermit).
    /// </summary>
    public int ExplosionVertices { get; }

    /// <summary>The distance (z_hi) beyond which the original draws the ship as a dot.</summary>
    public int DotDistance { get; }

    /// <summary>Load a model from a glTF file.</summary>
    public static ShipModel Load(string path)
    {
        try
        {
            var gltf = GltfReader.Load(path);
            var mesh = gltf.Array("meshes").FirstOrDefault();
            if (mesh.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The model has no mesh");
            }

            var primitive = mesh.GetProperty("primitives")[0];
            var positions = gltf.ReadAccessor(primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32());
            if (positions.Length > MaxVertices)
            {
                throw new InvalidDataException($"The model has {positions.Length} vertices, but at most {MaxVertices} are allowed");
            }

            var vertices = positions.Select(p => new Vector3((float)p[0], (float)p[1], (float)p[2])).ToArray();
            var elite = mesh.GetProperty("extras").GetProperty("elite");
            return new ShipModel(
                Path.GetFileNameWithoutExtension(path),
                vertices,
                Range(elite.GetProperty("gunVertex").GetInt32(), 0, Math.Max(0, vertices.Length - 1), "gunVertex"),
                Range(elite.GetProperty("explosionVertices").GetInt32(), 0, MaxVertices, "explosionVertices"),
                Range(elite.GetProperty("dotDistance").GetInt32(), 0, 255, "dotDistance"));
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException or FormatException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    private static int Range(int value, int min, int max, string what) =>
        value >= min && value <= max ? value : throw new InvalidDataException($"{what} {value} must be between {min} and {max}");
}

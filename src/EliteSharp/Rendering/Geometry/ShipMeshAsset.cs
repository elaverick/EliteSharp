using System.Numerics;
using System.Text.Json;
using EliteSharp.Formats;

namespace EliteSharp.Rendering.Geometry;

/// <summary>
/// A ship model's geometry, as the renderer reads it from the model's glTF
/// file (see tools/export_ships.py for the layout): the vertex positions, the
/// ship's closed surface (triangles wound anticlockwise when seen from
/// outside), the lines of its structure (where its faces meet, and the parts
/// that stick out), and the details drawn on its faces.
/// </summary>
/// <param name="Name">The model's name (its file name without the extension).</param>
public sealed record ShipMeshAsset(
    string Name,
    IReadOnlyList<Vector3> Points,
    IReadOnlyList<Triangle> Surface,
    IReadOnlyList<(int A, int B)> Structure,
    IReadOnlyList<(int A, int B)> Details)
{
    /// <summary>Load a model's geometry from its glTF file.</summary>
    public static ShipMeshAsset Load(string path)
    {
        try
        {
            var gltf = GltfReader.Load(path);
            var mesh = gltf.Array("meshes").First();
            double[][]? positions = null;
            var surface = new List<Triangle>();
            var structure = new List<(int, int)>();
            var details = new List<(int, int)>();
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                positions ??= gltf.ReadAccessor(primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                var indices = gltf.ReadAccessor(primitive.GetProperty("indices").GetInt32());
                string role = primitive.GetProperty("extras").GetProperty("role").GetString() ?? "";
                switch (role)
                {
                    case "surface":
                        for (int i = 0; i + 2 < indices.Length; i += 3)
                        {
                            surface.Add(new Triangle((int)indices[i][0], (int)indices[i + 1][0], (int)indices[i + 2][0]));
                        }

                        break;
                    case "structure" or "details":
                        var lines = role == "structure" ? structure : details;
                        for (int i = 0; i + 1 < indices.Length; i += 2)
                        {
                            lines.Add(((int)indices[i][0], (int)indices[i + 1][0]));
                        }

                        break;
                    default:
                        throw new InvalidDataException($"Unknown primitive role '{role}'");
                }
            }

            if (positions == null || surface.Count == 0)
            {
                throw new InvalidDataException("The model has no surface");
            }

            var points = positions.Select(p => new Vector3((float)p[0], (float)p[1], (float)p[2])).ToArray();
            return new ShipMeshAsset(Path.GetFileNameWithoutExtension(path), points, surface, structure, details);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e);
        }
    }
}

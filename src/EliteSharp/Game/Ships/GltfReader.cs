using System.Buffers.Binary;
using System.Text.Json;

namespace EliteSharp.Game.Ships;

/// <summary>
/// A minimal reader for glTF 2.0 files (the JSON form, .gltf), supporting what
/// the ship models need: reading accessors from buffers that are either
/// embedded as data URIs or stored in separate files next to the model.
/// </summary>
public sealed class GltfReader
{
    private readonly JsonElement _root;
    private readonly List<byte[]> _buffers = [];

    private GltfReader(JsonElement root, string directory)
    {
        _root = root;
        foreach (var buffer in Array("buffers"))
        {
            string uri = buffer.GetProperty("uri").GetString()
                ?? throw new InvalidDataException("Buffer has no URI");
            byte[] data = uri.StartsWith("data:", StringComparison.Ordinal)
                ? Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..])
                : File.ReadAllBytes(Path.Combine(directory, Uri.UnescapeDataString(uri)));
            if (data.Length < buffer.GetProperty("byteLength").GetInt32())
            {
                throw new InvalidDataException($"Buffer '{uri[..Math.Min(uri.Length, 40)]}' is shorter than its byteLength");
            }

            _buffers.Add(data);
        }
    }

    /// <summary>The root of the glTF JSON document.</summary>
    public JsonElement Root => _root;

    public static GltfReader Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        string version = document.RootElement.GetProperty("asset").GetProperty("version").GetString() ?? "";
        if (!version.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported glTF version '{version}'");
        }

        return new GltfReader(document.RootElement.Clone(), Path.GetDirectoryName(path) ?? ".");
    }

    /// <summary>The elements of a top-level array (such as "meshes"), or none if it is absent.</summary>
    public IEnumerable<JsonElement> Array(string name) =>
        _root.TryGetProperty(name, out var array) ? array.EnumerateArray() : [];

    /// <summary>Read an accessor as an array of elements, each with its components as doubles.</summary>
    public double[][] ReadAccessor(int index)
    {
        var accessor = _root.GetProperty("accessors")[index];
        int count = accessor.GetProperty("count").GetInt32();
        int components = accessor.GetProperty("type").GetString() switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            var type => throw new InvalidDataException($"Unsupported accessor type '{type}'"),
        };
        int componentType = accessor.GetProperty("componentType").GetInt32();
        int componentSize = componentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw new InvalidDataException($"Unsupported component type {componentType}"),
        };

        var result = new double[count][];
        if (!accessor.TryGetProperty("bufferView", out var viewIndex))
        {
            // An accessor with no buffer view is all zeroes
            for (int i = 0; i < count; i++)
            {
                result[i] = new double[components];
            }

            return result;
        }

        var view = _root.GetProperty("bufferViews")[viewIndex.GetInt32()];
        byte[] buffer = _buffers[view.GetProperty("buffer").GetInt32()];
        int offset = Optional(view, "byteOffset") + Optional(accessor, "byteOffset");
        int stride = Optional(view, "byteStride");
        if (stride == 0)
        {
            stride = components * componentSize;
        }

        for (int i = 0; i < count; i++)
        {
            var element = new double[components];
            for (int c = 0; c < components; c++)
            {
                var bytes = buffer.AsSpan(offset + i * stride + c * componentSize, componentSize);
                element[c] = componentType switch
                {
                    5120 => (sbyte)bytes[0],
                    5121 => bytes[0],
                    5122 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                    5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                    _ => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                };
            }

            result[i] = element;
        }

        return result;
    }

    private static int Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
}

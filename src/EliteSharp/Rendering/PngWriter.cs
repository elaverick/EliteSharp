using System.IO.Compression;

namespace EliteSharp.Rendering;

/// <summary>A minimal PNG writer for screenshots.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>Write an RGB image (3 bytes per pixel, rows top to bottom) as a PNG file.</summary>
    public static void Write(string path, int width, int height, byte[] rgb)
    {
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(file, "IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgb, y * width * 3, width * 3);
            }
        }

        WriteChunk(file, "IDAT", raw.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc ^ 0xFFFFFFFF);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}

using Silk.NET.Core;
using StbImageSharp;

namespace EliteSharp;

/// <summary>
/// The game's icon (elitesharp.ico), which is both the executable's icon and,
/// embedded in the executable, the window's icon. Each of the icon's sizes is
/// stored as a PNG, so they're decoded with the same library as the HUD's
/// images.
/// </summary>
public static class GameIcon
{
    /// <summary>The name of the icon embedded in the executable.</summary>
    private const string ResourceName = "elitesharp.ico";

    /// <summary>The bytes a PNG starts with.</summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G'];

    /// <summary>
    /// Read every size of the icon, as RGBA images, or none if the icon is
    /// missing or isn't made of PNGs (the window then keeps its default icon).
    /// </summary>
    public static RawImage[] Load()
    {
        using var stream = typeof(GameIcon).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            return [];
        }

        byte[] icon = new byte[stream.Length];
        stream.ReadExactly(icon);

        // The header is the reserved word, the type (1 for an icon) and the
        // number of images, followed by a 16-byte entry for each image whose
        // last two words are the image's size in bytes and its offset
        int count = BitConverter.ToUInt16(icon, 4);
        var images = new List<RawImage>();
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int size = BitConverter.ToInt32(icon, entry + 8);
            int offset = BitConverter.ToInt32(icon, entry + 12);
            var data = icon.AsSpan(offset, size);
            if (!data.StartsWith(PngSignature))
            {
                continue;
            }

            var image = ImageResult.FromMemory(data.ToArray(), ColorComponents.RedGreenBlueAlpha);
            images.Add(new RawImage(image.Width, image.Height, image.Data));
        }

        return [.. images];
    }
}

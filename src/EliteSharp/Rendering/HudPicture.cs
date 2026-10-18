using StbImageSharp;

namespace EliteSharp.Rendering;

/// <summary>
/// A full-colour picture in the HUD (such as a controller on the Controls
/// screen), from a PNG file, shown at the window's full resolution rather
/// than in the original's pixels. The game says which picture to show and
/// where (in the HUD's layout units), and the renderer loads it the first
/// time it is shown. Pictures are drawn behind the rest of the space view.
/// </summary>
public sealed record HudPicture(string Path, float X, float Y, float Width, float Height);

/// <summary>A picture's colours, ready for the GPU: premultiplied by their alpha, with mipmaps.</summary>
public sealed class PictureImage
{
    private PictureImage(int width, int height, IReadOnlyList<byte[]> levels)
    {
        Width = width;
        Height = height;
        Levels = levels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The RGBA texels of each level, row by row, from the full-size image down, each half the size of the one before.</summary>
    public IReadOnlyList<byte[]> Levels { get; }

    /// <summary>Read the size of a PNG, without decoding it.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var stream = File.OpenRead(path);
        var info = ImageInfo.FromStream(stream) ?? throw new InvalidDataException($"{System.IO.Path.GetFileName(path)}: the image can't be read");
        return (info.Width, info.Height);
    }

    /// <summary>Load a picture, making its mipmaps (down to about 16 pixels on the shorter side).</summary>
    /// <exception cref="InvalidDataException">The image can't be read.</exception>
    public static PictureImage Load(string path)
    {
        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) when (e is not IOException)
        {
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)}: the image can't be read ({e.Message})", e);
        }

        byte[] texels = image.Data;
        for (int i = 0; i < texels.Length; i += 4)
        {
            int alpha = texels[i + 3];
            texels[i] = (byte)((texels[i] * alpha + 127) / 255);
            texels[i + 1] = (byte)((texels[i + 1] * alpha + 127) / 255);
            texels[i + 2] = (byte)((texels[i + 2] * alpha + 127) / 255);
        }

        var levels = new List<byte[]> { texels };
        int width = image.Width, height = image.Height;
        while (Math.Min(width, height) >= 32)
        {
            int halfWidth = width / 2, halfHeight = height / 2;
            var previous = levels[^1];
            var level = new byte[halfWidth * halfHeight * 4];
            for (int y = 0; y < halfHeight; y++)
            {
                for (int x = 0; x < halfWidth; x++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = previous[((2 * y) * width + 2 * x) * 4 + c] + previous[((2 * y) * width + 2 * x + 1) * 4 + c]
                            + previous[((2 * y + 1) * width + 2 * x) * 4 + c] + previous[((2 * y + 1) * width + 2 * x + 1) * 4 + c];
                        level[(y * halfWidth + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
                }
            }

            levels.Add(level);
            width = halfWidth;
            height = halfHeight;
        }

        return new PictureImage(image.Width, image.Height, levels);
    }
}

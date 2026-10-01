using EliteSharp.Data;
using StbImageSharp;

namespace EliteSharp.Rendering;

/// <summary>
/// The HUD's images, packed into one texture that is uploaded to the GPU once:
/// the font, the dashboard and its two bulbs. Each texel is an ink, so the
/// images take on the current palette's colours: 0 is transparent,
/// <see cref="QuadInk"/> means "the ink of the quad being drawn" (for the
/// font, so text can be any colour), and anything else is that ink.
///
/// The font is imported from the original's bitmap (one bit per pixel), and
/// the dashboard and bulbs are loaded from PNG images in Assets/Images, one
/// pixel per texel, in the dashboard's colours (red, green, yellow, blue,
/// magenta, cyan and white), with transparent pixels where nothing is drawn.
/// </summary>
public static class HudAtlas
{
    /// <summary>The texel value that means "use the quad's ink".</summary>
    public const byte QuadInk = 255;

    /// <summary>The width of the texture in texels (one texel per pixel of the original's screen).</summary>
    public const int Width = 256;

    /// <summary>The height of the texture in texels.</summary>
    public const int Height = 96;

    /// <summary>The size of a character in pixels.</summary>
    public const int GlyphSize = 8;

    /// <summary>The first character in the font (a space).</summary>
    private const int FirstGlyph = 32;

    /// <summary>The number of characters in the font.</summary>
    private const int GlyphCount = 96;

    /// <summary>The top of the font in the texture (the font follows the dashboard).</summary>
    private const int FontTop = DashboardHeight;

    /// <summary>The height of the dashboard in pixels.</summary>
    public const int DashboardHeight = 56;

    /// <summary>The size of each bulb in pixels.</summary>
    public const int BulbSize = 8;

    /// <summary>The top of the bulbs and the solid texel in the texture (after the font).</summary>
    private const int BulbTop = FontTop + GlyphCount / (Width / GlyphSize) * GlyphSize;

    /// <summary>The dashboard image.</summary>
    public static readonly AtlasRegion Dashboard = new(0, 0, Width, DashboardHeight);

    /// <summary>The E.C.M. bulb.</summary>
    public static readonly AtlasRegion EcmBulb = new(0, BulbTop, BulbSize, BulbSize);

    /// <summary>The space station bulb.</summary>
    public static readonly AtlasRegion StationBulb = new(BulbSize, BulbTop, BulbSize, BulbSize);

    /// <summary>A single texel of <see cref="QuadInk"/>, for solid rectangles.</summary>
    public static readonly AtlasRegion Solid = new(2 * BulbSize, BulbTop, 1, 1);

    /// <summary>The folder containing the HUD's images.</summary>
    public static string ImageFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Images");

    private static readonly Lazy<byte[]> LoadedTexels = new(() => Build(ImageFolder));

    /// <summary>The texture's texels, row by row, built from the images in <see cref="ImageFolder"/> the first time they are needed.</summary>
    /// <exception cref="InvalidDataException">An image isn't valid (the message says which and why).</exception>
    public static byte[] Texels => LoadedTexels.Value;

    /// <summary>The character's image, or null if it isn't in the font.</summary>
    public static AtlasRegion? Glyph(char character)
    {
        int index = character - FirstGlyph;
        if (index < 0 || index >= GlyphCount)
        {
            return null;
        }

        int perRow = Width / GlyphSize;
        return new AtlasRegion(index % perRow * GlyphSize, FontTop + index / perRow * GlyphSize, GlyphSize, GlyphSize);
    }

    /// <summary>Build the texture's texels, row by row, with the images in a folder.</summary>
    /// <exception cref="InvalidDataException">An image isn't valid (the message says which and why).</exception>
    public static byte[] Build(string imageFolder)
    {
        var texels = new byte[Width * Height];

        // The font
        for (int glyph = 0; glyph < GlyphCount; glyph++)
        {
            var region = Glyph((char)(FirstGlyph + glyph))!.Value;
            for (int y = 0; y < GlyphSize; y++)
            {
                int bits = GameData.Font[glyph * GlyphSize + y];
                for (int x = 0; x < GlyphSize; x++)
                {
                    if ((bits & (0x80 >> x)) != 0)
                    {
                        texels[(region.Y + y) * Width + region.X + x] = QuadInk;
                    }
                }
            }
        }

        PutImage(texels, Dashboard, Path.Combine(imageFolder, "dashboard.png"));
        PutImage(texels, EcmBulb, Path.Combine(imageFolder, "ecm-bulb.png"));
        PutImage(texels, StationBulb, Path.Combine(imageFolder, "station-bulb.png"));

        texels[Solid.Y * Width + Solid.X] = QuadInk;
        return texels;
    }

    /// <summary>The inks for the colours in the images (as 0xRRGGBB), which are the dashboard's.</summary>
    private static readonly Dictionary<int, Ink> ImageInks = new()
    {
        [0xFF0000] = Ink.DashboardRed,
        [0x00FF00] = Ink.DashboardGreen,
        [0xFFFF00] = Ink.DashboardYellow,
        [0x0000FF] = Ink.DashboardBlue,
        [0xFF00FF] = Ink.DashboardMagenta,
        [0x00FFFF] = Ink.DashboardCyan,
        [0xFFFFFF] = Ink.DashboardWhite,
    };

    /// <summary>Put an image into its region of the texture, as inks, checking its size and colours.</summary>
    private static void PutImage(byte[] texels, AtlasRegion region, string path)
    {
        string file = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The HUD image '{path}' is missing", path);
        }

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) when (e is not IOException)
        {
            throw new InvalidDataException($"{file}: the image can't be read ({e.Message})", e);
        }

        if (image.Width != region.Width || image.Height != region.Height)
        {
            throw new InvalidDataException($"{file}: the image must be {region.Width} by {region.Height} pixels, not {image.Width} by {image.Height}");
        }

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4;
                byte alpha = image.Data[i + 3];
                int colour = image.Data[i] << 16 | image.Data[i + 1] << 8 | image.Data[i + 2];
                Ink ink;
                if (alpha == 0)
                {
                    ink = Ink.None;
                }
                else if (alpha != 255 || !ImageInks.TryGetValue(colour, out ink))
                {
                    throw new InvalidDataException(
                        $"{file} (pixel {x}, {y}): each pixel must be transparent, or one of the dashboard's colours "
                        + $"({string.Join(", ", ImageInks.Keys.Select(c => $"#{c:X6}"))}), not #{colour:X6} with alpha {alpha}");
                }

                texels[(region.Y + y) * Width + region.X + x] = (byte)ink;
            }
        }
    }
}

/// <summary>A rectangle of texels in the <see cref="HudAtlas"/>.</summary>
public readonly record struct AtlasRegion(int X, int Y, int Width, int Height);

using StbImageSharp;

namespace EliteSharp.Rendering;

/// <summary>
/// The HUD's images, packed into one texture that is uploaded to the GPU once:
/// the dashboard, its two bulbs and the font. Each texel is an ink, so the
/// images take on the current palette's colours: 0 is transparent,
/// <see cref="QuadInk"/> means "the ink of the quad being drawn" (for the
/// font, so text can be any colour), and anything else is that ink.
///
/// The images are PNGs in Assets/Images, one pixel per texel, with
/// transparent pixels where nothing is drawn. The dashboard and bulbs are in
/// the dashboard's colours (red, green, yellow, blue, magenta, cyan and
/// white), and the font is white.
///
/// The font (font.png) is a grid of 8 by 8 characters, 32 to a row, in
/// Unicode order from the space (U+0020), so the first three rows are the
/// printable ASCII characters, and the rows after them are U+0080 onwards
/// (the accented letters start at U+00C0). The font can have as many rows as
/// it needs, and a character whose cell is empty isn't in the font (apart
/// from the space). The font is last in the texture, so the texture is as
/// tall as the font needs it to be.
/// </summary>
public static class HudAtlas
{
    /// <summary>The texel value that means "use the quad's ink".</summary>
    public const byte QuadInk = 255;

    /// <summary>The width of the texture in texels (one texel per pixel of the original's screen).</summary>
    public const int Width = 256;

    /// <summary>The height of the texture in texels (which depends on the size of the font).</summary>
    public static int Height => Texels.Length / Width;

    /// <summary>The size of a character in pixels.</summary>
    public const int GlyphSize = 8;

    /// <summary>The first character in the font (a space).</summary>
    private const char FirstGlyph = ' ';

    /// <summary>The number of characters in each row of the font.</summary>
    private const int GlyphsPerRow = Width / GlyphSize;

    /// <summary>The height of the dashboard in pixels.</summary>
    public const int DashboardHeight = 56;

    /// <summary>The size of each bulb in pixels.</summary>
    public const int BulbSize = 8;

    /// <summary>The top of the bulbs and the solid texel in the texture (after the dashboard).</summary>
    private const int BulbTop = DashboardHeight;

    /// <summary>The top of the font in the texture (after the bulbs).</summary>
    private const int FontTop = BulbTop + BulbSize;

    /// <summary>The dashboard image.</summary>
    public static readonly AtlasRegion Dashboard = new(0, 0, Width, DashboardHeight);

    /// <summary>The E.C.M. bulb.</summary>
    public static readonly AtlasRegion EcmBulb = new(0, BulbTop, BulbSize, BulbSize);

    /// <summary>The space station bulb.</summary>
    public static readonly AtlasRegion StationBulb = new(BulbSize, BulbTop, BulbSize, BulbSize);

    /// <summary>A single texel of <see cref="QuadInk"/>, for solid rectangles.</summary>
    public static readonly AtlasRegion Solid = new(2 * BulbSize, BulbTop, 1, 1);

    /// <summary>The game's own folder containing the HUD's images (a mod can replace any of them).</summary>
    public static string ImageFolder => Path.Combine(GameAssets.BaseFolder, "Images");

    private static readonly Lazy<(byte[] Texels, bool[] InFont)> Loaded = new(() => Load(file => GameAssets.File("Images", file)));

    /// <summary>The texture's texels, row by row, built from the images (each the mod's or the game's) the first time they are needed.</summary>
    /// <exception cref="InvalidDataException">An image isn't valid (the message says which and why).</exception>
    public static byte[] Texels => Loaded.Value.Texels;

    /// <summary>Whether the character is in the font, so the game can print it.</summary>
    public static bool InFont(char character)
    {
        var inFont = Loaded.Value.InFont;
        int index = character - FirstGlyph;
        return index >= 0 && index < inFont.Length && inFont[index];
    }

    /// <summary>The character's image, or null if it isn't in the font.</summary>
    public static AtlasRegion? Glyph(char character)
    {
        if (!InFont(character))
        {
            return null;
        }

        int index = character - FirstGlyph;
        return new AtlasRegion(index % GlyphsPerRow * GlyphSize, FontTop + index / GlyphsPerRow * GlyphSize, GlyphSize, GlyphSize);
    }

    /// <summary>Build the texture's texels, row by row, with the images in a folder.</summary>
    /// <exception cref="InvalidDataException">An image isn't valid (the message says which and why).</exception>
    public static byte[] Build(string imageFolder) => Load(file => Path.Combine(imageFolder, file)).Texels;

    /// <summary>Build the texture's texels with the images (finding each image's path by its file name), and find which characters are in the font.</summary>
    private static (byte[] Texels, bool[] InFont) Load(Func<string, string> imagePath)
    {
        var font = ReadImage(imagePath("font.png"));
        if (font.Width != Width || font.Height == 0 || font.Height % GlyphSize != 0)
        {
            throw new InvalidDataException(
                $"font.png: the image must be {Width} pixels wide ({GlyphsPerRow} characters), and a whole number of "
                + $"{GlyphSize}-pixel rows of characters high, not {font.Width} by {font.Height}");
        }

        var texels = new byte[Width * (FontTop + font.Height)];
        PutImage(texels, Dashboard, imagePath("dashboard.png"));
        PutImage(texels, EcmBulb, imagePath("ecm-bulb.png"));
        PutImage(texels, StationBulb, imagePath("station-bulb.png"));
        texels[Solid.Y * Width + Solid.X] = QuadInk;
        PutImage(texels, new AtlasRegion(0, FontTop, Width, font.Height), "font.png", font, FontInks, "white");

        // A character is in the font if there is something in its cell (or it's the space)
        var inFont = new bool[font.Height / GlyphSize * GlyphsPerRow];
        for (int index = 0; index < inFont.Length; index++)
        {
            int left = index % GlyphsPerRow * GlyphSize;
            int top = FontTop + index / GlyphsPerRow * GlyphSize;
            inFont[index] = index == 0 || Enumerable.Range(0, GlyphSize * GlyphSize)
                .Any(i => texels[(top + i / GlyphSize) * Width + left + i % GlyphSize] != 0);
        }

        return (texels, inFont);
    }

    /// <summary>The inks for the colours in the dashboard and bulbs (as 0xRRGGBB), which are the dashboard's.</summary>
    private static readonly Dictionary<int, byte> DashboardInks = new()
    {
        [0xFF0000] = (byte)Ink.DashboardRed,
        [0x00FF00] = (byte)Ink.DashboardGreen,
        [0xFFFF00] = (byte)Ink.DashboardYellow,
        [0x0000FF] = (byte)Ink.DashboardBlue,
        [0xFF00FF] = (byte)Ink.DashboardMagenta,
        [0x00FFFF] = (byte)Ink.DashboardCyan,
        [0xFFFFFF] = (byte)Ink.DashboardWhite,
    };

    /// <summary>The ink for the font's colour (white), which is the ink of the text being printed.</summary>
    private static readonly Dictionary<int, byte> FontInks = new()
    {
        [0xFFFFFF] = QuadInk,
    };

    /// <summary>Read an image, as red, green, blue and alpha bytes.</summary>
    private static ImageResult ReadImage(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The HUD image '{path}' is missing", path);
        }

        try
        {
            return ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) when (e is not IOException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: the image can't be read ({e.Message})", e);
        }
    }

    /// <summary>Put the dashboard or a bulb into its region of the texture, as inks, checking its size and colours.</summary>
    private static void PutImage(byte[] texels, AtlasRegion region, string path)
    {
        string file = Path.GetFileName(path);
        var image = ReadImage(path);
        if (image.Width != region.Width || image.Height != region.Height)
        {
            throw new InvalidDataException($"{file}: the image must be {region.Width} by {region.Height} pixels, not {image.Width} by {image.Height}");
        }

        PutImage(texels, region, file, image, DashboardInks, "one of the dashboard's colours");
    }

    /// <summary>Put an image into its region of the texture, as inks, checking its colours.</summary>
    private static void PutImage(byte[] texels, AtlasRegion region, string file, ImageResult image, Dictionary<int, byte> inks, string colours)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * image.Width + x) * 4;
                byte alpha = image.Data[i + 3];
                int colour = image.Data[i] << 16 | image.Data[i + 1] << 8 | image.Data[i + 2];
                byte ink;
                if (alpha == 0)
                {
                    ink = (byte)Ink.None;
                }
                else if (alpha != 255 || !inks.TryGetValue(colour, out ink))
                {
                    throw new InvalidDataException(
                        $"{file} (pixel {x}, {y}): each pixel must be transparent, or {colours} "
                        + $"({string.Join(", ", inks.Keys.Select(c => $"#{c:X6}"))}), not #{colour:X6} with alpha {alpha}");
                }

                texels[(region.Y + y) * Width + region.X + x] = ink;
            }
        }
    }
}

/// <summary>A rectangle of texels in the <see cref="HudAtlas"/>.</summary>
public readonly record struct AtlasRegion(int X, int Y, int Width, int Height);

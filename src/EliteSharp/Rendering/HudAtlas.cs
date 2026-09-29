using EliteSharp.Data;

namespace EliteSharp.Rendering;

/// <summary>
/// The HUD's images, packed into one texture that is uploaded to the GPU once:
/// the font, the dashboard and its two bulbs. Each texel is an ink, so the
/// images take on the current palette's colours: 0 is transparent,
/// <see cref="QuadInk"/> means "the ink of the quad being drawn" (for the
/// font, so text can be any colour), and anything else is that ink.
///
/// The images are imported from the original's bitmaps when the game starts:
/// the font is one bit per pixel, and the dashboard and bulbs are in screen
/// mode 2, with two pixels (each two of the space view's pixels wide) in each
/// byte.
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

    /// <summary>Build the texture's texels, row by row.</summary>
    public static byte[] Build()
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

        // The dashboard, which is stored in character blocks of eight rows,
        // each row of the block being one byte
        var dashboard = GameData.Dashboard;
        for (int row = 0; row < DashboardHeight / 8; row++)
        {
            for (int column = 0; column < Width / 4; column++)
            {
                for (int line = 0; line < 8; line++)
                {
                    PutMode2Byte(texels, column * 4, Dashboard.Y + row * 8 + line, dashboard[row * 512 + column * 8 + line]);
                }
            }
        }

        // The bulbs, which are two columns of eight bytes
        foreach (var (bulb, bitmap) in new[] { (EcmBulb, GameData.EcmBulb), (StationBulb, GameData.StationBulb) })
        {
            for (int i = 0; i < 16; i++)
            {
                PutMode2Byte(texels, bulb.X + (i >> 3) * 4, bulb.Y + (i & 7), bitmap[i]);
            }
        }

        texels[Solid.Y * Width + Solid.X] = QuadInk;
        return texels;
    }

    /// <summary>
    /// The dashboard's ink for each of the colour numbers in its images (the
    /// original's mode 2 logical colours, as its dashboard palette shows them).
    /// </summary>
    private static readonly Ink[] DashboardInks =
    [
        Ink.None, Ink.DashboardRed, Ink.DashboardGreen, Ink.DashboardYellow,
        Ink.DashboardBlue, Ink.DashboardMagenta, Ink.DashboardCyan, Ink.DashboardWhite,
        Ink.DashboardRed, Ink.DashboardRed, Ink.DashboardGreen, Ink.DashboardYellow,
        Ink.DashboardBlue, Ink.DashboardMagenta, Ink.DashboardCyan, Ink.DashboardWhite,
    ];

    /// <summary>
    /// Put the two pixels of a mode 2 byte into the texture (each two texels
    /// wide). The first pixel's colour number is in bits 7, 5, 3 and 1, and
    /// the second's in bits 6, 4, 2 and 0.
    /// </summary>
    private static void PutMode2Byte(byte[] texels, int x, int y, int value)
    {
        for (int pixel = 0; pixel < 2; pixel++)
        {
            int bits = value << pixel;
            int colour = ((bits >> 4) & 8) | ((bits >> 3) & 4) | ((bits >> 2) & 2) | ((bits >> 1) & 1);
            byte ink = (byte)DashboardInks[colour];
            texels[y * Width + x + pixel * 2] = ink;
            texels[y * Width + x + pixel * 2 + 1] = ink;
        }
    }
}

/// <summary>A rectangle of texels in the <see cref="HudAtlas"/>.</summary>
public readonly record struct AtlasRegion(int X, int Y, int Width, int Height);

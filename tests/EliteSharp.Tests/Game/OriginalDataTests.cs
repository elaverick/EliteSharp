using System.Reflection;
using EliteSharp.Data;
using EliteSharp.Game;
using EliteSharp.Rendering;
using EliteSharp.Sound;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The data that used to be lifted from the original's binaries into
/// GameData.g.cs, and is now in Assets/trading.yml, Assets/Images, Assets/Sounds or in the game's code, must be
/// exactly the original's, so each is checked against the original bytes.
/// </summary>
public sealed class OriginalDataTests
{
    [Fact]
    public void TheMarketIsTheOriginals()
    {
        // QQ23: each commodity's base price, economic factor and unit, base
        // quantity and fluctuation mask
        var bytes = TradingData.Load().Commodities
            .SelectMany(c => new[] { (byte)c.BasePrice, (byte)c.FactorAndUnit, (byte)c.BaseQuantity, (byte)c.Fluctuation })
            .ToArray();
        Assert.Equal(OriginalMarketPrices, bytes);
    }

    [Fact]
    public void TheEquipmentPricesAreTheOriginals()
    {
        // PRXS: each price in tenths of a credit, as a 16-bit number (the
        // first, for fuel, is replaced by the price of the fuel we need)
        var bytes = TradingData.Load().EquipmentPrices
            .SelectMany(e => new[] { (byte)e.Price, (byte)(e.Price >> 8) })
            .ToArray();
        Assert.Equal(OriginalEquipmentPrices[2..], bytes);
    }

    [Fact]
    public void TheDefaultCommanderIsTheOriginals()
    {
        // NA%: the name, the commander data block and its checksums
        var game = new PrivateGame();
        game.Call("RestoreDefaultCommander");
        var saved = game.Get<byte[]>("_savedCommander");
        Assert.Equal(OriginalDefaultCommander, saved[..OriginalDefaultCommander.Length]);
        Assert.All(saved[OriginalDefaultCommander.Length..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void TheLettersOfTheSystemsNamesAreTheOriginals()
    {
        // QQ16: each pair of letters, where the single "A" is "A?"
        var pairs = (string[])typeof(EliteGame).Assembly.GetType("EliteSharp.Game.SystemNames")!
            .GetField("LetterPairs", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        var bytes = pairs.SelectMany(pair => pair.PadRight(2, '?')).Select(c => (byte)c).ToArray();
        Assert.Equal(OriginalTwoLetterTokens, bytes);
    }

    [Fact]
    public void TheDashboardAndItsBulbsAreTheOriginals()
    {
        // The images, decoded as the original's mode 2 bitmaps: P.DIALS2P (7
        // rows of character blocks, each 64 columns of 8 bytes), ECBT and SPBT
        var texels = HudAtlas.Build(HudAtlas.ImageFolder);
        var dashboard = File.ReadAllBytes(Path.Combine(OriginalSourceFolder(), "1-source-files", "images", "P.DIALS2P.bin"));
        for (int row = 0; row < 7; row++)
        {
            for (int column = 0; column < 64; column++)
            {
                for (int line = 0; line < 8; line++)
                {
                    AssertMode2Byte(texels, column * 4, HudAtlas.Dashboard.Y + row * 8 + line, dashboard[row * 512 + column * 8 + line]);
                }
            }
        }

        foreach (var (bulb, bitmap) in new[] { (HudAtlas.EcmBulb, OriginalEcmBulb), (HudAtlas.StationBulb, OriginalStationBulb) })
        {
            for (int i = 0; i < 16; i++)
            {
                AssertMode2Byte(texels, bulb.X + (i >> 3) * 4, bulb.Y + (i & 7), bitmap[i]);
            }
        }
    }

    [Fact]
    public void TheFontIsTheOriginals()
    {
        // P.FONT: the MOS character bitmaps for ASCII 32-127, 8 bytes each,
        // one bit per pixel (the leftmost pixel in bit 7)
        var texels = HudAtlas.Texels;
        var font = File.ReadAllBytes(Path.Combine(OriginalSourceFolder(), "1-source-files", "fonts", "P.FONT.bin"));
        for (int index = 0; index < 96; index++)
        {
            var glyph = HudAtlas.Glyph((char)(' ' + index));
            Assert.NotNull(glyph);
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    byte ink = (font[index * 8 + y] & (0x80 >> x)) != 0 ? HudAtlas.QuadInk : (byte)Ink.None;
                    Assert.Equal(ink, texels[(glyph.Value.Y + y) * HudAtlas.Width + glyph.Value.X + x]);
                }
            }
        }

        // Nothing after ASCII is in the font yet
        Assert.False(HudAtlas.InFont('é'));
        Assert.Null(HudAtlas.Glyph('é'));
    }

    [Theory]
    [InlineData("ecm-bulb.png", 8, 8, "ecm-bulb.png (pixel 0, 0): each pixel must be transparent, or one of the dashboard's colours")]
    [InlineData("ecm-bulb.png", 9, 8, "ecm-bulb.png: the image must be 8 by 8 pixels, not 9 by 8")]
    [InlineData("font.png", 256, 8, "font.png (pixel 0, 0): each pixel must be transparent, or white")]
    [InlineData("font.png", 256, 12, "font.png: the image must be 256 pixels wide (32 characters), and a whole number of 8-pixel rows")]
    public void AMistakeInAnImageIsReported(string image, int width, int height, string expected)
    {
        // A black image (black isn't one of the image's colours), or one that
        // is the wrong size
        string folder = Path.Combine(Path.GetTempPath(), $"elite-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string file in Directory.GetFiles(HudAtlas.ImageFolder))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }

            PngWriter.Write(Path.Combine(folder, image), width, height, new byte[width * height * 3]);
            var e = Assert.Throws<InvalidDataException>(() => HudAtlas.Build(folder));
            Assert.StartsWith(expected, e.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The dashboard's ink for each mode 2 colour number, as the original's
    /// dashboard palette shows them (0 is nothing).
    /// </summary>
    private static readonly Ink[] Mode2Inks =
    [
        Ink.None, Ink.DashboardRed, Ink.DashboardGreen, Ink.DashboardYellow,
        Ink.DashboardBlue, Ink.DashboardMagenta, Ink.DashboardCyan, Ink.DashboardWhite,
        Ink.DashboardRed, Ink.DashboardRed, Ink.DashboardGreen, Ink.DashboardYellow,
        Ink.DashboardBlue, Ink.DashboardMagenta, Ink.DashboardCyan, Ink.DashboardWhite,
    ];

    /// <summary>
    /// Check the texels for the two pixels of a mode 2 byte, each two texels
    /// wide (the first pixel's colour number is in bits 7, 5, 3 and 1, and the
    /// second's in bits 6, 4, 2 and 0).
    /// </summary>
    private static void AssertMode2Byte(byte[] texels, int x, int y, int value)
    {
        for (int pixel = 0; pixel < 2; pixel++)
        {
            int bits = value << pixel;
            int colour = ((bits >> 4) & 8) | ((bits >> 3) & 4) | ((bits >> 2) & 2) | ((bits >> 1) & 1);
            byte ink = (byte)Mode2Inks[colour];
            Assert.Equal(ink, texels[y * HudAtlas.Width + x + pixel * 2]);
            Assert.Equal(ink, texels[y * HudAtlas.Width + x + pixel * 2 + 1]);
        }
    }

    /// <summary>The folder of the original's source code and binaries, in the repository.</summary>
    private static string OriginalSourceFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "elite-source-code-bbc-master");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The original's source code (elite-source-code-bbc-master) isn't in the repository");
    }

    [Fact]
    public void TheHangarGroupsAreTheOriginals()
    {
        // HATB: four groups of three ships, each its type, x_lo (with bit 0
        // giving z_hi) and z_lo (with bit 0 giving the sign of x), where an
        // empty slot is type 0 (and its position isn't used)
        var groups = StaticField<(int Type, int X, int Z)[][]>("HangarGroups");
        Assert.Equal(4, groups.Length);
        for (int group = 0; group < 4; group++)
        {
            for (int slot = 0; slot < 3; slot++)
            {
                int i = (group * 3 + slot) * 3;
                if (slot >= groups[group].Length)
                {
                    Assert.Equal(0, OriginalHangarGroups[i]);
                    continue;
                }

                var (type, x, z) = groups[group][slot];
                int xLo = Math.Abs(x);
                int zLo = z & 0xFF;
                Assert.Equal(OriginalHangarGroups[i..(i + 3)], new[] { (byte)type, (byte)xLo, (byte)zLo });
                Assert.Equal(1 + (xLo & 1), z >> 8);
                Assert.Equal(x < 0, (zLo & 1) != 0);
            }
        }
    }

    [Fact]
    public void TheSoundEffectsPrioritiesAndVoicesAreTheOriginals()
    {
        // SFXPR: each effect's priority, and bit 0 of SFXBT: whether it uses
        // the noise voice (the rest of the original's sound data is in the
        // recordings, which tools/render_sounds.py makes from it)
        Assert.Equal(OriginalSoundPriorities.Select(b => (int)b), SoundEffects.All.Select(e => e.Priority));
        Assert.Equal(OriginalSoundBits.Select(b => (b & 1) != 0), SoundEffects.All.Select(e => e.NoiseVoice));
    }

    [Fact]
    public void TheSoundEffectsLoad()
    {
        // Each is rendered at 44.1 kHz in mono, and lasts a whole number of
        // runs of the original's 50 Hz sound interrupt
        var effects = SoundEffects.Load();
        Assert.Equal(SoundEffects.All.Length, effects.Length);
        Assert.All(effects, effect =>
        {
            Assert.Equal((1, 44100), (effect.Channels, effect.SampleRate));
            Assert.NotEmpty(effect.Samples);
            Assert.Equal(0, effect.Samples.Length % 882);
        });
    }

    [Fact]
    public void ASoundEffectThatCantBeReadIsReported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"elite-sound-{Guid.NewGuid():N}.ogg");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        try
        {
            var e = Assert.Throws<InvalidDataException>(() => SoundEffects.LoadSamples(path));
            Assert.StartsWith($"{Path.GetFileName(path)}: the sound can't be read", e.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheEnergyBombsBoltIsTheOriginals() =>
        Assert.Equal(OriginalBombBaseX.Select(b => (int)b), StaticField<int[]>("BombBoltBaseX"));

    [Fact]
    public void ThePauseKeysAreTheOriginals() =>
        Assert.Equal(OriginalPauseToggleKeys.Select(b => (int)b), StaticField<int[]>("ToggleOptionKeys"));

    private static T StaticField<T>(string name) =>
        (T)typeof(EliteGame).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    // The original bytes, as assembled from the BBC Master source (QQ23, PRXS,
    // NA%, BOMBPOS, TGINT, QQ16, HATB, SFXPR, SFXBT, ECBT and SPBT)

    private static readonly byte[] OriginalSoundPriorities =
    [
        0x4B, 0x5B, 0x3F, 0xEB, 0xFF, 0x09, 0xFF, 0x8B, 0xCF, 0xE7, 0xFF, 0xEF,
    ];

    private static readonly byte[] OriginalSoundBits =
    [
        0x40, 0x10, 0x01, 0xFC, 0xF3, 0x19, 0xF9, 0x7C, 0xF1, 0xFA, 0xFE, 0xFE,
    ];

    private static readonly byte[] OriginalHangarGroups =
    [
        0x0B, 0x44, 0x3B, 0x00, 0x82, 0xB0, 0x00, 0x00, 0x00, 0x05, 0x50, 0x11, 0x05, 0xD1, 0x28, 0x05,
        0x40, 0x06, 0x10, 0x60, 0x90, 0x13, 0x10, 0xD1, 0x00, 0x00, 0x00, 0x14, 0x51, 0xF8, 0x10, 0x60,
        0x75, 0x00, 0x00, 0x00,
    ];

    private static readonly byte[] OriginalEcmBulb =
    [
        0xFF, 0xFF, 0xAA, 0xFF, 0xFF, 0xAA, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0xFF,
    ];

    private static readonly byte[] OriginalStationBulb =
    [
        0xFF, 0xFF, 0xAA, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0x55, 0xFF, 0xFF,
    ];

    private static readonly byte[] OriginalTwoLetterTokens =
    [
        0x41, 0x4C, 0x4C, 0x45, 0x58, 0x45, 0x47, 0x45, 0x5A, 0x41, 0x43, 0x45, 0x42, 0x49, 0x53, 0x4F,
        0x55, 0x53, 0x45, 0x53, 0x41, 0x52, 0x4D, 0x41, 0x49, 0x4E, 0x44, 0x49, 0x52, 0x45, 0x41, 0x3F,
        0x45, 0x52, 0x41, 0x54, 0x45, 0x4E, 0x42, 0x45, 0x52, 0x41, 0x4C, 0x41, 0x56, 0x45, 0x54, 0x49,
        0x45, 0x44, 0x4F, 0x52, 0x51, 0x55, 0x41, 0x4E, 0x54, 0x45, 0x49, 0x53, 0x52, 0x49, 0x4F, 0x4E,
    ];

    private static readonly byte[] OriginalMarketPrices =
    [
        0x13, 0x82, 0x06, 0x01, 0x14, 0x81, 0x0A, 0x03, 0x41, 0x83, 0x02, 0x07, 0x28, 0x85, 0xE2, 0x1F,
        0x53, 0x85, 0xFB, 0x0F, 0xC4, 0x08, 0x36, 0x03, 0xEB, 0x1D, 0x08, 0x78, 0x9A, 0x0E, 0x38, 0x03,
        0x75, 0x06, 0x28, 0x07, 0x4E, 0x01, 0x11, 0x1F, 0x7C, 0x0D, 0x1D, 0x07, 0xB0, 0x89, 0xDC, 0x3F,
        0x20, 0x81, 0x35, 0x03, 0x61, 0xA1, 0x42, 0x07, 0xAB, 0xA2, 0x37, 0x1F, 0x2D, 0xC1, 0xFA, 0x0F,
        0x35, 0x0F, 0xC0, 0x07,
    ];

    private static readonly byte[] OriginalEquipmentPrices =
    [
        0x01, 0x00, 0x2C, 0x01, 0xA0, 0x0F, 0x70, 0x17, 0xA0, 0x0F, 0x10, 0x27, 0x82, 0x14, 0x10, 0x27,
        0x28, 0x23, 0x98, 0x3A, 0x10, 0x27, 0x50, 0xC3, 0x60, 0xEA, 0x40, 0x1F,
    ];

    private static readonly byte[] OriginalDefaultCommander =
    [
        0x4A, 0x41, 0x4D, 0x45, 0x53, 0x4F, 0x4E, 0x0D, 0x00, 0x14, 0xAD, 0x4A, 0x5A, 0x48, 0x02, 0x53,
        0xB7, 0x00, 0x00, 0x03, 0xE8, 0x46, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x16, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x10, 0x0F, 0x11,
        0x00, 0x03, 0x1C, 0x0E, 0x00, 0x00, 0x0A, 0x00, 0x11, 0x3A, 0x07, 0x09, 0x08, 0x00, 0x00, 0x00,
        0x00, 0x80, 0xAA, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    private static readonly byte[] OriginalBombBaseX =
    [
        0xE0, 0xE0, 0xC0, 0xA0, 0x80, 0x60, 0x40, 0x20, 0x00, 0x00,
    ];

    private static readonly byte[] OriginalPauseToggleKeys =
    [
        0x01, 0x41, 0x58, 0x46, 0x59, 0x4A, 0x4B, 0x55, 0x54,
    ];
}

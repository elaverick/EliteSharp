namespace EliteSharp.Rendering;

/// <summary>
/// The palettes for the space view (the top part of the screen, including the
/// 3D world), which the original switches between for different screens.
/// </summary>
public enum SpacePalette
{
    /// <summary>The space view: yellow, red and cyan.</summary>
    Space,

    /// <summary>The charts: yellow, red and white.</summary>
    Chart,

    /// <summary>The title screen: yellow, white and cyan.</summary>
    Title,

    /// <summary>The trading screens: yellow, magenta and white.</summary>
    Trade,

    /// <summary>The hyperspace effect: yellow, blue and white.</summary>
    Hyperspace,
}

/// <summary>
/// What each ink looks like: the space view's palette, and whether the
/// escape pod is fitted (which turns the dashboard's yellow white, as the
/// original's way of showing that the pod is on board).
///
/// Each ink is a pattern of four colours, one per pixel of the original's
/// screen, which repeats across the screen (a solid colour is the same colour
/// four times). The renderers look these up on the GPU, so each ink is just a
/// small number in the vertex and draw data.
/// </summary>
public readonly record struct Palette(SpacePalette Space, bool EscapePod)
{
    /// <summary>The number of colours in each ink's pattern.</summary>
    public const int PatternLength = 4;

    private const uint Transparent = 0;
    private const uint Black = 0xFF000000;
    private const uint Red = 0xFF0000FF;
    private const uint Green = 0xFF00FF00;
    private const uint Yellow = 0xFF00FFFF;
    private const uint Blue = 0xFFFF0000;
    private const uint Magenta = 0xFFFF00FF;
    private const uint Cyan = 0xFFFFFF00;
    private const uint White = 0xFFFFFFFF;

    /// <summary>The patterns for every combination of palette and escape pod, built once.</summary>
    private static readonly Dictionary<Palette, uint[]> Cache = [];

    /// <summary>
    /// The colours of every ink, as packed RGBA8 (red in the low byte), with
    /// <see cref="PatternLength"/> colours per ink, in the order of <see cref="Ink"/>.
    /// </summary>
    public uint[] Patterns
    {
        get
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(this, out var patterns))
                {
                    patterns = Build();
                    Cache[this] = patterns;
                }

                return patterns;
            }
        }
    }

    private uint[] Build()
    {
        // The space view's three colours
        (uint yellow, uint red, uint cyan) = Space switch
        {
            SpacePalette.Chart => (Yellow, Red, White),
            SpacePalette.Title => (Yellow, White, Cyan),
            SpacePalette.Trade => (Yellow, Magenta, White),
            SpacePalette.Hyperspace => (Yellow, Blue, White),
            _ => (Yellow, Red, Cyan),
        };

        var patterns = new uint[Inks.Count * PatternLength];
        void Set(Ink ink, uint a, uint b, uint c, uint d)
        {
            int i = (int)ink * PatternLength;
            (patterns[i], patterns[i + 1], patterns[i + 2], patterns[i + 3]) = (a, b, c, d);
        }

        void Solid(Ink ink, uint colour) => Set(ink, colour, colour, colour, colour);

        Solid(Ink.None, Transparent);
        Solid(Ink.Black, Black);
        Solid(Ink.Yellow, yellow);
        Solid(Ink.Red, red);
        Solid(Ink.Cyan, cyan);
        Set(Ink.Green, cyan, yellow, cyan, yellow);
        Set(Ink.White, cyan, red, cyan, red);
        Set(Ink.SunStripes, red, yellow, red, yellow);
        Set(Ink.SunStripesShifted, yellow, red, yellow, red);
        Set(Ink.Moray, cyan, red, Transparent, yellow);

        // The dashboard's colours are the same on every screen, and its pixels
        // are twice as wide as the space view's, so its stripes are too
        Solid(Ink.DashboardRed, Red);
        Solid(Ink.DashboardGreen, Green);
        Solid(Ink.DashboardYellow, EscapePod ? White : Yellow);
        Solid(Ink.DashboardBlue, Blue);
        Solid(Ink.DashboardMagenta, Magenta);
        Solid(Ink.DashboardCyan, Cyan);
        Solid(Ink.DashboardWhite, White);
        Set(Ink.DashboardStripes, Magenta, Magenta, Red, Red);
        return patterns;
    }
}

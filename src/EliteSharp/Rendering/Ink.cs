namespace EliteSharp.Rendering;

/// <summary>
/// The colours that the game draws in, for both the HUD and the 3D world. An
/// ink is a colour in the game's terms (such as "the space view's red"); the
/// current <see cref="Palette"/> decides what it looks like, as the original
/// shows the same colours differently on different screens (the space view's
/// red is white on the title screen, for example).
///
/// Some inks are stripes of the palette's colours, one pixel of the original's
/// screen wide, as the original makes its extra colours by mixing pixels (its
/// green is cyan and yellow stripes, for example); these are described by
/// <see cref="Palette"/> too.
/// </summary>
public enum Ink : byte
{
    /// <summary>Nothing (transparent).</summary>
    None,

    /// <summary>Opaque black, for the solid surfaces in the 3D world that hide what is behind them.</summary>
    Black,

    // The space view's three colours, and the original's stripes of them

    /// <summary>The space view's yellow.</summary>
    Yellow,

    /// <summary>The space view's red (which is white on the title screen and magenta on the trading screens).</summary>
    Red,

    /// <summary>The space view's cyan (which is white on the charts and trading screens).</summary>
    Cyan,

    /// <summary>The original's green: cyan and yellow stripes.</summary>
    Green,

    /// <summary>The original's white: cyan and red stripes.</summary>
    White,

    /// <summary>Red and yellow stripes, as used for the sun.</summary>
    SunStripes,

    /// <summary>Yellow and red stripes (the sun's stripes, shifted by a pixel).</summary>
    SunStripesShifted,

    /// <summary>The Moray's colour: cyan, red, a gap and yellow.</summary>
    Moray,

    // The dashboard's colours

    /// <summary>The dashboard's red.</summary>
    DashboardRed,

    /// <summary>The dashboard's green.</summary>
    DashboardGreen,

    /// <summary>The dashboard's yellow (which turns white when an escape pod is fitted).</summary>
    DashboardYellow,

    /// <summary>The dashboard's blue.</summary>
    DashboardBlue,

    /// <summary>The dashboard's magenta.</summary>
    DashboardMagenta,

    /// <summary>The dashboard's cyan.</summary>
    DashboardCyan,

    /// <summary>The dashboard's white.</summary>
    DashboardWhite,

    /// <summary>Magenta and red stripes, as used for the shield and energy bars.</summary>
    DashboardStripes,
}

/// <summary>Facts about the inks.</summary>
public static class Inks
{
    /// <summary>The number of inks.</summary>
    public const int Count = (int)Ink.DashboardStripes + 1;
}

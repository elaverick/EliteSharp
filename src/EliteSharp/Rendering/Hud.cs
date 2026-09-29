using EliteSharp.Rendering.Scene;

namespace EliteSharp.Rendering;

/// <summary>A line in the HUD, between the centres of two of the original's pixels.</summary>
public readonly record struct ScreenLine(int X1, int Y1, int X2, int Y2, Ink Ink);

/// <summary>
/// A line in a space view that can be wider than the original's screen, in
/// pixels that carry on past the original's edges (so x can be less than 0 or
/// more than 255), with the margin (the number of pixels that fit beyond each
/// side) that it was drawn for.
/// </summary>
public readonly record struct WideLine(int X1, int Y1, int X2, int Y2, Ink Ink, float Margin);

/// <summary>A filled rectangle in the HUD, in the original's pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height, Ink Ink);

/// <summary>
/// Things in the HUD that the game replaces as a whole whenever it draws them
/// (see <see cref="Hud.Group"/>).
/// </summary>
public enum HudGroup
{
    /// <summary>The crosshairs on a chart that show the selected system.</summary>
    ChartSelection,

    /// <summary>The energy bomb's lightning bolt.</summary>
    BombBolt,
}

/// <summary>
/// The HUD: everything drawn in 2D over the space view (text, lines, the
/// charts, the crosshairs and so on), plus the dashboard, which the game
/// builds afresh for each frame. The game says what is in the HUD, and the
/// HUD keeps it until the game removes it or clears the screen; each frame,
/// <see cref="Present"/> hands the renderer a snapshot of it, along with the
/// 3D world.
///
/// The HUD is laid out in the original's pixels, 256 across and 248 down (the
/// space view is the top 192 rows, and the dashboard the rest), and text is on
/// the original's grid of 8 by 8 pixel character cells, which keeps the
/// original's design at any size.
/// </summary>
public sealed class Hud
{
    /// <summary>The width of the HUD in the original's pixels.</summary>
    public const int Width = 256;

    /// <summary>The height of the space view in the original's pixels.</summary>
    public const int SpaceViewHeight = 192;

    /// <summary>The total height of the HUD, including the dashboard.</summary>
    public const int Height = 248;

    /// <summary>The size of a character cell in pixels.</summary>
    private const int CellSize = 8;

    /// <summary>
    /// The lines of the space view's border (BOX): along the top, and two
    /// pixels wide down each side.
    /// </summary>
    private static readonly ScreenLine[] BorderLines =
    [
        new(0, 0, Width - 1, 0, Ink.Yellow),
        new(1, 0, 1, SpaceViewHeight - 1, Ink.Yellow),
        new(0, 0, 0, SpaceViewHeight - 1, Ink.Yellow),
        new(Width - 1, 0, Width - 1, SpaceViewHeight - 1, Ink.Yellow),
        new(Width - 2, 0, Width - 2, SpaceViewHeight - 1, Ink.Yellow),
    ];

    private readonly Dictionary<(int Column, int Row), (char Character, Ink Ink)> _text = [];
    private readonly List<ScreenLine> _lines = [];
    private readonly List<ScreenRect> _rects = [];
    private readonly List<WideLine> _wideLines = [];
    private readonly Dictionary<HudGroup, List<ScreenLine>> _groups = [];
    private readonly HudBuilder _builder = new();
    private readonly FrameExchange _exchange;

    /// <summary>The group that lines are being drawn into, if any (see <see cref="Group"/>).</summary>
    private List<ScreenLine>? _currentGroup;

    public Hud(FrameExchange exchange)
    {
        _exchange = exchange;
    }

    /// <summary>The palette for the space view (which the original changes for different screens).</summary>
    public SpacePalette Palette { get; set; }

    /// <summary>
    /// HFX: whether the hyperspace colour effect is on, which shows the space
    /// view in the <see cref="SpacePalette.Hyperspace"/> palette.
    /// </summary>
    public bool HyperspaceColours { get; set; }

    /// <summary>Whether the escape pod is fitted (which turns the dashboard's yellow white).</summary>
    public bool EscapePodFitted { get; set; }

    /// <summary>Whether the dashboard is shown.</summary>
    public bool DashboardVisible { get; set; } = true;

    /// <summary>Whether the space view's border (BOX) is shown.</summary>
    public bool Border { get; set; }

    /// <summary>
    /// The number of the original's pixels that fit into the space view
    /// beyond each side of the original's screen, when the window is wider
    /// than the original's 4:3 (set by the renderer).
    /// </summary>
    public float SideMargin
    {
        get => _sideMargin;
        set => _sideMargin = value;
    }

    private volatile float _sideMargin;

    /// <summary>Called when building each frame, to add the dashboard.</summary>
    public Action<HudBuilder>? DashboardRenderer { get; set; }

    /// <summary>Called when building each frame, to copy the 3D world into it.</summary>
    public Action<SceneFrame>? CopyWorld { get; set; }

    /// <summary>Clear the space view: all its text, lines and rectangles, and the border.</summary>
    public void ClearSpaceView()
    {
        _text.Clear();
        _lines.Clear();
        _rects.Clear();
        _wideLines.Clear();
        _groups.Clear();
        Border = false;
    }

    /// <summary>Show a character in a text cell, replacing whatever was there (a space leaves the cell as it is).</summary>
    public void Print(int column, int row, char character, Ink ink)
    {
        if (character != ' ')
        {
            _text[(column, row)] = (character, ink);
        }
    }

    /// <summary>Remove the character in a text cell.</summary>
    public void EraseCharacter(int column, int row) => _text.Remove((column, row));

    /// <summary>Remove a character from a text cell, if it's the one there.</summary>
    public void EraseCharacter(int column, int row, char character)
    {
        if (_text.TryGetValue((column, row), out var cell) && cell.Character == character)
        {
            _text.Remove((column, row));
        }
    }

    /// <summary>
    /// Clear a band of pixel rows across the space view (inside the border),
    /// removing any text, lines and rectangles that lie within it.
    /// </summary>
    public void ClearRows(int firstPixelRow, int lastPixelRow)
    {
        int firstText = firstPixelRow / CellSize;
        int lastText = lastPixelRow / CellSize;
        foreach (var key in _text.Keys.Where(k => k.Row >= firstText && k.Row <= lastText && k.Column >= 1 && k.Column <= 30).ToList())
        {
            _text.Remove(key);
        }

        bool Within(int y1, int y2) => Math.Min(y1, y2) >= firstPixelRow && Math.Max(y1, y2) <= lastPixelRow;
        _lines.RemoveAll(l => Within(l.Y1, l.Y2) && Math.Min(l.X1, l.X2) >= 2 && Math.Max(l.X1, l.X2) <= 253);
        _rects.RemoveAll(r => Within(r.Y, r.Y + r.Height - 1));
        _wideLines.RemoveAll(l => Within(l.Y1, l.Y2));
    }

    /// <summary>Show a line (or, inside <see cref="Group"/>, add it to the group).</summary>
    public void DrawLine(int x1, int y1, int x2, int y2, Ink ink)
    {
        var line = new ScreenLine(x1, y1, x2, y2, ink);
        var lines = _currentGroup ?? _lines;
        if (!lines.Contains(line) && !lines.Contains(new ScreenLine(x2, y2, x1, y1, ink)))
        {
            lines.Add(line);
        }
    }

    /// <summary>Show a filled rectangle.</summary>
    public void DrawRect(int x, int y, int width, int height, Ink ink)
    {
        var rect = new ScreenRect(x, y, width, height, ink);
        if (!_rects.Contains(rect))
        {
            _rects.Add(rect);
        }
    }

    /// <summary>
    /// Show a line that can extend beyond the sides of the original's screen,
    /// by up to the given margin (from <see cref="SideMargin"/>). The line
    /// stays the same size, and the space view gets wider around it.
    /// </summary>
    public void DrawWideLine(int x1, int y1, int x2, int y2, Ink ink, float margin)
    {
        if (FindWideLine(x1, y1, x2, y2, ink) < 0)
        {
            _wideLines.Add(new WideLine(x1, y1, x2, y2, ink, margin));
        }
    }

    /// <summary>Remove a line drawn with <see cref="DrawWideLine"/>.</summary>
    public void EraseWideLine(int x1, int y1, int x2, int y2, Ink ink)
    {
        int index = FindWideLine(x1, y1, x2, y2, ink);
        if (index >= 0)
        {
            _wideLines.RemoveAt(index);
        }
    }

    private int FindWideLine(int x1, int y1, int x2, int y2, Ink ink) =>
        _wideLines.FindIndex(l => l.Ink == ink &&
            ((l.X1, l.Y1, l.X2, l.Y2) == (x1, y1, x2, y2) || (l.X1, l.Y1, l.X2, l.Y2) == (x2, y2, x1, y1)));

    /// <summary>
    /// Replace a group of lines: the group is emptied, and the lines drawn
    /// until the returned scope is disposed make up its new contents. The game
    /// uses this for things that it redraws in a new place (such as the
    /// crosshairs on the charts), so it just draws them where they are now.
    /// </summary>
    public GroupScope Group(HudGroup group)
    {
        if (!_groups.TryGetValue(group, out var lines))
        {
            lines = [];
            _groups[group] = lines;
        }

        lines.Clear();
        _currentGroup = lines;
        return new GroupScope(this);
    }

    /// <summary>Remove a group of lines.</summary>
    public void ClearGroup(HudGroup group) => _groups.Remove(group);

    /// <summary>The scope of a <see cref="Group"/>: lines go back to the space view when it ends.</summary>
    public readonly struct GroupScope(Hud hud) : IDisposable
    {
        public void Dispose() => hud._currentGroup = null;
    }

    /// <summary>
    /// Convert an x-coordinate in a space view that is widened by the given
    /// margin into the logical coordinates of the wide lines, where 0 to 256
    /// span the whole widened view (see <see cref="FrameData.Lines"/>).
    /// </summary>
    private static float ToWide(float x, float margin) => (x + 0.5f + margin) * Width / (Width + 2 * margin) - 0.5f;

    /// <summary>Build the current frame and hand it to the renderer.</summary>
    public void Present()
    {
        var frame = _exchange.Writing;
        frame.Clear();
        var builder = _builder;
        builder.Begin(frame);

        foreach (var rect in _rects)
        {
            builder.Rect(rect.X, rect.Y, rect.Width, rect.Height, rect.Ink);
        }

        foreach (var ((column, row), (character, ink)) in _text)
        {
            builder.Character(character, column * CellSize, row * CellSize, ink);
        }

        foreach (var line in _lines.Concat(_groups.Values.SelectMany(g => g)))
        {
            builder.Line(line.X1, line.Y1, line.X2, line.Y2, line.Ink);
        }

        if (Border)
        {
            // The border's sides move out to the edges of a widened space view,
            // keeping their shape (rather than being stretched apart)
            float margin = SideMargin;
            float Widen(int x) => ToWide(x < Width / 2 ? x - margin : x + margin, margin);
            foreach (var line in BorderLines)
            {
                builder.WideLine(Widen(line.X1), line.Y1, Widen(line.X2), line.Y2, line.Ink);
            }
        }

        foreach (var line in _wideLines)
        {
            builder.WideLine(ToWide(line.X1, line.Margin), line.Y1, ToWide(line.X2, line.Margin), line.Y2, line.Ink);
        }

        if (DashboardVisible)
        {
            builder.Layer = HudLayer.Dashboard;
            DashboardRenderer?.Invoke(builder);
        }

        if (CopyWorld != null)
        {
            CopyWorld(frame.World);
            frame.HasWorld = true;
        }

        frame.Palette = new Palette(HyperspaceColours ? SpacePalette.Hyperspace : Palette, EscapePodFitted);
        frame.DashboardVisible = DashboardVisible;
        _exchange.Publish();
    }
}

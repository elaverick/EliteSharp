using EliteSharp.Data;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Rendering;

/// <summary>
/// A 2D line in logical screen pixels with a BBC colour byte.
/// </summary>
public readonly record struct ScreenLine(int X1, int Y1, int X2, int Y2, int Colour);

/// <summary>
/// A line in a space view that can be wider than the original's screen, in
/// logical screen pixels that carry on past the original's edges (so x can
/// be less than 0 or more than 255), with the margin (the number of pixels
/// that fit beyond each side) that it was drawn for.
/// </summary>
public readonly record struct WideLine(int X1, int Y1, int X2, int Y2, int Colour, float Margin);

/// <summary>
/// A filled rectangle in logical screen pixels with a BBC colour byte.
/// </summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height, int Colour);

/// <summary>
/// The model of the BBC's screen for the space view part of the display (the
/// top 192 pixel rows). The original draws everything into screen memory using
/// EOR logic, so drawing something twice removes it; here that behaviour is
/// modelled with toggling primitives for the static parts of the display (text,
/// boxes, charts and crosshairs). Moving objects such as ships are in the 3D
/// world instead, which is added to each frame.
/// </summary>
public sealed class Screen
{
    /// <summary>The width of the screen in logical pixels.</summary>
    public const int Width = 256;

    /// <summary>The height of the space view in logical pixels.</summary>
    public const int SpaceViewHeight = 192;

    /// <summary>The total height of the screen, including the dashboard.</summary>
    public const int Height = 248;

    /// <summary>The colour of the space view's border (yellow, in the mode 1 palette).</summary>
    private const int BorderColour = 0b00001111;

    /// <summary>
    /// The lines of the space view's border (BOX): along the top, and two
    /// pixels wide down each side.
    /// </summary>
    private static readonly ScreenLine[] BorderLines =
    [
        new(0, 0, Width - 1, 0, BorderColour),
        new(1, 0, 1, SpaceViewHeight - 1, BorderColour),
        new(0, 0, 0, SpaceViewHeight - 1, BorderColour),
        new(Width - 1, 0, Width - 1, SpaceViewHeight - 1, BorderColour),
        new(Width - 2, 0, Width - 2, SpaceViewHeight - 1, BorderColour),
    ];

    /// <summary>Whether the space view's border is shown.</summary>
    private bool _border;
    private readonly List<ScreenLine> _canvasLines = [];
    private readonly List<WideLine> _wideLines = [];
    private readonly List<ScreenRect> _canvasRects = [];
    private readonly Dictionary<(int Col, int Row), List<(char Char, int Colour)>> _text = [];
    private readonly FrameBuilder _builder = new();
    private readonly FrameExchange _exchange;

    public Screen(FrameExchange exchange)
    {
        _exchange = exchange;
    }

    /// <summary>
    /// The offset into the TVT3 palette table for the space view palette (0 =
    /// space view, 16 = charts, 32 = title screen, 48 = trading screens).
    /// </summary>
    public int PaletteOffset { get; set; }

    /// <summary>HFX: the hyperspace colour effect.</summary>
    public bool HyperspaceColours { get; set; }

    /// <summary>The number of visible text rows (24 hides the dashboard, 31 shows it).</summary>
    public bool DashboardVisible { get; set; } = true;

    /// <summary>Whether the escape pod is fitted (which changes dashboard colour 3 to white).</summary>
    public bool EscapePodFitted { get; set; }

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
    public Action<FrameBuilder>? DashboardRenderer { get; set; }

    /// <summary>Called when building each frame, to take a snapshot of the 3D world.</summary>
    public Func<SceneFrame>? WorldSnapshot { get; set; }

    /// <summary>Clear the space view: all text, lines and ship outlines.</summary>
    public void ClearSpaceView()
    {
        _canvasLines.Clear();
        _wideLines.Clear();
        _canvasRects.Clear();
        _text.Clear();
        _border = false;
    }

    /// <summary>
    /// Draw the border around the space view. The original draws it with EOR
    /// logic, so drawing it a second time removes it (as the death screen does).
    /// </summary>
    public void ToggleBorder() => _border = !_border;

    /// <summary>
    /// Draw a line on the canvas. If toggle is set and an identical line is
    /// already there, it is removed instead (as with EOR drawing).
    /// </summary>
    public void DrawLine(int x1, int y1, int x2, int y2, int colour, bool toggle = true)
    {
        var line = new ScreenLine(x1, y1, x2, y2, colour);
        if (toggle)
        {
            int index = _canvasLines.IndexOf(line);
            if (index < 0)
            {
                index = _canvasLines.IndexOf(new ScreenLine(x2, y2, x1, y1, colour));
            }

            if (index >= 0)
            {
                _canvasLines.RemoveAt(index);
                return;
            }
        }

        _canvasLines.Add(line);
    }

    /// <summary>
    /// Draw a line that can extend beyond the sides of the original's screen,
    /// by up to the given margin (from <see cref="SideMargin"/>). The line
    /// stays the same size, and the space view gets wider around it. As with
    /// EOR drawing, drawing the same line again removes it.
    /// </summary>
    public void DrawWideLine(int x1, int y1, int x2, int y2, int colour, float margin)
    {
        int index = _wideLines.FindIndex(l => l.Colour == colour &&
            ((l.X1, l.Y1, l.X2, l.Y2) == (x1, y1, x2, y2) || (l.X1, l.Y1, l.X2, l.Y2) == (x2, y2, x1, y1)));
        if (index >= 0)
        {
            _wideLines.RemoveAt(index);
            return;
        }

        _wideLines.Add(new WideLine(x1, y1, x2, y2, colour, margin));
    }

    /// <summary>
    /// Convert an x-coordinate in a space view that is widened by the given
    /// margin into the logical coordinates of the wide lines, where 0 to 255
    /// span the whole widened view (see <see cref="FrameData.WideLines"/>).
    /// </summary>
    private static float ToWide(float x, float margin) => (x + 0.5f + margin) * Width / (Width + 2 * margin) - 0.5f;

    /// <summary>Draw a filled rectangle on the canvas, toggling as with EOR drawing.</summary>
    public void DrawRect(int x, int y, int width, int height, int colour, bool toggle = true)
    {
        var rect = new ScreenRect(x, y, width, height, colour);
        if (toggle)
        {
            int index = _canvasRects.IndexOf(rect);
            if (index >= 0)
            {
                _canvasRects.RemoveAt(index);
                return;
            }
        }

        _canvasRects.Add(rect);
    }

    /// <summary>
    /// Print a character at a text cell. Printing the same character in the same
    /// colour in the same cell a second time removes it, as with EOR printing.
    /// </summary>
    public void PrintCharacter(int column, int row, char character, int colour)
    {
        if (character == ' ')
        {
            return;
        }

        var key = (column, row);
        if (!_text.TryGetValue(key, out var glyphs))
        {
            glyphs = [];
            _text[key] = glyphs;
        }

        int index = glyphs.IndexOf((character, colour));
        if (index >= 0)
        {
            glyphs.RemoveAt(index);
        }
        else
        {
            glyphs.Add((character, colour));
        }
    }

    /// <summary>Erase a text cell (as the delete character does by zero-filling it).</summary>
    public void EraseCharacter(int column, int row) => _text.Remove((column, row));

    /// <summary>
    /// Clear a band of pixel rows across the space view (excluding the border),
    /// removing any text and canvas primitives that lie within it.
    /// </summary>
    public void ClearRows(int firstPixelRow, int lastPixelRow)
    {
        int firstText = firstPixelRow / 8;
        int lastText = lastPixelRow / 8;
        foreach (var key in _text.Keys.Where(k => k.Row >= firstText && k.Row <= lastText && k.Col >= 1 && k.Col <= 30).ToList())
        {
            _text.Remove(key);
        }

        _canvasLines.RemoveAll(l => Math.Min(l.Y1, l.Y2) >= firstPixelRow && Math.Max(l.Y1, l.Y2) <= lastPixelRow
                                    && Math.Min(l.X1, l.X2) >= 2 && Math.Max(l.X1, l.X2) <= 253);
        _canvasRects.RemoveAll(r => r.Y >= firstPixelRow && r.Y + r.Height - 1 <= lastPixelRow);
        _wideLines.RemoveAll(l => Math.Min(l.Y1, l.Y2) >= firstPixelRow && Math.Max(l.Y1, l.Y2) <= lastPixelRow);
    }


    /// <summary>Build the current frame and hand it to the renderer.</summary>
    public void Present()
    {
        _builder.Clear();

        foreach (var rect in _canvasRects)
        {
            _builder.Rect(rect.X, rect.Y, rect.Width, rect.Height, rect.Colour);
        }

        foreach (var line in _canvasLines)
        {
            _builder.Line(line.X1, line.Y1, line.X2, line.Y2, line.Colour);
        }

        if (_border)
        {
            // The border's sides move out to the edges of a widened space view,
            // keeping their shape (rather than being stretched apart)
            float margin = SideMargin;
            float Widen(int x) => ToWide(x < Width / 2 ? x - margin : x + margin, margin);
            foreach (var line in BorderLines)
            {
                _builder.WideLine(Widen(line.X1), line.Y1, Widen(line.X2), line.Y2, line.Colour);
            }
        }

        foreach (var line in _wideLines)
        {
            _builder.WideLine(ToWide(line.X1, line.Margin), line.Y1, ToWide(line.X2, line.Margin), line.Y2, line.Colour);
        }

        foreach (var ((column, row), glyphs) in _text)
        {
            foreach (var (character, colour) in glyphs)
            {
                AddGlyph(column * 8, row * 8, character, colour);
            }
        }

        if (DashboardVisible)
        {
            DashboardRenderer?.Invoke(_builder);
        }

        _exchange.Publish(_builder.Build(WorldSnapshot?.Invoke(), BuildSpacePalette(), BuildDashboardPalette(), HyperspaceColours, DashboardVisible));
    }

    /// <summary>Add a character from the MOS font, merging each row of pixels into runs.</summary>
    private void AddGlyph(int x, int y, char character, int colour)
    {
        int code = character;
        if (code < 32 || code > 127)
        {
            return;
        }

        int offset = (code - 32) * 8;
        for (int row = 0; row < 8; row++)
        {
            int bits = GameData.Font[offset + row];
            int column = 0;
            while (column < 8)
            {
                if ((bits & (0x80 >> column)) == 0)
                {
                    column++;
                    continue;
                }

                int start = column;
                while (column < 8 && (bits & (0x80 >> column)) != 0)
                {
                    column++;
                }

                _builder.Rect(x + start, y + row, column - start, 1, colour);
            }
        }
    }

    /// <summary>
    /// Decode the TVT3 palette block at the current offset into the sixteen
    /// physical colours (0-7) of the video ULA palette. Each palette byte maps a
    /// ULA index (bits 4-7) to a physical colour EOR 7 (bits 0-3). The shader
    /// forms the ULA index from the screen byte just as the ULA does, so the
    /// whole 16-entry palette is needed (it matters for the hyperspace effect).
    /// </summary>
    private int[] BuildSpacePalette()
    {
        // IRQ1 copies bytes #15 to #1 of the palette block (byte #0 is never
        // copied), so ULA index 0 keeps its default mapping to black
        var physical = new int[16];
        for (int i = 1; i < 16; i++)
        {
            int value = GameData.SpaceViewPalettes[(PaletteOffset + i) & 63];
            physical[(value >> 4) & 15] = (value & 7) ^ 7;
        }

        return physical;
    }

    /// <summary>Decode the TVT1 palette into physical colours for mode 2 logical colours 0-15.</summary>
    private int[] BuildDashboardPalette()
    {
        var physical = new int[16];
        for (int i = 0; i < 16; i++)
        {
            int value = GameData.DashboardPalette[i];
            if (i == 0)
            {
                // IRQ1 changes the first entry to map colour 3 to white if we
                // have an escape pod fitted
                value = EscapePodFitted ? 0x30 : 0x34;
            }

            physical[(value >> 4) & 15] = (value & 7) ^ 7;
        }

        return physical;
    }
}

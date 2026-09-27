using EliteSharp.Data;

namespace EliteSharp.Rendering;

/// <summary>
/// A 2D line in logical screen pixels with a BBC colour byte.
/// </summary>
public readonly record struct ScreenLine(int X1, int Y1, int X2, int Y2, int Colour);

/// <summary>
/// A filled rectangle in logical screen pixels with a BBC colour byte.
/// </summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height, int Colour);

/// <summary>
/// A 3D line in space relative to our ship.
/// </summary>
public readonly record struct SpaceLine(float X1, float Y1, float Z1, float X2, float Y2, float Z2, int Colour);

/// <summary>
/// The current on-screen image of a moving object (a ship, the planet, the sun,
/// the stardust, laser beams and so on). The original erases these images by
/// redrawing them with EOR logic before drawing them again; here, setting a new
/// image simply replaces the old one.
/// </summary>
public sealed class ObjectImage
{
    public readonly List<ScreenLine> Lines = [];
    public readonly List<SpaceLine> SpaceLines = [];
    public readonly List<ScreenRect> Rects = [];

    public bool IsEmpty => Lines.Count == 0 && SpaceLines.Count == 0 && Rects.Count == 0;
}

/// <summary>
/// The model of the BBC's screen for the space view part of the display (the
/// top 192 pixel rows). The original draws everything into screen memory using
/// EOR logic, so drawing something twice removes it; here that behaviour is
/// modelled with toggling primitives for the static parts of the display (text,
/// boxes, charts and crosshairs), while moving objects such as ships have their
/// images replaced each time they are redrawn.
/// </summary>
public sealed class Screen
{
    /// <summary>The width of the screen in logical pixels.</summary>
    public const int Width = 256;

    /// <summary>The height of the space view in logical pixels.</summary>
    public const int SpaceViewHeight = 192;

    /// <summary>The total height of the screen, including the dashboard.</summary>
    public const int Height = 248;

    private readonly List<ScreenLine> _canvasLines = [];
    private readonly List<ScreenRect> _canvasRects = [];
    private readonly Dictionary<(int Col, int Row), List<(char Char, int Colour)>> _text = [];
    private readonly Dictionary<object, ObjectImage> _images = [];
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

    /// <summary>Called when building each frame, to add the dashboard.</summary>
    public Action<FrameBuilder>? DashboardRenderer { get; set; }

    /// <summary>Clear the space view: all text, lines and object images.</summary>
    public void ClearSpaceView()
    {
        _canvasLines.Clear();
        _canvasRects.Clear();
        _text.Clear();
        _images.Clear();
    }

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
    }

    /// <summary>Set the on-screen image of a moving object, replacing any previous image.</summary>
    public void SetImage(object owner, ObjectImage image)
    {
        if (image.IsEmpty)
        {
            _images.Remove(owner);
        }
        else
        {
            _images[owner] = image;
        }
    }

    /// <summary>Remove the on-screen image of a moving object.</summary>
    public void RemoveImage(object owner) => _images.Remove(owner);

    public bool HasImage(object owner) => _images.ContainsKey(owner);

    public ObjectImage? GetImage(object owner) => _images.GetValueOrDefault(owner);

    /// <summary>
    /// Work out which pixels in the space view have something drawn in them
    /// (used by the hangar, whose lines stop when they hit the ships).
    /// </summary>
    public bool[,] RasterizeSpaceView()
    {
        var pixels = new bool[Width, SpaceViewHeight];

        void Plot(int x, int y)
        {
            if (x >= 0 && x < Width && y >= 0 && y < SpaceViewHeight)
            {
                pixels[x, y] = true;
            }
        }

        void Line(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double steps = Math.Min(Math.Max(Math.Abs(dx), Math.Abs(dy)), 4096);
            int count = (int)Math.Ceiling(steps);
            for (int i = 0; i <= count; i++)
            {
                double t = count == 0 ? 0 : (double)i / count;
                Plot((int)Math.Round(x1 + dx * t), (int)Math.Round(y1 + dy * t));
            }
        }

        void Rect(ScreenRect rect)
        {
            for (int y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                for (int x = rect.X; x < rect.X + rect.Width; x++)
                {
                    Plot(x, y);
                }
            }
        }

        foreach (var line in _canvasLines)
        {
            Line(line.X1, line.Y1, line.X2, line.Y2);
        }

        foreach (var rect in _canvasRects)
        {
            Rect(rect);
        }

        foreach (var ((column, row), glyphs) in _text)
        {
            foreach (var (character, _) in glyphs)
            {
                int offset = (character - 32) * 8;
                if (offset < 0 || offset + 8 > GameData.Font.Length)
                {
                    continue;
                }

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        if ((GameData.Font[offset + y] & (0x80 >> x)) != 0)
                        {
                            Plot(column * 8 + x, row * 8 + y);
                        }
                    }
                }
            }
        }

        foreach (var image in _images.Values)
        {
            foreach (var line in image.Lines)
            {
                Line(line.X1, line.Y1, line.X2, line.Y2);
            }

            foreach (var rect in image.Rects)
            {
                Rect(rect);
            }

            foreach (var line in image.SpaceLines)
            {
                Line(128 + 256 * line.X1 / line.Z1, 96 - 256 * line.Y1 / line.Z1,
                     128 + 256 * line.X2 / line.Z2, 96 - 256 * line.Y2 / line.Z2);
            }
        }

        return pixels;
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

        foreach (var image in _images.Values)
        {
            foreach (var rect in image.Rects)
            {
                _builder.Rect(rect.X, rect.Y, rect.Width, rect.Height, rect.Colour);
            }

            foreach (var line in image.Lines)
            {
                _builder.Line(line.X1, line.Y1, line.X2, line.Y2, line.Colour);
            }

            foreach (var line in image.SpaceLines)
            {
                _builder.Line3D(line.X1, line.Y1, line.Z1, line.X2, line.Y2, line.Z2, line.Colour);
            }
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

        _exchange.Publish(_builder.Build(BuildSpacePalette(), BuildDashboardPalette(), HyperspaceColours, DashboardVisible));
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

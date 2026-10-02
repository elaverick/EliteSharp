using System.Numerics;
using System.Runtime.InteropServices;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Rendering;

/// <summary>
/// A rectangle of the HUD, drawn from a region of the <see cref="HudAtlas"/>:
/// a character, an image, or (from the atlas's solid texel) a filled
/// rectangle. The GPU draws each one as an instance of a quad.
///
/// Positions are in the HUD's layout units, which are the original's pixels
/// (the display is 256 by 248 of them, with the origin at the top-left), so
/// the HUD keeps the original's proportions at any size.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct HudQuad(Vector4 rectangle, Vector4 source, Ink ink)
{
    /// <summary>The rectangle on the screen: x, y, width and height.</summary>
    public Vector4 Rectangle = rectangle;

    /// <summary>The region of the atlas: x, y, width and height in texels.</summary>
    public Vector4 Source = source;

    /// <summary>The ink for the atlas's <see cref="HudAtlas.QuadInk"/> texels.</summary>
    public uint Ink = (uint)ink;
}

/// <summary>A line of the HUD, from the centre of one pixel to the centre of another. The GPU draws each one as an instance.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct HudLine(Vector4 ends, Ink ink)
{
    /// <summary>The ends of the line: x1, y1, x2 and y2.</summary>
    public Vector4 Ends = ends;

    public uint Ink = (uint)ink;
}

/// <summary>
/// A complete frame to be drawn: the 3D world, the HUD, and the palette that
/// both are drawn with. The game fills it in, and the renderer draws it (see
/// <see cref="FrameExchange"/>). Frames are reused, so everything in one is
/// cleared and refilled rather than replaced.
///
/// The HUD is in three parts, each drawn with its own clipping: the space
/// view (the top 192 rows, over the 3D world), the lines that span the full
/// width of a widened space view (the border and the tunnels), and the
/// dashboard (the bottom 56 rows), along with the rectangles that span the
/// full width of the window beside it (the bottom of the border).
/// </summary>
public sealed class FrameData
{
    /// <summary>The 3D world (if <see cref="HasWorld"/> is set).</summary>
    public SceneFrame World { get; } = new();

    /// <summary>Whether the frame has a 3D world.</summary>
    public bool HasWorld { get; set; }

    /// <summary>The space view's rectangles.</summary>
    public List<HudQuad> SpaceQuads { get; } = new(1024);

    /// <summary>The dashboard's rectangles.</summary>
    public List<HudQuad> DashboardQuads { get; } = new(256);

    /// <summary>The space view's lines.</summary>
    public List<HudLine> SpaceLines { get; } = new(1024);

    /// <summary>
    /// The lines that span the full width of the space view, whose logical
    /// x-coordinates 0 and 256 are at the edges of a widened space view (so the
    /// renderer can stretch them to the edges of a window that is wider than
    /// the HUD).
    /// </summary>
    public List<HudLine> WideLines { get; } = new(256);

    /// <summary>
    /// The rectangles that span the full width of the window in the
    /// dashboard's rows (in the same coordinates as <see cref="WideLines"/>),
    /// which are drawn behind the dashboard.
    /// </summary>
    public List<HudQuad> DashboardWideQuads { get; } = new(4);

    /// <summary>What the inks look like.</summary>
    public Palette Palette { get; set; }

    /// <summary>Whether the dashboard is shown (it is hidden on the death screen).</summary>
    public bool DashboardVisible { get; set; } = true;

    public void Clear()
    {
        World.Clear();
        HasWorld = false;
        SpaceQuads.Clear();
        DashboardQuads.Clear();
        SpaceLines.Clear();
        WideLines.Clear();
        DashboardWideQuads.Clear();
    }
}

/// <summary>Which part of the HUD a <see cref="HudBuilder"/> is adding to.</summary>
public enum HudLayer
{
    /// <summary>The space view, clipped to its 192 rows.</summary>
    SpaceView,

    /// <summary>The dashboard.</summary>
    Dashboard,
}

/// <summary>Adds the HUD's rectangles and lines to a frame.</summary>
public sealed class HudBuilder
{
    private FrameData _frame = null!;

    /// <summary>Start adding to a frame (which should have been cleared).</summary>
    public void Begin(FrameData frame)
    {
        _frame = frame;
        Layer = HudLayer.SpaceView;
    }

    /// <summary>The part of the HUD that rectangles and images are added to.</summary>
    public HudLayer Layer { get; set; }

    private List<HudQuad> Quads => Layer == HudLayer.Dashboard ? _frame.DashboardQuads : _frame.SpaceQuads;

    /// <summary>Add a filled rectangle.</summary>
    public void Rect(float x, float y, float width, float height, Ink ink) =>
        Quads.Add(new HudQuad(new Vector4(x, y, width, height), Source(HudAtlas.Solid), ink));

    /// <summary>Add an image from the atlas, with its top-left at (x, y), one texel per pixel.</summary>
    public void Image(AtlasRegion region, float x, float y) =>
        Quads.Add(new HudQuad(new Vector4(x, y, region.Width, region.Height), Source(region), Ink.None));

    /// <summary>Add a character, with its top-left at (x, y).</summary>
    public void Character(char character, float x, float y, Ink ink)
    {
        if (HudAtlas.Glyph(character) is { } glyph)
        {
            Quads.Add(new HudQuad(new Vector4(x, y, glyph.Width, glyph.Height), Source(glyph), ink));
        }
    }

    /// <summary>Add a line between the centres of two of the space view's pixels.</summary>
    public void Line(float x1, float y1, float x2, float y2, Ink ink) =>
        _frame.SpaceLines.Add(new HudLine(new Vector4(x1 + 0.5f, y1 + 0.5f, x2 + 0.5f, y2 + 0.5f), ink));

    /// <summary>Add a line that spans the full width of the space view (see <see cref="FrameData.WideLines"/>).</summary>
    public void WideLine(float x1, float y1, float x2, float y2, Ink ink) =>
        _frame.WideLines.Add(new HudLine(new Vector4(x1 + 0.5f, y1 + 0.5f, x2 + 0.5f, y2 + 0.5f), ink));

    /// <summary>
    /// Add a filled rectangle that spans the full width of the window beside
    /// the dashboard (see <see cref="FrameData.DashboardWideQuads"/>).
    /// </summary>
    public void WideRect(float x, float y, float width, float height, Ink ink) =>
        _frame.DashboardWideQuads.Add(new HudQuad(new Vector4(x, y, width, height), Source(HudAtlas.Solid), ink));

    private static Vector4 Source(AtlasRegion region) => new(region.X, region.Y, region.Width, region.Height);
}

/// <summary>
/// Hands frames from the game thread to the render thread, with three frames
/// that take turns: the game fills one in and publishes it, the renderer draws
/// the most recently published one (and keeps it until it takes the next), and
/// the third holds the latest published frame until the renderer takes it. No
/// frame is written while it is being read, and the frames' storage is reused.
/// </summary>
public sealed class FrameExchange
{
    private readonly Lock _lock = new();
    private FrameData _writing = new();
    private FrameData _published = new();
    private FrameData _reading = new();
    private bool _hasPublished;
    private bool _hasRead;

    /// <summary>The frame for the game to fill in (it belongs to the game thread until it's published).</summary>
    public FrameData Writing => _writing;

    /// <summary>Publish the frame that the game has filled in, and give the game another to fill in.</summary>
    public void Publish()
    {
        lock (_lock)
        {
            (_writing, _published) = (_published, _writing);
            _hasPublished = true;
        }
    }

    /// <summary>
    /// The most recently published frame, for the renderer, which can use it
    /// until it calls this again (or null if no frame has been published yet).
    /// </summary>
    public FrameData? TakeLatest()
    {
        lock (_lock)
        {
            if (_hasPublished)
            {
                (_reading, _published) = (_published, _reading);
                _hasPublished = false;
                _hasRead = true;
            }

            return _hasRead ? _reading : null;
        }
    }
}

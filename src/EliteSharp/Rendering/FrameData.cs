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
/// both are drawn with. Built on the game thread, drawn by the renderer.
///
/// The HUD is in three parts, each drawn with its own clipping: the space
/// view (the top 192 rows, over the 3D world), the lines that span the full
/// width of a widened space view (the border and the tunnels), and the
/// dashboard (the bottom 56 rows).
/// </summary>
public sealed class FrameData
{
    /// <summary>The 3D world, or null if there isn't one.</summary>
    public SceneFrame? World;

    /// <summary>The HUD's rectangles: the space view's, then the dashboard's.</summary>
    public HudQuad[] Quads = [];

    public int SpaceQuadCount;

    public int DashboardQuadCount;

    /// <summary>
    /// The HUD's lines: the space view's, then the wide lines, whose logical
    /// x-coordinates 0 and 256 are at the edges of a widened space view (so the
    /// renderer can stretch them to the edges of a window that is wider than
    /// the HUD).
    /// </summary>
    public HudLine[] Lines = [];

    public int SpaceLineCount;

    public int WideLineCount;

    /// <summary>What the inks look like.</summary>
    public Palette Palette;

    /// <summary>Whether the dashboard is shown (it is hidden on the death screen).</summary>
    public bool DashboardVisible = true;
}

/// <summary>Which part of the HUD a <see cref="HudBuilder"/> is adding to.</summary>
public enum HudLayer
{
    /// <summary>The space view, clipped to its 192 rows.</summary>
    SpaceView,

    /// <summary>The dashboard.</summary>
    Dashboard,
}

/// <summary>Collects the HUD's rectangles and lines for a frame.</summary>
public sealed class HudBuilder
{
    private readonly List<HudQuad> _spaceQuads = new(1024);
    private readonly List<HudQuad> _dashboardQuads = new(256);
    private readonly List<HudLine> _spaceLines = new(1024);
    private readonly List<HudLine> _wideLines = new(256);

    /// <summary>The part of the HUD that rectangles and images are added to.</summary>
    public HudLayer Layer { get; set; }

    private List<HudQuad> Quads => Layer == HudLayer.Dashboard ? _dashboardQuads : _spaceQuads;

    public void Clear()
    {
        _spaceQuads.Clear();
        _dashboardQuads.Clear();
        _spaceLines.Clear();
        _wideLines.Clear();
        Layer = HudLayer.SpaceView;
    }

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
        _spaceLines.Add(new HudLine(new Vector4(x1 + 0.5f, y1 + 0.5f, x2 + 0.5f, y2 + 0.5f), ink));

    /// <summary>Add a line that spans the full width of the space view (see <see cref="FrameData.Lines"/>).</summary>
    public void WideLine(float x1, float y1, float x2, float y2, Ink ink) =>
        _wideLines.Add(new HudLine(new Vector4(x1 + 0.5f, y1 + 0.5f, x2 + 0.5f, y2 + 0.5f), ink));

    private static Vector4 Source(AtlasRegion region) => new(region.X, region.Y, region.Width, region.Height);

    public FrameData Build(SceneFrame? world, Palette palette, bool dashboardVisible) => new()
    {
        World = world,
        Quads = [.. _spaceQuads, .. _dashboardQuads],
        SpaceQuadCount = _spaceQuads.Count,
        DashboardQuadCount = _dashboardQuads.Count,
        Lines = [.. _spaceLines, .. _wideLines],
        SpaceLineCount = _spaceLines.Count,
        WideLineCount = _wideLines.Count,
        Palette = palette,
        DashboardVisible = dashboardVisible,
    };
}

/// <summary>
/// Hands completed frames from the game thread to the render thread. The render
/// thread always draws the most recently published frame.
/// </summary>
public sealed class FrameExchange
{
    private FrameData? _latest;

    public void Publish(FrameData frame) => Volatile.Write(ref _latest, frame);

    public FrameData? Latest => Volatile.Read(ref _latest);
}

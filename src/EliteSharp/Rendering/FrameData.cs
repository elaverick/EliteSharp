using System.Runtime.InteropServices;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Rendering;

/// <summary>
/// A vertex of the 2D display as sent to the GPU, in logical BBC screen pixels
/// (256 x 248, origin top-left).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Vertex(float x, float y, uint colour, uint flags)
{
    public float X = x;
    public float Y = y;

    /// <summary>The BBC colour byte (a mode 1 or mode 2 pixel pattern).</summary>
    public uint Colour = colour;

    /// <summary>See <see cref="VertexFlags"/>.</summary>
    public uint Flags = flags;

    public static readonly uint SizeInBytes = (uint)Marshal.SizeOf<Vertex>();
}

public static class VertexFlags
{
    /// <summary>Decode the colour byte as a mode 2 (dashboard) pattern rather than mode 1.</summary>
    public const uint Mode2 = 1;

    /// <summary>The primitive belongs to the dashboard rather than the space view (for clipping).</summary>
    public const uint Dashboard = 4;
}

/// <summary>
/// A complete frame to be drawn: the 3D world, and the 2D display as vertices for the triangle list and the
/// line list, plus the palette state. Built on the game thread, drawn by the
/// renderer.
/// </summary>
public sealed class FrameData
{
    /// <summary>The 3D world, or null if there isn't one.</summary>
    public SceneFrame? World;

    public Vertex[] Triangles = [];
    public int TriangleVertexCount;

    public Vertex[] Lines = [];
    public int LineVertexCount;

    /// <summary>
    /// The lines that span the full width of the space view (a line list): the
    /// space view's border, and the hangar. These are kept separate so the
    /// renderer can stretch them to the edges of a window that is wider than
    /// the 2D display, where logical x-coordinates 0 and 255 are at the left
    /// and right edges of the window.
    /// </summary>
    public Vertex[] WideLines = [];
    public int WideLineVertexCount;

    /// <summary>The sixteen physical colours (0-7) of the ULA palette for the space view.</summary>
    public int[] SpacePalette = new int[16];

    /// <summary>The sixteen physical colours (0-7) for mode 2 logical colours 0-15.</summary>
    public int[] DashboardPalette = new int[16];

    /// <summary>The hyperspace colour effect (the space view is decoded as mode 2).</summary>
    public bool HyperspaceColours;

    /// <summary>Whether the dashboard is visible (it is hidden on the death screen).</summary>
    public bool DashboardVisible = true;
}

/// <summary>
/// Accumulates primitives for a frame.
/// </summary>
public sealed class FrameBuilder
{
    private readonly List<Vertex> _triangles = new(16384);
    private readonly List<Vertex> _lines = new(8192);
    private readonly List<Vertex> _wideLines = new(16);

    public void Clear()
    {
        _triangles.Clear();
        _lines.Clear();
        _wideLines.Clear();
    }

    /// <summary>Add a 2D line in logical screen coordinates, drawn to the pixel centres.</summary>
    public void Line(float x1, float y1, float x2, float y2, int colour, uint flags = 0)
    {
        _lines.Add(new Vertex(x1 + 0.5f, y1 + 0.5f, (uint)colour, flags));
        _lines.Add(new Vertex(x2 + 0.5f, y2 + 0.5f, (uint)colour, flags));
    }

    /// <summary>Add a line that spans the full width of the space view (see <see cref="FrameData.WideLines"/>).</summary>
    public void WideLine(float x1, float y1, float x2, float y2, int colour)
    {
        _wideLines.Add(new Vertex(x1 + 0.5f, y1 + 0.5f, (uint)colour, 0));
        _wideLines.Add(new Vertex(x2 + 0.5f, y2 + 0.5f, (uint)colour, 0));
    }

    /// <summary>Add a filled rectangle in logical screen coordinates.</summary>
    public void Rect(float x, float y, float width, float height, int colour, uint flags = 0)
    {
        var c = (uint)colour;
        var a = new Vertex(x, y, c, flags);
        var b = new Vertex(x + width, y, c, flags);
        var d = new Vertex(x, y + height, c, flags);
        var e = new Vertex(x + width, y + height, c, flags);
        _triangles.Add(a);
        _triangles.Add(b);
        _triangles.Add(d);
        _triangles.Add(b);
        _triangles.Add(e);
        _triangles.Add(d);
    }

    public FrameData Build(SceneFrame? world, int[] spacePalette, int[] dashboardPalette, bool hyperspaceColours, bool dashboardVisible)
    {
        return new FrameData
        {
            World = world,
            WideLines = _wideLines.ToArray(),
            WideLineVertexCount = _wideLines.Count,
            Triangles = _triangles.ToArray(),
            TriangleVertexCount = _triangles.Count,
            Lines = _lines.ToArray(),
            LineVertexCount = _lines.Count,
            SpacePalette = (int[])spacePalette.Clone(),
            DashboardPalette = (int[])dashboardPalette.Clone(),
            HyperspaceColours = hyperspaceColours,
            DashboardVisible = dashboardVisible,
        };
    }
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

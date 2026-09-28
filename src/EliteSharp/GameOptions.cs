namespace EliteSharp;

/// <summary>How the 3D world is drawn.</summary>
public enum RendererKind
{
    /// <summary>As a real 3D scene, with a camera, depth buffer and GPU-resident models.</summary>
    World3D,

    /// <summary>
    /// As the original's 2D projection of it, drawn in the BBC's screen layout
    /// (the renderer that EliteSharp started with, kept for comparison).
    /// </summary>
    Classic,
}

/// <summary>Command-line options.</summary>
public sealed class GameOptions
{
    /// <summary>
    /// The number of iterations of the main loop per second. The original runs
    /// its main loop as fast as the 6502 allows, which is roughly this fast.
    /// </summary>
    public int MainLoopRate { get; set; } = 16;

    /// <summary>Whether to play sound.</summary>
    public bool Sound { get; set; } = true;

    /// <summary>The window scale (the logical screen is 256 x 248 pixels).</summary>
    public int Scale { get; set; } = 4;

    /// <summary>The window size in pixels, if given (otherwise it is the logical screen size times <see cref="Scale"/>).</summary>
    public (int Width, int Height)? WindowSize { get; set; }

    /// <summary>Whether to read game controllers.</summary>
    public bool Gamepad { get; set; } = true;

    /// <summary>How to draw the 3D world.</summary>
    public RendererKind Renderer { get; set; } = RendererKind.World3D;

    /// <summary>Whether to confine the 3D world to the original's 4:3 space view, rather than the full width of the window.</summary>
    public bool ClassicFrame { get; set; }

    /// <summary>Whether to start in full-screen mode.</summary>
    public bool FullScreen { get; set; }

    /// <summary>The folder containing the disc drive folders for saved commanders.</summary>
    public string DataFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EliteSharp");

    public static GameOptions Parse(string[] args)
    {
        var options = new GameOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (arg)
            {
                case "--fps" when next != null && int.TryParse(next, out int fps):
                    options.MainLoopRate = Math.Clamp(fps, 1, 50);
                    i++;
                    break;
                case "--scale" when next != null && int.TryParse(next, out int scale):
                    options.Scale = Math.Clamp(scale, 1, 8);
                    i++;
                    break;
                case "--data" when next != null:
                    options.DataFolder = next;
                    i++;
                    break;
                case "--nosound":
                    options.Sound = false;
                    break;
                case "--window" when next != null && TryParseSize(next, out var size):
                    options.WindowSize = size;
                    i++;
                    break;
                case "--renderer" when next != null:
                    options.Renderer = next.Equals("classic", StringComparison.OrdinalIgnoreCase) ? RendererKind.Classic : RendererKind.World3D;
                    i++;
                    break;
                case "--frame" when next != null:
                    options.ClassicFrame = next.Equals("classic", StringComparison.OrdinalIgnoreCase);
                    i++;
                    break;
                case "--nopad":
                    options.Gamepad = false;
                    break;
                case "--fullscreen":
                    options.FullScreen = true;
                    break;
            }
        }

        return options;
    }

    /// <summary>Parse a size such as "1920x1080".</summary>
    private static bool TryParseSize(string text, out (int Width, int Height) size)
    {
        size = default;
        var parts = text.Split('x', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height) && width >= 64 && height >= 64)
        {
            size = (width, height);
            return true;
        }

        return false;
    }
}

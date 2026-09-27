namespace EliteSharp;

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

    /// <summary>Whether to read game controllers.</summary>
    public bool Gamepad { get; set; } = true;

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
}

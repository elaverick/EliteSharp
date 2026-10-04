using EliteSharp.Data;

namespace EliteSharp;

/// <summary>Command-line options.</summary>
public sealed class GameOptions
{
    /// <summary>
    /// The number of iterations of the main loop per second. The original runs
    /// its main loop as fast as the 6502 allows, which is roughly this fast.
    /// </summary>
    public int MainLoopRate { get; set; } = 16;

    /// <summary>
    /// Whether the renderer moves the 3D world smoothly between the main
    /// loop's iterations (see <see cref="Rendering.FrameInterpolator"/>), rather
    /// than showing each one as it comes. Either way, the game runs at
    /// <see cref="MainLoopRate"/>.
    /// </summary>
    public bool Interpolate
    {
        get => _interpolate;
        set => Save(ref _interpolate, value, "smooth_motion");
    }

    /// <summary>
    /// Whether to draw one frame for each refresh of the display (vsync),
    /// rather than as many frames as possible. Either way, the game runs at
    /// <see cref="MainLoopRate"/>.
    /// </summary>
    public bool VSync
    {
        get => _vsync;
        set => Save(ref _vsync, value, "vsync");
    }

    /// <summary>Whether to play sound (the sound effects aren't even loaded if not).</summary>
    public bool Sound { get; set; } = true;

    /// <summary>The volume of the sound effects, from 0 (silent) to <see cref="MaxVolume"/>.</summary>
    public int EffectsVolume
    {
        get => _effectsVolume;
        set => Save(ref _effectsVolume, Math.Clamp(value, 0, MaxVolume), "sound_volume");
    }

    /// <summary>The volume of the music, from 0 (silent) to <see cref="MaxVolume"/>.</summary>
    public int MusicVolume
    {
        get => _musicVolume;
        set => Save(ref _musicVolume, Math.Clamp(value, 0, MaxVolume), "music_volume");
    }

    /// <summary>The loudest setting of <see cref="EffectsVolume"/> and <see cref="MusicVolume"/>.</summary>
    public const int MaxVolume = 10;

    /// <summary>The window scale (the logical screen is 256 x 248 pixels).</summary>
    public int Scale { get; set; } = 4;

    /// <summary>
    /// The size of the window in pixels when it isn't full screen (once the
    /// options are loaded, this is set, from <see cref="Scale"/> if need be).
    /// </summary>
    public (int Width, int Height)? WindowSize
    {
        get
        {
            lock (_windowSizeLock)
            {
                return _windowSize;
            }
        }

        set
        {
            lock (_windowSizeLock)
            {
                _windowSize = value;
            }

            if (value is var (width, height))
            {
                Settings?.Set("window_size", $"{width}x{height}");
            }
        }
    }

    /// <summary>Whether to read game controllers.</summary>
    public bool Gamepad
    {
        get => _gamepad;
        set => Save(ref _gamepad, value, "controller");
    }

    /// <summary>Whether to confine the 3D world to the original's 4:3 space view, rather than the full width of the window.</summary>
    public bool FourByThreeFrame
    {
        get => _fourByThreeFrame;
        set
        {
            _fourByThreeFrame = value;
            Settings?.Set("frame", value ? "4:3" : "wide");
        }
    }

    /// <summary>Whether the game is full screen (rather than in a window).</summary>
    public bool FullScreen
    {
        get => _fullScreen;
        set => Save(ref _fullScreen, value, "full_screen");
    }

    // The game and the window change these from their own threads
    private bool _interpolate = true, _vsync = true, _gamepad = true, _fourByThreeFrame, _fullScreen;
    private int _effectsVolume = MaxVolume, _musicVolume = MaxVolume;
    private (int Width, int Height)? _windowSize;
    private readonly object _windowSizeLock = new();

    /// <summary>
    /// The settings file, which changes to the settings are saved to (null
    /// until the options are loaded, so the command line's options, which
    /// only last for one run, aren't saved).
    /// </summary>
    public SettingsFile? Settings { get; private set; }

    /// <summary>Change a setting and save it to the settings file.</summary>
    private void Save<T>(ref T field, T value, string name)
    {
        field = value;
        switch (value)
        {
            case bool flag:
                Settings?.Set(name, flag);
                break;
            case int number:
                Settings?.Set(name, number);
                break;
        }
    }

    /// <summary>The folder for the settings file and the saved commanders (in its Commanders folder).</summary>
    public string DataFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EliteSharp");

    /// <summary>The language of the game's text (the strings file is Assets/Strings/&lt;language&gt;-strings.yml).</summary>
    public string Language { get; set; } = GameStrings.DefaultLanguage;

    /// <summary>A mod's folder, used in place of the Assets folder (see <see cref="GameAssets"/>), or null for none.</summary>
    public string? GameFolder { get; set; }

    /// <summary>
    /// Load the options: the settings file in the data folder, and then the
    /// command line's options on top (which last for this run only).
    /// </summary>
    public static GameOptions Load(string[] args)
    {
        var settings = SettingsFile.Load(Path.Combine(Parse(args).DataFolder, SettingsFile.FileName));
        var options = new GameOptions();
        options.Apply(settings);
        options.Apply(args);
        options.WindowSize ??= (256 * options.Scale, 248 * options.Scale);
        options.Settings = settings;
        return options;
    }

    /// <summary>Apply the settings in the settings file.</summary>
    private void Apply(SettingsFile settings)
    {
        FullScreen = settings.GetBool("full_screen") ?? FullScreen;
        VSync = settings.GetBool("vsync") ?? VSync;
        Interpolate = settings.GetBool("smooth_motion") ?? Interpolate;
        Gamepad = settings.GetBool("controller") ?? Gamepad;
        EffectsVolume = settings.GetInt("sound_volume") ?? EffectsVolume;
        MusicVolume = settings.GetInt("music_volume") ?? MusicVolume;
        if (settings.GetString("frame") is { } frame)
        {
            FourByThreeFrame = frame == "4:3";
        }

        if (settings.GetString("window_size") is { } size && TryParseSize(size, out var windowSize))
        {
            WindowSize = windowSize;
        }
    }

    /// <summary>Parse the command line's options (without the settings file).</summary>
    public static GameOptions Parse(string[] args)
    {
        var options = new GameOptions();
        options.Apply(args);
        return options;
    }

    /// <summary>Apply the command line's options.</summary>
    private void Apply(string[] args)
    {
        var options = this;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (arg)
            {
                case "--simulation-rate" when next != null && int.TryParse(next, out int simulationRate):
                    options.MainLoopRate = Math.Clamp(simulationRate, 1, 50);
                    i++;
                    break;
                case "--scale" when next != null && int.TryParse(next, out int scale):
                    options.Scale = Math.Clamp(scale, 1, 8);
                    options.WindowSize = null;
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
                case "--frame" when next != null:
                    options.FourByThreeFrame = next == "4:3";
                    i++;
                    break;
                case "--nopad":
                    options.Gamepad = false;
                    break;
                case "--nointerpolation":
                    options.Interpolate = false;
                    break;
                case "--novsync":
                    options.VSync = false;
                    break;
                case "--fullscreen":
                    options.FullScreen = true;
                    break;
                case "--game" or "-game" when next != null:
                    options.GameFolder = Path.GetFullPath(next);
                    i++;
                    break;
                case "--language" when next != null:
                    options.Language = next.ToLowerInvariant();
                    i++;
                    break;
            }
        }
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

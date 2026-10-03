using EliteSharp;
using EliteSharp.Data;
using EliteSharp.Game;
using EliteSharp.Game.Missions;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Vulkan;
using EliteSharp.Sound;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

var options = GameOptions.Parse(args);

// Load the ship assets, the missions, the game's text, the HUD's images and
// the sound effects (from the mod's folder, if there is one, or else the
// game's Assets folder) before anything else, so any problems are reported
// straight away
MissionCatalogue missions;
GameStrings strings;
DescriptionGrammar descriptions;
TradingData trading;
SoundSamples[]? soundEffects;
try
{
    if (options.GameFolder is { } gameFolder)
    {
        if (!Directory.Exists(gameFolder))
        {
            throw new DirectoryNotFoundException($"The game folder '{gameFolder}' is missing");
        }

        GameAssets.ModFolder = gameFolder;
    }

    ShipCatalogue.Reload();
    missions = MissionLoader.LoadGame();
    strings = GameStrings.Load(options.Language, HudAtlas.InFont);
    descriptions = DescriptionGrammar.Load(options.Language);
    trading = TradingData.Load();
    _ = HudAtlas.Texels;
    soundEffects = options.Sound ? SoundEffects.Load() : null;
}
catch (Exception e) when (e is InvalidDataException or IOException or MissionLoadException or ArgumentException { ParamName: "language" })
{
    Directory.CreateDirectory(options.DataFolder);
    File.WriteAllText(Path.Combine(options.DataFolder, "crash.log"), e.Message);
    Console.Error.WriteLine(e.Message);
    return 1;
}

var exchange = new FrameExchange();
var hud = new Hud(exchange);
var keyboard = new BbcKeyboard();
using var sound = soundEffects != null ? SoundEngine.TryCreate(soundEffects) : null;
using var gamepad = options.Gamepad ? Gamepad.TryCreate(keyboard) : null;
var game = new EliteGame(hud, keyboard, sound, options, gamepad, missions, strings, descriptions, trading);

var windowOptions = WindowOptions.DefaultVulkan with
{
    Title = "Elite",
    Size = options.WindowSize is var (width, height)
        ? new Vector2D<int>(width, height)
        : new Vector2D<int>(256 * options.Scale, 248 * options.Scale),
    WindowState = options.FullScreen ? WindowState.Fullscreen : WindowState.Normal,
};

var window = Window.Create(windowOptions);
VulkanRenderer? renderer = null;
Thread? gameThread = null;
Exception? gameError = null;
bool closeRequested = false;
bool fullScreenToggleRequested = false;

// Switch between a window and full screen (on the window's thread)
void ToggleFullScreen() =>
    window.WindowState = window.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;

window.Load += () =>
{
    renderer = new VulkanRenderer(window, ShipCatalogue.ModelPaths, options.FourByThreeFrame ? WorldFraming.FourByThree : WorldFraming.Wide);
    var input = window.CreateInput();
    foreach (var kb in input.Keyboards)
    {
        kb.KeyDown += (k, key, _) =>
        {
            // Alt+Enter toggles full-screen mode
            if (key == Key.Enter && (k.IsKeyPressed(Key.AltLeft) || k.IsKeyPressed(Key.AltRight)))
            {
                ToggleFullScreen();
                return;
            }

            keyboard.OnKeyDown(key);
        };
        kb.KeyUp += (_, key, _) => keyboard.OnKeyUp(key);
    }

    gameThread = new Thread(() =>
    {
        try
        {
            game.Run();
        }
        catch (Exception exception)
        {
            gameError = exception;
        }
    })
    {
        IsBackground = true,
        Name = "Elite",
    };
    gameThread.Start();

    int scriptIndex = Array.FindIndex(args, a => a.Equals("--script", StringComparison.OrdinalIgnoreCase));
    if (scriptIndex >= 0 && scriptIndex + 1 < args.Length)
    {
        // Test scripts can't press Alt+Enter (their keys go to the BBC's
        // keyboard), so "cmd fullscreen" does the same
        void Command(string command)
        {
            if (command == "fullscreen")
            {
                fullScreenToggleRequested = true;
            }
            else
            {
                game.DebugCommand(command);
            }
        }

        new ScriptRunner(args[scriptIndex + 1], keyboard, gamepad, path => renderer?.RequestCapture(path), () => closeRequested = true, Command).Start();
    }
};

window.Render += _ =>
{
    if (fullScreenToggleRequested)
    {
        fullScreenToggleRequested = false;
        ToggleFullScreen();
    }

    if (renderer != null)
    {
        hud.SideMargin = renderer.SideMargin;
        renderer.Draw(exchange.TakeLatest());
    }

    if (gameError != null || closeRequested)
    {
        window.Close();
    }
};

window.FramebufferResize += _ => renderer?.Resize();
window.FocusChanged += focused => gamepad?.SetFocus(focused);

window.Closing += () =>
{
    game.Quit();
    gameThread?.Join(1000);
    renderer?.Dispose();
    renderer = null;
};

window.Run();
window.Dispose();

if (gameError != null)
{
    Directory.CreateDirectory(options.DataFolder);
    File.WriteAllText(Path.Combine(options.DataFolder, "crash.log"), gameError.ToString());
    Console.Error.WriteLine(gameError);
    return 1;
}

return 0;

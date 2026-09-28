using EliteSharp;
using EliteSharp.Game;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Vulkan;
using EliteSharp.Sound;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

var options = GameOptions.Parse(args);

// Load the ship assets before anything else, so any problems are reported
// straight away
try
{
    ShipCatalogue.Reload();
}
catch (Exception e) when (e is InvalidDataException or IOException)
{
    Directory.CreateDirectory(options.DataFolder);
    File.WriteAllText(Path.Combine(options.DataFolder, "crash.log"), e.Message);
    Console.Error.WriteLine(e.Message);
    return 1;
}

var exchange = new FrameExchange();
var screen = new Screen(exchange);
var keyboard = new BbcKeyboard();
using var sound = options.Sound ? SoundEngine.TryCreate() : null;
using var gamepad = options.Gamepad ? Gamepad.TryCreate(keyboard) : null;
var game = new EliteGame(screen, keyboard, sound, options, gamepad);

var windowOptions = WindowOptions.DefaultVulkan with
{
    Title = options.Renderer == RendererKind.World3D ? "Elite - 3D renderer" : "Elite - classic renderer",
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

window.Load += () =>
{
    renderer = new VulkanRenderer(window, options.ClassicFrame ? WorldFraming.Classic : WorldFraming.Wide);
    var input = window.CreateInput();
    foreach (var kb in input.Keyboards)
    {
        kb.KeyDown += (k, key, _) =>
        {
            // Alt+Enter toggles full-screen mode
            if (key == Key.Enter && (k.IsKeyPressed(Key.AltLeft) || k.IsKeyPressed(Key.AltRight)))
            {
                window.WindowState = window.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;
                return;
            }

            // Alt+V switches between the 3D and classic renderers (not Alt+R,
            // which the NVIDIA and AMD overlays use)
            if (key == Key.V && (k.IsKeyPressed(Key.AltLeft) || k.IsKeyPressed(Key.AltRight)))
            {
                game.Renderer = game.Renderer == RendererKind.World3D ? RendererKind.Classic : RendererKind.World3D;
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
        new ScriptRunner(args[scriptIndex + 1], keyboard, gamepad, path => renderer?.RequestCapture(path), () => closeRequested = true, game.DebugCommand).Start();
    }
};

window.Render += _ =>
{
    // Show which renderer is running in the title bar
    string title = game.Renderer == RendererKind.World3D ? "Elite - 3D renderer" : "Elite - classic renderer";
    if (window.Title != title)
    {
        window.Title = title;
    }

    renderer?.Draw(exchange.Latest);
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

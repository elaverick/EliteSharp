using EliteSharp;
using EliteSharp.Game;
using EliteSharp.Input;
using EliteSharp.Rendering;
using EliteSharp.Sound;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

var options = GameOptions.Parse(args);
var exchange = new FrameExchange();
var screen = new Screen(exchange);
var keyboard = new BbcKeyboard();
using var sound = options.Sound ? SoundEngine.TryCreate() : null;
using var gamepad = options.Gamepad ? Gamepad.TryCreate(keyboard) : null;
var game = new EliteGame(screen, keyboard, sound, options, gamepad);

var windowOptions = WindowOptions.DefaultVulkan with
{
    Title = "Elite",
    Size = new Vector2D<int>(256 * options.Scale, 248 * options.Scale),
    WindowState = options.FullScreen ? WindowState.Fullscreen : WindowState.Normal,
};

var window = Window.Create(windowOptions);
VulkanRenderer? renderer = null;
Thread? gameThread = null;
Exception? gameError = null;
bool closeRequested = false;

window.Load += () =>
{
    renderer = new VulkanRenderer(window);
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

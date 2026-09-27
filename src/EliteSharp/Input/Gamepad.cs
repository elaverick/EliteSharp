using System.Diagnostics;
using Silk.NET.SDL;
using Thread = System.Threading.Thread;

namespace EliteSharp.Input;

/// <summary>The buttons on an Xbox-style controller (SDL's game controller layout).</summary>
[Flags]
public enum PadButton
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    Back = 1 << 4,
    Start = 1 << 5,
    LeftStick = 1 << 6,
    RightStick = 1 << 7,
    LeftShoulder = 1 << 8,
    RightShoulder = 1 << 9,
    DpadUp = 1 << 10,
    DpadDown = 1 << 11,
    DpadLeft = 1 << 12,
    DpadRight = 1 << 13,
}

/// <summary>
/// A snapshot of the controller's analogue inputs. The sticks have had their
/// dead zones and response curves applied, and range from -1 to +1 (with +x to
/// the right and +y down, as SDL reports them). The triggers range from 0 to 1.
/// </summary>
public readonly record struct PadAxes(float LeftX, float LeftY, float RightX, float RightY, float LeftTrigger, float RightTrigger)
{
    public bool LeftStickActive => LeftX != 0 || LeftY != 0;

    public bool RightStickActive => RightX != 0 || RightY != 0;
}

/// <summary>
/// Reads an Xbox-style game controller through SDL's game controller API, on
/// its own polling thread. Any controller SDL recognises is presented with the
/// Xbox layout; an Xbox controller is preferred if more than one is connected,
/// and controllers can be plugged in and removed at any time.
///
/// Button presses are passed through a <see cref="GamepadMapper"/>, which turns
/// them into BBC key presses on the <see cref="BbcKeyboard"/>, so the keyboard
/// and controller can be used together. The analogue sticks and triggers are
/// read directly by the game (see <see cref="Axes"/>).
/// </summary>
public sealed unsafe class Gamepad : IDisposable
{
    /// <summary>The radial dead zone of the sticks, as a fraction of full deflection.</summary>
    private const float StickDeadZone = 0.18f;

    /// <summary>The dead zone of the triggers.</summary>
    private const float TriggerDeadZone = 0.08f;

    /// <summary>
    /// The exponent of the sticks' response curve. Values above 1 give finer
    /// control near the centre while still reaching full deflection.
    /// </summary>
    private const float StickExponent = 1.6f;

    private readonly Sdl _sdl;
    private readonly GamepadMapper _mapper;
    private readonly Thread _thread;
    private volatile bool _stop;
    private GameController* _controller;
    private int _instanceId = -1;

    private volatile PadAxesBox _axes = new(default);
    private volatile bool _connected;
    private volatile bool _focused = true;

    // Injected state for scripted tests (overrides the real controller when set)
    private readonly object _injectLock = new();
    private PadButton _injectedButtons;
    private PadAxes? _injectedAxes;

    private Gamepad(Sdl sdl, BbcKeyboard keyboard)
    {
        _sdl = sdl;
        _mapper = new GamepadMapper(keyboard);
        _thread = new Thread(Run) { IsBackground = true, Name = "Gamepad" };
    }

    /// <summary>The mapper that turns buttons into key presses (the game sets its context).</summary>
    public GamepadMapper Mapper => _mapper;

    /// <summary>Whether a controller is connected (or test input is being injected).</summary>
    public bool Connected => _connected || _injectedAxes != null;

    /// <summary>The current analogue inputs.</summary>
    public PadAxes Axes => _injectedAxes ?? _axes.Value;

    /// <summary>The name of the connected controller, if any.</summary>
    public string? ControllerName { get; private set; }

    /// <summary>
    /// Start reading controllers, returning null if SDL's game controller
    /// support can't be initialised (the game then runs with the keyboard only).
    /// </summary>
    public static Gamepad? TryCreate(BbcKeyboard keyboard)
    {
        try
        {
            var sdl = Sdl.GetApi();

            // We have no SDL window, so SDL must be told to report controller
            // input even though none of its windows has the focus
            sdl.SetHint(Sdl.HintJoystickAllowBackgroundEvents, "1");
            if (sdl.Init(Sdl.InitGamecontroller) < 0)
            {
                return null;
            }

            var gamepad = new Gamepad(sdl, keyboard);
            gamepad._thread.Start();
            return gamepad;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Tell the controller whether the game window has the focus. SDL reports
    /// controller input in the background (it has no window of its own), so
    /// we ignore the controller while another application is in front.
    /// </summary>
    public void SetFocus(bool focused) => _focused = focused;

    /// <summary>Set the injected button and axis state (for scripted tests).</summary>
    public void Inject(PadButton buttons, PadAxes? axes)
    {
        lock (_injectLock)
        {
            _injectedButtons = buttons;
            _injectedAxes = axes;
        }
    }

    /// <summary>Press or release an injected button (for scripted tests).</summary>
    public void InjectButton(PadButton button, bool down)
    {
        lock (_injectLock)
        {
            _injectedButtons = down ? _injectedButtons | button : _injectedButtons & ~button;
            _injectedAxes ??= default;
        }
    }

    /// <summary>Set an injected axis value (for scripted tests), by name (lx, ly, rx, ry, lt, rt).</summary>
    public void InjectAxis(string name, float value)
    {
        lock (_injectLock)
        {
            var axes = _injectedAxes ?? default;
            _injectedAxes = name.ToLowerInvariant() switch
            {
                "lx" => axes with { LeftX = value },
                "ly" => axes with { LeftY = value },
                "rx" => axes with { RightX = value },
                "ry" => axes with { RightY = value },
                "lt" => axes with { LeftTrigger = value },
                "rt" => axes with { RightTrigger = value },
                _ => axes,
            };
        }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        OpenBestController();
        while (!_stop)
        {
            Event e;
            while (_sdl.PollEvent(&e) != 0)
            {
                switch ((EventType)e.Type)
                {
                    case EventType.Controllerdeviceadded:
                        if (_controller == null)
                        {
                            OpenBestController();
                        }

                        break;

                    case EventType.Controllerdeviceremoved:
                        if (e.Cdevice.Which == _instanceId)
                        {
                            CloseController();
                            OpenBestController();
                        }

                        break;
                }
            }

            PadButton buttons = _focused ? ReadButtons() : PadButton.None;
            _axes = new PadAxesBox(_focused ? ReadAxes() : default);

            PadButton injected;
            lock (_injectLock)
            {
                injected = _injectedButtons;
            }

            _mapper.Update(buttons | injected);
            Thread.Sleep(4);
        }

        CloseController();
    }

    /// <summary>Open a connected controller, preferring an Xbox controller.</summary>
    private void OpenBestController()
    {
        int best = -1;
        for (int i = 0; i < _sdl.NumJoysticks(); i++)
        {
            if (_sdl.IsGameController(i) == SdlBool.False)
            {
                continue;
            }

            if (best < 0)
            {
                best = i;
            }

            var controller = _sdl.GameControllerOpen(i);
            if (controller == null)
            {
                continue;
            }

            var type = _sdl.GameControllerGetType(controller);
            _sdl.GameControllerClose(controller);
            if (type is GameControllerType.Xbox360 or GameControllerType.Xboxone)
            {
                best = i;
                break;
            }
        }

        if (best < 0)
        {
            return;
        }

        _controller = _sdl.GameControllerOpen(best);
        if (_controller != null)
        {
            _instanceId = _sdl.JoystickInstanceID(_sdl.GameControllerGetJoystick(_controller));
            ControllerName = _sdl.GameControllerNameS(_controller);
            _connected = true;
        }
    }

    private void CloseController()
    {
        if (_controller != null)
        {
            _sdl.GameControllerClose(_controller);
        }

        _controller = null;
        _instanceId = -1;
        _connected = false;
        ControllerName = null;
        _axes = new PadAxesBox(default);
    }

    private static readonly (GameControllerButton Sdl, PadButton Pad)[] ButtonMap =
    [
        (GameControllerButton.A, PadButton.A),
        (GameControllerButton.B, PadButton.B),
        (GameControllerButton.X, PadButton.X),
        (GameControllerButton.Y, PadButton.Y),
        (GameControllerButton.Back, PadButton.Back),
        (GameControllerButton.Start, PadButton.Start),
        (GameControllerButton.Leftstick, PadButton.LeftStick),
        (GameControllerButton.Rightstick, PadButton.RightStick),
        (GameControllerButton.Leftshoulder, PadButton.LeftShoulder),
        (GameControllerButton.Rightshoulder, PadButton.RightShoulder),
        (GameControllerButton.DpadUp, PadButton.DpadUp),
        (GameControllerButton.DpadDown, PadButton.DpadDown),
        (GameControllerButton.DpadLeft, PadButton.DpadLeft),
        (GameControllerButton.DpadRight, PadButton.DpadRight),
    ];

    private PadButton ReadButtons()
    {
        if (_controller == null)
        {
            return PadButton.None;
        }

        var buttons = PadButton.None;
        foreach (var (sdlButton, padButton) in ButtonMap)
        {
            if (_sdl.GameControllerGetButton(_controller, sdlButton) != 0)
            {
                buttons |= padButton;
            }
        }

        return buttons;
    }

    private PadAxes ReadAxes()
    {
        if (_controller == null)
        {
            return default;
        }

        float Axis(GameControllerAxis axis) => Math.Clamp(_sdl.GameControllerGetAxis(_controller, axis) / 32767f, -1f, 1f);

        var (lx, ly) = ShapeStick(Axis(GameControllerAxis.Leftx), Axis(GameControllerAxis.Lefty));
        var (rx, ry) = ShapeStick(Axis(GameControllerAxis.Rightx), Axis(GameControllerAxis.Righty));
        return new PadAxes(lx, ly, rx, ry,
            ShapeTrigger(Axis(GameControllerAxis.Triggerleft)),
            ShapeTrigger(Axis(GameControllerAxis.Triggerright)));
    }

    /// <summary>
    /// Apply a radial dead zone and a response curve to a stick, rescaling so
    /// the output starts from zero at the edge of the dead zone.
    /// </summary>
    private static (float X, float Y) ShapeStick(float x, float y)
    {
        float magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude <= StickDeadZone)
        {
            return (0, 0);
        }

        float scaled = Math.Min(1f, (magnitude - StickDeadZone) / (1f - StickDeadZone));
        float curved = MathF.Pow(scaled, StickExponent);
        float factor = curved / magnitude;
        return (Math.Clamp(x * factor, -1f, 1f), Math.Clamp(y * factor, -1f, 1f));
    }

    private static float ShapeTrigger(float value) =>
        value <= TriggerDeadZone ? 0 : Math.Min(1f, (value - TriggerDeadZone) / (1f - TriggerDeadZone));

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
        _sdl.QuitSubSystem(Sdl.InitGamecontroller);
    }

    /// <summary>A box so the axes snapshot can be swapped atomically between threads.</summary>
    private sealed record PadAxesBox(PadAxes Value);
}

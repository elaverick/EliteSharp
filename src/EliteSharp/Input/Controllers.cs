using System.Diagnostics;
using Silk.NET.SDL;
using Thread = System.Threading.Thread;

namespace EliteSharp.Input;

/// <summary>
/// The buttons of a gamepad, numbered as SDL's game controller layout
/// numbers them (an Xbox controller's), for the test scripts.
/// </summary>
public enum PadButton
{
    A = 0,
    B = 1,
    X = 2,
    Y = 3,
    Back = 4,
    Guide = 5,
    Start = 6,
    LeftStick = 7,
    RightStick = 8,
    LeftShoulder = 9,
    RightShoulder = 10,
    DpadUp = 11,
    DpadDown = 12,
    DpadLeft = 13,
    DpadRight = 14,
}

/// <summary>
/// The controllers' analogue inputs, from the axes bound to each
/// <see cref="AxisFunction"/>, with their dead zones applied. Roll, pitch and
/// the cross-hairs range from -1 to +1 (roll is positive to the right, pitch
/// when pulled back, and the cross-hairs to the right and down), the speed
/// triggers from 0 to 1, and the throttle from 0 (closed) to 1 (open), or
/// null if no throttle is bound.
/// </summary>
public readonly record struct FlightAxes(
    float Roll, float Pitch, float ChartX, float ChartY, float SpeedUp, float SlowDown, float? Throttle)
{
    public bool StickActive => Roll != 0 || Pitch != 0;

    public bool ChartActive => ChartX != 0 || ChartY != 0;
}

/// <summary>What a controller's controls are doing at one moment.</summary>
public sealed class ControllerState(bool[] buttons, float[] axes, HatDirection[] hats)
{
    public static readonly ControllerState Empty = new([], [], []);

    /// <summary>Whether each button is pressed.</summary>
    public bool[] Buttons { get; } = buttons;

    /// <summary>Each axis, from -1 to +1 (a gamepad's triggers from 0 to 1, and its sticks with their dead zones applied).</summary>
    public float[] Axes { get; } = axes;

    /// <summary>The directions each hat is pushed in.</summary>
    public HatDirection[] Hats { get; } = hats;

    public float Axis(int index) => index < Axes.Length ? Axes[index] : 0;

    /// <summary>Whether a button or hat direction is pressed.</summary>
    public bool IsPressed(ControlInput input) => input.Kind switch
    {
        InputKind.Button => input.Index < Buttons.Length && Buttons[input.Index],
        InputKind.Hat => input.Index < Hats.Length && (Hats[input.Index] & input.Direction) != 0,
        _ => false,
    };

    /// <summary>The buttons and hat directions that are pressed.</summary>
    public List<ControlInput> Pressed()
    {
        var pressed = new List<ControlInput>();
        for (int i = 0; i < Buttons.Length; i++)
        {
            if (Buttons[i])
            {
                pressed.Add(ControlInput.Button(i));
            }
        }

        for (int i = 0; i < Hats.Length; i++)
        {
            foreach (var direction in HatDirections)
            {
                if ((Hats[i] & direction) != 0)
                {
                    pressed.Add(ControlInput.Hat(i, direction));
                }
            }
        }

        return pressed;
    }

    public static readonly HatDirection[] HatDirections = [HatDirection.Up, HatDirection.Right, HatDirection.Down, HatDirection.Left];
}

/// <summary>A connected controller.</summary>
public sealed class ControllerDevice
{
    internal ControllerDevice(int instanceId, string name, ControllerProfile profile, bool[] buttonsPresent, int axisCount, int hatCount)
    {
        InstanceId = instanceId;
        Name = name;
        Profile = profile;
        ButtonsPresent = buttonsPresent;
        AxisCount = axisCount;
        HatCount = hatCount;
        _state = new ControllerState(new bool[buttonsPresent.Length], new float[axisCount], new HatDirection[hatCount]);
    }

    /// <summary>SDL's instance ID for the device (which is negative for the test scripts' gamepad).</summary>
    public int InstanceId { get; }

    /// <summary>The device's own name, as SDL reports it (such as "PS5 Controller").</summary>
    public string Name { get; }

    /// <summary>The device's USB vendor ID (0 if it isn't known).</summary>
    public ushort Vendor { get; init; }

    /// <summary>The device's USB product ID (0 if it isn't known).</summary>
    public ushort Product { get; init; }

    /// <summary>The device's profile (its own, or the gamepad or generic profile).</summary>
    public ControllerProfile Profile { get; }

    /// <summary>
    /// Which of the buttons the device has (a joystick has all of them; a
    /// gamepad, those of SDL's game controller buttons that it has).
    /// </summary>
    public bool[] ButtonsPresent { get; }

    public int AxisCount { get; }

    public int HatCount { get; }

    /// <summary>What the controls are doing now (updated by the controllers' thread).</summary>
    public ControllerState State
    {
        get => _state;
        internal set => _state = value;
    }

    private volatile ControllerState _state;

    /// <summary>Every button and hat direction that the device has, in order.</summary>
    public IEnumerable<ControlInput> Inputs()
    {
        for (int i = 0; i < ButtonsPresent.Length; i++)
        {
            if (ButtonsPresent[i])
            {
                yield return ControlInput.Button(i);
            }
        }

        for (int i = 0; i < HatCount; i++)
        {
            foreach (var direction in ControllerState.HatDirections)
            {
                yield return ControlInput.Hat(i, direction);
            }
        }
    }

    /// <summary>Whether the device has an input (a button, hat direction or axis).</summary>
    public bool Has(ControlInput input) => input.Kind switch
    {
        InputKind.Button => input.Index < ButtonsPresent.Length && ButtonsPresent[input.Index],
        InputKind.Hat => input.Index < HatCount,
        _ => input.Index < AxisCount,
    };

    /// <summary>SDL's handle for the device (a GameController* for a gamepad, or else a Joystick*).</summary>
    internal nint Handle { get; init; }
}

/// <summary>
/// Reads every connected controller through SDL, on its own polling thread:
/// gamepads through SDL's game controller API (so any gamepad SDL recognises
/// has the Xbox layout), and joysticks (such as a HOTAS or a flight yoke)
/// through its joystick API, with the profile that matches their USB IDs (see
/// <see cref="ControllerProfile"/>). Controllers can be plugged in and
/// removed at any time, and any number of them can be used together.
///
/// Each controller's buttons are passed through a <see cref="ControllerMapper"/>
/// with its bindings, which turns them into BBC key presses on the
/// <see cref="BbcKeyboard"/>, so the keyboard and controllers can be used
/// together. The axes bound to roll, pitch, the throttle and so on are read
/// directly by the game (see <see cref="Axes"/>).
/// </summary>
public sealed unsafe class Controllers : IDisposable
{
    /// <summary>The radial dead zone of a gamepad's sticks, as a fraction of full deflection.</summary>
    private const float StickDeadZone = 0.18f;

    /// <summary>The dead zone of a gamepad's triggers, and of any axis bound to speeding up or slowing down.</summary>
    private const float TriggerDeadZone = 0.08f;

    /// <summary>
    /// The exponent of a gamepad's sticks' response curve. Values above 1 give
    /// finer control near the centre while still reaching full deflection.
    /// </summary>
    private const float StickExponent = 1.6f;

    /// <summary>How far a throttle must move to take over from the device that last moved its throttle.</summary>
    private const float ThrottleMovement = 0.02f;

    /// <summary>The instance ID of the gamepad that the test scripts press the buttons of.</summary>
    private const int InjectedId = -2;

    /// <summary>The number of buttons in SDL's game controller layout (up to the touchpad).</summary>
    private const int GamepadButtons = (int)GameControllerButton.Touchpad + 1;

    /// <summary>The number of axes in SDL's game controller layout.</summary>
    private const int GamepadAxes = (int)GameControllerAxis.Max;

    private readonly Sdl? _sdl;
    private readonly ControllerProfiles _profiles;
    private readonly ControllerBindingStore _bindings;
    private readonly ControllerMapper _mapper;
    private readonly Thread _thread;
    private readonly List<ControllerDevice> _devices = [];
    private volatile bool _stop;
    private volatile bool _focused = true;
    private volatile bool _enabled = true;
    private volatile ControllerDevice[] _published = [];
    private volatile Box<FlightAxes> _axes = new(default);

    /// <summary>The throttle values last read from each device, and the device whose throttle is in use.</summary>
    private readonly Dictionary<int, float> _throttles = [];
    private int _throttleOwner = int.MinValue;

    // The test scripts' gamepad, which is added when they first use it
    private readonly object _injectLock = new();
    private ControllerDevice? _injected;
    private readonly bool[] _injectedButtons = new bool[GamepadButtons];
    private readonly float[] _injectedAxes = new float[GamepadAxes];

    private Controllers(Sdl? sdl, BbcKeyboard keyboard, ControllerProfiles profiles, ControllerBindingStore bindings)
    {
        _sdl = sdl;
        _profiles = profiles;
        _bindings = bindings;
        _mapper = new ControllerMapper(keyboard);
        _thread = new Thread(Run) { IsBackground = true, Name = "Controllers" };
    }

    /// <summary>The mapper that turns buttons into key presses (the game sets its context).</summary>
    public ControllerMapper Mapper => _mapper;

    /// <summary>The players' bindings.</summary>
    public ControllerBindingStore Bindings => _bindings;

    /// <summary>Whether to read the controllers (when turned off on the Settings screen, they are ignored).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// While set (by the Controls screen, while it waits for a control to
    /// bind), the controls are read but do nothing.
    /// </summary>
    public bool Capturing
    {
        get => _capturing;
        set => _capturing = value;
    }

    private volatile bool _capturing;

    /// <summary>Whether any controller is connected and enabled (or test input is being injected).</summary>
    public bool Connected => (_enabled && _published.Length > 0) || _injected != null;

    /// <summary>The connected controllers (empty while they are switched off).</summary>
    public IReadOnlyList<ControllerDevice> Devices => _enabled ? _published : [];

    /// <summary>The current analogue inputs.</summary>
    public FlightAxes Axes => _axes.Value;

    /// <summary>
    /// Start reading controllers, returning null if SDL's controller support
    /// can't be initialised (the game then runs with the keyboard only).
    /// </summary>
    public static Controllers? TryCreate(BbcKeyboard keyboard, ControllerProfiles profiles, ControllerBindingStore bindings)
    {
        try
        {
            var sdl = Sdl.GetApi();

            // We have no SDL window, so SDL must be told to report controller
            // input even though none of its windows has the focus
            sdl.SetHint(Sdl.HintJoystickAllowBackgroundEvents, "1");
            if (sdl.Init(Sdl.InitGamecontroller | Sdl.InitJoystick) < 0)
            {
                return null;
            }

            var controllers = new Controllers(sdl, keyboard, profiles, bindings);
            controllers._thread.Start();
            return controllers;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Controllers without SDL, which only the test scripts' gamepad drives (for tests).</summary>
    public static Controllers CreateForTests(BbcKeyboard keyboard, ControllerProfiles profiles, ControllerBindingStore bindings) =>
        new(null, keyboard, profiles, bindings);

    /// <summary>
    /// Tell the controllers whether the game window has the focus. SDL reports
    /// controller input in the background (it has no window of its own), so
    /// we ignore the controllers while another application is in front.
    /// </summary>
    public void SetFocus(bool focused) => _focused = focused;

    /// <summary>Press or release a button on the test scripts' gamepad.</summary>
    public void InjectButton(PadButton button, bool down)
    {
        lock (_injectLock)
        {
            EnsureInjected();
            _injectedButtons[(int)button] = down;
        }
    }

    /// <summary>Set an axis of the test scripts' gamepad, by name (lx, ly, rx, ry, lt, rt).</summary>
    public void InjectAxis(string name, float value)
    {
        int axis = name.ToLowerInvariant() switch
        {
            "lx" => 0,
            "ly" => 1,
            "rx" => 2,
            "ry" => 3,
            "lt" => 4,
            "rt" => 5,
            _ => -1,
        };

        if (axis >= 0)
        {
            lock (_injectLock)
            {
                EnsureInjected();
                _injectedAxes[axis] = value;
            }
        }
    }

    private void EnsureInjected()
    {
        if (_injected == null)
        {
            var present = new bool[GamepadButtons];
            for (int i = 0; i <= (int)PadButton.DpadRight; i++)
            {
                present[i] = i != (int)PadButton.Guide;
            }

            _injected = new ControllerDevice(InjectedId, "Test Gamepad", _profiles.Gamepad, present, GamepadAxes, 0);
        }
    }

    /// <summary>Read the controllers once (the polling thread does this continually; tests call it directly).</summary>
    public void Poll()
    {
        bool reading = _focused && _enabled;
        var active = new List<ControllerDevice>();
        foreach (var device in _devices)
        {
            device.State = reading ? Read(device) : new ControllerState(new bool[device.ButtonsPresent.Length], new float[device.AxisCount], new HatDirection[device.HatCount]);
            active.Add(device);
        }

        lock (_injectLock)
        {
            if (_injected != null)
            {
                _injected.State = new ControllerState((bool[])_injectedButtons.Clone(), (float[])_injectedAxes.Clone(), []);
                active.Add(_injected);
            }
        }

        _published = [.. active];
        bool capturing = _capturing;
        _mapper.Update([.. active.Select(d => (d.InstanceId, _bindings.For(d.Profile), (IReadOnlyCollection<ControlInput>)d.State.Pressed()))], capturing);
        _axes = new Box<FlightAxes>(capturing ? default : CombineAxes(active));
    }

    private void Run()
    {
        OpenAll();
        while (!_stop)
        {
            Event e;
            while (_sdl!.PollEvent(&e) != 0)
            {
                switch ((EventType)e.Type)
                {
                    case EventType.Joydeviceadded:
                        Open(e.Jdevice.Which);
                        break;

                    case EventType.Joydeviceremoved:
                        Close(e.Jdevice.Which);
                        break;
                }
            }

            Poll();
            Thread.Sleep(4);
        }

        foreach (var device in _devices.ToList())
        {
            Close(device.InstanceId);
        }
    }

    /// <summary>Open every connected controller.</summary>
    private void OpenAll()
    {
        for (int i = 0; i < _sdl!.NumJoysticks(); i++)
        {
            Open(i);
        }
    }

    /// <summary>
    /// Open the controller with the given device index (if it isn't already
    /// open): as a joystick if a profile matches its USB IDs, or else as a
    /// gamepad if SDL recognises it as one, or else as a joystick with the
    /// generic profile.
    /// </summary>
    private void Open(int index)
    {
        var sdl = _sdl!;
        int instanceId = sdl.JoystickGetDeviceInstanceID(index);
        if (instanceId < 0 || _devices.Any(d => d.InstanceId == instanceId))
        {
            return;
        }

        ushort vendor = sdl.JoystickGetDeviceVendor(index), product = sdl.JoystickGetDeviceProduct(index);
        var profile = _profiles.Find(vendor, product, sdl.JoystickNameForIndexS(index));
        if (profile == null && sdl.IsGameController(index) == SdlBool.True)
        {
            var controller = sdl.GameControllerOpen(index);
            if (controller != null)
            {
                var present = new bool[GamepadButtons];
                for (int i = 0; i < present.Length; i++)
                {
                    present[i] = sdl.GameControllerHasButton(controller, (GameControllerButton)i) == SdlBool.True;
                }

                var gamepadProfile = _profiles.GamepadFor(GamepadTypeName(sdl.GameControllerGetType(controller)));
                _devices.Add(new ControllerDevice(instanceId, sdl.GameControllerNameS(controller) ?? gamepadProfile.Name,
                    gamepadProfile, present, GamepadAxes, 0) { Handle = (nint)controller, Vendor = vendor, Product = product });
            }

            return;
        }

        var joystick = sdl.JoystickOpen(index);
        if (joystick == null)
        {
            return;
        }

        string name = sdl.JoystickNameS(joystick) ?? "Joystick";
        if (profile == null)
        {
            // An unrecognised joystick has the generic profile, but its own
            // bindings (by its GUID, which is the same for every one of its model)
            byte* guid = stackalloc byte[33];
            sdl.JoystickGetGUIDString(sdl.JoystickGetGUID(joystick), guid, 33);
            string id = $"{ControllerProfile.GenericId}-{new string((sbyte*)guid)}";
            profile = _profiles.Generic.WithId(id, name);
        }

        var buttons = new bool[Math.Max(0, sdl.JoystickNumButtons(joystick))];
        Array.Fill(buttons, true);
        _devices.Add(new ControllerDevice(instanceId, name, profile, buttons,
            Math.Max(0, sdl.JoystickNumAxes(joystick)), Math.Max(0, sdl.JoystickNumHats(joystick)))
        {
            Handle = (nint)joystick,
            Vendor = vendor,
            Product = product,
        });
    }

    /// <summary>
    /// The name of a kind of gamepad, as the profiles' gamepad_types give it
    /// (SDL's names for the kinds, in lower case, such as "ps5").
    /// </summary>
    private static string GamepadTypeName(GameControllerType type) => type switch
    {
        GameControllerType.Xbox360 => "xbox360",
        GameControllerType.Xboxone => "xboxone",
        GameControllerType.PS3 => "ps3",
        GameControllerType.PS4 => "ps4",
        GameControllerType.PS5 => "ps5",
        GameControllerType.NintendoSwitchPro => "switch_pro",
        GameControllerType.NintendoSwitchJoyconLeft => "joycon_left",
        GameControllerType.NintendoSwitchJoyconRight => "joycon_right",
        GameControllerType.NintendoSwitchJoyconPair => "joycon_pair",
        GameControllerType.AmazonLuna => "luna",
        GameControllerType.GoogleStadia => "stadia",
        GameControllerType.NvidiaShield => "shield",
        GameControllerType.Virtual => "virtual",
        _ => "unknown",
    };

    /// <summary>Close the controller with the given instance ID.</summary>
    private void Close(int instanceId)
    {
        var device = _devices.FirstOrDefault(d => d.InstanceId == instanceId);
        if (device == null)
        {
            return;
        }

        if (device.Profile.IsGamepad)
        {
            _sdl!.GameControllerClose((GameController*)device.Handle);
        }
        else
        {
            _sdl!.JoystickClose((Joystick*)device.Handle);
        }

        _devices.Remove(device);
        _throttles.Remove(instanceId);
    }

    /// <summary>Read a controller's buttons, axes and hats.</summary>
    private ControllerState Read(ControllerDevice device)
    {
        var sdl = _sdl!;
        var buttons = new bool[device.ButtonsPresent.Length];
        var axes = new float[device.AxisCount];
        var hats = new HatDirection[device.HatCount];

        if (device.Profile.IsGamepad)
        {
            var controller = (GameController*)device.Handle;
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i] = device.ButtonsPresent[i] && sdl.GameControllerGetButton(controller, (GameControllerButton)i) != 0;
            }

            float Axis(GameControllerAxis axis) => Math.Clamp(sdl.GameControllerGetAxis(controller, axis) / 32767f, -1f, 1f);

            (axes[0], axes[1]) = ShapeStick(Axis(GameControllerAxis.Leftx), Axis(GameControllerAxis.Lefty));
            (axes[2], axes[3]) = ShapeStick(Axis(GameControllerAxis.Rightx), Axis(GameControllerAxis.Righty));
            axes[4] = ShapeTrigger(Axis(GameControllerAxis.Triggerleft));
            axes[5] = ShapeTrigger(Axis(GameControllerAxis.Triggerright));
        }
        else
        {
            var joystick = (Joystick*)device.Handle;
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i] = sdl.JoystickGetButton(joystick, i) != 0;
            }

            for (int i = 0; i < axes.Length; i++)
            {
                axes[i] = Math.Clamp(sdl.JoystickGetAxis(joystick, i) / 32767f, -1f, 1f);
            }

            for (int i = 0; i < hats.Length; i++)
            {
                hats[i] = (HatDirection)(sdl.JoystickGetHat(joystick, i) & 0x0F);
            }
        }

        return new ControllerState(buttons, axes, hats);
    }

    /// <summary>
    /// Work out the analogue inputs from every controller's bound axes: the
    /// controller pushed furthest for each of the centred axes and triggers,
    /// and the throttle that was moved last.
    /// </summary>
    private FlightAxes CombineAxes(List<ControllerDevice> devices)
    {
        float roll = 0, pitch = 0, chartX = 0, chartY = 0, speedUp = 0, slowDown = 0;
        float? throttle = null;
        foreach (var device in devices)
        {
            var bindings = _bindings.For(device.Profile);
            var state = device.State;

            // A gamepad's sticks already have their dead zones (and its
            // triggers start from 0)
            float deadZone = device.Profile.IsGamepad ? 0 : device.Profile.DeadZone;

            float? Value(AxisFunction function)
            {
                if (bindings.AxisFor(function) is not { } binding || binding.Axis >= state.Axes.Length)
                {
                    return null;
                }

                float value = state.Axes[binding.Axis];
                return binding.Reversed ? -value : value;
            }

            Furthest(ref roll, Centred(Value(AxisFunction.Roll), deadZone));
            Furthest(ref pitch, Centred(Value(AxisFunction.Pitch), deadZone));
            Furthest(ref chartX, Centred(Value(AxisFunction.ChartX), deadZone));
            Furthest(ref chartY, Centred(Value(AxisFunction.ChartY), deadZone));
            speedUp = Math.Max(speedUp, Trigger(Value(AxisFunction.SpeedUp)));
            slowDown = Math.Max(slowDown, Trigger(Value(AxisFunction.SlowDown)));

            if (Value(AxisFunction.Throttle) is { } lever)
            {
                // The throttle goes from 0 (fully back) to 1 (fully forward)
                // (and a throttle takes over when it is moved, or if there's
                // no other)
                float position = (lever + 1) / 2;
                bool known = _throttles.TryGetValue(device.InstanceId, out float last);
                bool moved = !known || MathF.Abs(position - last) >= ThrottleMovement;
                if (moved)
                {
                    _throttles[device.InstanceId] = position;
                }

                if ((moved && known) || !_throttles.ContainsKey(_throttleOwner))
                {
                    _throttleOwner = device.InstanceId;
                }

                if (_throttleOwner == device.InstanceId)
                {
                    throttle = position;
                }
            }
        }

        return new FlightAxes(roll, pitch, chartX, chartY, speedUp, slowDown, throttle);
    }

    /// <summary>Keep whichever value is further from the centre.</summary>
    private static void Furthest(ref float current, float value)
    {
        if (MathF.Abs(value) > MathF.Abs(current))
        {
            current = value;
        }
    }

    /// <summary>A centred axis with a dead zone, rescaled to start from zero at its edge.</summary>
    private static float Centred(float? value, float deadZone)
    {
        if (value is not { } v || MathF.Abs(v) <= deadZone)
        {
            return 0;
        }

        return MathF.Sign(v) * Math.Min(1f, (MathF.Abs(v) - deadZone) / (1f - deadZone));
    }

    /// <summary>An axis used as a trigger: how far it is pushed in the positive direction, with a dead zone.</summary>
    private static float Trigger(float? value) => value is { } v ? ShapeTrigger(v) : 0;

    /// <summary>
    /// Apply a radial dead zone and a response curve to a gamepad's stick,
    /// rescaling so the output starts from zero at the edge of the dead zone.
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
        if (_sdl != null)
        {
            _thread.Join(500);
            _sdl.QuitSubSystem(Sdl.InitGamecontroller | Sdl.InitJoystick);
        }
    }

    /// <summary>A box so a snapshot can be swapped atomically between threads.</summary>
    private sealed record Box<T>(T Value);
}

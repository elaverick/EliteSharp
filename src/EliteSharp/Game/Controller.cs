using EliteSharp.Input;

namespace EliteSharp.Game;

/// <summary>
/// The game controller. The buttons are turned into key presses by the
/// <see cref="GamepadMapper"/>, so here we only deal with the analogue inputs.
///
/// The BBC Master version supports an analogue joystick: when it is configured
/// (JSTK), DK15 in DOKEY copies the high bytes of the joystick's ADC channels
/// straight into JSTX and JSTY, the roll and pitch rates that the keyboard
/// otherwise nudges up and down in steps. The left stick is fed through
/// exactly that code, so it flies just like the original's analogue joystick
/// (including the Y-channel and reverse-both options, and the key damping in
/// the main loop). To let the keyboard and controller be used together, the
/// stick only takes over while it is deflected; when it springs back to the
/// centre it sets the rates back to the centre (as a sprung joystick would)
/// and hands control back to the keys. Selecting joystick mode with "K" while
/// paused makes the stick take over permanently, as in the original.
///
/// The speed controls are digital: each iteration of the main loop, holding
/// Space or "?" changes the speed by one. The triggers drive these keys with
/// pulse-width modulation, so the speed changes at a rate proportional to how
/// far the trigger is pressed (a fully pressed trigger is the same as holding
/// the key down).
/// </summary>
public sealed partial class EliteGame
{
    private readonly Gamepad? _gamepad;

    /// <summary>Overrides the controller context (for the title screens and the pause loop).</summary>
    private PadContext? _padContextOverride;

    /// <summary>Whether the left stick was controlling the roll and pitch last time round.</summary>
    private bool _padFlying;

    /// <summary>The pulse-width modulation accumulators for the triggers.</summary>
    private float _accelerateCycle, _decelerateCycle;

    private int JSTGY => ToggleOptions[4];
    private int JSTE => ToggleOptions[5];

    /// <summary>Whether a controller is connected.</summary>
    private bool PadConnected => _gamepad?.Connected == true;

    /// <summary>Tell the controller mapper what the game is doing.</summary>
    private void UpdatePadContext()
    {
        if (_gamepad == null)
        {
            return;
        }

        _gamepad.Mapper.Context = _padContextOverride ?? (QQ11 == 0 ? PadContext.SpaceView : PadContext.Screen);
        _gamepad.Mapper.DockingComputerOn = Auto != 0;
    }

    /// <summary>
    /// Convert a stick's x-axis into the high byte of a BBC ADC channel
    /// (JOPOS), where full left is &amp;FF and full right is 0.
    /// </summary>
    private static int AdcX(float x) => Math.Clamp(128 - (int)MathF.Round(x * 127), 0, 255);

    /// <summary>
    /// Convert a stick's y-axis (which is positive when pulled back) into the
    /// high byte of a BBC ADC channel, where full forward is &amp;FF.
    /// </summary>
    private static int AdcY(float y) => Math.Clamp(128 - (int)MathF.Round(y * 127), 0, 255);

    /// <summary>DK15: the roll rate from the joystick's x-channel.</summary>
    private int StickJSTX(float x) => ((AdcX(x) ^ JSTE) | 1) & 0xFF;

    /// <summary>DK15: the pitch rate from the joystick's y-channel.</summary>
    private int StickJSTY(float y) => (AdcY(y) ^ 0xFF ^ JSTE ^ JSTGY) & 0xFF;

    /// <summary>
    /// Apply the controller's triggers to the speed keys (KY2 is Space, speed
    /// up, and KY1 is "?", slow down).
    /// </summary>
    private void ApplyPadTriggers()
    {
        if (!PadConnected)
        {
            return;
        }

        var axes = _gamepad!.Axes;
        if (Pulse(ref _accelerateCycle, axes.RightTrigger))
        {
            KY2 = true;
        }

        if (Pulse(ref _decelerateCycle, axes.LeftTrigger))
        {
            KY1 = true;
        }
    }

    /// <summary>
    /// Pulse-width modulation: returns true on a proportion of calls equal to
    /// the value (0 to 1). The first call after the value becomes non-zero
    /// always returns true, so a light touch still responds straight away.
    /// </summary>
    private static bool Pulse(ref float cycle, float value)
    {
        if (value <= 0)
        {
            cycle = 0;
            return false;
        }

        if (cycle == 0)
        {
            cycle = 1;
        }
        else
        {
            cycle += value;
        }

        if (cycle >= 1)
        {
            cycle -= 1;
            if (cycle == 0)
            {
                cycle = float.Epsilon;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// DK15: read the joystick into the roll and pitch rates. Returns true if
    /// the stick is in control, in which case the roll and pitch keys are
    /// ignored (as in the original when the joystick is configured).
    /// </summary>
    private bool ReadPadStick()
    {
        if (!PadConnected)
        {
            _padFlying = false;
            return false;
        }

        var axes = _gamepad!.Axes;
        if (JSTK != 0 || axes.LeftStickActive)
        {
            JSTX = StickJSTX(axes.LeftX);
            JSTY = StickJSTY(axes.LeftY);
            _padFlying = true;
            return true;
        }

        if (_padFlying)
        {
            // The stick has sprung back to the centre, so centre the controls
            // and hand them back to the keyboard
            JSTX = 128;
            JSTY = 128;
            _padFlying = false;
        }

        return false;
    }

    /// <summary>
    /// TT17 with the joystick: move the chart cross-hairs with the stick,
    /// returning false if no stick is being used. Either stick can be used
    /// (the right stick takes priority).
    /// </summary>
    private bool ReadPadCursor(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!PadConnected)
        {
            return false;
        }

        var axes = _gamepad!.Axes;
        float sx, sy;
        if (axes.RightStickActive)
        {
            (sx, sy) = (axes.RightX, axes.RightY);
        }
        else if (axes.LeftStickActive || JSTK != 0)
        {
            (sx, sy) = (axes.LeftX, axes.LeftY);
        }
        else
        {
            return false;
        }

        y = TJS1(StickJSTY(sy));
        x = TJS1(StickJSTX(sx) ^ 0xFF);
        return true;
    }

    /// <summary>TJS1: A = round(A / 32) - 4, turning a joystick value into a cursor movement of -4 to +4.</summary>
    private static int TJS1(int a) => (a >> 5) + ((a >> 4) & 1) - 4;
}

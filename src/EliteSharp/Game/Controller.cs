using EliteSharp.Input;

namespace EliteSharp.Game;

/// <summary>
/// The game controllers. The buttons are turned into key presses by the
/// <see cref="ControllerMapper"/>, so here we only deal with the analogue
/// inputs: the axes bound to roll, pitch, the throttle, the speed triggers and
/// the chart's cross-hairs (see <see cref="FlightAxes"/>).
///
/// The BBC Master version supports an analogue joystick: when it is configured
/// (JSTK), DK15 in DOKEY copies the high bytes of the joystick's ADC channels
/// straight into JSTX and JSTY, the roll and pitch rates that the keyboard
/// otherwise nudges up and down in steps. The roll and pitch axes (a
/// gamepad's left stick, or a HOTAS's or yoke's stick) are fed through exactly
/// that code, so they fly just like the original's analogue joystick
/// (including the Y-channel and reverse-both options, and the key damping in
/// the main loop). To let the keyboard and controllers be used together, the
/// stick only takes over while it is deflected; when it springs back to the
/// centre it sets the rates back to the centre (as a sprung joystick would)
/// and hands control back to the keys. Selecting joystick mode with "K" while
/// paused makes the stick take over permanently, as in the original.
///
/// The Master also supports the Bitstik, whose rotation sets the speed
/// directly (BS2 in the main loop), and a throttle lever is fed through that
/// code: its position is the speed, from 0 when it is fully back to 40 when it
/// is fully forward, and the speed keys only work while it is at 0.
///
/// Otherwise, the speed controls are digital: each iteration of the main
/// loop, holding Space or "?" changes the speed by one. The speed triggers
/// drive these keys with pulse-width modulation, so the speed changes at a
/// rate proportional to how far the trigger is pressed (a fully pressed
/// trigger is the same as holding the key down).
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The game controllers, or null if controllers aren't being used.</summary>
    private readonly Controllers? _controllers;

    /// <summary>Overrides the controller context (for the title screens and the pause loop).</summary>
    private PadContext? _padContextOverride;

    /// <summary>Whether the stick was controlling the roll and pitch last time round.</summary>
    private bool _padFlying;

    /// <summary>The pulse-width modulation accumulators for the speed triggers.</summary>
    private float _accelerateCycle, _decelerateCycle;

    /// <summary>
    /// JSTGY: &amp;FF for the standard joystick y-channel (the default), or 0 if it
    /// is reversed (toggled with "Y" while paused).
    /// </summary>
    private int JoystickYStandard => ToggleOptions[4];

    /// <summary>JSTE: &amp;FF if both joystick channels are reversed (toggled with "J" while paused).</summary>
    private int JoystickReversed => ToggleOptions[5];

    /// <summary>Whether a controller is connected.</summary>
    private bool PadConnected => _controllers?.Connected == true;

    /// <summary>Tell the controller mapper what the game is doing.</summary>
    private void UpdatePadContext()
    {
        if (_controllers == null)
        {
            return;
        }

        _controllers.Mapper.Context = _padContextOverride ?? (_viewType == 0 ? PadContext.SpaceView : PadContext.Screen);
        _controllers.Mapper.DockingComputerOn = _autoDocking != 0;
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
    private int StickRollRate(float x) => ((AdcX(x) ^ JoystickReversed) | 1) & 0xFF;

    /// <summary>DK15: the pitch rate from the joystick's y-channel.</summary>
    private int StickPitchRate(float y) => (AdcY(y) ^ 0xFF ^ JoystickReversed ^ JoystickYStandard) & 0xFF;

    /// <summary>
    /// The throttle as the high byte of the Bitstik's rotation channel
    /// (JOPOS+2), scaled so that the throttle's full travel covers the speeds
    /// from 0 to 40 (which the main loop gets by dividing it by 4), or null if
    /// no throttle is bound. The throttle is left out while the docking
    /// computer is on, as it sets the speed itself.
    /// </summary>
    private int? ReadPadThrottle()
    {
        if (!PadConnected || _autoDocking != 0 || _controllers!.Axes.Throttle is not { } throttle)
        {
            return null;
        }

        return Math.Clamp((int)MathF.Round(throttle * 160), 0, 255);
    }

    /// <summary>
    /// Apply the controllers' speed triggers to the speed keys (KY2 is Space,
    /// speed up, and KY1 is "?", slow down).
    /// </summary>
    private void ApplyPadTriggers()
    {
        if (!PadConnected)
        {
            return;
        }

        var axes = _controllers!.Axes;
        if (Pulse(ref _accelerateCycle, axes.SpeedUp))
        {
            _keySpeedUp = true;
        }

        if (Pulse(ref _decelerateCycle, axes.SlowDown))
        {
            _keySlowDown = true;
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

        var axes = _controllers!.Axes;
        if (JoystickEnabled != 0 || axes.StickActive)
        {
            _rollRate = StickRollRate(axes.Roll);
            _pitchRate = StickPitchRate(axes.Pitch);
            _padFlying = true;
            return true;
        }

        if (_padFlying)
        {
            // The stick has sprung back to the centre, so centre the controls
            // and hand them back to the keyboard
            _rollRate = 128;
            _pitchRate = 128;
            _padFlying = false;
        }

        return false;
    }

    /// <summary>
    /// TT17 with the joystick: move the chart cross-hairs with the stick,
    /// returning false if no stick is being used. The cross-hairs' axes (a
    /// gamepad's right stick) take priority over the roll and pitch axes.
    /// </summary>
    private bool ReadPadCursor(out int deltaX, out int deltaY)
    {
        deltaX = 0;
        deltaY = 0;
        if (!PadConnected)
        {
            return false;
        }

        var axes = _controllers!.Axes;
        float stickX, stickY;
        if (axes.ChartActive)
        {
            (stickX, stickY) = (axes.ChartX, axes.ChartY);
        }
        else if (axes.StickActive || JoystickEnabled != 0)
        {
            (stickX, stickY) = (axes.Roll, axes.Pitch);
        }
        else
        {
            return false;
        }

        deltaY = JoystickToCursorStep(StickPitchRate(stickY));
        deltaX = JoystickToCursorStep(StickRollRate(stickX) ^ 0xFF);
        return true;
    }

    /// <summary>How far the stick must be pushed to move through a list (such as on the trading screens).</summary>
    private const float PadListThreshold = 0.4f;

    /// <summary>
    /// The direction the stick (the roll and pitch axes) is pushed in, for
    /// moving through lists: -1, 0 or +1 along whichever axis it is pushed
    /// further (with +y down).
    /// </summary>
    private (int X, int Y) ReadPadStickDirection()
    {
        if (!PadConnected)
        {
            return (0, 0);
        }

        var axes = _controllers!.Axes;
        float x = axes.Roll, y = axes.Pitch;
        if (MathF.Max(MathF.Abs(x), MathF.Abs(y)) < PadListThreshold)
        {
            return (0, 0);
        }

        return MathF.Abs(x) > MathF.Abs(y) ? (MathF.Sign(x), 0) : (0, MathF.Sign(y));
    }

    /// <summary>TJS1: A = round(A / 32) - 4, turning a joystick value into a cursor movement of -4 to +4.</summary>
    private static int JoystickToCursorStep(int value) => (value >> 5) + ((value >> 4) & 1) - 4;
}

namespace EliteSharp.Input;

/// <summary>What the game is doing, which decides what the controller's buttons do.</summary>
public enum PadContext
{
    /// <summary>In flight, looking out of the cockpit (the space view).</summary>
    SpaceView,

    /// <summary>Any other screen: charts, status, market, title screens, and everything while docked.</summary>
    Screen,

    /// <summary>The game is paused.</summary>
    Paused,
}

/// <summary>
/// Turns controller buttons into BBC key presses. The mapping depends on the
/// game's context, and holding the left shoulder button (LB) gives a second
/// layer of functions. The keys a button produces are decided when the button
/// is pressed, so changing context or layer while a button is held doesn't
/// produce phantom presses.
///
/// Space view:
///   A fire laser, B launch missile, X target missile, Y unarm missile,
///   RB E.C.M., D-pad front/rear/left/right view, L3 in-system jump,
///   R3 docking computer on/off, Start pause, Back short-range chart
///
/// Other screens (and when docked):
///   D-pad cursor keys (the sticks also move the chart cross-hairs), A Return,
///   Y "Y", B "N" (for the yes/no prompts), RB Shift (fast cross-hairs),
///   Start front view (or launch when docked), Back short-range chart,
///   L3 in-system jump, R3 docking computer on/off
///
/// With LB held (in either of the above):
///   D-pad up status, down market prices, left short-range chart, right
///   long-range chart; A hyperspace, B galactic hyperspace, X data on system,
///   Y inventory; RB energy bomb; Back escape pod; Start equip ship
///
/// Paused:
///   Start or B resume, Back quit to the title screen, D-pad left/right
///   volume down/up, D-pad up/down sound on/off, Y reverse joystick Y channel,
///   X toggle joystick-only mode, RB the Settings screen
/// </summary>
public sealed class GamepadMapper(BbcKeyboard keyboard)
{
    private static readonly PadButton[] AllButtons = Enum.GetValues<PadButton>().Where(b => b != PadButton.None).ToArray();

    private readonly Dictionary<PadButton, int[]> _pressedKeys = [];
    private PadButton _previous;

    /// <summary>The game's current context (set by the game thread).</summary>
    public volatile PadContext Context;

    /// <summary>Whether the docking computer is on (so R3 turns it off).</summary>
    public volatile bool DockingComputerOn;

    /// <summary>Update the key presses from the current state of the buttons.</summary>
    public void Update(PadButton buttons)
    {
        var context = Context;
        bool layer = context != PadContext.Paused && (buttons & PadButton.LeftShoulder) != 0;
        var newlyPressed = new List<int>();

        foreach (var button in AllButtons)
        {
            bool down = (buttons & button) != 0;
            bool wasDown = (_previous & button) != 0;
            if (down && !wasDown)
            {
                int[] keys = Map(button, context, layer);
                _pressedKeys[button] = keys;
                newlyPressed.AddRange(keys);
            }
            else if (!down && wasDown)
            {
                _pressedKeys.Remove(button);
            }
        }

        _previous = buttons;
        keyboard.SetPadKeys(_pressedKeys.Values.SelectMany(k => k), newlyPressed);
    }

    /// <summary>Ask for the Settings screen (which has no BBC key), pressing no keys.</summary>
    private int[] RequestSettings()
    {
        keyboard.RequestSettings();
        return [];
    }

    private int[] Map(PadButton button, PadContext context, bool layer)
    {
        if (context == PadContext.Paused)
        {
            return button switch
            {
                PadButton.Start or PadButton.B => [BbcKeyboard.Delete],
                PadButton.Back => [BbcKeyboard.Escape],
                PadButton.DpadLeft => [BbcKeyboard.Comma],
                PadButton.DpadRight => [BbcKeyboard.Period],
                PadButton.DpadUp => [BbcKeyboard.S],
                PadButton.DpadDown => [BbcKeyboard.Q],
                PadButton.Y => [BbcKeyboard.Y],
                PadButton.X => [BbcKeyboard.K],
                PadButton.RightShoulder => RequestSettings(),
                _ => [],
            };
        }

        if (layer)
        {
            return button switch
            {
                PadButton.DpadUp => [BbcKeyboard.F8],
                PadButton.DpadDown => [BbcKeyboard.F7],
                PadButton.DpadLeft => [BbcKeyboard.F5],
                PadButton.DpadRight => [BbcKeyboard.F4],
                PadButton.A => [BbcKeyboard.H],
                PadButton.B => [BbcKeyboard.Ctrl, BbcKeyboard.H],
                PadButton.X => [BbcKeyboard.F6],
                PadButton.Y => [BbcKeyboard.F9],
                PadButton.RightShoulder => [BbcKeyboard.Tab],
                PadButton.Back => [BbcKeyboard.Escape],
                PadButton.Start => [BbcKeyboard.F3],
                _ => [],
            };
        }

        // Buttons that do the same thing in the space view and other screens
        switch (button)
        {
            case PadButton.LeftStick:
                return [BbcKeyboard.J];
            case PadButton.RightStick:
                return [DockingComputerOn ? BbcKeyboard.P : BbcKeyboard.C];
            case PadButton.Back:
                return [BbcKeyboard.F5];
        }

        if (context == PadContext.SpaceView)
        {
            return button switch
            {
                PadButton.A => [BbcKeyboard.A],
                PadButton.B => [BbcKeyboard.M],
                PadButton.X => [BbcKeyboard.T],
                PadButton.Y => [BbcKeyboard.U],
                PadButton.RightShoulder => [BbcKeyboard.E],
                PadButton.Start => [BbcKeyboard.Copy],
                PadButton.DpadUp => [BbcKeyboard.F0],
                PadButton.DpadDown => [BbcKeyboard.F1],
                PadButton.DpadLeft => [BbcKeyboard.F2],
                PadButton.DpadRight => [BbcKeyboard.F3],
                _ => [],
            };
        }

        return button switch
        {
            PadButton.A => [BbcKeyboard.Return],
            PadButton.B => [BbcKeyboard.N],
            PadButton.Y => [BbcKeyboard.Y],
            PadButton.RightShoulder => [BbcKeyboard.Shift],
            PadButton.Start => [BbcKeyboard.F0],
            PadButton.DpadUp => [BbcKeyboard.Up],
            PadButton.DpadDown => [BbcKeyboard.Down],
            PadButton.DpadLeft => [BbcKeyboard.Left],
            PadButton.DpadRight => [BbcKeyboard.Right],
            _ => [],
        };
    }
}

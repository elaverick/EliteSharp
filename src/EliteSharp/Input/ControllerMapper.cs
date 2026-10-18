namespace EliteSharp.Input;

/// <summary>What the game is doing, which decides what the controllers' buttons do.</summary>
public enum PadContext
{
    /// <summary>In flight, looking out of the cockpit (the space view).</summary>
    SpaceView,

    /// <summary>Any other screen: charts, status, market, title screens, and everything while docked.</summary>
    Screen,

    /// <summary>The game is paused.</summary>
    Paused,

    /// <summary>Typing a line of text (such as a save's label), where the controllers can only accept, cancel or delete.</summary>
    TextEntry,
}

/// <summary>
/// Turns the controllers' buttons into BBC key presses, with each
/// controller's bindings (see <see cref="ControllerBindings"/>). The page of
/// bindings depends on the game's context, and in flight and on the other
/// screens, holding the button bound to <see cref="PadAction.SecondFunctions"/>
/// switches to the second functions. The keys a button produces are decided
/// when the button is pressed, so changing context or page while a button is
/// held doesn't produce phantom presses.
/// </summary>
public sealed class ControllerMapper(BbcKeyboard keyboard)
{
    private readonly Dictionary<(int Device, ControlInput Input), int[]> _pressedKeys = [];
    private readonly Dictionary<int, HashSet<ControlInput>> _previous = [];

    /// <summary>The game's current context (set by the game thread).</summary>
    public volatile PadContext Context;

    /// <summary>Whether the docking computer is on (so its button turns it off).</summary>
    public volatile bool DockingComputerOn;

    /// <summary>
    /// Update the key presses from the buttons (and hat directions) pressed on
    /// each controller, given by its instance ID with its bindings. If
    /// <paramref name="suppressed"/> is set, the presses are noted (so they
    /// don't count as presses later) but press no keys.
    /// </summary>
    public void Update(IReadOnlyList<(int Device, ControllerBindings Bindings, IReadOnlyCollection<ControlInput> Pressed)> devices, bool suppressed = false)
    {
        var context = Context;
        var newlyPressed = new List<int>();

        foreach (var (device, bindings, pressed) in devices)
        {
            var previous = _previous.GetValueOrDefault(device) ?? [];
            var page = PageFor(context, bindings, pressed);

            foreach (var input in pressed)
            {
                if (!previous.Contains(input))
                {
                    int[] keys = suppressed || bindings.ActionFor(page, input) is not { } action ? [] : KeysFor(action);
                    _pressedKeys[(device, input)] = keys;
                    newlyPressed.AddRange(keys);
                }
            }

            foreach (var input in previous)
            {
                if (!pressed.Contains(input))
                {
                    _pressedKeys.Remove((device, input));
                }
            }

            _previous[device] = [.. pressed];
        }

        // Forget the controllers that have gone
        foreach (int device in _previous.Keys.Where(d => !devices.Any(x => x.Device == d)).ToList())
        {
            _previous.Remove(device);
            foreach (var key in _pressedKeys.Keys.Where(k => k.Device == device).ToList())
            {
                _pressedKeys.Remove(key);
            }
        }

        keyboard.SetPadKeys(_pressedKeys.Values.SelectMany(k => k), newlyPressed);
    }

    /// <summary>The page of bindings for a context, with the second functions if their button is held.</summary>
    public static ControlPage PageFor(PadContext context, ControllerBindings bindings, IReadOnlyCollection<ControlInput> pressed)
    {
        switch (context)
        {
            case PadContext.Paused:
                return ControlPage.Paused;
            case PadContext.TextEntry:
                return ControlPage.Typing;
        }

        if (bindings.InputsFor(ControlPage.SecondFunctions, PadAction.SecondFunctions).Any(pressed.Contains))
        {
            return ControlPage.SecondFunctions;
        }

        return context == PadContext.SpaceView ? ControlPage.Flight : ControlPage.Screens;
    }

    /// <summary>The BBC keys an action presses (asking for the Settings screen, which has no key, presses none).</summary>
    private int[] KeysFor(PadAction action)
    {
        switch (action)
        {
            case PadAction.Settings:
                keyboard.RequestSettings();
                return [];
            case PadAction.DockingComputer:
                return [DockingComputerOn ? BbcKeyboard.P : BbcKeyboard.C];
        }

        return Keys(action);
    }

    /// <summary>The BBC keys an action presses.</summary>
    public static int[] Keys(PadAction action) => action switch
    {
        PadAction.FireLaser => [BbcKeyboard.A],
        PadAction.LaunchMissile => [BbcKeyboard.M],
        PadAction.TargetMissile => [BbcKeyboard.T],
        PadAction.UnarmMissile => [BbcKeyboard.U],
        PadAction.Ecm => [BbcKeyboard.E],
        PadAction.EnergyBomb => [BbcKeyboard.Tab],
        PadAction.EscapePod => [BbcKeyboard.Escape],
        PadAction.SpeedUp => [BbcKeyboard.Space],
        PadAction.SlowDown => [BbcKeyboard.Slash],
        PadAction.RollLeft => [BbcKeyboard.Comma],
        PadAction.RollRight => [BbcKeyboard.Period],
        PadAction.Climb => [BbcKeyboard.X],
        PadAction.Dive => [BbcKeyboard.S],
        PadAction.FrontView or PadAction.Launch => [BbcKeyboard.F0],
        PadAction.RearView => [BbcKeyboard.F1],
        PadAction.LeftView => [BbcKeyboard.F2],
        PadAction.RightView or PadAction.EquipShip => [BbcKeyboard.F3],
        PadAction.GalacticChart => [BbcKeyboard.F4],
        PadAction.ShortRangeChart => [BbcKeyboard.F5],
        PadAction.DataOnSystem => [BbcKeyboard.F6],
        PadAction.MarketPrices => [BbcKeyboard.F7],
        PadAction.Status => [BbcKeyboard.F8],
        PadAction.Inventory => [BbcKeyboard.F9],
        PadAction.Hyperspace => [BbcKeyboard.H],
        PadAction.GalacticHyperspace => [BbcKeyboard.Ctrl, BbcKeyboard.H],
        PadAction.InSystemJump => [BbcKeyboard.J],
        PadAction.DockingComputer => [BbcKeyboard.C],
        PadAction.Pause => [BbcKeyboard.Copy],
        PadAction.Select or PadAction.Accept => [BbcKeyboard.Return],
        PadAction.Yes => [BbcKeyboard.Y],
        PadAction.No => [BbcKeyboard.N],
        PadAction.Delete or PadAction.Resume => [BbcKeyboard.Delete],
        PadAction.CursorUp => [BbcKeyboard.Up],
        PadAction.CursorDown => [BbcKeyboard.Down],
        PadAction.CursorLeft => [BbcKeyboard.Left],
        PadAction.CursorRight => [BbcKeyboard.Right],
        PadAction.FastCursor => [BbcKeyboard.Shift],
        PadAction.Quit or PadAction.Cancel => [BbcKeyboard.Escape],
        PadAction.VolumeDown => [BbcKeyboard.Comma],
        PadAction.VolumeUp => [BbcKeyboard.Period],
        PadAction.SoundOn => [BbcKeyboard.S],
        PadAction.SoundOff => [BbcKeyboard.Q],
        PadAction.ReversePitch => [BbcKeyboard.Y],
        PadAction.ReverseBoth => [BbcKeyboard.J],
        PadAction.ControllerOnly => [BbcKeyboard.K],
        _ => [],
    };
}

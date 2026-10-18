using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Input;

/// <summary>The directions of a hat switch, as SDL reports them (a diagonal is two directions at once).</summary>
[Flags]
public enum HatDirection : byte
{
    None = 0,
    Up = 1,
    Right = 2,
    Down = 4,
    Left = 8,
}

/// <summary>The kinds of control on a controller.</summary>
public enum InputKind
{
    Button,
    Hat,
    Axis,
}

/// <summary>
/// One control on a controller: a button, one direction of a hat switch, or
/// an axis. Buttons and axes are numbered as SDL numbers them (for a gamepad,
/// in SDL's game controller layout; for a joystick, as the device reports
/// them), from 0.
/// </summary>
public readonly record struct ControlInput(InputKind Kind, int Index, HatDirection Direction = HatDirection.None)
{
    public static ControlInput Button(int index) => new(InputKind.Button, index);

    public static ControlInput Hat(int index, HatDirection direction) => new(InputKind.Hat, index, direction);

    public static ControlInput Axis(int index) => new(InputKind.Axis, index);

    /// <summary>The input as it is written in the files: "button 3", "hat 0 up" or "axis 2".</summary>
    public override string ToString() => Kind switch
    {
        InputKind.Button => $"button {Index}",
        InputKind.Hat => $"hat {Index} {Direction.ToString().ToLowerInvariant()}",
        _ => $"axis {Index}",
    };

    /// <summary>Read an input written as <see cref="ToString"/> writes it.</summary>
    public static bool TryParse(string text, out ControlInput input)
    {
        input = default;
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return false;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "button" when parts.Length == 2:
                input = Button(index);
                return true;
            case "axis" when parts.Length == 2:
                input = Axis(index);
                return true;
            case "hat" when parts.Length == 3 && Enum.TryParse(parts[2], true, out HatDirection direction)
                && direction is HatDirection.Up or HatDirection.Right or HatDirection.Down or HatDirection.Left:
                input = Hat(index, direction);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// The sets of button bindings, one for each thing the game can be doing
/// (see <see cref="PadContext"/>). The second functions are the buttons'
/// functions in flight and on the other screens while the button bound to
/// <see cref="PadAction.SecondFunctions"/> is held.
/// </summary>
public enum ControlPage
{
    Flight,
    Screens,
    SecondFunctions,
    Paused,
    Typing,
}

/// <summary>What a button can do: each presses one or more BBC keys (or, for a few, does something else).</summary>
public enum PadAction
{
    FireLaser,
    LaunchMissile,
    TargetMissile,
    UnarmMissile,
    Ecm,
    EnergyBomb,
    EscapePod,
    SpeedUp,
    SlowDown,
    RollLeft,
    RollRight,
    Climb,
    Dive,
    FrontView,
    RearView,
    LeftView,
    RightView,
    InSystemJump,
    DockingComputer,
    Hyperspace,
    GalacticHyperspace,
    ShortRangeChart,
    GalacticChart,
    DataOnSystem,
    MarketPrices,
    Status,
    Inventory,
    Launch,
    EquipShip,
    Pause,
    Select,
    Yes,
    No,
    Delete,
    CursorUp,
    CursorDown,
    CursorLeft,
    CursorRight,
    FastCursor,
    Settings,

    /// <summary>Held for the second functions (it presses no keys itself).</summary>
    SecondFunctions,

    // While paused
    Resume,
    Quit,
    VolumeDown,
    VolumeUp,
    SoundOn,
    SoundOff,
    ReversePitch,
    ReverseBoth,
    ControllerOnly,

    // While typing
    Accept,
    Cancel,
}

/// <summary>What an axis can do.</summary>
public enum AxisFunction
{
    /// <summary>Roll (positive to the right), like the BBC joystick's x-channel.</summary>
    Roll,

    /// <summary>Pitch (positive when pulled back, to climb), like the BBC joystick's y-channel.</summary>
    Pitch,

    /// <summary>A throttle lever, whose position is the speed (positive is forward, faster).</summary>
    Throttle,

    /// <summary>A trigger that speeds up at a rate that depends on how far it is pressed.</summary>
    SpeedUp,

    /// <summary>A trigger that slows down at a rate that depends on how far it is pressed.</summary>
    SlowDown,

    /// <summary>Moves the cross-hairs on the charts across (positive to the right).</summary>
    ChartX,

    /// <summary>Moves the cross-hairs on the charts up and down (positive down).</summary>
    ChartY,
}

/// <summary>An axis bound to an <see cref="AxisFunction"/>, which can work the other way round.</summary>
public readonly record struct AxisBinding(int Axis, bool Reversed)
{
    /// <summary>The binding as it is written in the files: "axis 2", or "axis 2 reversed".</summary>
    public override string ToString() => Reversed ? $"axis {Axis} reversed" : $"axis {Axis}";

    /// <summary>Read a binding written as <see cref="ToString"/> writes it.</summary>
    public static bool TryParse(string text, out AxisBinding binding)
    {
        binding = default;
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || !parts[0].Equals("axis", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int axis))
        {
            return false;
        }

        bool reversed = parts.Length == 3;
        if (reversed && !parts[2].Equals("reversed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        binding = new AxisBinding(axis, reversed);
        return true;
    }
}

/// <summary>
/// What a controller's controls do: which axis does each
/// <see cref="AxisFunction"/>, and on each <see cref="ControlPage"/>, which
/// buttons (or hat directions) do each <see cref="PadAction"/>. On a page, a
/// button does at most one action, but an action can have more than one
/// button.
///
/// The bindings are written in YAML (in a controller's profile, as its
/// defaults, and in the player's controllers.yml), as a mapping of "axes"
/// and the pages' names to mappings of the functions' and actions' names
/// (in snake case, such as fire_laser) to inputs (such as "button 0", or a
/// list of them).
/// </summary>
public sealed class ControllerBindings
{
    private readonly Dictionary<AxisFunction, AxisBinding> _axes = [];
    private readonly Dictionary<ControlPage, Dictionary<PadAction, List<ControlInput>>> _pages =
        Enum.GetValues<ControlPage>().ToDictionary(p => p, _ => new Dictionary<PadAction, List<ControlInput>>());

    /// <summary>The actions on the flight, other screens and second functions pages (the game's keys).</summary>
    private static readonly PadAction[] GameActions =
    [
        PadAction.FireLaser, PadAction.LaunchMissile, PadAction.TargetMissile, PadAction.UnarmMissile, PadAction.Ecm,
        PadAction.EnergyBomb, PadAction.EscapePod, PadAction.SpeedUp, PadAction.SlowDown, PadAction.RollLeft,
        PadAction.RollRight, PadAction.Climb, PadAction.Dive, PadAction.FrontView, PadAction.RearView, PadAction.LeftView,
        PadAction.RightView, PadAction.InSystemJump, PadAction.DockingComputer, PadAction.Hyperspace,
        PadAction.GalacticHyperspace, PadAction.ShortRangeChart, PadAction.GalacticChart, PadAction.DataOnSystem,
        PadAction.MarketPrices, PadAction.Status, PadAction.Inventory, PadAction.Launch, PadAction.EquipShip,
        PadAction.Pause, PadAction.Select, PadAction.Yes, PadAction.No, PadAction.Delete, PadAction.CursorUp,
        PadAction.CursorDown, PadAction.CursorLeft, PadAction.CursorRight, PadAction.FastCursor, PadAction.Settings,
    ];

    /// <summary>The actions for moving around the screens, which come first on that page.</summary>
    private static readonly PadAction[] ScreenActions =
    [
        PadAction.Select, PadAction.Yes, PadAction.No, PadAction.Delete, PadAction.CursorUp, PadAction.CursorDown,
        PadAction.CursorLeft, PadAction.CursorRight, PadAction.FastCursor, PadAction.Launch, PadAction.EquipShip,
    ];

    /// <summary>The actions that can be bound on each page, in the order the Controls screen lists them.</summary>
    public static IReadOnlyList<PadAction> ActionsOn(ControlPage page) => page switch
    {
        ControlPage.Flight => GameActions,
        ControlPage.Screens => [.. ScreenActions, .. GameActions.Except(ScreenActions)],
        ControlPage.SecondFunctions => [PadAction.SecondFunctions, .. GameActions],
        ControlPage.Paused =>
        [
            PadAction.Resume, PadAction.Quit, PadAction.VolumeDown, PadAction.VolumeUp, PadAction.SoundOn,
            PadAction.SoundOff, PadAction.ReversePitch, PadAction.ReverseBoth, PadAction.ControllerOnly, PadAction.Settings,
        ],
        _ => [PadAction.Accept, PadAction.Cancel, PadAction.Delete],
    };

    /// <summary>The axis bound to a function, if there is one.</summary>
    public AxisBinding? AxisFor(AxisFunction function) => _axes.TryGetValue(function, out var binding) ? binding : null;

    /// <summary>The inputs bound to an action on a page.</summary>
    public IReadOnlyList<ControlInput> InputsFor(ControlPage page, PadAction action) =>
        _pages[page].TryGetValue(action, out var inputs) ? inputs : [];

    /// <summary>The action an input does on a page, if any.</summary>
    public PadAction? ActionFor(ControlPage page, ControlInput input)
    {
        foreach (var (action, inputs) in _pages[page])
        {
            if (inputs.Contains(input))
            {
                return action;
            }
        }

        return null;
    }

    /// <summary>Bind an axis to a function (null unbinds it).</summary>
    public void SetAxis(AxisFunction function, AxisBinding? binding)
    {
        if (binding is { } value)
        {
            _axes[function] = value;
        }
        else
        {
            _axes.Remove(function);
        }
    }

    /// <summary>
    /// Bind an input to an action on a page, in place of the action's other
    /// inputs, and taking it from any other action on the page.
    /// </summary>
    public void Bind(ControlPage page, PadAction action, ControlInput input)
    {
        Remove(page, input);
        _pages[page][action] = [input];
    }

    /// <summary>Add an input to an action's inputs on a page, taking it from any other action on the page.</summary>
    public void Add(ControlPage page, PadAction action, ControlInput input)
    {
        Remove(page, input);
        if (!_pages[page].TryGetValue(action, out var inputs))
        {
            inputs = [];
            _pages[page][action] = inputs;
        }

        inputs.Add(input);
    }

    /// <summary>Unbind an action on a page.</summary>
    public void Clear(ControlPage page, PadAction action) => _pages[page].Remove(action);

    /// <summary>Take an input away from whichever action on the page has it.</summary>
    private void Remove(ControlPage page, ControlInput input)
    {
        foreach (var (action, inputs) in _pages[page].ToList())
        {
            if (inputs.Remove(input) && inputs.Count == 0)
            {
                _pages[page].Remove(action);
            }
        }
    }

    /// <summary>A copy of the bindings, to change without affecting these.</summary>
    public ControllerBindings Clone()
    {
        var copy = new ControllerBindings();
        foreach (var (function, binding) in _axes)
        {
            copy._axes[function] = binding;
        }

        foreach (var (page, actions) in _pages)
        {
            foreach (var (action, inputs) in actions)
            {
                copy._pages[page][action] = [.. inputs];
            }
        }

        return copy;
    }

    /// <summary>The name of a page, function or action in the files (snake case, such as "fire_laser").</summary>
    public static string FileName<T>(T value) where T : struct, Enum
    {
        string name = value.ToString();
        var text = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(name[i]));
        }

        return text.ToString();
    }

    private static bool TryParseName<T>(string name, out T value) where T : struct, Enum =>
        Enum.TryParse(name.Replace("_", ""), true, out value) && Enum.IsDefined(value);

    /// <summary>
    /// Read bindings from a YAML mapping. Pages that aren't in it are left
    /// empty, unless <paramref name="fallback"/> is given, in which case they
    /// (and the axes, if they aren't in it) are copied from that.
    /// </summary>
    /// <exception cref="InvalidDataException">The bindings aren't valid (the message says where and why).</exception>
    public static ControllerBindings Read(YamlMappingNode node, string source, ControllerBindings? fallback = null)
    {
        var bindings = new ControllerBindings();
        bool hasAxes = false;
        var pagesRead = new HashSet<ControlPage>();
        foreach (var (keyNode, valueNode) in node.Children)
        {
            string key = ((YamlScalarNode)keyNode).Value ?? "";
            if (valueNode is not YamlMappingNode entries)
            {
                throw new InvalidDataException($"{source}: '{key}' must be a mapping of names to inputs");
            }

            if (key == "axes")
            {
                hasAxes = true;
                foreach (var (nameNode, inputNode) in entries.Children)
                {
                    string name = ((YamlScalarNode)nameNode).Value ?? "";
                    if (!TryParseName(name, out AxisFunction function))
                    {
                        throw new InvalidDataException($"{source}: '{name}' isn't an axis function ({string.Join(", ", Enum.GetValues<AxisFunction>().Select(FileName))})");
                    }

                    string text = (inputNode as YamlScalarNode)?.Value ?? "";
                    if (!AxisBinding.TryParse(text, out var binding))
                    {
                        throw new InvalidDataException($"{source}: the {name} axis must be written like 'axis 2' or 'axis 2 reversed', not '{text}'");
                    }

                    bindings._axes[function] = binding;
                }

                continue;
            }

            if (!TryParseName(key, out ControlPage page))
            {
                throw new InvalidDataException($"{source}: '{key}' isn't 'axes' or a page ({string.Join(", ", Enum.GetValues<ControlPage>().Select(FileName))})");
            }

            pagesRead.Add(page);
            var allowed = ActionsOn(page);
            foreach (var (nameNode, inputNode) in entries.Children)
            {
                string name = ((YamlScalarNode)nameNode).Value ?? "";
                if (!TryParseName(name, out PadAction action) || !allowed.Contains(action))
                {
                    throw new InvalidDataException($"{source}: '{name}' isn't an action on the {key} page");
                }

                IEnumerable<YamlNode> items = inputNode is YamlSequenceNode list ? list.Children : [inputNode];
                foreach (var item in items)
                {
                    string text = (item as YamlScalarNode)?.Value ?? "";
                    if (!ControlInput.TryParse(text, out var input) || input.Kind == InputKind.Axis)
                    {
                        throw new InvalidDataException($"{source}: {name} on the {key} page must be written like 'button 3' or 'hat 0 up', not '{text}'");
                    }

                    bindings.Add(page, action, input);
                }
            }
        }

        if (fallback != null)
        {
            if (!hasAxes)
            {
                foreach (var (function, binding) in fallback._axes)
                {
                    bindings._axes[function] = binding;
                }
            }

            foreach (var page in Enum.GetValues<ControlPage>().Where(p => !pagesRead.Contains(p)))
            {
                foreach (var (action, inputs) in fallback._pages[page])
                {
                    bindings._pages[page][action] = [.. inputs];
                }
            }
        }

        return bindings;
    }

    /// <summary>Write the bindings as YAML, indented by the given number of spaces.</summary>
    public void Write(List<string> lines, int indent)
    {
        string pad = new(' ', indent);
        lines.Add($"{pad}axes:{(_axes.Count == 0 ? " {}" : "")}");
        foreach (var function in Enum.GetValues<AxisFunction>())
        {
            if (_axes.TryGetValue(function, out var binding))
            {
                lines.Add($"{pad}  {FileName(function)}: {binding}");
            }
        }

        foreach (var page in Enum.GetValues<ControlPage>())
        {
            var actions = _pages[page];
            lines.Add($"{pad}{FileName(page)}:{(actions.Count == 0 ? " {}" : "")}");
            foreach (var action in ActionsOn(page))
            {
                if (actions.TryGetValue(action, out var inputs))
                {
                    string value = inputs.Count == 1 ? inputs[0].ToString() : $"[{string.Join(", ", inputs)}]";
                    lines.Add($"{pad}  {FileName(action)}: {value}");
                }
            }
        }
    }
}

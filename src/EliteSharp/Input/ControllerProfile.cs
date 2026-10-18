using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Input;

/// <summary>The kinds of controller.</summary>
public enum ControllerKind
{
    /// <summary>A gamepad, read through SDL's game controller layout (an Xbox controller's).</summary>
    Gamepad,

    /// <summary>A stick and throttle (hands on throttle and stick).</summary>
    Hotas,

    /// <summary>A flight yoke.</summary>
    Yoke,

    /// <summary>Any other joystick.</summary>
    Joystick,
}

/// <summary>A control in a profile: its name, and where it is on the profile's picture (if it's shown there).</summary>
public sealed record ProfileControl(string Name, int? X = null, int? Y = null);

/// <summary>
/// A controller profile, from Assets/Controllers: what a model of controller
/// is called, how to recognise it (by its USB vendor and product IDs), the
/// names of its buttons, axes and hats (in the order the device reports
/// them) and where they are on its picture, and what they do until the
/// player changes them (see <see cref="ControllerBindings"/>).
///
/// A profile is a YAML file:
///
///   name: Saitek X52             the name to show
///   kind: hotas                  gamepad, hotas, yoke or joystick
///   devices: [06A3:075C]         the vendor and product IDs, in hexadecimal
///   gamepad_types: [ps5]         for a gamepad, the kinds SDL says it is
///   names: [GRS Flight Yoke]     or, for a device whose IDs aren't known, text in its name
///   image: x52.png               the picture, in the same folder (optional)
///   dead_zone: 0.05              the dead zone of the centred axes (optional)
///   buttons:                     the buttons' names, from button 0, each
///     - Trigger @ 412,180        followed by where it is in the picture
///     - "-"                      (a button whose name isn't known)
///   axes: [...]                  the axes' names, likewise
///   hats: [...]                  the hats' names, likewise
///   defaults: {...}              the bindings (see ControllerBindings)
///
/// A device that has more controls than its profile names shows the others
/// as "Button 35" and so on (counting from 1, as Windows does). The gamepad
/// profiles name SDL's game controller buttons and axes, in SDL's order: a
/// gamepad uses the profile for the kind of gamepad SDL says it is (such as
/// ps5.yml for a PlayStation 5 controller), or else xbox.yml. Any other
/// joystick that no profile matches uses generic.yml.
/// </summary>
public sealed class ControllerProfile
{
    /// <summary>The name of the gamepad profile's file (without its extension).</summary>
    public const string GamepadId = "xbox";

    /// <summary>The name of the profile for joysticks that no other profile matches.</summary>
    public const string GenericId = "generic";

    /// <summary>The id: the profile's file name without its extension (or, for an unrecognised joystick, one made from its GUID).</summary>
    public required string Id { get; init; }

    /// <summary>The controller's name.</summary>
    public required string Name { get; init; }

    public ControllerKind Kind { get; init; }

    /// <summary>The USB vendor and product IDs of the devices this profile is for.</summary>
    public IReadOnlyList<(ushort Vendor, ushort Product)> Devices { get; init; } = [];

    /// <summary>For a gamepad profile, the kinds of gamepad it is for (such as "ps5", see <see cref="Controllers"/>).</summary>
    public IReadOnlyList<string> GamepadTypes { get; init; } = [];

    /// <summary>Text in the names of the devices this profile is for (for devices whose USB IDs aren't known).</summary>
    public IReadOnlyList<string> DeviceNames { get; init; } = [];

    /// <summary>The path of the picture of the controller, if it has one.</summary>
    public string? ImagePath { get; init; }

    /// <summary>The dead zone of the centred axes (roll, pitch and the cross-hairs) of a joystick, as a fraction of their travel.</summary>
    public float DeadZone { get; init; } = 0.05f;

    public IReadOnlyList<ProfileControl> Buttons { get; init; } = [];

    public IReadOnlyList<ProfileControl> Axes { get; init; } = [];

    public IReadOnlyList<ProfileControl> Hats { get; init; } = [];

    /// <summary>The bindings until the player changes them.</summary>
    public ControllerBindings Defaults { get; init; } = new();

    public bool IsGamepad => Kind == ControllerKind.Gamepad;

    /// <summary>This profile with a different id and name (for an unrecognised joystick).</summary>
    public ControllerProfile WithId(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Kind = Kind,
        Devices = Devices,
        GamepadTypes = GamepadTypes,
        DeviceNames = DeviceNames,
        ImagePath = ImagePath,
        DeadZone = DeadZone,
        Buttons = Buttons,
        Axes = Axes,
        Hats = Hats,
        Defaults = Defaults,
    };

    /// <summary>The name of a button, axis or hat direction.</summary>
    public string InputName(ControlInput input) => input.Kind switch
    {
        InputKind.Button => Named(Buttons, input.Index, "Button"),
        InputKind.Axis => Named(Axes, input.Index, "Axis"),
        _ => $"{Named(Hats, input.Index, "Hat")} {input.Direction}",
    };

    private static string Named(IReadOnlyList<ProfileControl> controls, int index, string kind) =>
        index < controls.Count && controls[index].Name.Length > 0 ? controls[index].Name : $"{kind} {index + 1}";

    /// <summary>Where a control is on the picture, if the profile says.</summary>
    public (int X, int Y)? Position(ControlInput input)
    {
        var controls = input.Kind switch
        {
            InputKind.Button => Buttons,
            InputKind.Axis => Axes,
            _ => Hats,
        };

        return input.Index < controls.Count && controls[input.Index] is { X: { } x, Y: { } y } ? (x, y) : null;
    }

    /// <summary>Read a profile from the contents of its file.</summary>
    /// <exception cref="InvalidDataException">The profile isn't valid (the message says where and why).</exception>
    public static ControllerProfile Parse(string yaml, string id, string source, string folder)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException e)
        {
            throw new InvalidDataException($"{source} (line {e.Start.Line}): {e.Message}", e);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"{source}: the profile must be a mapping");
        }

        string? Scalar(string key) => root.Children.TryGetValue(key, out var node) ? (node as YamlScalarNode)?.Value : null;

        string name = Scalar("name") ?? throw new InvalidDataException($"{source}: the profile has no name");
        var kind = ControllerKind.Joystick;
        if (Scalar("kind") is { } kindText && !Enum.TryParse(kindText, true, out kind))
        {
            throw new InvalidDataException($"{source}: the kind must be gamepad, hotas, yoke or joystick, not '{kindText}'");
        }

        var devices = new List<(ushort, ushort)>();
        foreach (string text in Strings(root, "devices", source))
        {
            string[] parts = text.Split(':');
            if (parts.Length != 2
                || !ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort vendor)
                || !ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort product))
            {
                throw new InvalidDataException($"{source}: a device must be written as its vendor and product IDs in hexadecimal, like 06A3:075C, not '{text}'");
            }

            devices.Add((vendor, product));
        }

        string? imagePath = null;
        if (Scalar("image") is { } image)
        {
            imagePath = Path.Combine(folder, image);
            if (!File.Exists(imagePath))
            {
                throw new InvalidDataException($"{source}: the image '{imagePath}' is missing");
            }
        }

        float deadZone = 0.05f;
        if (Scalar("dead_zone") is { } deadZoneText
            && (!float.TryParse(deadZoneText, NumberStyles.Float, CultureInfo.InvariantCulture, out deadZone) || deadZone is < 0 or >= 1))
        {
            throw new InvalidDataException($"{source}: the dead zone must be a number from 0 to less than 1, not '{deadZoneText}'");
        }

        var defaults = root.Children.TryGetValue("defaults", out var defaultsNode)
            ? defaultsNode is YamlMappingNode mapping
                ? ControllerBindings.Read(mapping, source)
                : throw new InvalidDataException($"{source}: the defaults must be a mapping")
            : new ControllerBindings();

        return new ControllerProfile
        {
            Id = id,
            Name = name,
            Kind = kind,
            Devices = devices,
            GamepadTypes = [.. Strings(root, "gamepad_types", source).Select(t => t.ToLowerInvariant())],
            DeviceNames = [.. Strings(root, "names", source)],
            ImagePath = imagePath,
            DeadZone = deadZone,
            Buttons = Controls(root, "buttons", source),
            Axes = Controls(root, "axes", source),
            Hats = Controls(root, "hats", source),
            Defaults = defaults,
        };
    }

    /// <summary>A list of strings (or a single string) in the profile.</summary>
    private static IEnumerable<string> Strings(YamlMappingNode root, string key, string source)
    {
        if (!root.Children.TryGetValue(key, out var node))
        {
            return [];
        }

        IEnumerable<YamlNode> items = node is YamlSequenceNode list ? list.Children : [node];
        return items.Select(item => (item as YamlScalarNode)?.Value
            ?? throw new InvalidDataException($"{source}: each of the {key} must be a single value"));
    }

    /// <summary>A list of controls, each written as its name, optionally followed by " @ x,y" (its place on the picture).</summary>
    private static List<ProfileControl> Controls(YamlMappingNode root, string key, string source) =>
        [.. Strings(root, key, source).Select(text =>
        {
            int at = text.LastIndexOf('@');
            if (at < 0)
            {
                return new ProfileControl(UnnamedMark(text.Trim()));
            }

            string[] position = text[(at + 1)..].Split(',');
            if (position.Length != 2
                || !int.TryParse(position[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(position[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                throw new InvalidDataException($"{source}: the {key} must be written like 'Trigger @ 120,80', not '{text}'");
            }

            return new ProfileControl(UnnamedMark(text[..at].Trim()), x, y);
        })];

    /// <summary>A control's name, or "" for one whose name isn't known (written as "-"), which is shown by its number.</summary>
    private static string UnnamedMark(string name) => name == "-" ? "" : name;
}

/// <summary>The controller profiles in Assets/Controllers (a mod's replace the game's, file by file).</summary>
public sealed class ControllerProfiles
{
    private readonly Dictionary<string, ControllerProfile> _profiles;

    private ControllerProfiles(Dictionary<string, ControllerProfile> profiles)
    {
        _profiles = profiles;
        Gamepad = Get(ControllerProfile.GamepadId);
        Generic = Get(ControllerProfile.GenericId);
    }

    /// <summary>The profile for a gamepad that no other gamepad profile is for (the Xbox controller's).</summary>
    public ControllerProfile Gamepad { get; }

    /// <summary>The profile for joysticks that no other profile matches.</summary>
    public ControllerProfile Generic { get; }

    public IEnumerable<ControllerProfile> All => _profiles.Values;

    private ControllerProfile Get(string id) => _profiles.TryGetValue(id, out var profile)
        ? profile
        : throw new InvalidDataException($"The controller profile '{id}.yml' is missing");

    /// <summary>Find the profile with the given id, if there is one.</summary>
    public ControllerProfile? Find(string id) => _profiles.GetValueOrDefault(id);

    /// <summary>The profile for a kind of gamepad (such as "ps5"), or else the Xbox controller's.</summary>
    public ControllerProfile GamepadFor(string type) =>
        _profiles.Values.FirstOrDefault(p => p.IsGamepad && p.GamepadTypes.Contains(type)) ?? Gamepad;

    /// <summary>Find the profile for a device by its USB vendor and product IDs, if there is one (other than the gamepad profiles).</summary>
    public ControllerProfile? Find(ushort vendor, ushort product) =>
        _profiles.Values.FirstOrDefault(p => !p.IsGamepad && p.Devices.Contains((vendor, product)));

    /// <summary>
    /// Find the profile for a device: by its USB vendor and product IDs, or
    /// else by its name (which contains one of a profile's names, ignoring
    /// case), if there is one (other than the gamepad profiles).
    /// </summary>
    public ControllerProfile? Find(ushort vendor, ushort product, string? name) =>
        Find(vendor, product) ?? (name == null ? null : _profiles.Values.FirstOrDefault(p =>
            !p.IsGamepad && p.DeviceNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))));

    /// <summary>Load the profiles (the mod's, if it has any, and the game's).</summary>
    /// <exception cref="InvalidDataException">A profile isn't valid, or the gamepad or generic profile is missing.</exception>
    public static ControllerProfiles Load() => Load(GameAssets.FoldersNamed("Controllers"));

    /// <summary>Load the profiles from the given folders (a profile in an earlier folder replaces one of the same name in a later folder).</summary>
    public static ControllerProfiles Load(IEnumerable<string> folders)
    {
        var profiles = new Dictionary<string, ControllerProfile>();
        foreach (string folder in folders.Reverse())
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.yml").Order(StringComparer.Ordinal))
            {
                string id = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                profiles[id] = ControllerProfile.Parse(File.ReadAllText(path), id, Path.GetFileName(path), folder);
            }
        }

        return new ControllerProfiles(profiles);
    }
}

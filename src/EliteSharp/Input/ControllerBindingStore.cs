using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Input;

/// <summary>
/// The player's controller bindings, controllers.yml in the data folder: for
/// each controller whose bindings have been changed on the Controls screen,
/// all of its bindings (see <see cref="ControllerBindings"/>), under its
/// profile's id. Any other controller uses its profile's defaults.
///
/// The file is read when the game starts and written whenever the bindings
/// change. A file (or a controller's part of it) that can't be read is
/// reported and ignored, so it never stops the game.
/// </summary>
public sealed class ControllerBindingStore
{
    /// <summary>The name of the file in the data folder.</summary>
    public const string FileName = "controllers.yml";

    private readonly string? _path;
    private readonly Dictionary<string, YamlMappingNode> _unread = [];
    private readonly Dictionary<string, ControllerBindings> _changed = [];
    private readonly object _lock = new();

    private ControllerBindingStore(string? path)
    {
        _path = path;
    }

    /// <summary>A store that isn't saved anywhere (for tests).</summary>
    public static ControllerBindingStore InMemory() => new(null);

    /// <summary>Read the file, or start an empty one if it is missing.</summary>
    public static ControllerBindingStore Load(string path)
    {
        var store = new ControllerBindingStore(path);
        try
        {
            if (File.Exists(path))
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(File.ReadAllText(path)));
                if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root)
                {
                    foreach (var (key, value) in root.Children)
                    {
                        if (key is YamlScalarNode { Value: { } id } && value is YamlMappingNode bindings)
                        {
                            store._unread[id] = bindings;
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or YamlException)
        {
            Console.Error.WriteLine($"Ignoring the controller bindings file {path}: {e.Message}");
        }

        return store;
    }

    /// <summary>A controller's bindings: the player's, or else its profile's defaults.</summary>
    public ControllerBindings For(ControllerProfile profile)
    {
        lock (_lock)
        {
            if (_changed.TryGetValue(profile.Id, out var bindings))
            {
                return bindings;
            }

            if (_unread.Remove(profile.Id, out var node))
            {
                try
                {
                    bindings = ControllerBindings.Read(node, $"{FileName} ({profile.Id})", profile.Defaults);
                    _changed[profile.Id] = bindings;
                    return bindings;
                }
                catch (Exception e) when (e is InvalidDataException or InvalidCastException)
                {
                    Console.Error.WriteLine($"Ignoring the bindings for {profile.Id}: {e.Message}");
                }
            }

            return profile.Defaults;
        }
    }

    /// <summary>Whether the player has changed a controller's bindings.</summary>
    public bool IsChanged(ControllerProfile profile)
    {
        For(profile);
        lock (_lock)
        {
            return _changed.ContainsKey(profile.Id);
        }
    }

    /// <summary>Change a controller's bindings (which shouldn't be changed afterwards), and save them.</summary>
    public void Set(ControllerProfile profile, ControllerBindings bindings)
    {
        lock (_lock)
        {
            _unread.Remove(profile.Id);
            _changed[profile.Id] = bindings;
            Save();
        }
    }

    /// <summary>Put a controller's bindings back to its profile's defaults.</summary>
    public void Reset(ControllerProfile profile)
    {
        lock (_lock)
        {
            _unread.Remove(profile.Id);
            _changed.Remove(profile.Id);
            Save();
        }
    }

    private void Save()
    {
        if (_path == null)
        {
            return;
        }

        var lines = new List<string> { "# EliteSharp's controller bindings (changed on the Controls screen, from the Settings screen)" };
        foreach (var (id, bindings) in _changed.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            lines.Add($"{id}:");
            bindings.Write(lines, 2);
        }

        // Keep any controller that wasn't read (not having been plugged in)
        foreach (var (id, node) in _unread.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var document = new YamlDocument(new YamlMappingNode(new YamlScalarNode(id), node));
            var writer = new StringWriter();
            new YamlStream(document).Save(writer, assignAnchors: false);
            lines.AddRange(writer.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l != "..."));
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write the controller bindings file {_path}: {e.Message}");
        }
    }
}

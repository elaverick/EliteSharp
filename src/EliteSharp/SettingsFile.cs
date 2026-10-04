using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp;

/// <summary>
/// The settings file, settings.yml in the data folder: the settings chosen on
/// the Settings screen, or changed while playing (such as switching to full
/// screen with Alt+Enter), which the game starts with next time. It is a YAML
/// mapping of setting names to values, and only holds the settings that have
/// been changed, so the others keep their defaults.
///
/// Changes are written out half a second after the last one, so dragging a
/// slider or the window's edge doesn't write the file over and over (and
/// <see cref="Flush"/> writes any that are waiting when the game closes).
/// </summary>
public sealed class SettingsFile
{
    /// <summary>The name of the settings file in the data folder.</summary>
    public const string FileName = "settings.yml";

    /// <summary>How long to wait after a change before writing the file, in milliseconds.</summary>
    private const int WriteDelay = 500;

    private readonly string _path;
    private readonly Dictionary<string, string> _values;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _dirty;

    private SettingsFile(string path, Dictionary<string, string> values)
    {
        _path = path;
        _values = values;
    }

    /// <summary>
    /// Read the settings file, or start an empty one if it is missing. A file
    /// that can't be read is reported and ignored (and replaced when a setting
    /// changes), so a broken file never stops the game.
    /// </summary>
    public static SettingsFile Load(string path)
    {
        var values = new Dictionary<string, string>();
        try
        {
            if (File.Exists(path))
            {
                using var reader = new StreamReader(path);
                var stream = new YamlStream();
                stream.Load(reader);
                if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root)
                {
                    foreach (var (key, value) in root.Children)
                    {
                        if (key is YamlScalarNode { Value: { } name } && value is YamlScalarNode { Value: { } text })
                        {
                            values[name] = text;
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or YamlException)
        {
            Console.Error.WriteLine($"Ignoring the settings file {path}: {e.Message}");
        }

        return new SettingsFile(path, values);
    }

    /// <summary>Get a true or false setting, if it's in the file.</summary>
    public bool? GetBool(string name) =>
        _values.TryGetValue(name, out string? text) && bool.TryParse(text, out bool value) ? value : null;

    /// <summary>Get a whole-number setting, if it's in the file.</summary>
    public int? GetInt(string name) =>
        _values.TryGetValue(name, out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    /// <summary>Get a setting as it's written, if it's in the file.</summary>
    public string? GetString(string name) => _values.GetValueOrDefault(name);

    /// <summary>Change a setting, and write the file shortly.</summary>
    public void Set(string name, bool value) => Set(name, value ? "true" : "false");

    /// <summary>Change a setting, and write the file shortly.</summary>
    public void Set(string name, int value) => Set(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Change a setting, and write the file shortly.</summary>
    public void Set(string name, string value)
    {
        lock (_lock)
        {
            if (_values.TryGetValue(name, out string? old) && old == value)
            {
                return;
            }

            _values[name] = value;
            _dirty = true;
            _timer ??= new Timer(_ => Flush());
            _timer.Change(WriteDelay, Timeout.Infinite);
        }
    }

    /// <summary>Write the file now, if any settings have changed since it was last written.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var lines = new List<string> { "# EliteSharp's settings (changed from the Settings screen, F12)" };
                lines.AddRange(_values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}: {Quote(v.Value)}"));
                File.WriteAllLines(_path, lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not write the settings file {_path}: {e.Message}");
            }
        }
    }

    /// <summary>Quote a value if YAML would read it as something else (such as "4:3").</summary>
    private static string Quote(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') ? value : $"\"{value}\"";
}

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Data;

/// <summary>
/// The game's fixed text in one language, loaded from a strings file (such as
/// Data/Strings/en-strings.yml), and looked up by key, such as
/// "equipment.fuel" for the "fuel" string in the "equipment" section.
/// </summary>
public sealed class GameStrings
{
    /// <summary>The language the game uses unless told otherwise.</summary>
    public const string DefaultLanguage = "en";

    private readonly Dictionary<string, string> _strings;

    private GameStrings(string language, string source, Dictionary<string, string> strings)
    {
        Language = language;
        Source = source;
        _strings = strings;
    }

    /// <summary>The folder containing the strings files.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Data", "Strings");

    /// <summary>The language, such as "en".</summary>
    public string Language { get; }

    /// <summary>Where the strings came from (the file name), for error messages.</summary>
    public string Source { get; }

    /// <summary>All the keys.</summary>
    public IEnumerable<string> Keys => _strings.Keys;

    /// <summary>The name of the strings file for a language, such as "en-strings.yml".</summary>
    public static string FileName(string language) => $"{language}-strings.yml";

    /// <summary>Load the strings for a language from its file in a folder.</summary>
    public static GameStrings Load(string language, string? folder = null)
    {
        string path = Path.Combine(folder ?? DefaultFolder, FileName(language));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There are no strings for the language '{language}' (the file '{path}' is missing)", path);
        }

        return Parse(language, File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Read the strings for a language from the contents of a strings file.</summary>
    public static GameStrings Parse(string language, string yaml, string source)
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

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"{source}: the file must contain a single mapping of sections and strings");
        }

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(root, "");
        return new GameStrings(language, source, strings);

        void Add(YamlMappingNode mapping, string prefix)
        {
            foreach (var (keyNode, value) in mapping.Children)
            {
                string key = prefix + ((YamlScalarNode)keyNode).Value;
                switch (value)
                {
                    case YamlMappingNode section:
                        Add(section, key + ".");
                        break;
                    case YamlScalarNode { Value: { } text }:
                        strings[key] = text;
                        break;
                    default:
                        throw new InvalidDataException($"{source} (line {value.Start.Line}): '{key}' must be a string or a section of strings");
                }
            }
        }
    }

    /// <summary>The string with the given key, such as "equipment.fuel".</summary>
    /// <exception cref="KeyNotFoundException">There is no string with that key.</exception>
    public string Get(string key) =>
        _strings.TryGetValue(key, out var text)
            ? text
            : throw new KeyNotFoundException($"There is no string '{key}' in {Source} (language '{Language}')");
}

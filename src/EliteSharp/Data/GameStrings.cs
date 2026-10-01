using EliteSharp.Rendering;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Data;

/// <summary>
/// The game's fixed text in one language, loaded from a strings file (such as
/// Assets/Strings/en-strings.yml), and looked up by key, such as
/// "equipment.fuel" for the "fuel" string in the "equipment" section.
/// </summary>
public sealed class GameStrings
{
    /// <summary>The language the game uses unless told otherwise.</summary>
    public const string DefaultLanguage = "en";

    /// <summary>The placeholders that the strings can use, such as {cash}, each of which prints a value.</summary>
    public static readonly IReadOnlySet<string> Placeholders = new HashSet<string>(StringComparer.Ordinal)
    {
        "cash", "galaxy", "current_system", "system", "commander", "default_commander", "drive",
    };

    private readonly Dictionary<string, (string Text, IReadOnlyList<TextPart> Parts)> _strings;

    private GameStrings(string language, string source, Dictionary<string, (string, IReadOnlyList<TextPart>)> strings)
    {
        Language = language;
        Source = source;
        _strings = strings;
    }

    /// <summary>The folder containing the strings files.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Strings");

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
    /// <exception cref="InvalidDataException">The file isn't valid (the message says where and why).</exception>
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

        var strings = new Dictionary<string, (string, IReadOnlyList<TextPart>)>(StringComparer.Ordinal);
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
                        strings[key] = (text, SplitText(text, $"{source} (line {value.Start.Line}): '{key}'"));
                        break;
                    default:
                        throw new InvalidDataException($"{source} (line {value.Start.Line}): '{key}' must be a string or a section of strings");
                }
            }
        }
    }

    /// <summary>The string with the given key, such as "equipment.fuel".</summary>
    /// <exception cref="KeyNotFoundException">There is no string with that key.</exception>
    public string Get(string key) => Find(key).Text;

    /// <summary>The string with the given key, split into text and placeholders.</summary>
    /// <exception cref="KeyNotFoundException">There is no string with that key.</exception>
    public IReadOnlyList<TextPart> GetParts(string key) => Find(key).Parts;

    private (string Text, IReadOnlyList<TextPart> Parts) Find(string key) =>
        _strings.TryGetValue(key, out var found)
            ? found
            : throw new KeyNotFoundException($"There is no string '{key}' in {Source} (language '{Language}')");

    /// <summary>
    /// Split a string into its text and placeholders, checking that the game
    /// can print it (each character must be in the font, apart from \n, which
    /// is a newline, and \a, which is a beep).
    /// </summary>
    private static List<TextPart> SplitText(string text, string where)
    {
        var parts = new List<TextPart>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                int end = text.IndexOf('}', i);
                string name = end < 0 ? text[i..] : text[(i + 1)..end];
                if (end < 0 || !Placeholders.Contains(name))
                {
                    throw new InvalidDataException($"{where} contains an unknown placeholder '{{{name}}}' (expected one of {string.Join(", ", Placeholders.Select(p => $"{{{p}}}"))})");
                }

                if (i > start)
                {
                    parts.Add(new LiteralText(text[start..i]));
                }

                parts.Add(new Placeholder(name));
                i = end;
                start = end + 1;
            }
            else if (c is not ('\n' or '\a') && (c == '}' || !HudAtlas.InFont(c)))
            {
                throw new InvalidDataException($"{where} contains '{c}', which the game can't print (it isn't in the font, Assets/Images/font.png)");
            }
        }

        if (text.Length > start)
        {
            parts.Add(new LiteralText(text[start..]));
        }

        return parts;
    }
}

/// <summary>Part of a string of the game's fixed text.</summary>
public abstract record TextPart;

/// <summary>Text, which the game prints as it is written (in capitals where the screen is in capitals).</summary>
public sealed record LiteralText(string Text) : TextPart;

/// <summary>A placeholder, such as {cash}, which the game prints a value in place of.</summary>
public sealed record Placeholder(string Name) : TextPart;

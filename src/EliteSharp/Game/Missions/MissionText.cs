using System.Text;

namespace EliteSharp.Game.Missions;

/// <summary>
/// A piece of mission text: ordinary characters, printed exactly as written,
/// and references to named values, written as <c>{{name}}</c>.
/// </summary>
public sealed class MissionText
{
    /// <summary>The name of the built-in value that holds the commander's name.</summary>
    public const string CommanderName = "commanderName";

    private MissionText(string source, IReadOnlyList<Part> parts)
    {
        Source = source;
        Parts = parts;
    }

    /// <summary>A part of the text: either some characters, or a reference to a named value.</summary>
    public readonly record struct Part(string Text, bool IsReference);

    /// <summary>The text as written.</summary>
    public string Source { get; }

    /// <summary>The characters and references, in order.</summary>
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>The names of the values the text refers to.</summary>
    public IEnumerable<string> References => Parts.Where(p => p.IsReference).Select(p => p.Text);

    /// <summary>Parse some text, throwing a <see cref="FormatException"/> if a reference is malformed.</summary>
    public static MissionText Parse(string source)
    {
        var parts = new List<Part>();
        int position = 0;
        while (position < source.Length)
        {
            int open = source.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                parts.Add(new Part(source[position..], false));
                break;
            }

            if (open > position)
            {
                parts.Add(new Part(source[position..open], false));
            }

            int close = source.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new FormatException($"'{{{{' at character {open + 1} has no matching '}}}}'");
            }

            string name = source[(open + 2)..close].Trim();
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                throw new FormatException($"'{{{{{name}}}}}' isn't a valid reference (names use letters, digits, '-' and '_')");
            }

            parts.Add(new Part(name, true));
            position = close + 2;
        }

        return new MissionText(source, parts);
    }

    /// <summary>
    /// The text with its references filled in by the given function, working
    /// from left to right, so that any random choices are made in the order
    /// they appear (as the original's text printer does).
    /// </summary>
    public string Resolve(Func<string, string> reference)
    {
        var builder = new StringBuilder();
        foreach (var part in Parts)
        {
            builder.Append(part.IsReference ? reference(part.Text) : part.Text);
        }

        return builder.ToString();
    }

    public override string ToString() => Source;
}

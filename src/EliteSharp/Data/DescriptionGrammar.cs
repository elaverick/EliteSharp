using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Data;

/// <summary>
/// The text that the game generates for the systems, in one language, from a
/// descriptions file (such as Assets/Strings/en-descriptions.yml): the "goat
/// soup" system descriptions, the special descriptions of a few systems, and
/// the species' names. See the file for how the descriptions are made.
/// </summary>
public sealed class DescriptionGrammar
{
    /// <summary>The operations in braces that descriptions can use, by name.</summary>
    private static readonly IReadOnlyDictionary<string, DescriptionOperation> Operations = new Dictionary<string, DescriptionOperation>(StringComparer.Ordinal)
    {
        ["all_caps"] = DescriptionOperation.AllCaps,
        ["sentence_case"] = DescriptionOperation.SentenceCase,
        ["lower_case"] = DescriptionOperation.LowerCase,
        ["system"] = DescriptionOperation.SystemName,
        ["system_adjective"] = DescriptionOperation.SystemAdjective,
        ["random_word"] = DescriptionOperation.RandomWord,
        ["capitalise"] = DescriptionOperation.CapitaliseNextLetter,
        ["justify"] = DescriptionOperation.JustifyOn,
        ["left_align"] = DescriptionOperation.LeftAlign,
    };

    /// <summary>The number of pairs of letters (or single letters) that random words are made from (MT18 picks one with a random number AND 62).</summary>
    public const int RandomWordPairCount = 32;

    private DescriptionGrammar(
        DescriptionText description,
        DescriptionText adjectiveSuffix,
        IReadOnlyList<string> randomWordPairs,
        IReadOnlyList<SpecialDescription> specialDescriptions,
        SpeciesNames species,
        IReadOnlyDictionary<string, DescriptionRule> rules)
    {
        Description = description;
        AdjectiveSuffix = adjectiveSuffix;
        RandomWordPairs = randomWordPairs;
        SpecialDescriptions = specialDescriptions;
        Species = species;
        Rules = rules;
    }

    /// <summary>The folder containing the descriptions files.</summary>
    public static string DefaultFolder => GameStrings.DefaultFolder;

    /// <summary>The text that makes a system's description.</summary>
    public DescriptionText Description { get; }

    /// <summary>What {system_adjective} adds to the system's name (such as "IAN", for "Lavian").</summary>
    public DescriptionText AdjectiveSuffix { get; }

    /// <summary>The pairs of letters (or single letters) that random words are made from.</summary>
    public IReadOnlyList<string> RandomWordPairs { get; }

    /// <summary>The descriptions that replace the generated ones for a few systems.</summary>
    public IReadOnlyList<SpecialDescription> SpecialDescriptions { get; }

    /// <summary>The words that the species' names are made from.</summary>
    public SpeciesNames Species { get; }

    /// <summary>The rules that descriptions use, by name.</summary>
    public IReadOnlyDictionary<string, DescriptionRule> Rules { get; }

    /// <summary>The name of the descriptions file for a language, such as "en-descriptions.yml".</summary>
    public static string FileName(string language) => $"{language}-descriptions.yml";

    /// <summary>Load the descriptions for a language from its file in a folder.</summary>
    public static DescriptionGrammar Load(string language, string? folder = null)
    {
        string path = Path.Combine(folder ?? DefaultFolder, FileName(language));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There are no system descriptions for the language '{language}' (the file '{path}' is missing)", path);
        }

        return Parse(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Read the descriptions from the contents of a descriptions file.</summary>
    /// <exception cref="InvalidDataException">The file isn't valid (the message says where and why).</exception>
    public static DescriptionGrammar Parse(string yaml, string source) => new Reader(source).Read(yaml);

    /// <summary>Reads and checks a descriptions file.</summary>
    private sealed class Reader(string source)
    {
        public DescriptionGrammar Read(string yaml)
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
                throw Error(null, "the file must contain a single mapping");
            }

            CheckKeys(root, "description", "adjective_suffix", "random_word_pairs", "special_descriptions", "species", "rules");

            // The rules first, so the text can refer to them
            var ruleNodes = Mapping(Required(root, "rules"), "rules");
            var rules = new Dictionary<string, DescriptionRule>(StringComparer.Ordinal);
            var names = ruleNodes.Children.Keys.Select(key => ((YamlScalarNode)key).Value!).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, value) in ruleNodes.Children)
            {
                string name = ((YamlScalarNode)key).Value!;
                rules[name] = ReadRule(name, value, names);
            }

            var description = Text(Required(root, "description"), "description", names);
            var adjectiveSuffix = Text(Required(root, "adjective_suffix"), "adjective_suffix", names);

            var pairs = Sequence(Required(root, "random_word_pairs"), "random_word_pairs")
                .Select(node => Scalar(node, "random_word_pairs"))
                .ToList();
            if (pairs.Count != RandomWordPairCount || pairs.Any(pair => pair.Length is < 1 or > 2 || pair.Any(c => c < 'A' || c > 'Z')))
            {
                throw Error(root["random_word_pairs"], $"'random_word_pairs' must be {RandomWordPairCount} pairs of capital letters (or single capital letters)");
            }

            var specials = new List<SpecialDescription>();
            foreach (var node in Sequence(Required(root, "special_descriptions"), "special_descriptions"))
            {
                var special = Mapping(node, "special_descriptions");
                CheckKeys(special, "galaxy", "system", "text");
                int galaxy = Number(Required(special, "galaxy"), "galaxy", 1, 128);
                int system = Number(Required(special, "system"), "system", 0, 255);
                specials.Add(new SpecialDescription(galaxy - 1, system, Text(Required(special, "text"), "special description", names)));
            }

            var speciesNode = Mapping(Required(root, "species"), "species");
            CheckKeys(speciesNode, "human_colonials", "size", "colour", "appearance", "kind");
            var species = new SpeciesNames(
                SpeciesWord(Required(speciesNode, "human_colonials"), "human_colonials"),
                SpeciesWords(speciesNode, "size", 3),
                SpeciesWords(speciesNode, "colour", 6),
                SpeciesWords(speciesNode, "appearance", 6),
                SpeciesWords(speciesNode, "kind", 8));

            var result = new DescriptionGrammar(description, adjectiveSuffix, pairs, specials, species, rules);
            CheckRulesAreUsed(result, ruleNodes);
            return result;
        }

        private DescriptionRule ReadRule(string name, YamlNode node, HashSet<string> names)
        {
            if (node is YamlMappingNode mapping)
            {
                CheckKeys(mapping, "oneOf");
                var choices = Sequence(Required(mapping, "oneOf"), $"rule '{name}'")
                    .Select(choice => Text(choice, $"rule '{name}'", names))
                    .ToList();
                if (choices.Count < 2)
                {
                    throw Error(node, $"rule '{name}' must have at least two choices");
                }

                return new DescriptionRule(choices, Random: true);
            }

            return new DescriptionRule([Text(node, $"rule '{name}'", names)], Random: false);
        }

        /// <summary>Read some description text, checking its operations, rules and characters.</summary>
        private DescriptionText Text(YamlNode node, string what, HashSet<string> names)
        {
            string text = Scalar(node, what);
            var parts = new List<DescriptionPart>();
            int literalStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is not ('{' or '\n'))
                {
                    if (c < ' ' || c > 'Z')
                    {
                        throw Error(node, $"{what} contains '{c}', which the game can't print in a description (letters must be capitals)");
                    }

                    continue;
                }

                if (i > literalStart)
                {
                    parts.Add(new DescriptionLiteral(text[literalStart..i]));
                }

                if (c == '{' && i + 1 < text.Length && text[i + 1] == '{')
                {
                    int end = text.IndexOf("}}", i, StringComparison.Ordinal);
                    string name = end < 0 ? text[i..] : text[(i + 2)..end];
                    if (end < 0 || !names.Contains(name))
                    {
                        throw Error(node, $"{what} refers to an unknown rule '{{{{{name}}}}}'");
                    }

                    parts.Add(new DescriptionReference(name));
                    i = end + 1;
                }
                else if (c == '{')
                {
                    int end = text.IndexOf('}', i);
                    string name = end < 0 ? text[i..] : text[(i + 1)..end];
                    if (end < 0 || !Operations.TryGetValue(name, out var operation))
                    {
                        throw Error(node, $"{what} contains an unknown operation '{{{name}}}'");
                    }

                    parts.Add(new DescriptionCommand(operation));
                    i = end;
                }
                else
                {
                    parts.Add(new DescriptionCommand(DescriptionOperation.Newline));
                }

                literalStart = i + 1;
            }

            if (text.Length > literalStart)
            {
                parts.Add(new DescriptionLiteral(text[literalStart..]));
            }

            return new DescriptionText(parts);
        }

        private string SpeciesWord(YamlNode node, string what)
        {
            string word = Scalar(node, $"species '{what}'");
            if (word.Any(c => c < ' ' || c > '_'))
            {
                throw Error(node, $"species '{what}' contains a character the game can't print (letters must be capitals)");
            }

            return word;
        }

        private IReadOnlyList<string> SpeciesWords(YamlMappingNode species, string key, int count)
        {
            var words = Sequence(Required(species, key), $"species '{key}'").Select(node => SpeciesWord(node, key)).ToList();
            if (words.Count != count)
            {
                // The system's seeds pick from a fixed number of words
                throw Error(species[key], $"species '{key}' must have {count} words");
            }

            return words;
        }

        /// <summary>Check that every rule can be printed, and that no rule prints itself.</summary>
        private void CheckRulesAreUsed(DescriptionGrammar descriptions, YamlMappingNode ruleNodes)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            Visit(descriptions.Description);
            Visit(descriptions.AdjectiveSuffix);
            foreach (var special in descriptions.SpecialDescriptions)
            {
                Visit(special.Text);
            }

            foreach (var (key, value) in ruleNodes.Children)
            {
                if (!used.Contains(((YamlScalarNode)key).Value!))
                {
                    throw Error(value, $"rule '{((YamlScalarNode)key).Value}' is never used");
                }
            }

            void Visit(DescriptionText text)
            {
                foreach (var part in text.Parts)
                {
                    if (part is not DescriptionReference reference)
                    {
                        continue;
                    }

                    if (!visiting.Add(reference.Rule))
                    {
                        throw Error(ruleNodes, $"rule '{reference.Rule}' refers to itself");
                    }

                    if (used.Add(reference.Rule))
                    {
                        foreach (var choice in descriptions.Rules[reference.Rule].Choices)
                        {
                            Visit(choice);
                        }
                    }

                    visiting.Remove(reference.Rule);
                }
            }
        }

        private void CheckKeys(YamlMappingNode mapping, params string[] allowed)
        {
            foreach (var key in mapping.Children.Keys)
            {
                string name = ((YamlScalarNode)key).Value!;
                if (!allowed.Contains(name))
                {
                    throw Error(key, $"unknown key '{name}' (expected {string.Join(", ", allowed)})");
                }
            }
        }

        private YamlNode Required(YamlMappingNode mapping, string key) =>
            mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : throw Error(mapping, $"'{key}' is missing");

        private YamlMappingNode Mapping(YamlNode node, string what) =>
            node as YamlMappingNode ?? throw Error(node, $"{what} must be a mapping");

        private IReadOnlyList<YamlNode> Sequence(YamlNode node, string what) =>
            (node as YamlSequenceNode)?.Children.ToList() ?? throw Error(node, $"{what} must be a list");

        private string Scalar(YamlNode node, string what) =>
            node is YamlScalarNode { Value: { } text } ? text : throw Error(node, $"{what} must be some text");

        private int Number(YamlNode node, string what, int lowest, int highest) =>
            int.TryParse(Scalar(node, what), out int number) && number >= lowest && number <= highest
                ? number
                : throw Error(node, $"'{what}' must be a number from {lowest} to {highest}");

        private InvalidDataException Error(YamlNode? node, string message) =>
            new(node == null ? $"{source}: {message}" : $"{source} (line {node.Start.Line}): {message}");
    }
}

/// <summary>Part of some description text: some text to print, an operation, or a reference to a rule.</summary>
public abstract record DescriptionPart;

/// <summary>Some text to print (spaces, punctuation and capital letters), in the text case that the description is in.</summary>
public sealed record DescriptionLiteral(string Text) : DescriptionPart;

/// <summary>An operation, such as {system_adjective}, which prints something or changes how the text that follows is printed.</summary>
public sealed record DescriptionCommand(DescriptionOperation Operation) : DescriptionPart;

/// <summary>
/// The operations that description text can use (the original's extended
/// text control codes, whose numbers are in brackets).
/// </summary>
public enum DescriptionOperation
{
    /// <summary>{all_caps}: switch to ALL CAPS (MT1, code 1).</summary>
    AllCaps,

    /// <summary>{sentence_case}: switch to Sentence Case (MT2, code 2).</summary>
    SentenceCase,

    /// <summary>{lower_case}: switch to lower case (MT13, code 13).</summary>
    LowerCase,

    /// <summary>{system}: print the system's name (code 3).</summary>
    SystemName,

    /// <summary>{system_adjective}: print the system's adjective, such as "Lavian" (MT17, code 17).</summary>
    SystemAdjective,

    /// <summary>{random_word}: print a random word (MT18, code 18).</summary>
    RandomWord,

    /// <summary>{capitalise}: print the next letter as a capital (MT19, code 19).</summary>
    CapitaliseNextLetter,

    /// <summary>{justify}: justify the text that follows (MT14, code 14).</summary>
    JustifyOn,

    /// <summary>{left_align}: left-align the text that follows (MT15, code 15).</summary>
    LeftAlign,

    /// <summary>\n: a newline (code 12).</summary>
    Newline,
}

/// <summary>A reference to a rule, which prints the rule.</summary>
public sealed record DescriptionReference(string Rule) : DescriptionPart;

/// <summary>Some description text.</summary>
public sealed record DescriptionText(IReadOnlyList<DescriptionPart> Parts);

/// <summary>A rule: some text, or (if it is random) a choice of text.</summary>
public sealed record DescriptionRule(IReadOnlyList<DescriptionText> Choices, bool Random);

/// <summary>A description that replaces the generated one for a system (galaxy numbered from 0).</summary>
public sealed record SpecialDescription(int Galaxy, int System, DescriptionText Text);

/// <summary>The words that the species' names are made from (TT75 to TT207).</summary>
public sealed record SpeciesNames(
    string HumanColonials,
    IReadOnlyList<string> Size,
    IReadOnlyList<string> Colour,
    IReadOnlyList<string> Appearance,
    IReadOnlyList<string> Kind);

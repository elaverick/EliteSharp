using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Data;

/// <summary>
/// The text that the game generates for the systems, in one language, from a
/// descriptions file (such as Data/Strings/en-descriptions.yml): the "goat
/// soup" system descriptions, the special descriptions of a few systems, and
/// the species' names. See the file for how the descriptions are made.
/// </summary>
public sealed class DescriptionGrammar
{
    /// <summary>The operations in braces that descriptions can use, and the extended text control codes (DETOK) they stand for.</summary>
    public static readonly IReadOnlyDictionary<string, int> Codes = new Dictionary<string, int>
    {
        ["all_caps"] = 1,
        ["sentence_case"] = 2,
        ["system"] = 3,
        ["line_feed"] = 10,
        ["lower_case"] = 13,
        ["justify"] = 14,
        ["left_align"] = 15,
        ["system_adjective"] = 17,
        ["random_word"] = 18,
        ["capitalise"] = 19,
    };

    /// <summary>The number of pairs of letters that random words are made from (MT18 picks one with a random number AND 62).</summary>
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

    /// <summary>The pairs of letters that random words are made from.</summary>
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
            if (pairs.Count != RandomWordPairCount || pairs.Any(pair => pair.Length != 2 || pair.Any(c => c < ' ' || c > '_')))
            {
                throw Error(root["random_word_pairs"], $"'random_word_pairs' must be {RandomWordPairCount} pairs of capital letters");
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
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
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
                    if (end < 0 || !Codes.TryGetValue(name, out int code))
                    {
                        throw Error(node, $"{what} contains an unknown operation '{{{name}}}'");
                    }

                    parts.Add(new DescriptionCharacter(code));
                    i = end;
                }
                else if (c == '\n')
                {
                    parts.Add(new DescriptionCharacter(12));
                }
                else if (c >= ' ' && c <= 'Z')
                {
                    parts.Add(new DescriptionCharacter(c));
                }
                else
                {
                    throw Error(node, $"{what} contains '{c}', which the game can't print in a description (letters must be capitals)");
                }
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

/// <summary>Part of some description text: a character or control code, or a reference to a rule.</summary>
public abstract record DescriptionPart;

/// <summary>A character to print, or an extended text control code (below 32).</summary>
public sealed record DescriptionCharacter(int Code) : DescriptionPart;

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

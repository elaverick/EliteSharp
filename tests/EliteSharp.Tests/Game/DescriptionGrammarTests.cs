using EliteSharp.Data;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The system descriptions file (Assets/Strings/en-descriptions.yml) loads, and
/// a mistake in a descriptions file is reported when the game starts.
/// </summary>
public sealed class DescriptionGrammarTests
{
    private static string English => File.ReadAllText(Path.Combine(DescriptionGrammar.DefaultFolder, "en-descriptions.yml"));

    [Fact]
    public void TheEnglishDescriptionsLoad()
    {
        var descriptions = DescriptionGrammar.Load("en");

        // The original's 36 random tokens, plus two rules for text that is used twice
        Assert.Equal(38, descriptions.Rules.Count);
        Assert.Equal(36, descriptions.Rules.Values.OfType<RandomDescriptionRule>().Count());
        Assert.Equal(["misfortune", "renown"], descriptions.Rules.Where(rule => rule.Value is FixedDescriptionRule).Select(rule => rule.Key).Order());
        Assert.All(descriptions.Rules.Values.OfType<RandomDescriptionRule>(), rule => Assert.Equal(5, rule.Choices.Count));
        Assert.Equal(32, descriptions.RandomWordPairs.Count);
        Assert.Equal(4, descriptions.SpecialDescriptions.Count);
        Assert.Equal("HUMAN COLONIAL", descriptions.Species.HumanColonials);
    }

    [Fact]
    public void DescriptionTextIsReadAsTextOperationsAndRules()
    {
        var descriptions = DescriptionGrammar.Load("en");

        Assert.Equal<DescriptionPart>(
        [
            new DescriptionCommand(DescriptionOperation.LowerCase),
            new DescriptionCommand(DescriptionOperation.JustifyOn),
            new DescriptionCommand(DescriptionOperation.CapitaliseNextLetter),
            new DescriptionReference("subject"),
            new DescriptionLiteral(" IS "),
            new DescriptionReference("summary"),
            new DescriptionLiteral("."),
            new DescriptionCommand(DescriptionOperation.Newline),
            new DescriptionCommand(DescriptionOperation.LeftAlign),
        ], descriptions.Description.Parts);
    }

    [Fact]
    public void TextBetweenOperationsIsReadAsOnePart()
    {
        var descriptions = DescriptionGrammar.Load("en");

        // Teorge, in galaxy 1
        var teorge = descriptions.SpecialDescriptions.Single(special => special.Galaxy == 0 && special.System == 211);
        Assert.Equal<DescriptionPart>(
        [
            new DescriptionCommand(DescriptionOperation.LowerCase),
            new DescriptionCommand(DescriptionOperation.JustifyOn),
            new DescriptionCommand(DescriptionOperation.CapitaliseNextLetter),
            new DescriptionLiteral("THE COLONISTS HERE HAVE VIOLATED"),
            new DescriptionCommand(DescriptionOperation.SentenceCase),
            new DescriptionLiteral(" INTERGALACTIC CLONING PROTOCOL"),
            new DescriptionCommand(DescriptionOperation.LowerCase),
            new DescriptionLiteral(" AND SHOULD BE AVOIDED."),
            new DescriptionCommand(DescriptionOperation.Newline),
            new DescriptionCommand(DescriptionOperation.LeftAlign),
        ], teorge.Text.Parts);
    }

    [Fact]
    public void AMissingLanguageIsReported()
    {
        var e = Assert.Throws<FileNotFoundException>(() => DescriptionGrammar.Load("xx"));
        Assert.StartsWith("There are no system descriptions for the language 'xx'", e.Message);
    }

    [Theory]
    [InlineData("      - \"HOOPY\"", "      - \"{{hoopy}}\"", "rule 'fabulous' refers to an unknown rule '{{hoopy}}'")]
    [InlineData("      - \"HOOPY\"", "      - \"{hoopy}\"", "rule 'fabulous' contains an unknown operation '{hoopy}'")]
    [InlineData("      - \"HOOPY\"", "      - \"Hoopy\"", "rule 'fabulous' contains 'o', which the game can't print in a description (letters must be capitals)")]
    [InlineData("      - \"HOOPY\"", "      - \"{{attraction}}\"", "rule 'attraction' refers to itself")]
    [InlineData("RE, A, ER", "RE, ER", "'random_word_pairs' must be 32 pairs of capital letters")]
    [InlineData("RE, A, ER", "RE, ABC, ER", "'random_word_pairs' must be 32 pairs of capital letters")]
    [InlineData("FELINE, INSECT]", "FELINE]", "species 'kind' must have 8 words")]
    [InlineData("rules:\n", "rules:\n  unused: \"NOTHING\"\n", "rule 'unused' is never used")]
    public void MistakesAreReported(string find, string replace, string expected)
    {
        string yaml = English.ReplaceLineEndings("\n");
        Assert.Contains(find, yaml);
        var e = Assert.Throws<InvalidDataException>(() => DescriptionGrammar.Parse(yaml.Replace(find, replace), "xx-descriptions.yml"));
        Assert.Contains(expected, e.Message);
        Assert.StartsWith("xx-descriptions.yml", e.Message);
    }

    /// <summary>The choices of the "sport_setting" rule in the English descriptions.</summary>
    private const string SportSettingChoices = "      - \"ICE\"\n      - \"MUD\"\n      - \"ZERO-{capitalise}G\"\n      - \"VACUUM\"\n      - \"{system_adjective} ULTRA\"\n";

    /// <summary>The English descriptions, with the "sport_setting" rule's choices replaced by the given ones.</summary>
    private static string WithSportSettingChoices(params string[] choices)
    {
        string yaml = English.ReplaceLineEndings("\n");
        Assert.Contains(SportSettingChoices, yaml);
        return yaml.Replace(SportSettingChoices, string.Concat(choices.Select(choice => $"      - \"{choice}\"\n")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void ARandomRuleMustHaveFiveChoices(int count)
    {
        string[] choices = Enumerable.Range(0, count).Select(i => $"CHOICE {(char)('A' + i)}").ToArray();

        var e = Assert.Throws<InvalidDataException>(() => DescriptionGrammar.Parse(WithSportSettingChoices(choices), "xx-descriptions.yml"));
        Assert.Matches(@"^xx-descriptions\.yml \(line \d+\): ", e.Message);
        Assert.EndsWith($"rule 'sport_setting' must have exactly 5 choices (it has {count})", e.Message);
    }

    [Fact]
    public void ARandomRuleWithFiveChoicesKeepsThemInOrder()
    {
        var descriptions = DescriptionGrammar.Parse(WithSportSettingChoices("E", "D", "C", "B", "A"), "xx-descriptions.yml");

        var rule = Assert.IsType<RandomDescriptionRule>(descriptions.Rules["sport_setting"]);
        Assert.Equal(["E", "D", "C", "B", "A"], rule.Choices.Select(choice => Assert.IsType<DescriptionLiteral>(Assert.Single(choice.Parts)).Text));
    }

    [Fact]
    public void AFixedRuleHasOneText()
    {
        var descriptions = DescriptionGrammar.Load("en");

        var rule = Assert.IsType<FixedDescriptionRule>(descriptions.Rules["renown"]);
        Assert.Equal<DescriptionPart>(
        [
            new DescriptionReference("degree"),
            new DescriptionLiteral(" "),
            new DescriptionReference("fame"),
            new DescriptionLiteral(" FOR "),
            new DescriptionReference("feature"),
        ], rule.Text.Parts);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void ARandomRuleCantBeMadeWithoutFiveChoices(int count)
    {
        var choices = Enumerable.Repeat(new DescriptionText([new DescriptionLiteral("X")]), count);

        Assert.Throws<ArgumentException>(() => new RandomDescriptionRule(choices));
    }

    [Fact]
    public void ARandomRulesChoicesCantBeChangedAfterwards()
    {
        var choices = Enumerable.Range(0, 5).Select(i => new DescriptionText([new DescriptionLiteral($"{i}")])).ToArray();
        var rule = new RandomDescriptionRule(choices);

        choices[0] = new DescriptionText([new DescriptionLiteral("CHANGED")]);

        Assert.Equal("0", Assert.IsType<DescriptionLiteral>(Assert.Single(rule.Choices[0].Parts)).Text);
        Assert.False(rule.Choices is DescriptionText[]);
    }

    /// <summary>
    /// A file whose structure is wrong (such as a list or a mapping where a
    /// name or some text should be) is reported, with where it is, rather than
    /// failing in some other way.
    /// </summary>
    [Theory]
    [InlineData("rules:\n", "[a, b]: \"X\"\nrules:\n", "line 71): a key must be a name (some text), not a list or a mapping")]
    [InlineData("rules:\n", "rules:\n  [a, b]: \"X\"\n", "line 72): a key must be a name (some text), not a list or a mapping")]
    [InlineData("rules:\n", "rules:\n  {a: 1}: \"X\"\n", "line 72): a key must be a name (some text), not a list or a mapping")]
    [InlineData("species:\n", "species:\n  [size]: [LARGE]\n", "line 65): a key must be a name (some text), not a list or a mapping")]
    [InlineData("    oneOf:\n      - \"FABULOUS\"", "    [oneOf]:\n      - \"FABULOUS\"", "line 222): a key must be a name (some text), not a list or a mapping")]
    [InlineData("  - galaxy: 1\n", "  - [galaxy]: 1\n", "line 49): a key must be a name (some text), not a list or a mapping")]
    [InlineData("  renown: \"{{degree}} {{fame}} FOR {{feature}}\"", "  renown: [\"{{degree}}\"]", "line 86): rule 'renown' must be some text")]
    [InlineData("    oneOf:\n      - \"FABULOUS\"", "    oneOf:\n      - [\"FABULOUS\"]", "line 223): rule 'fabulous' must be some text")]
    [InlineData("    oneOf:\n      - \"FABULOUS\"\n      - \"EXOTIC\"\n      - \"HOOPY\"\n      - \"UNUSUAL\"\n      - \"EXCITING\"", "    oneOf: \"FABULOUS\"", "line 222): rule 'fabulous' must be a list")]
    [InlineData("description: \"{lower_case}{justify}{capitalise}{{subject}} IS {{summary}}.\\n{left_align}\"", "description: {text: \"X\"}", "line 36): description must be some text")]
    [InlineData("special_descriptions:\n  - galaxy: 1", "special_descriptions:\n  - [1]\n  - galaxy: 1", "line 49): special_descriptions must be a mapping")]
    [InlineData("  size: [LARGE, FIERCE, SMALL]", "  size: [[LARGE], FIERCE, SMALL]", "line 66): species 'size' must be some text")]
    [InlineData("  human_colonials: \"HUMAN COLONIAL\"", "  human_colonials: {name: \"HUMAN COLONIAL\"}", "line 65): species 'human_colonials' must be some text")]
    public void AMalformedFileIsReported(string find, string replace, string expected)
    {
        string yaml = English.ReplaceLineEndings("\n");
        Assert.Contains(find, yaml);

        var e = Assert.Throws<InvalidDataException>(() => DescriptionGrammar.Parse(yaml.Replace(find, replace), "xx-descriptions.yml"));
        Assert.Equal($"xx-descriptions.yml ({expected}", e.Message);
    }

    [Fact]
    public void AFileThatIsntValidYamlIsReported()
    {
        // An unclosed {, which YamlDotNet reports with an InvalidOperationException, not a YamlException
        string yaml = English.ReplaceLineEndings("\n").Replace("description: \"", "description: {text: \"");

        var e = Assert.Throws<InvalidDataException>(() => DescriptionGrammar.Parse(yaml, "xx-descriptions.yml"));
        Assert.StartsWith("xx-descriptions.yml: the file isn't valid YAML (", e.Message);
        Assert.IsType<InvalidOperationException>(e.InnerException);
    }

    [Fact]
    public void RulesThatArentAMappingAreReported()
    {
        // The rules are the last thing in the file
        string yaml = English.ReplaceLineEndings("\n");
        yaml = yaml[..yaml.IndexOf("\nrules:\n", StringComparison.Ordinal)] + "\nrules: [subject]\n";

        var e = Assert.Throws<InvalidDataException>(() => DescriptionGrammar.Parse(yaml, "xx-descriptions.yml"));
        Assert.Equal("xx-descriptions.yml (line 71): rules must be a mapping", e.Message);
    }
}

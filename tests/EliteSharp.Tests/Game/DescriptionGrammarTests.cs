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
        Assert.Equal(36, descriptions.Rules.Values.Count(rule => rule.Random));
        Assert.All(descriptions.Rules.Values.Where(rule => rule.Random), rule => Assert.Equal(5, rule.Choices.Count));
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

        var rule = descriptions.Rules["sport_setting"];
        Assert.True(rule.Random);
        Assert.Equal(["E", "D", "C", "B", "A"], rule.Choices.Select(choice => Assert.IsType<DescriptionLiteral>(Assert.Single(choice.Parts)).Text));
    }
}

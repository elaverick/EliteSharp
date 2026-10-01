using EliteSharp.Data;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The system descriptions file (Data/Strings/en-descriptions.yml) loads, and
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
    [InlineData("      - \"ICE\"\n      - \"MUD\"\n      - \"ZERO-{capitalise}G\"\n      - \"VACUUM\"\n      - \"{system_adjective} ULTRA\"", "      - \"ICE\"", "rule 'sport_setting' must have at least two choices")]
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
}

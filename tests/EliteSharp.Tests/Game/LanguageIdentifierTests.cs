using EliteSharp.Data;
using EliteSharp.Rendering;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The language that the game's text files are chosen by must be a simple
/// language identifier, such as "en" or "en-GB", so it can't make their file
/// names refer to some other file.
/// </summary>
public sealed class LanguageIdentifierTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    [InlineData("en-gb")]
    [InlineData("zh-Hant-TW")]
    [InlineData("es-419")]
    public void ALanguageIdentifierIsValid(string language)
    {
        Assert.True(LanguageIdentifier.IsValid(language));
    }

    [Theory]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("english")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../en")]
    [InlineData("..\\en")]
    [InlineData("en/..")]
    [InlineData("Strings/en")]
    [InlineData("C:en")]
    [InlineData("/en")]
    [InlineData("en.")]
    [InlineData("en.GB")]
    [InlineData("en_GB")]
    [InlineData("en-")]
    [InlineData("-en")]
    [InlineData("en--GB")]
    [InlineData("en GB")]
    [InlineData(" en")]
    [InlineData("en\n")]
    [InlineData("en\0")]
    [InlineData("en-G")]
    [InlineData("en-ABCDEFGHI")]
    [InlineData("ën")]
    public void AnythingElseIsNotALanguageIdentifier(string language)
    {
        Assert.False(LanguageIdentifier.IsValid(language));
    }

    /// <summary>A language that would find the English files through a path (in the Assets folder, next to Strings).</summary>
    private const string EnglishThroughAPath = "../Strings/en";

    [Fact]
    public void TheGamesTextIsntLoadedForAnInvalidLanguage()
    {
        var e = Assert.Throws<ArgumentException>(() => GameStrings.Load(EnglishThroughAPath, HudAtlas.InFont));
        Assert.Equal("language", e.ParamName);
        Assert.StartsWith("'../Strings/en' isn't a language identifier", e.Message);
    }

    [Fact]
    public void TheSystemDescriptionsArentLoadedForAnInvalidLanguage()
    {
        var e = Assert.Throws<ArgumentException>(() => DescriptionGrammar.Load(EnglishThroughAPath));
        Assert.Equal("language", e.ParamName);
        Assert.StartsWith("'../Strings/en' isn't a language identifier", e.Message);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("FR", "fr")]
    [InlineData("en-GB", "en-gb")]
    public void TheLanguageOptionChoosesTheLanguage(string argument, string expected)
    {
        var options = GameOptions.Parse(["--language", argument]);

        Assert.Equal(expected, options.Language);
        Assert.True(LanguageIdentifier.IsValid(options.Language));
    }
}

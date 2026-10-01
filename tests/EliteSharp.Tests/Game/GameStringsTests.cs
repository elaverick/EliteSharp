using EliteSharp.Data;
using EliteSharp.Game;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The game's fixed text comes from the strings file for the chosen language
/// (Assets/Strings/en-strings.yml for English), and is printed just as the
/// original prints it from its token tables.
/// </summary>
public sealed class GameStringsTests
{
    [Fact]
    public void TheEnglishStringsLoad()
    {
        var strings = GameStrings.Load("en");

        Assert.Equal("en", strings.Language);
        Assert.Equal("en-strings.yml", strings.Source);
        Assert.Contains("commodities.alien_items", strings.Keys);
    }

    [Fact]
    public void AStringIsLookedUpByItsKey()
    {
        var strings = GameStrings.Load("en");

        Assert.Equal("DOCKING COMPUTERS ON", strings.Get("messages.docking_computers_on"));
        Assert.Equal("Galactic Hyperspace ", strings.Get("equipment.galactic_hyperspace"));
        Assert.Equal("Cash:{cash}", strings.Get("market.cash_balance"));
    }

    [Fact]
    public void AMissingKeyIsReported()
    {
        var strings = GameStrings.Load("en");

        var e = Assert.Throws<KeyNotFoundException>(() => strings.Get("messages.no_such_message"));
        Assert.Equal("There is no string 'messages.no_such_message' in en-strings.yml (language 'en')", e.Message);
    }

    [Fact]
    public void AMissingLanguageIsReported()
    {
        var e = Assert.Throws<FileNotFoundException>(() => GameStrings.Load("xx"));
        Assert.StartsWith("There are no strings for the language 'xx'", e.Message);
    }

    [Fact]
    public void AStringsFileMustContainOnlySectionsAndStrings()
    {
        var e = Assert.Throws<InvalidDataException>(() => GameStrings.Parse("xx", "hud:\n  cash: [1, 2]\n", "xx-strings.yml"));
        Assert.Equal("xx-strings.yml (line 2): 'hud.cash' must be a string or a section of strings", e.Message);
    }

    [Fact]
    public void AStringIsSplitIntoTextAndPlaceholders()
    {
        var strings = GameStrings.Load("en");

        Assert.Equal<TextPart>([new LiteralText("Cash:"), new Placeholder("cash")], strings.GetParts("market.cash_balance"));
        Assert.Equal<TextPart>([new Placeholder("current_system"), new LiteralText(" MARKET PRICES")], strings.GetParts("market.title"));
    }

    [Theory]
    [InlineData("{money}", "xx-strings.yml (line 2): 'market.cash' contains an unknown placeholder '{money}'")]
    [InlineData("Café", "xx-strings.yml (line 2): 'market.cash' contains 'é', which the game can't print")]
    public void AStringThatTheGameCantPrintIsReported(string text, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() => GameStrings.Parse("xx", $"market:\n  cash: \"{text}\"\n", "xx-strings.yml"));
        Assert.StartsWith(expected, e.Message);
    }

    [Fact]
    public void TheCodesInAStringPrintTheGamesValues()
    {
        var game = NewGame();

        game.Call("PrintText", "market.cash_balance");
        game.Call("PrintText", "charts.galactic");

        Assert.Equal("CASH:    100.0 CR", game.ScreenText()[0]);
        Assert.Equal("GALACTIC CHART  1", game.ScreenText()[1]);
    }

    [Theory]
    [InlineData(0x20, 0, 0xFF, "(C) Acornsoft 1986")]
    [InlineData(0, 0, 0xFF, "(C) ACORNSOFT 1986")]
    [InlineData(0x20, 0x80, 0xFF, "(C) acornsoft 1986")]
    public void TheTextIsPrintedAsItIsWrittenInTheScreensCase(int lowerCaseMask, int lowerCaseEnabled, int notPrintingWord, string expected)
    {
        // As written in Sentence Case, in capitals in capitals, and with the
        // words in lower case in lower case (apart from the capital in the
        // middle of "(C)", as in the original, which asks for a capital there)
        var game = NewGame();
        game.Set("_lowerCaseMask", lowerCaseMask);
        game.Set("_lowerCaseEnabled", lowerCaseEnabled);
        game.Set("_notPrintingWord", notPrintingWord);

        game.Call("PrintExtendedText", "title.copyright");

        Assert.Equal(expected, game.ScreenText()[0]);
    }

    [Fact]
    public void TextThatCarriesOnAWordStartsInLowerCase()
    {
        // As in the original, where the authors' names on the title screen
        // end in the middle of a word, so the prompt after them starts in
        // lower case
        var game = NewGame();
        game.Call("PrintExtendedText", "title.authors");
        game.Set("_cursorX", 1);
        game.Set("_cursorY", 2);

        game.Call("PrintExtendedText", "title.press_space");

        Assert.Equal("By D.Braben & I.Bell", game.ScreenText()[0]);
        Assert.Equal("press Space Or Fire,Commander.", game.ScreenText()[1]);
    }

    [Fact]
    public void TheStatusScreenShowsTheEnglishText()
    {
        var game = NewGame();

        game.Call("ShowStatus");

        string[] screen = game.ScreenText();
        Assert.Equal("      COMMANDER JAMESON", screen[0]);
        Assert.Equal("Hyperspace System   :Cemave", screen[4]);
        Assert.Equal("Condition           :Green", screen[5]);
        Assert.Equal("Fuel:7.0 Light Years", screen[6]);
        Assert.Equal("Cash:    100.0 Cr", screen[7]);
        Assert.Equal("Legal Status: Clean", screen[8]);
        Assert.Equal("Rating: Harmless", screen[9]);
        Assert.Equal("EQUIPMENT:", screen[11]);
        Assert.Equal("     Front Pulse Laser", screen[12]);
    }

    [Fact]
    public void ATranslationNeedsNoChangesToTheGame()
    {
        string yaml = File.ReadAllText(Path.Combine(GameStrings.DefaultFolder, "en-strings.yml"))
            .Replace("fuel: \"Fuel\"", "fuel: \"Carburant\"")
            .Replace("inventory: \"INVENTORY\\n\"", "inventory: \"INVENTAIRE\\n\"");
        var game = NewGame(GameStrings.Parse("fr", yaml, "fr-strings.yml"));

        game.Call("ShowInventory");

        string[] screen = game.ScreenText();
        Assert.Equal("          INVENTAIRE", screen[0]);
        Assert.Equal("Carburant:7.0 Light Years", screen[3]);
    }

    [Fact]
    public void InFlightMessagesComeFromTheStrings()
    {
        var game = NewGame();
        game.Set("_viewType", 0);
        game.Set("_messageDestroyed", 3);

        game.Call("ShowMessage", "equipment.fuel_scoops");

        Assert.Equal("    FUEL SCOOPS DESTROYED", game.ScreenText()[20]);
    }

    /// <summary>A new game with the default commander, with the screen cleared.</summary>
    private static PrivateGame NewGame(GameStrings? strings = null)
    {
        var game = new PrivateGame(strings);
        game.Call("Begin");
        game.Call("ApplySavedCommander");
        game.Call("ClearScreen", 1);
        return game;
    }
}

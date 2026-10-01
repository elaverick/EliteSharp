using System.Reflection;
using EliteSharp.Data;
using EliteSharp.Game;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The game's fixed text comes from the strings file for the chosen language
/// (Data/Strings/en-strings.yml for English), and is printed just as the
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
        Assert.Equal("GALACTIC HYPERSPACE ", strings.Get("equipment.galactic_hyperspace"));
        Assert.Equal("CASH:{cash}", strings.Get("market.cash_balance"));
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
    public void EveryEnglishStringCanBePrinted()
    {
        // Each string is printed in the style of a standard token or an
        // extended token, so each must only use the codes and characters
        // that one of them can print
        var strings = GameStrings.Load("en");
        var game = new PrivateGame(strings);
        foreach (string key in strings.Keys)
        {
            Assert.True(CanConvert("_textCodes", "TextCodes", '_') || CanConvert("_extendedTextCodes", "ExtendedTextCodes", 'Z'), key);

            bool CanConvert(string cache, string codes, char highest)
            {
                try
                {
                    var table = typeof(EliteGame).GetField(codes, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                    game.Call("TextToCodes", key, game.Get<object>(cache), table, highest);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }
        }
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

    [Fact]
    public void AnUnknownCodeIsReported()
    {
        string yaml = File.ReadAllText(Path.Combine(GameStrings.DefaultFolder, "en-strings.yml"))
            .Replace("cash_balance: \"CASH:{cash}\"", "cash_balance: \"CASH:{money}\"");
        var game = NewGame(GameStrings.Parse("xx", yaml, "xx-strings.yml"));

        var e = Assert.Throws<InvalidDataException>(() => game.Call("PrintText", "market.cash_balance"));
        Assert.Equal("The string 'market.cash_balance' in xx-strings.yml contains an unknown code '{money}'", e.Message);
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
            .Replace("fuel: \"FUEL\"", "fuel: \"CARBURANT\"")
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

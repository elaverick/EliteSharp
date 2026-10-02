using EliteSharp.Data;

namespace EliteSharp.Tests.Game;

/// <summary>
/// A mistake in the trading data file (Assets/trading.yml) is reported when
/// the game starts.
/// </summary>
public sealed class TradingDataTests
{
    private static string Trading => File.ReadAllText(TradingData.DefaultPath).ReplaceLineEndings("\n");

    [Theory]
    [InlineData("equipment:\n  missile: 30.0", "equipment:\n  [missile]: 30.0", "line 48): a key must be a name (some text), not a list or a mapping")]
    [InlineData("equipment:\n  missile: 30.0", "equipment:\n  {missile: 1}: 30.0", "line 48): a key must be a name (some text), not a list or a mapping")]
    [InlineData("equipment:\n  missile: 30.0", "equipment:\n  missile: [30.0]", "line 48): the price of 'missile' must be a number of credits, to one decimal place")]
    public void AMalformedFileIsReported(string find, string replace, string expected)
    {
        string yaml = Trading;
        Assert.Contains(find, yaml);

        var e = Assert.Throws<InvalidDataException>(() => TradingData.Parse(yaml.Replace(find, replace), "xx-trading.yml"));
        Assert.Equal($"xx-trading.yml ({expected}", e.Message);
    }
}

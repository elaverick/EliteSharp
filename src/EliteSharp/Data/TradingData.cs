using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Data;

/// <summary>A commodity in the markets (see Assets/trading.yml).</summary>
/// <param name="Name">The commodity's name (its text is "commodities.&lt;name&gt;" in the strings).</param>
/// <param name="BasePrice">The base price, in units of 0.4 credits.</param>
/// <param name="EconomicFactor">How much the price rises (and the quantity falls) for each step of the economy from rich industrial to poor agricultural.</param>
/// <param name="Unit">The unit: 0 for tonnes, 1 for kilograms or 2 for grams.</param>
/// <param name="BaseQuantity">The base quantity for sale (which can be negative, so there is none in some economies).</param>
/// <param name="Fluctuation">The mask for the random number that is added to the price and quantity.</param>
public sealed record Commodity(string Name, int BasePrice, int EconomicFactor, int Unit, int BaseQuantity, int Fluctuation)
{
    /// <summary>
    /// The economic factor and unit as the original packs them into the
    /// second byte of the commodity's QQ23 entry (the factor's size in bits
    /// 0-4, the unit in bits 5-6, and bit 7 set if the factor is negative),
    /// which the market calculations use.
    /// </summary>
    public int FactorAndUnit => (EconomicFactor < 0 ? 0x80 : 0) | (Unit << 5) | Math.Abs(EconomicFactor);
}

/// <summary>
/// The goods in the markets and the prices of the equipment for sale, from
/// Assets/trading.yml (the original's QQ23 and PRXS tables).
/// </summary>
public sealed class TradingData
{
    /// <summary>The number of commodities (the size of the cargo hold and market tables).</summary>
    public const int CommodityCount = 17;

    private static readonly string[] Units = ["t", "kg", "g"];

    private TradingData(IReadOnlyList<Commodity> commodities, IReadOnlyList<(string Name, int Price)> equipment)
    {
        Commodities = commodities;
        EquipmentPrices = equipment;
    }

    /// <summary>The trading data file.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Assets", "trading.yml");

    /// <summary>The commodities, in the order of the market.</summary>
    public IReadOnlyList<Commodity> Commodities { get; }

    /// <summary>The equipment on the Equip Ship screen after fuel, and its price in tenths of a credit.</summary>
    public IReadOnlyList<(string Name, int Price)> EquipmentPrices { get; }

    /// <summary>Load the trading data from a file.</summary>
    public static TradingData Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The trading data file '{path}' is missing", path);
        }

        return Parse(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Read the trading data from the contents of a trading data file.</summary>
    /// <exception cref="InvalidDataException">The file isn't valid (the message says where and why).</exception>
    public static TradingData Parse(string yaml, string source)
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
        catch (InvalidOperationException e)
        {
            // YamlDotNet reports some malformed files this way
            throw new InvalidDataException($"{source}: the file isn't valid YAML ({e.Message})", e);
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"{source}: the file must contain a single mapping");
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("commodities"), out var commoditiesNode) || commoditiesNode is not YamlSequenceNode commodityList)
        {
            throw Error(root, "'commodities' must be a list of the commodities");
        }

        var commodities = new List<Commodity>();
        foreach (var node in commodityList)
        {
            if (node is not YamlMappingNode commodity)
            {
                throw Error(node, "each commodity must be a mapping");
            }

            string unit = Text(commodity, "unit");
            if (!Units.Contains(unit))
            {
                throw Error(commodity, $"'unit' must be one of {string.Join(", ", Units)}");
            }

            commodities.Add(new Commodity(
                Text(commodity, "name"),
                Number(commodity, "base_price", 0, 255),
                Number(commodity, "economic_factor", -31, 31),
                Array.IndexOf(Units, unit),
                Number(commodity, "base_quantity", -128, 127),
                Number(commodity, "fluctuation", 0, 255)));
        }

        if (commodities.Count != CommodityCount)
        {
            throw Error(commodityList, $"there must be {CommodityCount} commodities");
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("equipment"), out var equipmentNode) || equipmentNode is not YamlMappingNode equipmentList)
        {
            throw Error(root, "'equipment' must be a mapping of equipment to prices");
        }

        var equipment = new List<(string, int)>();
        foreach (var (key, value) in equipmentList.Children)
        {
            if (key is not YamlScalarNode { Value: { } name })
            {
                throw Error(key, "a key must be a name (some text), not a list or a mapping");
            }

            if (value is not YamlScalarNode { Value: { } price }
                || !decimal.TryParse(price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal credits)
                || credits * 10 != decimal.Truncate(credits * 10)
                || credits * 10 > 0xFFFF)
            {
                throw Error(value, $"the price of '{name}' must be a number of credits, to one decimal place");
            }

            equipment.Add((name, (int)(credits * 10)));
        }

        return new TradingData(commodities, equipment);

        string Text(YamlMappingNode mapping, string key) =>
            mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode { Value: { } text }
                ? text
                : throw Error(mapping, $"'{key}' is missing");

        int Number(YamlMappingNode mapping, string key, int lowest, int highest)
        {
            string text = Text(mapping, key);
            bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int number)
                : int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
            return parsed && number >= lowest && number <= highest
                ? number
                : throw Error(mapping[key], $"'{key}' must be a number from {lowest} to {highest}");
        }

        InvalidDataException Error(YamlNode node, string message) => new($"{source} (line {node.Start.Line}): {message}");
    }
}

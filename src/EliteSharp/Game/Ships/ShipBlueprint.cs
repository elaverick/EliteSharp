using System.Globalization;
using System.Text.Json.Serialization;

namespace EliteSharp.Game.Ships;

/// <summary>
/// A vertex in a ship blueprint (VERTEX macro): coordinates, the four faces that
/// the vertex belongs to, and the visibility distance beyond which it is hidden.
/// </summary>
public readonly record struct ShipVertex(int X, int Y, int Z, int Face1, int Face2, int Face3, int Face4, int Visibility);

/// <summary>
/// An edge in a ship blueprint (EDGE macro): the two vertices it joins, the two
/// faces either side of it and its visibility distance.
/// </summary>
public readonly record struct ShipEdge(int Vertex1, int Vertex2, int Face1, int Face2, int Visibility);

/// <summary>
/// A face in a ship blueprint (FACE macro): the face normal (before scaling by
/// the blueprint's normal scale factor) and the distance beyond which the face
/// is always treated as visible.
/// </summary>
public readonly record struct ShipFace(int NormalX, int NormalY, int NormalZ, int Visibility);

/// <summary>
/// A ship type's attributes, as stored in its JSON file in Assets/Ships (see
/// ship.schema.json there for a description of each property).
/// </summary>
public sealed record ShipAttributes
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public required int Type { get; init; }

    public required string Name { get; init; }

    /// <summary>The path of the ship's glTF model, relative to the JSON file.</summary>
    public required string Model { get; init; }

    public int MaxEnergy { get; init; }

    public int MaxSpeed { get; init; }

    public int LaserPower { get; init; }

    public int Missiles { get; init; }

    /// <summary>The bounty in credits (to one decimal place).</summary>
    public double Bounty { get; init; }

    public int TargetRadius { get; init; }

    /// <summary>The most cargo canisters released when the ship is destroyed.</summary>
    public int Canisters { get; init; }

    /// <summary>The market item that the ship becomes when scooped, if any.</summary>
    public string? ScoopedAs { get; init; }

    /// <summary>The kill points awarded for destroying the ship (in 256ths).</summary>
    public double KillPoints { get; init; }

    /// <summary>The ship's colour, as a mode 1 screen byte in hex.</summary>
    public required string Colour { get; init; }

    /// <summary>The colour of the ship on the scanner, as a mode 2 screen byte in hex.</summary>
    public required string ScannerColour { get; init; }

    /// <summary>The default NEWB flags (see <see cref="ShipBlueprint.FlagNames"/>).</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
}

/// <summary>
/// Everything the game needs to know about a type of ship: its attributes (from
/// its JSON file) and its model (from its glTF file). The properties present
/// the data in the form the original stores it in the blueprints at XX21 and
/// the tables indexed by ship type, so the game code works exactly as before.
/// </summary>
public sealed class ShipBlueprint
{
    /// <summary>The names of the bits in the NEWB flags, from bit 0 to bit 7.</summary>
    public static readonly string[] FlagNames =
        ["trader", "bountyHunter", "hostile", "pirate", "docking", "innocent", "cop", "escapePod"];

    /// <summary>The market items, in the order of the market (QQ23).</summary>
    public static readonly string[] MarketItems =
    [
        "Food", "Textiles", "Radioactives", "Slaves", "Liquor/Wines", "Luxuries",
        "Narcotics", "Computers", "Machinery", "Alloys", "Firearms", "Furs",
        "Minerals", "Gold", "Platinum", "Gem-Stones", "Alien Items",
    ];

    public ShipBlueprint(string id, ShipAttributes attributes, ShipModel model)
    {
        Id = id;
        Attributes = attributes;
        Model = model;
        BlueprintNumber = Range(attributes.Type, 1, ShipCatalogue.Count, "type");
        Name = attributes.Name;
        MaxEnergy = Range(attributes.MaxEnergy, 0, 255, "maxEnergy");
        MaxSpeed = Range(attributes.MaxSpeed, 0, 255, "maxSpeed");
        LaserAndMissiles = (Range(attributes.LaserPower, 0, 31, "laserPower") << 3)
            | Range(attributes.Missiles, 0, 7, "missiles");
        Bounty = Fixed(attributes.Bounty, 10, 0xFFFF, "bounty");
        TargetableArea = Range(attributes.TargetRadius, 0, 255, "targetRadius") * attributes.TargetRadius;

        int scooped = 0;
        if (attributes.ScoopedAs != null)
        {
            // Byte #0 holds the market item less one, so Food can't be scooped
            int item = Array.FindIndex(MarketItems, m => m.Equals(attributes.ScoopedAs, StringComparison.OrdinalIgnoreCase));
            if (item < 1)
            {
                throw new InvalidDataException($"scoopedAs '{attributes.ScoopedAs}' must be one of: {string.Join(", ", MarketItems[1..])}");
            }

            scooped = item - 1;
        }

        CanisterAndScoopByte = (scooped << 4) | Range(attributes.Canisters, 0, 15, "canisters");

        int killPoints = Fixed(attributes.KillPoints, 256, 0xFFFF, "killPoints");
        KillInteger = killPoints >> 8;
        KillFraction = killPoints & 0xFF;

        Colour = ParseByte(attributes.Colour, "colour");
        ScannerColour = ParseByte(attributes.ScannerColour, "scannerColour");

        foreach (string flag in attributes.Flags)
        {
            int bit = Array.FindIndex(FlagNames, f => f.Equals(flag, StringComparison.OrdinalIgnoreCase));
            if (bit < 0)
            {
                throw new InvalidDataException($"Unknown flag '{flag}' (the flags are: {string.Join(", ", FlagNames)})");
            }

            DefaultBehaviour |= 1 << bit;
        }
    }

    /// <summary>The ship's asset name (its JSON file name without the type number).</summary>
    public string Id { get; }

    public ShipAttributes Attributes { get; }

    public ShipModel Model { get; }

    /// <summary>The blueprint's position in the XX21 table (1-33), which is also its ship type.</summary>
    public int BlueprintNumber { get; }

    public string Name { get; }

    /// <summary>
    /// Blueprint byte #0: bits 0-3 contain the maximum number of canisters released
    /// on demise, bits 4-7 contain the market item (less 1) when the ship is scooped.
    /// </summary>
    public int CanisterAndScoopByte { get; }

    public int MaxCanisters => CanisterAndScoopByte & 0x0F;

    /// <summary>The market item when scooped (bits 4-7 of byte #0 plus 1), or 0 for none.</summary>
    public int ScoopedItem => CanisterAndScoopByte >> 4;

    /// <summary>Bytes #1-2: the targetable area (the square of the targeting radius).</summary>
    public int TargetableArea { get; }

    /// <summary>Byte #5: the size of the ship line heap, i.e. 1 + 4 * the maximum number of visible edges.</summary>
    public int LineHeapSize => 1 + 4 * Model.MaxVisibleEdges;

    public int MaxVisibleEdges => Model.MaxVisibleEdges;

    /// <summary>Byte #6 / 4: the vertex from which the ship fires its lasers.</summary>
    public int GunVertex => Model.GunVertex;

    /// <summary>Byte #7: the explosion count, stored as 4 * n + 6 for n exploding vertices.</summary>
    public int ExplosionCountByte => 4 * Model.ExplosionVertices + 6;

    /// <summary>Bytes #10-11: the bounty in Cr * 10.</summary>
    public int Bounty { get; }

    /// <summary>Byte #13: the distance (z_hi) beyond which the ship is shown as a dot.</summary>
    public int VisibilityDistance => Model.DotDistance;

    /// <summary>Byte #14: the ship's maximum energy.</summary>
    public int MaxEnergy { get; }

    /// <summary>Byte #15: the ship's maximum speed.</summary>
    public int MaxSpeed { get; }

    /// <summary>Byte #18: face normals are scaled by 2^NormalScale.</summary>
    public int NormalScale => Model.NormalScale;

    /// <summary>Byte #19: bits 3-7 contain the laser power, bits 0-2 the number of missiles.</summary>
    public int LaserAndMissiles { get; }

    public int LaserPower => LaserAndMissiles >> 3;

    public int Missiles => LaserAndMissiles & 7;

    public IReadOnlyList<ShipVertex> Vertices => Model.Vertices;

    public IReadOnlyList<ShipEdge> Edges => Model.Edges;

    public IReadOnlyList<ShipFace> Faces => Model.Faces;

    /// <summary>shpcol: the ship's colour (a mode 1 screen byte).</summary>
    public int Colour { get; }

    /// <summary>scacol: the ship's colour on the scanner (a mode 2 screen byte).</summary>
    public int ScannerColour { get; }

    /// <summary>E%: the ship's default NEWB flags.</summary>
    public int DefaultBehaviour { get; }

    /// <summary>KWH%: the integer part of the kill points for the ship.</summary>
    public int KillInteger { get; }

    /// <summary>KWL%: the fractional part of the kill points for the ship (in 256ths).</summary>
    public int KillFraction { get; }

    public override string ToString() => $"{Name} (type {BlueprintNumber})";

    private static int Range(int value, int min, int max, string what) =>
        value >= min && value <= max ? value : throw new InvalidDataException($"{what} {value} must be between {min} and {max}");

    /// <summary>Convert a value to a whole number of 1/scale units, checking it is exact and in range.</summary>
    private static int Fixed(double value, int scale, int max, string what)
    {
        double scaled = value * scale;
        int whole = (int)Math.Round(scaled);
        if (Math.Abs(scaled - whole) > 1e-6 || whole < 0 || whole > max)
        {
            throw new InvalidDataException($"{what} {value.ToString(CultureInfo.InvariantCulture)} must be a multiple of 1/{scale} between 0 and {(double)max / scale}");
        }

        return whole;
    }

    private static int ParseByte(string text, string what)
    {
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) && value is >= 0 and <= 255
            ? value
            : throw new InvalidDataException($"{what} '{text}' must be a hex byte such as \"0xFF\"");
    }
}

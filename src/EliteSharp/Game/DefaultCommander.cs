namespace EliteSharp.Game;

/// <summary>
/// NA%: the commander that a new career starts with, Commander Jameson,
/// docked at Lave in galaxy 1 with 100 credits, a pulse laser and three
/// missiles. The original keeps this as a saved commander, which
/// RestoreDefaultCommander builds from these values.
/// </summary>
internal static class DefaultCommander
{
    /// <summary>NAME: the commander's name.</summary>
    public const string Name = "JAMESON";

    /// <summary>QQ0 and QQ1: the galactic coordinates of the system we start in (Lave).</summary>
    public const int SystemX = 20, SystemY = 173;

    /// <summary>QQ21: the seeds for galaxy 1, from which every system in every galaxy is generated.</summary>
    public static ReadOnlySpan<byte> GalaxySeeds => [0x4A, 0x5A, 0x48, 0x02, 0x53, 0xB7];

    /// <summary>CASH: 100.0 credits (in tenths of a credit).</summary>
    public const int Cash = 1000;

    /// <summary>QQ14: 7.0 light years of fuel (in tenths of a light year).</summary>
    public const int Fuel = 70;

    /// <summary>LASER: a pulse laser on the front view (its power, as in PulseLaserPower).</summary>
    public const int FrontLaser = 15;

    /// <summary>CRGO: the capacity of the cargo hold.</summary>
    public const int CargoCapacity = 22;

    /// <summary>NOMSL: the number of missiles.</summary>
    public const int Missiles = 3;

    /// <summary>AVL: the quantities for sale in Lave's market when we start, for each commodity.</summary>
    public static ReadOnlySpan<byte> MarketAvailability => [16, 15, 17, 0, 3, 28, 14, 0, 0, 10, 0, 17, 58, 7, 9, 8, 0];

    /// <summary>SVC: the save count.</summary>
    public const int SaveCount = 0x80;
}

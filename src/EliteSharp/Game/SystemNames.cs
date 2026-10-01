namespace EliteSharp.Game;

/// <summary>The letters that the systems' names are made from.</summary>
internal static class SystemNames
{
    /// <summary>
    /// QQ16: the letters that a system's seeds pick for each part of its name,
    /// numbered from 0 (a seed of 0 adds nothing, so the first pair is never
    /// used in a name). Each is two letters, apart from the single "A", which
    /// the original stores as "A?" and prints without the "?".
    /// </summary>
    public static readonly string[] LetterPairs =
    [
        "AL", "LE", "XE", "GE", "ZA", "CE", "BI", "SO", "US", "ES", "AR", "MA", "IN", "DI", "RE", "A",
        "ER", "AT", "EN", "BE", "RA", "LA", "VE", "TI", "ED", "OR", "QU", "AN", "TE", "IS", "RI", "ON",
    ];
}

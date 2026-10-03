namespace EliteSharp;

/// <summary>
/// Where the game's assets are: the Assets folder next to the game, or a mod's
/// folder (see the --game option) in its place. A mod needn't have every file:
/// a file that isn't in the mod's folder comes from the Assets folder.
/// </summary>
public static class GameAssets
{
    /// <summary>The game's own Assets folder.</summary>
    public static string BaseFolder => Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>The mod's folder, which is used in place of the Assets folder, or null if there is no mod.</summary>
    public static string? ModFolder { get; set; }

    /// <summary>The folders to look in for an asset, the mod's first.</summary>
    public static IReadOnlyList<string> Folders => ModFolder is { } mod ? [mod, BaseFolder] : [BaseFolder];

    /// <summary>
    /// The path of an asset file (such as "Strings", "en-strings.yml"): the
    /// mod's, if it has one, or else the game's own (even if that is missing,
    /// so a missing file is reported where it should be).
    /// </summary>
    public static string File(params string[] parts) =>
        Folders.Select(folder => Path.Combine([folder, .. parts])).FirstOrDefault(System.IO.File.Exists)
        ?? Path.Combine([BaseFolder, .. parts]);

    /// <summary>The asset folders with this name (such as "Ships") that exist, the mod's first.</summary>
    public static IReadOnlyList<string> FoldersNamed(string name) =>
        Folders.Select(folder => Path.Combine(folder, name)).Where(Directory.Exists).ToList();
}

using System.Globalization;
using System.Text.Json;

namespace EliteSharp.Game;

/// <summary>
/// A saved game: a commander as they were when they were saved, with the
/// label they were saved under. <see cref="Data"/> is the commander file in
/// the original's format (the commander data block from TP onwards, with its
/// checksums), so everything else about the save is read from it.
/// </summary>
public sealed record CommanderSave(string Path, string Commander, string Label, DateTimeOffset Saved, byte[] Data)
{
    /// <summary>CASH: the commander's cash, in tenths of a credit.</summary>
    public long Cash => (uint)(Data[9] << 24 | Data[10] << 16 | Data[11] << 8 | Data[12]);

    /// <summary>GCNT: the galaxy number, from 1 to 8.</summary>
    public int Galaxy => Data[15] + 1;

    /// <summary>TALLY: the kill tally.</summary>
    public int KillTally => Data[71] | Data[72] << 8;

    /// <summary>The name of the system the commander is docked at, in capitals.</summary>
    public string SystemName => CommanderStore.SystemNameAt(Data.AsSpan(3, 6), Data[1], Data[2]);

    /// <summary>When the save was made, in local time, as it is shown on the screen.</summary>
    public string SavedText => Saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The date of the save, in local time, as it is shown in the lists.</summary>
    public string SavedDateText => Saved.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// The saved games, which aren't in the original: each save is a file in the
/// Commanders folder in the data folder, and a commander can have as many
/// saves as they like. A save is a small JSON file holding the commander's
/// name, the label it was saved under, when it was saved, and the commander
/// file in the original's format (as hex).
/// </summary>
public sealed class CommanderStore(string dataFolder)
{
    /// <summary>The name of the folder in the data folder that holds the saves.</summary>
    public const string FolderName = "Commanders";

    /// <summary>The size of a commander file in the original's format.</summary>
    public const int FileSize = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The folder that holds the saves.</summary>
    public string Folder => System.IO.Path.Combine(dataFolder, FolderName);

    /// <summary>
    /// Read every save, newest first, skipping any file that isn't a save
    /// (or can't be read).
    /// </summary>
    public List<CommanderSave> LoadAll()
    {
        Directory.CreateDirectory(Folder);

        var saves = new List<CommanderSave>();
        foreach (string path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            if (Read(path) is { } save)
            {
                saves.Add(save);
            }
        }

        saves.Sort((a, b) => b.Saved.CompareTo(a.Saved));
        return saves;
    }

    /// <summary>
    /// The commanders with saves, each with their saves (newest first), with
    /// the commander saved most recently first.
    /// </summary>
    public static List<(string Commander, List<CommanderSave> Saves)> GroupByCommander(IEnumerable<CommanderSave> saves) =>
        [.. saves
            .GroupBy(s => s.Commander, StringComparer.Ordinal)
            .Select(g => (g.Key, g.OrderByDescending(s => s.Saved).ToList()))
            .OrderByDescending(g => g.Item2[0].Saved)];

    /// <summary>
    /// Write a save, as a new file, or over <paramref name="replacing"/> if
    /// it is given, and return it.
    /// </summary>
    public CommanderSave Write(string commander, string label, byte[] data, DateTimeOffset saved, CommanderSave? replacing = null)
    {
        Directory.CreateDirectory(Folder);
        string path = replacing?.Path ?? NewPath(commander, saved);
        var file = new SaveFile(1, commander, label, saved, Convert.ToHexString(data));

        // Write the whole file before it replaces the old one, so a failed
        // write never leaves half a save
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temporary, path, overwrite: true);
        return new CommanderSave(path, commander, label, saved, data);
    }

    /// <summary>Delete a save.</summary>
    public static void Delete(CommanderSave save) => File.Delete(save.Path);

    /// <summary>
    /// Returns true if the data is a commander file (one that LOD would load:
    /// the right size, and the first byte doesn't have bit 7 set).
    /// </summary>
    public static bool IsCommanderFile(byte[] data) => data.Length == FileSize && (data[0] & 0x80) == 0;

    /// <summary>
    /// The name of the system at galactic coordinates (x, y) in the galaxy
    /// with the given seeds, in capitals (as the chart's routines work it out
    /// from the seeds), or "" if there is no system there.
    /// </summary>
    public static string SystemNameAt(ReadOnlySpan<byte> galaxySeeds, int x, int y)
    {
        Span<int> seeds = stackalloc int[6];
        for (int i = 0; i < 6; i++)
        {
            seeds[i] = galaxySeeds[i];
        }

        for (int system = 0; system < 256; system++)
        {
            if (seeds[3] == x && seeds[1] == y)
            {
                // cpl: each pair of seeds picks the next part of the name
                var name = new System.Text.StringBuilder();
                int pairs = (seeds[0] & 0x40) != 0 ? 4 : 3;
                for (int pair = 0; pair < pairs; pair++)
                {
                    int token = seeds[5] & 31;
                    if (token != 0)
                    {
                        name.Append(SystemNames.LetterPairs[token]);
                    }

                    Twist(seeds);
                }

                return name.ToString();
            }

            // TT20: move on to the next system
            for (int i = 0; i < 4; i++)
            {
                Twist(seeds);
            }
        }

        return "";
    }

    /// <summary>TT54: twist the seeds once (s0 = s1, s1 = s2, s2 = s0 + s1 + s2).</summary>
    private static void Twist(Span<int> seeds)
    {
        int lo = seeds[0] + seeds[2];
        int hi = seeds[1] + seeds[3] + (lo > 0xFF ? 1 : 0);
        lo &= 0xFF;
        hi &= 0xFF;

        seeds[0] = seeds[2];
        seeds[1] = seeds[3];
        seeds[3] = seeds[5];
        seeds[2] = seeds[4];

        int newLo = lo + seeds[2];
        seeds[4] = newLo & 0xFF;
        seeds[5] = (hi + seeds[3] + (newLo > 0xFF ? 1 : 0)) & 0xFF;
    }

    /// <summary>Read a save, or return null if the file isn't a save.</summary>
    private static CommanderSave? Read(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(path), JsonOptions);
            if (file?.Commander is not { Length: > 0 } commander || file.Data == null)
            {
                return null;
            }

            byte[] data = Convert.FromHexString(file.Data);
            return IsCommanderFile(data)
                ? new CommanderSave(path, commander, file.Label ?? "", file.Saved, data)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A new file name for a save, from the commander's name and the time.</summary>
    private string NewPath(string commander, DateTimeOffset saved)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string name = new([.. commander.Select(c => invalid.Contains(c) || c == '.' ? '_' : c)]);
        string stem = $"{name}-{saved.ToLocalTime():yyyyMMdd-HHmmss}";
        string path = System.IO.Path.Combine(Folder, stem + ".json");
        for (int n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(Folder, $"{stem}-{n}.json");
        }

        return path;
    }

    /// <summary>The contents of a save file.</summary>
    private sealed record SaveFile(int Version, string? Commander, string? Label, DateTimeOffset Saved, string? Data);
}

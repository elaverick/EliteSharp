using System.Reflection;
using EliteSharp.Game;

namespace EliteSharp.Tests.Game;

/// <summary>
/// Commanders are saved as labelled saves in the Commanders folder (see
/// <see cref="CommanderStore"/>), each holding a commander file in the
/// original's format.
/// </summary>
public sealed class CommanderSaveTests : IDisposable
{
    /// <summary>Galaxy 1's seeds (QQ21), as the default commander starts with.</summary>
    private static readonly byte[] GalaxyOneSeeds = [0x4A, 0x5A, 0x48, 0x02, 0x53, 0xB7];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "EliteSharpTests-" + Guid.NewGuid().ToString("N"));

    public CommanderSaveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(20, 173, "LAVE")]
    [InlineData(11, 174, "DISO")]
    [InlineData(13, 186, "LEESTI")]
    [InlineData(64, 137, "")]
    public void ASystemIsFoundByItsCoordinates(int x, int y, string name)
    {
        Assert.Equal(name, CommanderStore.SystemNameAt(GalaxyOneSeeds, x, y));
    }

    [Fact]
    public void ASaveIsWrittenAndReadBack()
    {
        var store = NewStore();
        byte[] data = CommanderFile(cash: 12345, galaxy: 2, killTally: 0x0301);
        var saved = new DateTimeOffset(2026, 10, 4, 14, 32, 0, TimeSpan.Zero);
        store.Write("FRED", "Docked at Lave", data, saved);

        var save = Assert.Single(store.LoadAll());
        Assert.Equal("FRED", save.Commander);
        Assert.Equal("Docked at Lave", save.Label);
        Assert.Equal(saved, save.Saved);
        Assert.Equal(data, save.Data);
        Assert.Equal(12345, save.Cash);
        Assert.Equal(3, save.Galaxy);
        Assert.Equal(0x0301, save.KillTally);
        Assert.Equal("LAVE", save.SystemName);
    }

    [Fact]
    public void ACommanderCanHaveManySavesAndTheNewestComeFirst()
    {
        var store = NewStore();
        var start = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        store.Write("FRED", "one", CommanderFile(), start);
        store.Write("JIM", "two", CommanderFile(), start.AddMinutes(1));
        store.Write("FRED", "three", CommanderFile(), start.AddMinutes(2));

        // Two saves in the same second still get a file each
        store.Write("FRED", "four", CommanderFile(), start.AddMinutes(2));

        var commanders = CommanderStore.GroupByCommander(store.LoadAll());
        Assert.Equal(["FRED", "JIM"], commanders.Select(c => c.Commander));
        Assert.Equal(3, commanders[0].Saves.Count);
        Assert.Equal("one", commanders[0].Saves[^1].Label);
    }

    [Fact]
    public void ASaveCanBeWrittenOverOrDeleted()
    {
        var store = NewStore();
        var start = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var first = store.Write("FRED", "old", CommanderFile(cash: 1), start);
        store.Write("FRED", "new", CommanderFile(cash: 2), start.AddHours(1), replacing: first);

        var save = Assert.Single(store.LoadAll());
        Assert.Equal("new", save.Label);
        Assert.Equal(2, save.Cash);

        CommanderStore.Delete(save);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void FilesThatAreNotSavesAreIgnored()
    {
        var store = NewStore();
        store.LoadAll();
        File.WriteAllText(Path.Combine(store.Folder, "junk.json"), "not json");
        File.WriteAllText(Path.Combine(store.Folder, "empty.json"), "{}");
        var bad = CommanderFile();
        bad[0] = 0x80;
        store.Write("BAD", "bad", bad, DateTimeOffset.Now);

        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void TheGameSavesAndLoadsACommander()
    {
        var game = new PrivateGame();
        game.Get<GameOptions>("_options").DataFolder = _folder;
        game.Call("RestoreDefaultCommander");
        game.Call("ApplySavedCommander");
        game.Set("_cash", 54321u);

        Assert.True((bool)Invoke(game, "SaveCommander", "FRED", "My save", null)!);
        var save = Assert.Single(NewStore().LoadAll());
        Assert.Equal("FRED", save.Commander);
        Assert.Equal("My save", save.Label);
        Assert.Equal("LAVE", save.SystemName);
        Assert.Equal(54321, save.Cash);
        Assert.Equal("FRED", game.Get<string>("CommanderName"));

        // Start again as Jameson, then load the save
        game.Call("RestoreDefaultCommander");
        game.Call("ApplySavedCommander");
        Assert.Equal(1000u, game.Get<uint>("_cash"));

        Invoke(game, "LoadCommander", save);
        game.Call("ApplySavedCommander");
        Assert.Equal(54321u, game.Get<uint>("_cash"));
        Assert.Equal("FRED", game.Get<string>("CommanderName"));

        // The checksum matches, so the commander isn't marked as a cheat
        Assert.Equal(0, game.Get<int>("_competitionFlags") & 0x80);
    }

    private CommanderStore NewStore() => new(_folder);

    /// <summary>A commander file docked at Lave, with the given cash, galaxy (0-7) and kill tally.</summary>
    private static byte[] CommanderFile(long cash = 1000, int galaxy = 0, int killTally = 0)
    {
        var data = new byte[CommanderStore.FileSize];
        data[1] = 20;
        data[2] = 173;
        GalaxyOneSeeds.CopyTo(data, 3);
        data[9] = (byte)(cash >> 24);
        data[10] = (byte)(cash >> 16);
        data[11] = (byte)(cash >> 8);
        data[12] = (byte)cash;
        data[15] = (byte)galaxy;
        data[71] = (byte)killTally;
        data[72] = (byte)(killTally >> 8);
        return data;
    }

    private static object? Invoke(PrivateGame game, string method, params object?[] args) =>
        typeof(EliteGame).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game.Game, args);
}

using EliteSharp.Game.Missions;

namespace EliteSharp.Tests.Missions;

/// <summary>
/// A stand-in for the game, which records everything the mission runtime asks
/// it to do, so the tests can check the missions without the HUD or renderer.
/// </summary>
internal sealed class FakeMissionHost : IMissionHost
{
    private readonly Queue<int> _randomNumbers = new();

    public List<string> Calls { get; } = [];

    public int MissionFlags { get; set; }

    public int Galaxy { get; set; }

    public (int X, int Y) CurrentSystem { get; set; }

    public int KillTally { get; set; }

    public string CommanderName { get; set; } = "JAMESON";

    public Dictionary<int, int> ShipCounts { get; } = [];

    public int Cash { get; private set; }

    public List<MissionEquipment> Equipment { get; } = [];

    /// <summary>The random numbers drawn, in order.</summary>
    public List<int> RandomNumbersDrawn { get; } = [];

    /// <summary>Put us in a system, given its galaxy (from 1) and name.</summary>
    public FakeMissionHost At(int galaxy, string system)
    {
        var found = GalaxyAtlas.Find(galaxy - 1, system).Single();
        Galaxy = found.Galaxy;
        CurrentSystem = (found.X, found.Y);
        return this;
    }

    /// <summary>Queue random numbers for the host to return (after these run out, it returns 0).</summary>
    public FakeMissionHost WithRandomNumbers(params int[] numbers)
    {
        foreach (int number in numbers)
        {
            _randomNumbers.Enqueue(number);
        }

        return this;
    }

    public int ShipCount(int shipType) => ShipCounts.GetValueOrDefault(shipType);

    public int NextRandom()
    {
        int number = _randomNumbers.Count > 0 ? _randomNumbers.Dequeue() : 0;
        RandomNumbersDrawn.Add(number);
        return number;
    }

    public void AddCash(int tenths)
    {
        Cash += tenths;
        Calls.Add($"addCash {tenths}");
    }

    public void FitEquipment(MissionEquipment equipment)
    {
        Equipment.Add(equipment);
        Calls.Add($"fit {equipment}");
    }

    public void SpawnTarget(int shipType, int ai) => Calls.Add($"spawnTarget {shipType} ai={ai}");

    public void SpawnHostileShip(int shipType) => Calls.Add($"spawnHostile {shipType}");

    public void ClearScreen() => Calls.Add("clearScreen");

    public void ShowText(int? row, int? column, bool justify, IReadOnlyList<string> lines, bool newlineAtEnd)
    {
        Calls.Add($"text row={row} column={column} justify={justify} newlineAtEnd={newlineAtEnd}");
        Calls.AddRange(lines.Select(l => $"  |{l}|"));
    }

    public void Pause(int frames) => Calls.Add($"pause {frames}");

    public void WaitForKey() => Calls.Add("waitForKey");

    public void IntroduceShip(int shipType) => Calls.Add($"introduceShip {shipType}");

    public void ShowShipUntilKey() => Calls.Add("showShipUntilKey");

    public void ShowSystemDescription(string text) => Calls.Add($"description |{text}|");

    /// <summary>The text printed, one line per entry.</summary>
    public IEnumerable<string> TextLines => Calls.Where(c => c.StartsWith("  |", StringComparison.Ordinal)).Select(c => c[3..^1]);
}

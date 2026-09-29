using EliteSharp.Game.Missions;
using EliteSharp.Game.Ships;

namespace EliteSharp.Game;

/// <summary>
/// The game's side of the missions: the operations the mission runtime uses
/// (see <see cref="IMissionHost"/>), carried out with the game's own text
/// printing, ships and commander state. The missions themselves are defined in
/// Assets/Missions.
/// </summary>
public sealed partial class EliteGame : IMissionHost
{
    /// <summary>The missions, which the game consults when we dock, when ships spawn or leave, and on the Data on System screen.</summary>
    private readonly MissionRuntime _missions;

    int IMissionHost.MissionFlags
    {
        get => _missionStatus;
        set => _missionStatus = value & 0xFF;
    }

    int IMissionHost.Galaxy => _galaxyNumber;

    (int X, int Y) IMissionHost.CurrentSystem => (_currentSystemX, _currentSystemY);

    int IMissionHost.KillTally
    {
        get => _killTally;
        set => _killTally = value & 0xFFFF;
    }

    string IMissionHost.CommanderName => CommanderName;

    int IMissionHost.ShipCount(int shipType) => _shipCounts[shipType];

    int IMissionHost.NextRandom() => NextRandom();

    void IMissionHost.AddCash(int tenths) => AddCash(tenths);

    void IMissionHost.FitEquipment(MissionEquipment equipment)
    {
        switch (equipment)
        {
            case MissionEquipment.NavalEnergyUnit:
                _energyUnit = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(equipment), equipment, "Unknown equipment");
        }
    }

    void IMissionHost.SpawnTarget(int shipType, int ai)
    {
        // The pirate spawning code has already set up the new ship's position
        _currentShip.Ai = ai;
        AddShip(shipType);
    }

    void IMissionHost.SpawnHostileShip(int shipType)
    {
        if (shipType == ShipType.Thargoid)
        {
            // A Thargoid always brings a Thargon with it
            SpawnThargoid();
            return;
        }

        SetUpDistantShip();
        _currentShip.Ai = 0xFF;
        AddShip(shipType);
    }

    void IMissionHost.ClearScreen()
    {
        _cursorX = 1;
        ClearScreen(1);
    }

    void IMissionHost.ShowText(int? row, int? column, bool justify, IReadOnlyList<string> lines, bool newlineAtEnd)
    {
        _colour = Cyan;
        if (row is { } y)
        {
            MoveToRowInCyan(y);
        }

        if (justify)
        {
            SetJustified();
        }
        else
        {
            SetLeftAligned();
        }

        if (column is { } x)
        {
            _cursorX = x;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            PrintString(lines[i]);
            if (i < lines.Count - 1 || newlineAtEnd)
            {
                PrintCharacter(12);
            }
        }
    }

    void IMissionHost.Pause(int frames) => Delay(frames);

    /// <summary>PAUSE2: wait for a key to be released and then pressed.</summary>
    void IMissionHost.WaitForKey()
    {
        while (ReadKey() != 0)
        {
            WaitForVsync();
        }

        while (ReadKey() == 0)
        {
            WaitForVsync();
        }
    }

    /// <summary>BRIEF (part): show the ship flying in close, spinning, and moving off to the top of the screen.</summary>
    void IMissionHost.IntroduceShip(int shipType)
    {
        ResetWorkspace();
        _shipType = shipType;
        AddShip(_shipType);
        _cursorX = 1;
        _currentShip.Z = 1 << 8;
        ClearScreen(13);
        _mainLoopCounter = 64;

        do
        {
            // BRL1: spin the ship in front of us
            _currentShip.RollCounter = 0x7F;
            _currentShip.PitchCounter = 0x7F;
            DrawShip();
            MoveShip();
            ThrottleMainLoop();
            _mainLoopCounter = (_mainLoopCounter - 1) & 0xFF;
        }
        while (_mainLoopCounter != 0);

        while (true)
        {
            // BRL2: fly the ship away
            int xLo = _currentShip.XLo >> 1;
            _currentShip.X = ComposeCoordinate(xLo, _currentShip.XHi, _currentShip.XSign);
            int zLo = _currentShip.ZLo;
            zLo = (zLo + 1) & 0xFF;
            if (zLo == 0)
            {
                break;
            }

            zLo = (zLo + 1) & 0xFF;
            _currentShip.Z = ComposeCoordinate(zLo, _currentShip.ZHi, _currentShip.ZSign);
            if (zLo == 0)
            {
                break;
            }

            int yLo = Math.Min(_currentShip.YLo + 1, 120);
            _currentShip.Y = ComposeCoordinate(yLo, _currentShip.YHi, _currentShip.YSign);
            DrawShip();
            MoveShip();
            ThrottleMainLoop();
            _mainLoopCounter = (_mainLoopCounter - 1) & 0xFF;
        }

        // BR2
        _currentShip.Z = ComposeCoordinate(0, (_currentShip.ZHi + 1) & 0xFF, _currentShip.ZSign);
        ShowRotatingShip();
    }

    /// <summary>PAUSE: keep the ship spinning until a key is pressed, then clear the screen and move to row 10.</summary>
    void IMissionHost.ShowShipUntilKey()
    {
        while (ShowRotatingShip() != 0)
        {
        }

        // PAL1
        while (ShowRotatingShip() == 0)
        {
        }

        _currentShip.Flags = 0;
        ClearScreen(1);
        DrawShip();

        // Fall through into MT23
        MoveToRowInCyan(10);
    }

    /// <summary>
    /// PDESC (part): print a mission's system description, justified and in
    /// capitals, followed by a full stop, as for the game's other special
    /// system descriptions.
    /// </summary>
    void IMissionHost.ShowSystemDescription(string text)
    {
        SetJustified();
        SetAllCaps();
        PrintString(text);
        PrintCharacter('.');
        PrintCharacter(12);
        SetLeftAligned();
    }

    /// <summary>PAS1: display a rotating ship at the top of the screen and read the keyboard.</summary>
    private int ShowRotatingShip()
    {
        _currentShip.Y = ComposeCoordinate(120, _currentShip.YHi, _currentShip.YSign);
        _currentShip.X = ComposeCoordinate(0, _currentShip.XHi, _currentShip.XSign);
        _currentShip.Z = ComposeCoordinate(0, 2, _currentShip.ZSign);
        DrawShip();
        MoveShip();
        ThrottleMainLoop();
        return ReadKey();
    }

    /// <summary>Print a string exactly as it is, through the text printer (so it is justified if justification is on).</summary>
    private void PrintString(string text)
    {
        foreach (char c in text)
        {
            PrintCharacter(c);
        }
    }
}

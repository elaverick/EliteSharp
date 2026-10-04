using System.Text;

namespace EliteSharp.Game;

/// <summary>
/// Saving and loading commanders. The original saves each commander to a file
/// on a disc; here a commander can have any number of saves, each with a
/// label, kept by <see cref="CommanderStore"/> (the screens for choosing them
/// are in CommanderScreens.cs). The commander data block in each save is in
/// the same format as the original's commander files, with its checksums.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>NT%: the size of the commander data block (TP to SVC+2).</summary>
    private const int CommanderDataSize = 76;

    /// <summary>
    /// NA%: the last saved commander, as the 8-byte name (terminated by a
    /// carriage return) followed by the commander data block and checksums.
    /// </summary>
    private readonly byte[] _savedCommander = new byte[8 + CommanderDataSize + 16];

    /// <summary>The last line of text entered with MT26 (INWK+5 in the original).</summary>
    private string _lastInput = "";

    /// <summary>
    /// JAMESON: restore the default commander into NA%, laid out as a saved
    /// commander (see <see cref="CopyCommanderToSaveBlock"/>), with its checksums.
    /// </summary>
    private void RestoreDefaultCommander()
    {
        Array.Clear(_savedCommander);
        Encoding.ASCII.GetBytes(DefaultCommander.Name, _savedCommander);
        _savedCommander[DefaultCommander.Name.Length] = 13;

        int b = 8;
        _savedCommander[b + 1] = DefaultCommander.SystemX;
        _savedCommander[b + 2] = DefaultCommander.SystemY;
        DefaultCommander.GalaxySeeds.CopyTo(_savedCommander.AsSpan(b + 3));
        _savedCommander[b + 11] = DefaultCommander.Cash >> 8;
        _savedCommander[b + 12] = DefaultCommander.Cash & 0xFF;
        _savedCommander[b + 13] = DefaultCommander.Fuel;
        _savedCommander[b + 16] = DefaultCommander.FrontLaser;
        _savedCommander[b + 22] = DefaultCommander.CargoCapacity;
        _savedCommander[b + 51] = DefaultCommander.Missiles;
        DefaultCommander.MarketAvailability.CopyTo(_savedCommander.AsSpan(b + 53));
        _savedCommander[b + 73] = DefaultCommander.SaveCount;

        // CHK and CHK2
        int check = CalculateChecksum();
        _savedCommander[b + 75] = (byte)check;
        _savedCommander[b + 74] = (byte)(check ^ 0xA9);
    }

    /// <summary>CHECK: calculate the checksum of the commander data block in NA%.</summary>
    private int CalculateChecksum()
    {
        int index = CommanderDataSize - 3;
        int checksum = index;
        int carry = 0;
        for (; index > 0; index--)
        {
            int sum = checksum + _savedCommander[7 + index] + carry;
            carry = sum > 0xFF ? 1 : 0;
            checksum = (sum & 0xFF) ^ _savedCommander[8 + index];
        }

        return checksum;
    }

    /// <summary>DFAULT: copy the commander in NA% into the game's state.</summary>
    private void ApplySavedCommander()
    {
        // The name, up to the carriage return
        var name = new StringBuilder();
        for (int i = 0; i < 7 && _savedCommander[i] != 13; i++)
        {
            name.Append((char)_savedCommander[i]);
        }

        CommanderName = name.ToString();

        int b = 8;
        _missionStatus = _savedCommander[b + 0];
        _currentSystemX = _savedCommander[b + 1];
        _currentSystemY = _savedCommander[b + 2];
        for (int i = 0; i < 6; i++)
        {
            _galaxySeeds[i] = _savedCommander[b + 3 + i];
        }

        _cash = (uint)(_savedCommander[b + 9] << 24 | _savedCommander[b + 10] << 16 | _savedCommander[b + 11] << 8 | _savedCommander[b + 12]);
        _fuel = _savedCommander[b + 13];
        _competitionFlags = _savedCommander[b + 14];
        _galaxyNumber = _savedCommander[b + 15];
        for (int i = 0; i < 4; i++)
        {
            _lasers[i] = _savedCommander[b + 16 + i];
        }

        _cargoCapacity = _savedCommander[b + 22];
        for (int i = 0; i < 17; i++)
        {
            _cargo[i] = _savedCommander[b + 23 + i];
        }

        _ecm = _savedCommander[b + 40];
        _fuelScoops = _savedCommander[b + 41];
        _energyBomb = _savedCommander[b + 42];
        _energyUnit = _savedCommander[b + 43];
        _dockingComputer = _savedCommander[b + 44];
        _galacticHyperdrive = _savedCommander[b + 45];
        _escapePod = _savedCommander[b + 46];
        _killTallyFraction = _savedCommander[b + 50];
        _missiles = _savedCommander[b + 51];
        _legalStatus = _savedCommander[b + 52];
        for (int i = 0; i < 17; i++)
        {
            _marketAvailability[i] = _savedCommander[b + 53 + i];
        }

        _marketRandom = _savedCommander[b + 70];
        _killTally = _savedCommander[b + 71] | (_savedCommander[b + 72] << 8);
        _saveCount = _savedCommander[b + 73];

        _viewType = 0;

        // The original loops forever here if the checksum doesn't match, so
        // we skip that check, but we do set the cheat flag in COK if CHK2
        // doesn't match
        int check = CalculateChecksum() ^ 0xA9;
        int cok = _competitionFlags;
        if (check != _savedCommander[b + 74])
        {
            cok |= 0x80;
        }

        _competitionFlags = cok | 0b00001000;
    }

    /// <summary>Copy the game's commander state into NA% (as SV1 does before saving).</summary>
    private void CopyCommanderToSaveBlock()
    {
        int b = 8;
        _savedCommander[b + 0] = (byte)_missionStatus;
        _savedCommander[b + 1] = (byte)_currentSystemX;
        _savedCommander[b + 2] = (byte)_currentSystemY;
        for (int i = 0; i < 6; i++)
        {
            _savedCommander[b + 3 + i] = (byte)_galaxySeeds[i];
        }

        _savedCommander[b + 9] = (byte)(_cash >> 24);
        _savedCommander[b + 10] = (byte)(_cash >> 16);
        _savedCommander[b + 11] = (byte)(_cash >> 8);
        _savedCommander[b + 12] = (byte)_cash;
        _savedCommander[b + 13] = (byte)_fuel;
        _savedCommander[b + 14] = (byte)_competitionFlags;
        _savedCommander[b + 15] = (byte)_galaxyNumber;
        for (int i = 0; i < 4; i++)
        {
            _savedCommander[b + 16 + i] = (byte)_lasers[i];
        }

        _savedCommander[b + 20] = 0;
        _savedCommander[b + 21] = 0;
        _savedCommander[b + 22] = (byte)_cargoCapacity;
        for (int i = 0; i < 17; i++)
        {
            _savedCommander[b + 23 + i] = (byte)_cargo[i];
        }

        _savedCommander[b + 40] = (byte)_ecm;
        _savedCommander[b + 41] = (byte)_fuelScoops;
        _savedCommander[b + 42] = (byte)_energyBomb;
        _savedCommander[b + 43] = (byte)_energyUnit;
        _savedCommander[b + 44] = (byte)_dockingComputer;
        _savedCommander[b + 45] = (byte)_galacticHyperdrive;
        _savedCommander[b + 46] = (byte)_escapePod;
        _savedCommander[b + 47] = 0;
        _savedCommander[b + 48] = 0;
        _savedCommander[b + 49] = 0;
        _savedCommander[b + 50] = (byte)_killTallyFraction;
        _savedCommander[b + 51] = (byte)_missiles;
        _savedCommander[b + 52] = (byte)_legalStatus;
        for (int i = 0; i < 17; i++)
        {
            _savedCommander[b + 53 + i] = (byte)_marketAvailability[i];
        }

        _savedCommander[b + 70] = (byte)_marketRandom;
        _savedCommander[b + 71] = (byte)_killTally;
        _savedCommander[b + 72] = (byte)(_killTally >> 8);
        _savedCommander[b + 73] = (byte)_saveCount;
    }

    /// <summary>RLINE+2: the maximum number of characters that MT26 accepts.</summary>
    private int _inputLimit = 9;

    /// <summary>
    /// MT26: read a line of text from the keyboard into the input buffer,
    /// returning false (C set) if ESCAPE was pressed.
    /// </summary>
    private bool ReadLine()
    {
        var savedColour = _colour;
        _colour = Red;
        Delay(8);
        var input = new StringBuilder();
        while (true)
        {
            // OSW0L
            int key = WaitForKey();
            if (key == 13)
            {
                // OSW03
                _lastInput = input.ToString();
                PutCharacter(12);
                _colour = savedColour;
                return true;
            }

            if (key == 27)
            {
                // OSW04
                _lastInput = input.ToString();
                _colour = savedColour;
                return false;
            }

            if (key == 127)
            {
                // OSW05
                if (input.Length == 0)
                {
                    PutCharacter(7);
                    continue;
                }

                input.Length--;
                PutCharacter(127);
                continue;
            }

            if (input.Length >= _inputLimit || key < '!' || key >= '{')
            {
                // OSW01
                PutCharacter(7);
                continue;
            }

            input.Append((char)key);
            PutCharacter(key);
        }
    }

    /// <summary>The saved games, which are read when they are first needed.</summary>
    private CommanderStore Store => _store ??= new CommanderStore(_options.DataFolder);

    private CommanderStore? _store;

    /// <summary>The name stored in NA%.</summary>
    private string SavedCommanderName()
    {
        var name = new StringBuilder();
        for (int i = 0; i < 7 && _savedCommander[i] != 13; i++)
        {
            name.Append((char)_savedCommander[i]);
        }

        return name.ToString();
    }

    /// <summary>TRNME: store a commander's name in NA% (up to 7 characters, terminated by a carriage return).</summary>
    private void StoreCommanderName(string name)
    {
        name = name.Length > 7 ? name[..7] : name;
        for (int i = 0; i < 8; i++)
        {
            _savedCommander[i] = i < name.Length ? (byte)name[i] : (byte)13;
        }
    }

    /// <summary>The commander in NA% as a commander file, as wfile saves it.</summary>
    private byte[] CommanderFileData()
    {
        var data = new byte[CommanderStore.FileSize];
        Array.Copy(_savedCommander, 8, data, 0, CommanderDataSize + 1);
        return data;
    }

    /// <summary>
    /// SV1: save the current commander under the given name and label, as a
    /// new save or over an old one, returning false if the file couldn't be
    /// written. As in the original, the commander becomes the last saved
    /// commander (which we go back to if we die) even if the file isn't written.
    /// </summary>
    private bool SaveCommander(string name, string label, CommanderSave? replacing)
    {
        StoreCommanderName(name);
        _saveCount >>= 1;
        CopyCommanderToSaveBlock();

        // CHK and CHK2
        int check = CalculateChecksum();
        _savedCommander[8 + 75] = (byte)check;
        _savedCommander[8 + 74] = (byte)(check ^ 0xA9);

        bool saved = true;
        try
        {
            Store.Write(SavedCommanderName(), label, CommanderFileData(), DateTimeOffset.Now, replacing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            saved = false;
        }

        // SVEX9
        ApplySavedCommander();
        return saved;
    }

    /// <summary>LOD and TRNME: make a save the last saved commander, ready for DFAULT to load.</summary>
    private void LoadCommander(CommanderSave save)
    {
        Array.Copy(save.Data, 0, _savedCommander, 8, CommanderDataSize + 1);
        StoreCommanderName(save.Commander);
    }
}

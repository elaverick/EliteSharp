using System.Text;

namespace EliteSharp.Game;

/// <summary>
/// Saving and loading commanders. The original uses the BBC's disc filing
/// system, with drives 0-3; here each drive is a folder on disk, and the
/// commander files are saved in the same 256-byte format as the original.
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

    /// <summary>CATF: non-zero while the disc catalogue is being printed.</summary>
    private int _printingCatalogue;

    /// <summary>The last line of text entered with MT26 (INWK+5 in the original).</summary>
    private string _lastInput = "";

    /// <summary>The folder for a drive number.</summary>
    private string DriveFolder(int drive)
    {
        string folder = Path.Combine(_options.DataFolder, $"Drive{drive}");
        Directory.CreateDirectory(folder);
        return folder;
    }

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

    /// <summary>
    /// SVE: display the disc access menu and process the choice. Returns true
    /// (C set) if a new commander was loaded.
    /// </summary>
    private bool DiscAccessMenu()
    {
        while (true)
        {
            SetTradingPalette();
            PrintDiscAccessMenu();
            int key = WaitForKey();
            switch (key)
            {
                case '1':
                {
                    // loading
                    AskForCommanderName();
                    int drive = AskForDrive();
                    if (drive < 0)
                    {
                        return true;
                    }

                    if (!LoadCommanderFile(drive))
                    {
                        continue;
                    }

                    StoreCommanderName();
                    return true;
                }

                case '2':
                {
                    // SV1: saving
                    AskForCommanderName();
                    StoreCommanderName();
                    _saveCount >>= 1;

                    // The original prints extended token 4 here, which is empty
                    CopyCommanderToSaveBlock();
                    int check = CalculateChecksum();
                    _savedCommander[8 + 75] = (byte)check;
                    _savedCommander[8 + 74] = (byte)(check ^ 0xA9);
                    PrintNewline();
                    PrintNewline();
                    int drive = AskForDrive();
                    if (drive >= 0)
                    {
                        SaveCommanderFile(drive);
                    }

                    // SVEX9
                    ApplySavedCommander();
                    return false;
                }

                case '3':
                    // feb10: show the catalogue
                    ShowCatalogue();
                    WaitForKey();
                    continue;

                case '4':
                    DeleteCommanderFile();
                    continue;

                case '5':
                    // jan18: restore the default commander
                    PrintExtendedText("disk.are_you_sure");
                    if (WaitForYesNo())
                    {
                        RestoreDefaultCommander();
                        ApplySavedCommander();
                        return true;
                    }

                    return false;

                default:
                    // feb13
                    return false;
            }
        }
    }

    /// <summary>YESNO: wait for "Y" or "N", returning true for "Y".</summary>
    private bool WaitForYesNo()
    {
        while (true)
        {
            int key = WaitForKey();
            if (key == 'Y')
            {
                return true;
            }

            if (key == 'N')
            {
                return false;
            }
        }
    }

    /// <summary>GTNMEW: ask for a commander's name.</summary>
    private void AskForCommanderName()
    {
        _inputLimit = 7;
        PrintExtendedText("disk.commander_name");
        ReadLine();
        _inputLimit = 9;
        if (_lastInput.Length == 0)
        {
            // TR1: use the current name
            _lastInput = SavedCommanderName();
        }
    }

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

    /// <summary>TRNME: copy the entered name into NA%.</summary>
    private void StoreCommanderName()
    {
        string name = _lastInput.Length > 7 ? _lastInput[..7] : _lastInput;
        for (int i = 0; i < 8; i++)
        {
            _savedCommander[i] = i < name.Length ? (byte)name[i] : (byte)13;
        }

        _savedCommander[name.Length] = 13;
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

    /// <summary>GTDRV: ask for a drive number, returning -1 (C set) if the key wasn't a drive.</summary>
    private int AskForDrive()
    {
        PrintExtendedText("disk.which_drive");
        int key = WaitForKey() | 0b00010000;
        PutCharacter(key);
        PrintCharacter(12);
        if (key < '0' || key >= '4')
        {
            return -1;
        }

        return key - '0';
    }

    /// <summary>The DFS file name for a commander name.</summary>
    private static string FileNameFor(string name) => "E." + name.Trim().ToUpperInvariant();

    /// <summary>wfile: save the commander in NA% to a file.</summary>
    private void SaveCommanderFile(int drive)
    {
        var data = new byte[256];
        Array.Copy(_savedCommander, 8, data, 0, CommanderDataSize + 1);
        string name = SavedCommanderName();
        try
        {
            File.WriteAllBytes(Path.Combine(DriveFolder(drive), FileNameFor(name)), data);
        }
        catch (IOException)
        {
            // The original would show a disc error here
        }
    }

    /// <summary>LOD: load a commander file into NA%, returning false if the file isn't a commander file.</summary>
    private bool LoadCommanderFile(int drive)
    {
        byte[]? data = null;
        try
        {
            string path = Path.Combine(DriveFolder(drive), FileNameFor(_lastInput));
            if (File.Exists(path))
            {
                data = File.ReadAllBytes(path);
            }
        }
        catch (IOException)
        {
        }

        if (data == null || data.Length < CommanderDataSize + 1 || (data[0] & 0x80) != 0)
        {
            // ELT2F: not a valid file
            PrintIllegalFile();
            WaitForKey();
            return false;
        }

        Array.Copy(data, 0, _savedCommander, 8, CommanderDataSize + 1);
        return true;
    }

    /// <summary>CATS: ask for a drive number and show the disc catalogue.</summary>
    private bool ShowCatalogue()
    {
        int drive = AskForDrive();
        if (drive < 0)
        {
            return false;
        }

        _catalogueDriveCharacter = '0' + drive;
        PrintCatalogueHeader();
        _printingCatalogue = 1;
        _cursorX = 1;

        // Print the catalogue in the style of the DFS *CAT command
        string[] files;
        try
        {
            files = Directory.GetFiles(DriveFolder(drive))
                .Select(Path.GetFileName)
                .Where(f => f != null && f.Length <= 9)
                .Select(f => f!)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException)
        {
            files = [];
        }

        PrintLine("ELITE        (00)");
        PrintLine($"Drive {drive}      Option 0 (off)");
        PrintLine("Dir. :0.$      Lib. :0.$");
        PrintLine("");
        for (int i = 0; i < files.Length; i += 2)
        {
            string line = "  " + files[i].PadRight(14);
            if (i + 1 < files.Length)
            {
                line += files[i + 1];
            }

            PrintLine(line);
        }

        _printingCatalogue = 0;
        return true;

        void PrintLine(string text)
        {
            foreach (char c in text)
            {
                PutCharacter(c);
            }

            PutCharacter(13);
            PutCharacter(10);
        }
    }

    /// <summary>DELT: delete a file from a disc.</summary>
    private void DeleteCommanderFile()
    {
        if (!ShowCatalogue())
        {
            return;
        }

        int drive = _catalogueDriveCharacter - '0';
        PrintExtendedText("disk.commander_name");
        ReadLine();
        if (_lastInput.Length == 0)
        {
            return;
        }

        try
        {
            string path = Path.Combine(DriveFolder(drive), FileNameFor(_lastInput));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// FILEPR: print the name of the current filing system. The original
    /// prints extended token 3 + DISK, which in this version is the catalogue
    /// heading or nothing (token 4 is empty), and only the disc error message
    /// uses this, which this game doesn't show.
    /// </summary>
    private void PrintFilingSystem()
    {
        if (FilingSystemToggle == 0)
        {
            PrintCatalogueHeader();
        }
    }

    /// <summary>
    /// OTHERFILEPR: print the name of the other filing system. The original
    /// prints extended token 2 - DISK, which in this version is the drive
    /// prompt or the disc access menu (and nothing uses this).
    /// </summary>
    private void PrintOtherFilingSystem()
    {
        if (FilingSystemToggle == 0)
        {
            PrintExtendedText("disk.which_drive");
        }
        else
        {
            PrintDiscAccessMenu();
        }
    }

    /// <summary>Clear the screen for a disc screen, and start its title in capitals in column 6.</summary>
    private void StartDiscScreen()
    {
        _cursorX = 1;
        ClearScreen(1);
        DrawTitleLine();
        SetAllCaps();
        MoveToColumn6();
    }

    /// <summary>Print the disc access menu.</summary>
    private void PrintDiscAccessMenu()
    {
        StartDiscScreen();
        PrintExtendedText("disk_menu.title");
        PrintCharacter(12);
        PrintCharacter(10);
        PrintCharacter(10);
        SetSentenceCase();
        foreach (string item in new[] { "load", "save", "catalogue", "delete", "default", "exit" })
        {
            PrintExtendedText("disk_menu." + item);
            PrintCharacter(12);
            PrintCharacter(10);
        }
    }

    /// <summary>Print the title of the disc catalogue.</summary>
    private void PrintCatalogueHeader()
    {
        StartDiscScreen();
        PrintExtendedText("disk.catalogue_header");
        PrintCharacter(12);
        PrintCharacter(10);
    }

    /// <summary>Say that the file isn't a commander file, in capitals.</summary>
    private void PrintIllegalFile()
    {
        PrintCharacter(12);
        SetAllCaps();
        PrintExtendedText("disk.illegal_file");
    }
}

using System.Text;
using EliteSharp.Data;

namespace EliteSharp.Game;

/// <summary>
/// Saving and loading commanders. The original uses the BBC's disc filing
/// system, with drives 0-3; here each drive is a folder on disk, and the
/// commander files are saved in the same 256-byte format as the original.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>NT%: the size of the commander data block (TP to SVC+2).</summary>
    private const int NT = 76;

    /// <summary>
    /// NA%: the last saved commander, as the 8-byte name (terminated by a
    /// carriage return) followed by the commander data block and checksums.
    /// </summary>
    private readonly byte[] NA = new byte[8 + NT + 16];

    /// <summary>CATF: non-zero while the disc catalogue is being printed.</summary>
    private int CATF;

    /// <summary>The last line of text entered with MT26 (INWK+5 in the original).</summary>
    private string _lastInput = "";

    /// <summary>The folder for a drive number.</summary>
    private string DriveFolder(int drive)
    {
        string folder = Path.Combine(_options.DataFolder, $"Drive{drive}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>JAMESON: restore the default JAMESON commander into NA%.</summary>
    private void JAMESON()
    {
        Array.Clear(NA);
        Array.Copy(GameData.DefaultCommander, NA, Math.Min(GameData.DefaultCommander.Length, NA.Length));
    }

    /// <summary>CHECK: calculate the checksum of the commander data block in NA%.</summary>
    private int CHECK()
    {
        int x = NT - 3;
        int a = x;
        int carry = 0;
        for (; x > 0; x--)
        {
            int sum = a + NA[7 + x] + carry;
            carry = sum > 0xFF ? 1 : 0;
            a = (sum & 0xFF) ^ NA[8 + x];
        }

        return a;
    }

    /// <summary>DFAULT: copy the commander in NA% into the game's state.</summary>
    private void DFAULT()
    {
        // The name, up to the carriage return
        var name = new StringBuilder();
        for (int i = 0; i < 7 && NA[i] != 13; i++)
        {
            name.Append((char)NA[i]);
        }

        CommanderName = name.ToString();

        int b = 8;
        TP = NA[b + 0];
        QQ0 = NA[b + 1];
        QQ1 = NA[b + 2];
        for (int i = 0; i < 6; i++)
        {
            QQ21[i] = NA[b + 3 + i];
        }

        CASH = (uint)(NA[b + 9] << 24 | NA[b + 10] << 16 | NA[b + 11] << 8 | NA[b + 12]);
        QQ14 = NA[b + 13];
        COK = NA[b + 14];
        GCNT = NA[b + 15];
        for (int i = 0; i < 4; i++)
        {
            LASER[i] = NA[b + 16 + i];
        }

        CRGO = NA[b + 22];
        for (int i = 0; i < 17; i++)
        {
            QQ20[i] = NA[b + 23 + i];
        }

        ECM = NA[b + 40];
        BST = NA[b + 41];
        BOMB = NA[b + 42];
        ENGY = NA[b + 43];
        DKCMP = NA[b + 44];
        GHYP = NA[b + 45];
        ESCP = NA[b + 46];
        TALLYL = NA[b + 50];
        NOMSL = NA[b + 51];
        FIST = NA[b + 52];
        for (int i = 0; i < 17; i++)
        {
            AVL[i] = NA[b + 53 + i];
        }

        QQ26 = NA[b + 70];
        TALLY = NA[b + 71] | (NA[b + 72] << 8);
        SVC = NA[b + 73];

        QQ11 = 0;

        // The original loops forever here if the checksum doesn't match, so
        // we skip that check, but we do set the cheat flag in COK if CHK2
        // doesn't match
        int check = CHECK() ^ 0xA9;
        int cok = COK;
        if (check != NA[b + 74])
        {
            cok |= 0x80;
        }

        COK = cok | 0b00001000;
    }

    /// <summary>Copy the game's commander state into NA% (as SV1 does before saving).</summary>
    private void CommanderToNA()
    {
        int b = 8;
        NA[b + 0] = (byte)TP;
        NA[b + 1] = (byte)QQ0;
        NA[b + 2] = (byte)QQ1;
        for (int i = 0; i < 6; i++)
        {
            NA[b + 3 + i] = (byte)QQ21[i];
        }

        NA[b + 9] = (byte)(CASH >> 24);
        NA[b + 10] = (byte)(CASH >> 16);
        NA[b + 11] = (byte)(CASH >> 8);
        NA[b + 12] = (byte)CASH;
        NA[b + 13] = (byte)QQ14;
        NA[b + 14] = (byte)COK;
        NA[b + 15] = (byte)GCNT;
        for (int i = 0; i < 4; i++)
        {
            NA[b + 16 + i] = (byte)LASER[i];
        }

        NA[b + 20] = 0;
        NA[b + 21] = 0;
        NA[b + 22] = (byte)CRGO;
        for (int i = 0; i < 17; i++)
        {
            NA[b + 23 + i] = (byte)QQ20[i];
        }

        NA[b + 40] = (byte)ECM;
        NA[b + 41] = (byte)BST;
        NA[b + 42] = (byte)BOMB;
        NA[b + 43] = (byte)ENGY;
        NA[b + 44] = (byte)DKCMP;
        NA[b + 45] = (byte)GHYP;
        NA[b + 46] = (byte)ESCP;
        NA[b + 47] = 0;
        NA[b + 48] = 0;
        NA[b + 49] = 0;
        NA[b + 50] = (byte)TALLYL;
        NA[b + 51] = (byte)NOMSL;
        NA[b + 52] = (byte)FIST;
        for (int i = 0; i < 17; i++)
        {
            NA[b + 53 + i] = (byte)AVL[i];
        }

        NA[b + 70] = (byte)QQ26;
        NA[b + 71] = (byte)TALLY;
        NA[b + 72] = (byte)(TALLY >> 8);
        NA[b + 73] = (byte)SVC;
    }

    /// <summary>
    /// SVE: display the disc access menu and process the choice. Returns true
    /// (C set) if a new commander was loaded.
    /// </summary>
    private bool SVE()
    {
        while (true)
        {
            TRADEMODE2();
            DETOK(1);
            int key = TT217();
            switch (key)
            {
                case '1':
                {
                    // loading
                    GTNMEW();
                    int drive = GTDRV();
                    if (drive < 0)
                    {
                        return true;
                    }

                    if (!LOD(drive))
                    {
                        continue;
                    }

                    TRNME();
                    return true;
                }

                case '2':
                {
                    // SV1: saving
                    GTNMEW();
                    TRNME();
                    SVC >>= 1;
                    DETOK(4);
                    CommanderToNA();
                    int check = CHECK();
                    NA[8 + 75] = (byte)check;
                    NA[8 + 74] = (byte)(check ^ 0xA9);
                    TT67();
                    TT67();
                    int drive = GTDRV();
                    if (drive >= 0)
                    {
                        wfile(drive);
                    }

                    // SVEX9
                    DFAULT();
                    return false;
                }

                case '3':
                    // feb10: show the catalogue
                    CATS();
                    TT217();
                    continue;

                case '4':
                    DELT();
                    continue;

                case '5':
                    // jan18: restore the default commander
                    DETOK(224);
                    if (YESNO())
                    {
                        JAMESON();
                        DFAULT();
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
    private bool YESNO()
    {
        while (true)
        {
            int key = TT217();
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
    private void GTNMEW()
    {
        _inputLimit = 7;
        DETOK(8);
        MT26();
        _inputLimit = 9;
        if (_lastInput.Length == 0)
        {
            // TR1: use the current name
            _lastInput = CurrentNaName();
        }
    }

    /// <summary>The name stored in NA%.</summary>
    private string CurrentNaName()
    {
        var name = new StringBuilder();
        for (int i = 0; i < 7 && NA[i] != 13; i++)
        {
            name.Append((char)NA[i]);
        }

        return name.ToString();
    }

    /// <summary>TRNME: copy the entered name into NA%.</summary>
    private void TRNME()
    {
        string name = _lastInput.Length > 7 ? _lastInput[..7] : _lastInput;
        for (int i = 0; i < 8; i++)
        {
            NA[i] = i < name.Length ? (byte)name[i] : (byte)13;
        }

        NA[name.Length] = 13;
    }

    /// <summary>RLINE+2: the maximum number of characters that MT26 accepts.</summary>
    private int _inputLimit = 9;

    /// <summary>
    /// MT26: read a line of text from the keyboard into the input buffer,
    /// returning false (C set) if ESCAPE was pressed.
    /// </summary>
    private bool MT26()
    {
        int savedColour = COL;
        COL = RED;
        DELAY(8);
        var input = new StringBuilder();
        while (true)
        {
            // OSW0L
            int key = TT217();
            if (key == 13)
            {
                // OSW03
                _lastInput = input.ToString();
                CHPR(12);
                COL = savedColour;
                return true;
            }

            if (key == 27)
            {
                // OSW04
                _lastInput = input.ToString();
                COL = savedColour;
                return false;
            }

            if (key == 127)
            {
                // OSW05
                if (input.Length == 0)
                {
                    CHPR(7);
                    continue;
                }

                input.Length--;
                CHPR(127);
                continue;
            }

            if (input.Length >= _inputLimit || key < '!' || key >= '{')
            {
                // OSW01
                CHPR(7);
                continue;
            }

            input.Append((char)key);
            CHPR(key);
        }
    }

    /// <summary>GTDRV: ask for a drive number, returning -1 (C set) if the key wasn't a drive.</summary>
    private int GTDRV()
    {
        DETOK(2);
        int key = TT217() | 0b00010000;
        CHPR(key);
        TT26(12);
        if (key < '0' || key >= '4')
        {
            return -1;
        }

        return key - '0';
    }

    /// <summary>The DFS file name for a commander name.</summary>
    private static string FileNameFor(string name) => "E." + name.Trim().ToUpperInvariant();

    /// <summary>wfile: save the commander in NA% to a file.</summary>
    private void wfile(int drive)
    {
        var data = new byte[256];
        Array.Copy(NA, 8, data, 0, NT + 1);
        string name = CurrentNaName();
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
    private bool LOD(int drive)
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

        if (data == null || data.Length < NT + 1 || (data[0] & 0x80) != 0)
        {
            // ELT2F: not a valid file
            DETOK(9);
            TT217();
            return false;
        }

        Array.Copy(data, 0, NA, 8, NT + 1);
        return true;
    }

    /// <summary>CATS: ask for a drive number and show the disc catalogue.</summary>
    private bool CATS()
    {
        int drive = GTDRV();
        if (drive < 0)
        {
            return false;
        }

        DTW7 = '0' + drive;
        DETOK(3);
        CATF = 1;
        XC = 1;

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

        CATF = 0;
        return true;

        void PrintLine(string text)
        {
            foreach (char c in text)
            {
                CHPR(c);
            }

            CHPR(13);
            CHPR(10);
        }
    }

    /// <summary>DELT: delete a file from a disc.</summary>
    private void DELT()
    {
        if (!CATS())
        {
            return;
        }

        int drive = DTW7 - '0';
        DETOK(8);
        MT26();
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

    /// <summary>FILEPR: print the name of the current filing system.</summary>
    private void FILEPR() => DETOK(3 + DISK);

    /// <summary>OTHERFILEPR: print the name of the other filing system.</summary>
    private void OTHERFILEPR() => DETOK(2 - DISK);
}

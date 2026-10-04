using EliteSharp.Data;
using EliteSharp.Input;

namespace EliteSharp.Game;

/// <summary>
/// Reading the keyboard.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>
    /// TRTB%: the translation table from internal key numbers to ASCII (and
    /// the codes used for the function and cursor keys).
    /// </summary>
    private static readonly byte[] KeyTranslationTable = BuildKeyTranslationTable();

    /// <summary>Build the TRTB% table from its rows in the original (one row of ten keys per internal key number row).</summary>
    private static byte[] BuildKeyTranslationTable()
    {
        var table = new byte[128];
        byte[][] rows =
        [
            [0x51, 0x33, 0x34, 0x35, 0x84, 0x38, 0x87, 0x2D, 0x5E, 0x8C],
            [0x80, 0x57, 0x45, 0x54, 0x37, 0x49, 0x39, 0x30, 0x5F, 0x8E],
            [0x31, 0x32, 0x44, 0x52, 0x36, 0x55, 0x4F, 0x50, 0x5B, 0x8F],
            [0x01, 0x41, 0x58, 0x46, 0x59, 0x4A, 0x4B, 0x40, 0x3A, 0x0D],
            [0x02, 0x53, 0x43, 0x47, 0x48, 0x4E, 0x4C, 0x3B, 0x5D, 0x7F],
            [0x00, 0x5A, 0x20, 0x56, 0x42, 0x4D, 0x2C, 0x2E, 0x2F, 0x8B],
            [0x1B, 0x81, 0x82, 0x83, 0x85, 0x86, 0x88, 0x89, 0x5C, 0x8D],
        ];

        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < 10; column++)
            {
                table[((row + 1) << 4) + column] = rows[row][column];
            }
        }

        return table;
    }

    /// <summary>
    /// IKNS: the internal key numbers of the flight keys, in the order of the
    /// key logger (KY17, KY14, KY15, KY20, KY7, KY5, KY18, KY6, KY19, KY12,
    /// KY2, KY16, KY3, KY4, KY1, KY13).
    /// </summary>
    private static readonly int[] FlightKeyNumbers =
    [
        BbcKeyboard.E, BbcKeyboard.T, BbcKeyboard.U, BbcKeyboard.P, BbcKeyboard.A, BbcKeyboard.X,
        BbcKeyboard.J, BbcKeyboard.S, BbcKeyboard.C, BbcKeyboard.Tab, BbcKeyboard.Space, BbcKeyboard.M,
        BbcKeyboard.Comma, BbcKeyboard.Period, BbcKeyboard.Slash, BbcKeyboard.Escape,
    ];

    /// <summary>ZEKTRAN: clear the key logger.</summary>
    private void ClearKeyLogger()
    {
        _keyPressed = 0;
        _keyEcm = _keyTargetMissile = _keyUnarmMissile = _keyDockingComputerOff = _keyFireLaser = _keyClimb = _keyJump = _keyDive = false;
        _keyDockingComputerOn = _keyEnergyBomb = _keySpeedUp = _keyFireMissile = _keyRollLeft = _keyRollRight = _keySlowDown = _keyEscapePod = false;
    }

    /// <summary>
    /// FILLKL: scan the keyboard and update the key logger, returning the
    /// internal key number of the highest key pressed in KL (or 0).
    /// </summary>
    private int ScanKeyboard()
    {
        UpdatePadContext();
        ClearKeyLogger();
        int highestKey = 0;
        for (int key = 16; key < 128; key++)
        {
            if (!_keyboard.IsPressed(key))
            {
                continue;
            }

            highestKey = key;
            int index = Array.IndexOf(FlightKeyNumbers, key);
            if (index >= 0)
            {
                SetFlightKey(index);
            }
        }

        // If "S" is being pressed, ignore "C" (the docking computer)
        if (_keyDive)
        {
            _keyDockingComputerOn = false;
        }

        _keyPressed = highestKey;
        return highestKey;
    }

    /// <summary>Set the flight key flag at the given position in the key logger.</summary>
    private void SetFlightKey(int index)
    {
        switch (index)
        {
            case 0: _keyEcm = true; break;
            case 1: _keyTargetMissile = true; break;
            case 2: _keyUnarmMissile = true; break;
            case 3: _keyDockingComputerOff = true; break;
            case 4: _keyFireLaser = true; break;
            case 5: _keyClimb = true; break;
            case 6: _keyJump = true; break;
            case 7: _keyDive = true; break;
            case 8: _keyDockingComputerOn = true; break;
            case 9: _keyEnergyBomb = true; break;
            case 10: _keySpeedUp = true; break;
            case 11: _keyFireMissile = true; break;
            case 12: _keyRollLeft = true; break;
            case 13: _keyRollRight = true; break;
            case 14: _keySlowDown = true; break;
            case 15: _keyEscapePod = true; break;
        }
    }

    /// <summary>
    /// RDKEY: scan the keyboard, returning the ASCII code of the key pressed
    /// (or 0). This is also where the Settings screen opens, whatever the game
    /// is doing, as every screen reads the keyboard this way.
    /// </summary>
    private int ReadKey()
    {
        if (_keyboard.TakeSettingsRequest())
        {
            if (_settingsOpen)
            {
                // Asking again closes the Settings screen, as Escape does
                return 0x1B;
            }

            ShowSettings();
        }

        int key = ScanKeyboard();
        _keyPressed = KeyTranslationTable[key];
        return _keyPressed;
    }

    /// <summary>CTRL: return &amp;80 if CTRL is being pressed.</summary>
    private int CtrlPressed() => _keyboard.IsHeld(BbcKeyboard.Ctrl) ? 0x80 : 0;

    /// <summary>DKS5 with the Shift key: return true if SHIFT is being pressed.</summary>
    private bool ShiftPressed() => _keyboard.IsHeld(BbcKeyboard.Shift);

    /// <summary>
    /// DOKEY: scan for the flight keys, apply the docking computer if it is
    /// on, update the roll and pitch rates, and check for the pause key.
    /// Returns the ASCII code of the key pressed.
    /// </summary>
    private int ReadFlightKeys()
    {
        ReadKey();
        ApplyPadTriggers();

        if (_autoDocking != 0)
        {
            // The docking computer "presses" the flight keys
            var saved = _currentShip;
            ResetWorkspace();
            _currentShip.Nose.Z = 1;
            _currentShip.Side.X = -1;
            int savedType = _shipType;
            _shipType = 0x80 | 96;
            _currentShip.Speed = _speed;
            ApplyDockingManoeuvres();

            _speed = Math.Min(_currentShip.Speed, 22);

            int acceleration = _currentShip.Acceleration;
            if (acceleration != 0)
            {
                if ((acceleration & 0x80) != 0)
                {
                    _keySlowDown = true;
                }
                else
                {
                    _keySpeedUp = true;
                }
            }

            // DK11: roll
            int roll = _currentShip.RollCounter;
            int rollMagnitude = (roll << 1) & 0xFF;
            if (rollMagnitude == 0)
            {
                _rollRate = 128;
            }
            else
            {
                // A positive roll "presses" KY3 and a negative roll KY4,
                // unless the roll is 64 or more, in which case the key is
                // released and the roll rate is set to 64
                bool positive = (roll & 0x80) == 0;
                bool press = (rollMagnitude & 0x80) == 0;
                if (!press)
                {
                    _rollRate = 64;
                }

                if (positive)
                {
                    _keyRollLeft = press;
                }
                else
                {
                    _keyRollRight = press;
                }
            }

            // Pitch
            int pitch = _currentShip.PitchCounter;
            int pitchMagnitude = (pitch << 1) & 0xFF;
            if (pitchMagnitude == 0)
            {
                _pitchRate = 128;
            }
            else if ((pitch & 0x80) != 0)
            {
                _keyClimb = true;
            }
            else
            {
                _keyDive = true;
            }

            _shipType = savedType;
            _currentShip = saved;
        }

        // DK15: read the joystick (the controller's left stick), and if it
        // isn't in control, DK152: apply the roll and pitch keys
        if (ReadPadStick())
        {
            CheckForPause();
            return _keyPressed;
        }

        int rate = _rollRate;
        if (_keyRollLeft)
        {
            rate = IncreaseRate(rate, 7);
        }

        if (_keyRollRight)
        {
            rate = DecreaseRate(rate, 7);
        }

        _rollRate = rate;

        rate = _pitchRate;
        if (_keyClimb)
        {
            rate = DecreaseRate(rate, 14);
        }

        if (_keyDive)
        {
            rate = IncreaseRate(rate, 14);
        }

        _pitchRate = rate;

        CheckForPause();
        return _keyPressed;
    }

    /// <summary>BUMP2: increase the roll or pitch rate, snapping to the centre if we pass it.</summary>
    private int IncreaseRate(int rate, int amount)
    {
        rate += amount;
        if (rate > 0xFF)
        {
            rate = 0xFF;
        }

        // RE2
        return (rate & 0x80) == 0 ? CentreRate(rate) : rate;
    }

    /// <summary>REDU2: decrease the roll or pitch rate, snapping to the centre if we pass it.</summary>
    private int DecreaseRate(int rate, int amount)
    {
        rate -= amount;
        if (rate < 0)
        {
            rate = 1;
        }

        // RE3
        return (rate & 0x80) != 0 ? CentreRate(rate) : rate;
    }

    /// <summary>djd1: snap the roll or pitch rate to the centre, unless keyboard damping is disabled.</summary>
    private int CentreRate(int rate) => AutoRecentreDisabled != 0 ? rate : 128;

    /// <summary>cntr: apply keyboard damping, moving the roll or pitch rate towards the centre.</summary>
    private int DampRate(int rate)
    {
        if (_autoDocking == 0 && DampingDisabled != 0)
        {
            return rate;
        }

        // cnt2
        if ((rate & 0x80) == 0)
        {
            // BUMP
            return rate + 1;
        }

        rate--;
        if ((rate & 0x80) != 0)
        {
            return rate;
        }

        return rate + 1;
    }

    /// <summary>DK4: check for the pause key (COPY) and process the options while paused.</summary>
    private void CheckForPause()
    {
        if (_keyPressed != 0x8B)
        {
            return;
        }

        // FREEZE
        _padContextOverride = PadContext.Paused;
        try
        {
            PauseLoop();
        }
        finally
        {
            _padContextOverride = null;
        }
    }

    /// <summary>FREEZE: the pause loop, where the configuration options can be changed.</summary>
    private void PauseLoop()
    {
        while (true)
        {
            WaitForVsync();
            int key = ReadKey();
            if (key == 'Q')
            {
                _soundDisabled = 0xFF;
            }

            // DK6: toggle the configuration options
            for (int option = 0; option < 9; option++)
            {
                ToggleOption(key, option);
            }

            int volume = _volume;
            if (key == '.' || key == ',')
            {
                volume += key == '.' ? 1 : -1;
                if ((volume & 0xF8) == 0)
                {
                    _volume = volume;
                }

                Beep();
                Delay(10);
            }

            if (key == 'S')
            {
                _soundDisabled = 0;
            }

            if (key == 0x1B)
            {
                throw new GameJumpException(GameJump.RestartAfterDeath);
            }

            if (key == 0x7F)
            {
                return;
            }
        }
    }

    /// <summary>
    /// TGINT: the keys that toggle the configuration options while paused, in
    /// the order of <see cref="ToggleOptions"/> (the 1 is for CAPS LOCK).
    /// </summary>
    private static readonly int[] ToggleOptionKeys = [1, 'A', 'X', 'F', 'Y', 'J', 'K', 'U', 'T'];

    /// <summary>DKS3: toggle a configuration option if its key is being pressed.</summary>
    private void ToggleOption(int key, int option)
    {
        if (key != ToggleOptionKeys[option])
        {
            return;
        }

        ToggleOptions[option] ^= 0xFF;

        if ((ToggleOptions[option] & 0x80) != 0)
        {
            Bell();
        }

        Bell();
        Delay(20);
    }

    /// <summary>
    /// TT17: scan the keyboard for cursor key presses, returning the key
    /// pressed, and the cursor movements in x and y.
    /// </summary>
    private int ReadCursorKeys(out int deltaX, out int deltaY)
    {
        deltaX = 0;
        deltaY = 0;
        if (_viewType == 0)
        {
            return ReadFlightKeys();
        }

        // TT17afterall
        ReadFlightKeys();
        if (ReadPadCursor(out deltaX, out deltaY))
        {
            return _keyPressed;
        }

        // TJ1
        int key = _keyPressed;
        if (key == 0x8C)
        {
            deltaX--;
        }

        if (key == 0x8D)
        {
            deltaX++;
        }

        if (key == 0x8E)
        {
            deltaY--;
        }

        if (key == 0x8F)
        {
            deltaY++;
        }

        if (ShiftPressed())
        {
            // speedup
            deltaX *= 4;
            deltaY *= 4;
        }

        return key;
    }

    /// <summary>TT217: wait until a key is pressed (after any current key is released), and return its ASCII code.</summary>
    private int WaitForKey()
    {
        // t: wait for all keys to be released
        do
        {
            Delay(2);
        }
        while (AnyKeyHeld());

        // t2: wait for a key press
        while (true)
        {
            int key = ReadKey();
            if (key != 0)
            {
                return key;
            }

            WaitForVsync();
        }
    }

    /// <summary>Returns true if any key that RDKEY scans is being held down.</summary>
    private bool AnyKeyHeld()
    {
        for (int key = 16; key < 128; key++)
        {
            if (_keyboard.IsHeld(key) && KeyTranslationTable[key] != 0)
            {
                return true;
            }
        }

        return false;
    }
}

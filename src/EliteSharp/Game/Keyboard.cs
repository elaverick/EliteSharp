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
    private static readonly byte[] TRTB = BuildKeyTranslationTable();

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
    private static readonly int[] IKNS =
    [
        BbcKeyboard.E, BbcKeyboard.T, BbcKeyboard.U, BbcKeyboard.P, BbcKeyboard.A, BbcKeyboard.X,
        BbcKeyboard.J, BbcKeyboard.S, BbcKeyboard.C, BbcKeyboard.Tab, BbcKeyboard.Space, BbcKeyboard.M,
        BbcKeyboard.Comma, BbcKeyboard.Period, BbcKeyboard.Slash, BbcKeyboard.Escape,
    ];

    /// <summary>ZEKTRAN: clear the key logger.</summary>
    private void ZEKTRAN()
    {
        KL = 0;
        KY17 = KY14 = KY15 = KY20 = KY7 = KY5 = KY18 = KY6 = false;
        KY19 = KY12 = KY2 = KY16 = KY3 = KY4 = KY1 = KY13 = false;
    }

    /// <summary>
    /// FILLKL: scan the keyboard and update the key logger, returning the
    /// internal key number of the highest key pressed in KL (or 0).
    /// </summary>
    private int FILLKL()
    {
        UpdatePadContext();
        ZEKTRAN();
        int kl = 0;
        for (int key = 16; key < 128; key++)
        {
            if (!_keyboard.IsPressed(key))
            {
                continue;
            }

            kl = key;
            int index = Array.IndexOf(IKNS, key);
            if (index >= 0)
            {
                SetFlightKey(index);
            }
        }

        // If "S" is being pressed, ignore "C" (the docking computer)
        if (KY6)
        {
            KY19 = false;
        }

        KL = kl;
        return kl;
    }

    /// <summary>Set the flight key flag at the given position in the key logger.</summary>
    private void SetFlightKey(int index)
    {
        switch (index)
        {
            case 0: KY17 = true; break;
            case 1: KY14 = true; break;
            case 2: KY15 = true; break;
            case 3: KY20 = true; break;
            case 4: KY7 = true; break;
            case 5: KY5 = true; break;
            case 6: KY18 = true; break;
            case 7: KY6 = true; break;
            case 8: KY19 = true; break;
            case 9: KY12 = true; break;
            case 10: KY2 = true; break;
            case 11: KY16 = true; break;
            case 12: KY3 = true; break;
            case 13: KY4 = true; break;
            case 14: KY1 = true; break;
            case 15: KY13 = true; break;
        }
    }

    /// <summary>RDKEY: scan the keyboard, returning the ASCII code of the key pressed (or 0).</summary>
    private int RDKEY()
    {
        int key = FILLKL();
        KL = TRTB[key];
        return KL;
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
    private int DOKEY()
    {
        RDKEY();
        ApplyPadTriggers();

        if (Auto != 0)
        {
            // The docking computer "presses" the flight keys
            var saved = INWK;
            ZINF();
            INWK.Nose.Z = 96 << 8;
            INWK.Side.X = -(96 << 8);
            int savedType = TYPE;
            TYPE = 0x80 | 96;
            INWK.Speed = DELTA;
            DOCKIT();

            DELTA = Math.Min(INWK.Speed, 22);

            int acceleration = INWK.Acceleration;
            if (acceleration != 0)
            {
                if ((acceleration & 0x80) != 0)
                {
                    KY1 = true;
                }
                else
                {
                    KY2 = true;
                }
            }

            // DK11: roll
            int roll = INWK.RollCounter;
            int rollMagnitude = (roll << 1) & 0xFF;
            if (rollMagnitude == 0)
            {
                JSTX = 128;
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
                    JSTX = 64;
                }

                if (positive)
                {
                    KY3 = press;
                }
                else
                {
                    KY4 = press;
                }
            }

            // Pitch
            int pitch = INWK.PitchCounter;
            int pitchMagnitude = (pitch << 1) & 0xFF;
            if (pitchMagnitude == 0)
            {
                JSTY = 128;
            }
            else if ((pitch & 0x80) != 0)
            {
                KY5 = true;
            }
            else
            {
                KY6 = true;
            }

            TYPE = savedType;
            INWK = saved;
        }

        // DK15: read the joystick (the controller's left stick), and if it
        // isn't in control, DK152: apply the roll and pitch keys
        if (ReadPadStick())
        {
            DK4();
            return KL;
        }

        int x = JSTX;
        if (KY3)
        {
            x = BUMP2(x, 7);
        }

        if (KY4)
        {
            x = REDU2(x, 7);
        }

        JSTX = x;

        x = JSTY;
        if (KY5)
        {
            x = REDU2(x, 14);
        }

        if (KY6)
        {
            x = BUMP2(x, 14);
        }

        JSTY = x;

        DK4();
        return KL;
    }

    /// <summary>BUMP2: increase the roll or pitch rate, snapping to the centre if we pass it.</summary>
    private int BUMP2(int x, int amount)
    {
        x += amount;
        if (x > 0xFF)
        {
            x = 0xFF;
        }

        // RE2
        return (x & 0x80) == 0 ? djd1(x) : x;
    }

    /// <summary>REDU2: decrease the roll or pitch rate, snapping to the centre if we pass it.</summary>
    private int REDU2(int x, int amount)
    {
        x -= amount;
        if (x < 0)
        {
            x = 1;
        }

        // RE3
        return (x & 0x80) != 0 ? djd1(x) : x;
    }

    /// <summary>djd1: snap the roll or pitch rate to the centre, unless keyboard damping is disabled.</summary>
    private int djd1(int x) => DJD != 0 ? x : 128;

    /// <summary>cntr: apply keyboard damping, moving the roll or pitch rate towards the centre.</summary>
    private int cntr(int x)
    {
        if (Auto == 0 && DAMP != 0)
        {
            return x;
        }

        // cnt2
        if ((x & 0x80) == 0)
        {
            // BUMP
            return x + 1;
        }

        x--;
        if ((x & 0x80) != 0)
        {
            return x;
        }

        return x + 1;
    }

    /// <summary>DK4: check for the pause key (COPY) and process the options while paused.</summary>
    private void DK4()
    {
        if (KL != 0x8B)
        {
            return;
        }

        // FREEZE
        _padContextOverride = PadContext.Paused;
        try
        {
            FREEZE();
        }
        finally
        {
            _padContextOverride = null;
        }
    }

    /// <summary>FREEZE: the pause loop, where the configuration options can be changed.</summary>
    private void FREEZE()
    {
        while (true)
        {
            WSCAN();
            int x = RDKEY();
            if (x == 'Q')
            {
                DNOIZ = 0xFF;
            }

            // DK6: toggle the configuration options
            for (int y = 0; y < 9; y++)
            {
                DKS3(x, y);
            }

            int volume = VOL;
            if (x == '.' || x == ',')
            {
                volume += x == '.' ? 1 : -1;
                if ((volume & 0xF8) == 0)
                {
                    VOL = volume;
                }

                BEEP();
                DELAY(10);
            }

            if (x == 'S')
            {
                DNOIZ = 0;
            }

            if (x == 0x1B)
            {
                throw new GameJumpException(GameJump.Death2);
            }

            if (x == 0x7F)
            {
                return;
            }
        }
    }

    /// <summary>DKS3: toggle a configuration option if its key is being pressed.</summary>
    private void DKS3(int key, int y)
    {
        if (key != GameData.PauseToggleKeys[y])
        {
            return;
        }

        ToggleOptions[y] ^= 0xFF;

        if ((ToggleOptions[y] & 0x80) != 0)
        {
            BELL();
        }

        BELL();
        DELAY(20);
    }

    /// <summary>
    /// TT17: scan the keyboard for cursor key presses, returning the key
    /// pressed, and the cursor movements in x and y.
    /// </summary>
    private int TT17(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (QQ11 == 0)
        {
            return DOKEY();
        }

        // TT17afterall
        DOKEY();
        if (ReadPadCursor(out x, out y))
        {
            return KL;
        }

        // TJ1
        int kl = KL;
        if (kl == 0x8C)
        {
            x--;
        }

        if (kl == 0x8D)
        {
            x++;
        }

        if (kl == 0x8E)
        {
            y--;
        }

        if (kl == 0x8F)
        {
            y++;
        }

        if (ShiftPressed())
        {
            // speedup
            x *= 4;
            y *= 4;
        }

        return kl;
    }

    /// <summary>TT217: wait until a key is pressed (after any current key is released), and return its ASCII code.</summary>
    private int TT217()
    {
        // t: wait for all keys to be released
        do
        {
            DELAY(2);
        }
        while (AnyKeyHeld());

        // t2: wait for a key press
        while (true)
        {
            int a = RDKEY();
            if (a != 0)
            {
                return a;
            }

            WSCAN();
        }
    }

    /// <summary>Returns true if any key that RDKEY scans is being held down.</summary>
    private bool AnyKeyHeld()
    {
        for (int key = 16; key < 128; key++)
        {
            if (_keyboard.IsHeld(key) && TRTB[key] != 0)
            {
                return true;
            }
        }

        return false;
    }
}

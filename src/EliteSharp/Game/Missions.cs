using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Input;

namespace EliteSharp.Game;

/// <summary>
/// The title screens, the mission briefings and the ship hangar.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>distaway: the distance of the ship on the title screen.</summary>
    private int distaway;

    /// <summary>
    /// TITLE: display a title screen with a rotating ship and a recursive
    /// token, and wait for a key press, returning its ASCII code.
    /// </summary>
    private int TITLE(int token, int type, int distance)
    {
        _padContextOverride = PadContext.Screen;
        try
        {
            return TITLE2(token, type, distance);
        }
        finally
        {
            _padContextOverride = null;
        }
    }

    private int TITLE2(int token, int type, int distance)
    {
        distaway = distance;
        TYPE = type;
        RESET();
        ZEKTRAN();
        ZINF();
        DOVDU19(32);
        TT66(13);
        COL = RED;
        QQ11 = 0;
        INWK.Nose.Z = 96 << 8;
        INWK.Z = 96 << 8;
        INWK.RollCounter = 127;
        INWK.PitchCounter = 127;
        QQ17 = 128;
        NWSHP(TYPE);

        XC = 6;
        plf(30);
        TT26(10);
        XC = 6;
        if (PATG != 0)
        {
            DETOK(13);
        }

        // awe
        DELTA = 0;
        JSTK = 0;
        YC = 20;
        XC = 1;
        DETOK(token);
        XC = 7;
        DETOK(12);
        CNT2 = 12;
        MCNT = 5;
        JSTK = 0;

        while (true)
        {
            // TLL2: move the ship towards us
            if (INWK.ZHi != 1)
            {
                INWK.Z -= 256;
            }

            // TL1
            MVEIT();
            INWK.Z = ComposeCoordinate(distaway, INWK.ZHi, INWK.ZSign);
            INWK.X = ComposeCoordinate(0, INWK.XHi, INWK.XSign);
            INWK.Y = ComposeCoordinate(0, INWK.YHi, INWK.YSign);
            LL9();
            MCNT = (MCNT - 1) & 0xFF;

            ThrottleMainLoop();
            int key = RDKEY();
            if (key != 0)
            {
                return key;
            }
        }
    }

    // ------------------------------------------------------------------------
    // Missions
    // ------------------------------------------------------------------------

    /// <summary>BRIEF: start mission 1 and show the mission briefing.</summary>
    private void BRIEF()
    {
        TP |= 1;
        BRIS();
        ZINF();
        TYPE = ShipType.Constrictor;
        NWSHP(TYPE);
        XC = 1;
        INWK.Z = 1 << 8;
        TT66(13);
        MCNT = 64;

        do
        {
            // BRL1: spin the Constrictor in front of us
            INWK.RollCounter = 0x7F;
            INWK.PitchCounter = 0x7F;
            LL9();
            MVEIT();
            ThrottleMainLoop();
            MCNT = (MCNT - 1) & 0xFF;
        }
        while (MCNT != 0);

        while (true)
        {
            // BRL2: fly the Constrictor away
            int xLo = INWK.XLo >> 1;
            INWK.X = ComposeCoordinate(xLo, INWK.XHi, INWK.XSign);
            int zLo = INWK.ZLo;
            zLo = (zLo + 1) & 0xFF;
            if (zLo == 0)
            {
                break;
            }

            zLo = (zLo + 1) & 0xFF;
            INWK.Z = ComposeCoordinate(zLo, INWK.ZHi, INWK.ZSign);
            if (zLo == 0)
            {
                break;
            }

            int yLo = Math.Min(INWK.YLo + 1, 120);
            INWK.Y = ComposeCoordinate(yLo, INWK.YHi, INWK.YSign);
            LL9();
            MVEIT();
            ThrottleMainLoop();
            MCNT = (MCNT - 1) & 0xFF;
        }

        // BR2
        INWK.Z = ComposeCoordinate(0, (INWK.ZHi + 1) & 0xFF, INWK.ZSign);
        PAS1();
        BRP(10);
    }

    /// <summary>BRP: print an extended token and go to the docking bay.</summary>
    private void BRP(int token)
    {
        COL = CYAN;
        DETOK(token);
        BAY();
    }

    /// <summary>BRIEF2: start mission 2.</summary>
    private void BRIEF2()
    {
        TP |= 0b00000100;
        BRP(11);
    }

    /// <summary>BRIEF3: receive the briefing and plans for mission 2.</summary>
    private void BRIEF3()
    {
        TP = (TP & 0b11110000) | 0b00001010;
        BRP(222);
    }

    /// <summary>DEBRIEF2: finish mission 2.</summary>
    private void DEBRIEF2()
    {
        TP |= 0b00000100;
        ENGY = 2;
        TALLY = (TALLY + 0x100) & 0xFFFF;
        BRP(223);
    }

    /// <summary>DEBRIEF: finish mission 1.</summary>
    private void DEBRIEF()
    {
        TP &= 0xFE;
        MCASH(50000);
        BRP(15);
    }

    /// <summary>BRIS: clear the screen, show "Incoming Message" and wait.</summary>
    private void BRIS()
    {
        DETOK(216);
        DELAY(100);
    }

    /// <summary>PAUSE: display the rotating ship and wait for a key, then clear the screen.</summary>
    private void PAUSE()
    {
        while (PAS1() != 0)
        {
        }

        // PAL1
        while (PAS1() == 0)
        {
        }

        INWK.Flags = 0;
        TT66(1);
        LL9();

        // Fall through into MT23
        MT29(10);
    }

    /// <summary>PAUSE2: wait for a key to be released and then pressed.</summary>
    private void PAUSE2()
    {
        while (RDKEY() != 0)
        {
            WSCAN();
        }

        while (RDKEY() == 0)
        {
            WSCAN();
        }
    }

    /// <summary>PAS1: display a rotating ship at the top of the screen and read the keyboard.</summary>
    private int PAS1()
    {
        INWK.Y = ComposeCoordinate(120, INWK.YHi, INWK.YSign);
        INWK.X = ComposeCoordinate(0, INWK.XHi, INWK.XSign);
        INWK.Z = ComposeCoordinate(0, 2, INWK.ZSign);
        LL9();
        MVEIT();
        ThrottleMainLoop();
        return RDKEY();
    }

    // ------------------------------------------------------------------------
    // The ship hangar
    // ------------------------------------------------------------------------

    /// <summary>HANGFLAG: non-zero if there are multiple ships in the hangar.</summary>
    private int HANGFLAG;

    /// <summary>HALL: draw the ships in the hangar, then the hangar itself.</summary>
    private void HALL()
    {
        DOVDU19(0);
        TT66(0);
        int a = DORND();
        if ((a & 0x80) != 0)
        {
            // Draw a group of three ships from HATB
            a &= 3;
            int x = a * 9;
            for (int i = 0; i < 3; i++)
            {
                // HAL8/HAL9
                XX15[2] = GameData.HangarGroups[x];
                XX15[1] = GameData.HangarGroups[x + 1];
                XX15[0] = GameData.HangarGroups[x + 2];
                x += 3;
                HAS1();
            }

            HANGFLAG = 128;
        }
        else
        {
            // HA7: draw a single random ship
            XX15[1] = a >> 1;
            XX15[0] = DORND();
            int type = (DORND() & 3) + ShipType.Sidewinder + (_carry ? 1 : 0);
            XX15[2] = type;
            HAS1();
            HANGFLAG = 0;
        }

        // HA9
        HANGER();
    }

    /// <summary>HAS1: draw a ship in the hangar, using the type and position bytes in XX15.</summary>
    private void HAS1()
    {
        // Each ship in the hangar gets its own on-screen image
        INWK = new WorkspaceShip();
        INWK.ResetOrientationAndPosition();

        int zLo = XX15[0];
        int xSign = (zLo & 1) != 0 ? 0x80 : 0;
        int xLo = XX15[1];
        int zHi = 1 + (xLo & 1);
        INWK.Z = (zHi << 8) | zLo;
        INWK.X = xSign != 0 ? -xLo : xLo;
        RAT2 = 0x80;

        int rotations = DORND();
        do
        {
            // HAL5: rotate the ship around its roof axis
            MVS5(ref INWK.Side.X, ref INWK.Nose.X);
            MVS5(ref INWK.Side.Y, ref INWK.Nose.Y);
            MVS5(ref INWK.Side.Z, ref INWK.Nose.Z);
            rotations = (rotations - 1) & 0xFF;
        }
        while (rotations != 0);

        int type = XX15[2];
        if (type == 0)
        {
            return;
        }

        var blueprint = BlueprintFor(type);
        if (blueprint == null)
        {
            return;
        }

        XX0 = blueprint;
        TYPE = type;

        // Sit the ship on the hangar floor, using its size
        int size = EliteMaths.Ll5(blueprint.TargetableArea & 0xFFFF);
        int yLo = ((100 - size) & 0xFF) >> 1;
        INWK.Y = -yLo;

        TIDY();
        LL9();
    }

    /// <summary>
    /// HANGER: draw the hangar floor and back wall, with the lines stopping
    /// when they bump into the ships that are already on-screen.
    /// </summary>
    private void HANGER()
    {
        var occupied = _screen.RasterizeSpaceView();

        void Draw(int x1, int y1, int x2, int y2)
        {
            _screen.DrawLine(x1, y1, x2, y2, RED, toggle: false);
            for (int y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
            {
                for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
                {
                    occupied[x, y] = true;
                }
            }
        }

        // Scan along a row from a starting point in a direction until we hit
        // something, and draw the line up to that point
        void Scan(int y, int start, int step, int limit)
        {
            int x = start;
            int end = -1;
            while (x != limit && !occupied[x, y])
            {
                end = x;
                x += step;
            }

            if (end >= 0)
            {
                Draw(start, y, end, y);
            }
        }

        // The floor
        for (int t = 2; t < 13; t++)
        {
            int y = CentreY + 130 / t;
            Scan(y, 2, 1, 256);
            Scan(y, 253, -1, -1);
            if (HANGFLAG != 0)
            {
                Scan(y, 128, 1, 256);
                Scan(y, 127, -1, -1);
            }
        }

        // The back wall
        for (int i = 1; i < 32; i++)
        {
            int x = i * 8;
            int end = -1;
            for (int y = 1; y < 2 * CentreY && !occupied[x, y]; y++)
            {
                end = y;
            }

            if (end >= 1)
            {
                Draw(x, 1, x, end);
            }
        }
    }
}

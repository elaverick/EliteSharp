using EliteSharp.Game.Ships;

namespace EliteSharp.Game;

/// <summary>
/// The main game loop and the start and end of the game.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>BEGIN: initialise the configuration variables and the commander, and start the game.</summary>
    private void BEGIN()
    {
        // Zero the configuration variables from COMC to DISK
        Array.Clear(ToggleOptions);
        DNOIZ = 0;
        DISK = 0;
        JSTK = 0;
        COMC = 0;
        Array.Clear(mscol);

        JAMESON();
    }

    /// <summary>TT170: the entry point for the start of the game (and after pressing ESCAPE while paused).</summary>
    private void TT170()
    {
        RESET();
        DEATH2();
    }

    /// <summary>DEATH2: reset most of the game and restart from the title screen.</summary>
    private void DEATH2()
    {
        RES2();
        BR1();
    }

    /// <summary>BR1 (part 1): show the "Load New Commander (Y/N)?" title screen.</summary>
    private void BR1()
    {
        ZEKTRAN();
        XC = 3;
        int key = TITLE(6, ShipType.CobraMkIII, 200);
        if (key == 'Y')
        {
            DFAULT();
            SVE();
        }

        QU5();
    }

    /// <summary>QU5 and BR1 (part 2): show the "Press Fire or Space, Commander" title screen and start the game docked.</summary>
    private void QU5()
    {
        DFAULT();
        msblob();
        TITLE(7, ShipType.Cougar, 100);
        ping();
        TT111();
        jmp();

        // likeTT112: copy the current system's seeds into QQ2
        for (int i = 5; i >= 0; i--)
        {
            QQ2[i] = QQ15[i];
        }

        EV = 0;
        QQ28 = QQ3;
        tek = QQ5;
        gov = QQ4;
        BAY();
    }

    /// <summary>BAY: go to the docking bay (i.e. show the Status Mode screen).</summary>
    private void BAY()
    {
        QQ12 = 0xFF;
        throw new GameJumpException(GameJump.ForceKey, f8);
    }

    /// <summary>
    /// The main game loop (TT100, MLOOP and FRCE). If a key is given, we start
    /// by processing that key (FRCE), otherwise we start at MLOOP.
    /// </summary>
    private void MainLoop(int? forcedKey)
    {
        bool startAtFrce = forcedKey.HasValue;
        bool startAtMloop = !forcedKey.HasValue;
        int key = forcedKey ?? 0;
        int cursorX = 0, cursorY = 0;

        while (true)
        {
            try
            {
                if (!startAtFrce)
                {
                    if (!startAtMloop)
                    {
                        TT100();
                    }

                    startAtMloop = false;

                    // MLOOP: cool the lasers and update the dashboard
                    if (GNTMP != 0)
                    {
                        GNTMP--;
                    }

                    if (LASCT != 0)
                    {
                        int x = LASCT - 1;
                        if (x != 0)
                        {
                            x--;
                        }

                        LASCT = x;
                    }

                    DIALS();

                    if (QQ11 != 0 && ((QQ11 & PATG) & 1) == 0)
                    {
                        DELAY(2);
                    }
                    else if (QQ12 != 0)
                    {
                        // The docked loop doesn't go through TT100, so make
                        // sure it doesn't spin without sending frames
                        WSCAN();
                    }

                    key = TT17(out cursorX, out cursorY);
                }

                startAtFrce = false;

                // FRCE: process the key
                TT102(key, cursorX, cursorY);
                cursorX = cursorY = 0;

                // If we are docked, loop back to MLOOP, otherwise TT100
                startAtMloop = QQ12 != 0;
            }
            catch (GameJumpException jump) when (jump.Target == GameJump.ForceKey)
            {
                key = jump.Key;
                cursorX = cursorY = 0;
                startAtFrce = true;
            }
            catch (GameJumpException jump) when (jump.Target == GameJump.MainLoop)
            {
                startAtFrce = false;
                startAtMloop = true;
            }
        }
    }

    /// <summary>
    /// TT100 (main game loop parts 1 to 4): call the main flight loop, remove
    /// in-flight messages, and potentially spawn new ships.
    /// </summary>
    private void TT100()
    {
        ThrottleMainLoop();
        RunDebugCommands();
        M();

        // Count down the in-flight message delay
        DLY = (DLY - 1) & 0xFF;
        if (DLY == 0)
        {
            me2();
        }
        else if ((DLY & 0x80) != 0)
        {
            DLY = (DLY + 1) & 0xFF;
        }

        // me3
        MCNT = (MCNT - 1) & 0xFF;
        if (MCNT != 0)
        {
            return;
        }

        SpawnShips();
    }

    /// <summary>Main game loop parts 1 to 4: spawn traders, junk, cops, pirates, bounty hunters and Thargoids.</summary>
    private void SpawnShips()
    {
        if (MJ != 0)
        {
            return;
        }

        int a = DORND();
        if (a < 35 && Junk < 3)
        {
            // Spawn a trader, asteroid or cargo canister
            ZINF();
            INWK.Z = 38 * 256;
            a = DORND();
            int x = _randX;

            // ROL x_hi twice sets bit 1 of x_hi to the C flag
            int xHi = _carry ? 2 : 0;
            INWK.X = ComposeCoordinate(a, xHi, a & 0x80);
            INWK.Y = ComposeCoordinate(x, 0, x & 0x80);

            a = DORND();
            x = _randX;
            if (_overflow)
            {
                // MTT4: spawn a trader, and then fall through into TT100 to
                // run the main flight loop again
                MTT4();
                TT100();
                return;
            }

            INWK.RollCounter = a | 0b01101111;
            if (SSPR != 0)
            {
                return;
            }

            a = x;
            if (_carry)
            {
                // MTT2
                INWK.PitchCounter = a | 0b01111111;
            }
            else
            {
                INWK.Speed = (a & 31) | 16;
            }

            // MTT3
            a = DORND();
            int type;
            if (a >= 252)
            {
                type = ShipType.RockHermit;
                INWK.Ai = ShipType.RockHermit;
            }
            else
            {
                // thongs: C is set if A >= 10
                type = (a & 1) + ShipType.CargoCanister + (a >= 10 ? 1 : 0);
            }

            NWSHP(type);
        }

        // MTT1 (part 3): potentially spawn a cop
        if (SSPR != 0)
        {
            return;
        }

        int badness = (BAD() << 1) & 0xFF;
        if (Many[ShipType.Viper] != 0)
        {
            badness |= FIST;
        }

        a = Ze();
        if (a == 136)
        {
            // fothg: spawn a Thargoid, or very rarely a Cougar
            if ((Planet.ZLo & 0b00111110) == 0)
            {
                INWK.Speed = 18;
                INWK.Ai = 0b01111001;
                NWSHP(ShipType.Cougar);
                return;
            }

            // fothg2
            GTHG();
            SpawnPiratesOrBountyHunter();
            return;
        }

        if (a < badness)
        {
            NWSHP(ShipType.Viper);
        }

        if (Many[ShipType.Viper] != 0)
        {
            return;
        }

        // Part 4
        EV = (EV - 1) & 0xFF;
        if ((EV & 0x80) == 0)
        {
            return;
        }

        EV = (EV + 1) & 0xFF;

        if ((TP & 0b00001100) == 0b00001000)
        {
            a = DORND();
            if (a >= 220)
            {
                // fothg2
                GTHG();
            }
        }

        SpawnPiratesOrBountyHunter();
    }

    /// <summary>
    /// MTT4: spawn a trader (a Cobra Mk III, Python, Boa or Anaconda) using the
    /// position already set up in INWK.
    /// </summary>
    private void MTT4()
    {
        int a = DORND() >> 1;
        INWK.Ai = a;
        INWK.RollCounter = a;
        INWK.Speed = (a & 31) | 16;

        a = DORND();
        if ((a & 0x80) == 0)
        {
            // Spawn a ship that is trying to dock
            INWK.Ai |= 0b11000000;
            INWK.Newb = 0b00010000;
        }

        // nodo: A = 0 or 2, plus the C flag from DORND
        int type = (a & 2) + (_carry ? 1 : 0) + ShipType.CobraMkIII;
        if (type != ShipType.RockHermit)
        {
            NWSHP(type);
        }
    }

    /// <summary>
    /// nopl and LABEL_2 (main game loop part 4): potentially spawn a lone bounty
    /// hunter, the Constrictor, or a group of up to four pirates.
    /// </summary>
    private void SpawnPiratesOrBountyHunter()
    {
        int a = DORND();
        if (gov != 0)
        {
            if (a >= 90 || (a & 7) < gov)
            {
                return;
            }
        }

        // LABEL_2
        a = Ze();
        if (a >= 100)
        {
            // mt1: spawn a group of pirates
            a &= 3;
            EV = a;
            int count = a;
            do
            {
                int t = DORND();
                a = DORND() & t & 7;
                NWSHP(a + ShipType.PackHunters + (_carry ? 1 : 0));
                count--;
            }
            while (count >= 0);

            return;
        }

        EV = (EV + 1) & 0xFF;

        // The C flag is clear here as we passed through the BCS above
        int type = (a & 3) + ShipType.CobraMkIIIPirate;
        if (THERE())
        {
            INWK.Ai = 0b11111001;
            int tp = TP & 0b00000011;
            if ((tp & 1) != 0 && ((tp >> 1) | Many[ShipType.Constrictor]) == 0)
            {
                // YESCON: spawn the Constrictor
                NWSHP(ShipType.Constrictor);
                return;
            }
        }

        // NOCON
        INWK.Newb = 0b00000100;
        a = DORND();
        INWK.Ai = (((a << 1) | (a >= 200 ? 1 : 0)) & 0xFF) | 0b11000000;
        NWSHP(type);
    }

    /// <summary>Compose a signed coordinate from sign-magnitude bytes.</summary>
    private static int ComposeCoordinate(int lo, int hi, int sign)
    {
        int magnitude = (lo & 0xFF) | ((hi & 0xFF) << 8) | ((sign & 0x7F) << 16);
        return (sign & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// TT102: process function keys, the save key, hyperspace and chart keys,
    /// and update the hyperspace countdown.
    /// </summary>
    private void TT102(int key, int cursorX, int cursorY)
    {
        switch (key)
        {
            case f8:
                STATUS();
                return;
            case f4:
                TT22();
                return;
            case f5:
                TT23();
                return;
            case f6:
                TT111();
                TT25();
                return;
            case f9:
                TT213();
                return;
            case f7:
                TT167();
                return;
            case f0:
                TT110();
                return;
        }

        // fvw
        if ((QQ12 & 0x80) != 0)
        {
            // We are docked
            if (key == f3)
            {
                EQSHP();
                return;
            }

            if (key == f1)
            {
                TT219();
                return;
            }

            if (key == '@')
            {
                if (SVE())
                {
                    throw new GameJumpException(GameJump.QU5);
                }

                BAY();
            }

            if (key == f2)
            {
                TT208();
                return;
            }
        }
        else
        {
            // INSP: change the space view
            switch (key)
            {
                case f1:
                    LOOK1(1);
                    return;
                case f2:
                    LOOK1(2);
                    return;
                case f3:
                    LOOK1(3);
                    return;
            }
        }

        // LABEL_3
        if (KL == 'H')
        {
            hyp();
            return;
        }

        if (KL == 'D')
        {
            T95();
            return;
        }

        if (KL == 'F')
        {
            if (QQ12 != 0 && (QQ11 & 0b11000000) != 0)
            {
                HME2();
            }

            return;
        }

        // HME1
        if ((QQ11 & 0b11000000) != 0 && QQ22Hi == 0)
        {
            if (KL == 'O')
            {
                TT103();
                ping();
                TT103();
                return;
            }

            // ee2: move the crosshairs
            TT16(cursorX, cursorY);
        }

        // TT107: update the hyperspace countdown
        if (QQ22Hi == 0)
        {
            return;
        }

        QQ22 = (QQ22 - 1) & 0xFF;
        if (QQ22 != 0)
        {
            return;
        }

        ee3(QQ22Hi - 1);
        QQ22 = 5;
        ee3(QQ22Hi);
        QQ22Hi--;
        if (QQ22Hi != 0)
        {
            return;
        }

        TT18();
    }

    /// <summary>T95: print the distance to the selected system.</summary>
    private void T95()
    {
        if ((QQ11 & 0b11000000) == 0)
        {
            return;
        }

        hm();
        cpl();
        QQ17 = 0x80;
        TT26(12);
        TT146();
    }

    /// <summary>BAD: calculate how bad we have been from the amount of contraband in our hold.</summary>
    private int BAD() => ((QQ20[3] + QQ20[6]) * 2 + QQ20[10]) & 0xFF;

    /// <summary>
    /// FAROF2: returns true (C set) if INWK is within distance A of us in all
    /// three axes (i.e. x_hi, y_hi and z_hi are all less than or equal to A).
    /// </summary>
    private bool FAROF2(int a) => a >= INWK.XHi && a >= INWK.YHi && a >= INWK.ZHi;

    /// <summary>FAROF: FAROF2 with a distance of 224.</summary>
    private bool FAROF() => FAROF2(224);

    /// <summary>MAS4: OR A with the high bytes of the ship's coordinates.</summary>
    private int MAS4(int a) => a | INWK.XHi | INWK.YHi | INWK.ZHi;

    /// <summary>DEATH: display the death screen.</summary>
    private void DEATH()
    {
        NOISE(soexpl);
        RES2();
        DELTA = (DELTA << 2) & 0xFF;
        DET1(24);
        TT66(13);
        QQ11 = 0;
        BOX();
        nWq();
        COL = CYAN;
        XC = 12;
        YC = 12;
        ex(146);

        do
        {
            // D1
            int a = Ze();
            int x = _randX;
            a >>= 2;
            int xLo = a;
            MCNT = 0xFF;
            int yLo = a ^ 0b00101010;
            int zLo = yLo | 0b01010000;
            INWK.X = ComposeCoordinate(xLo, 0, INWK.XSign & 0x80);
            INWK.Y = ComposeCoordinate(yLo, 0, INWK.YSign & 0x80);
            INWK.Z = zLo;
            INWK.Ai = 0;
            int roll = x & 0b10001111;
            INWK.RollCounter = roll;
            LASCT = 64;

            // SEC, ROR A
            INWK.PitchCounter = ((roll >> 1) | 0x80) & 0b10000111;

            // The byte at XX21 + 7 is always non-zero, so this depends on
            // the C flag, which was set by the ROR above to bit 0 of the roll
            int type = (roll & 1) != 0 ? ShipType.AlloyPlate : ShipType.CargoCanister;
            fq1(type);

            int killed = DORND() & 0x80;
            if (INF != null)
            {
                INF.Flags = killed;
            }
        }
        while (Slots[4] == null);

        DELTA = 0;
        ThrottleMainLoop();
        M();

        do
        {
            ThrottleMainLoop();
            M();
            LASCT = (LASCT - 1) & 0xFF;
        }
        while (LASCT != 0);

        DET1(31);
        throw new GameJumpException(GameJump.Death2);
    }

    /// <summary>DOENTRY: dock at the space station, show the ship hangar and work out any mission progression.</summary>
    private void DOENTRY()
    {
        RES2();
        LAUN();
        DELTA = 0;
        GNTMP = 0;
        QQ22Hi = 0;
        FSH = 0xFF;
        ASH = 0xFF;
        ENERGY = 0xFF;
        HALL();
        DELAY(44);

        int missionStatus = TP & 0b00000011;
        if (missionStatus == 0)
        {
            if ((TALLY >> 8) != 0 && (GCNT >> 1) == 0)
            {
                BRIEF();
            }

            BAY();
        }

        if (missionStatus == 0b00000011)
        {
            DEBRIEF();
        }

        // EN2: mission 2
        if (GCNT == 2)
        {
            int status = TP & 0b00001111;
            if (status == 0b00000010)
            {
                if ((TALLY >> 8) >= 5)
                {
                    BRIEF2();
                }
            }
            else if (status == 0b00000110)
            {
                if (QQ0 == 215 && QQ1 == 84)
                {
                    BRIEF3();
                }
            }
            else if (status == 0b00001010)
            {
                if (QQ0 == 63 && QQ1 == 72)
                {
                    DEBRIEF2();
                }
            }
        }

        // EN4
        BAY();
    }

    /// <summary>
    /// ESCAPE: launch our escape pod, watch our Cobra fly off, and dock at the
    /// station with our cargo and legal status wiped.
    /// </summary>
    private void ESCAPE()
    {
        RES2();
        TYPE = ShipType.CobraMkIII;
        if (!FRS1(ShipType.CobraMkIII))
        {
            FRS1(ShipType.CobraMkIIIPirate);
        }

        // ES1: set up the Cobra that we just launched from (the new ship is
        // still in INWK)
        INWK.Speed = 8;
        INWK.PitchCounter = 194;
        INWK.Ai = 194 >> 1;

        do
        {
            // ESL1
            MVEIT();
            if ((QQ11 | VIEW) == 0)
            {
                LL9();
            }

            INWK.Ai = (INWK.Ai - 1) & 0xFF;
            ThrottleMainLoop();
        }
        while (INWK.Ai != 0);

        SCAN();

        Array.Clear(QQ20);
        FIST = 0;
        ESCP = 0;
        QQ14 = 70;
        DOENTRY();
    }
}

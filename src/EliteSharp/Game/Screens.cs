using EliteSharp.Data;

namespace EliteSharp.Game;

/// <summary>
/// The docked and chart screens: status, market, cargo, equipment, charts and
/// system data, along with the galaxy and system seed calculations.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>ZZ (as set by TT111): the number of the selected system (0-255).</summary>
    private int SystemNumber;

    /// <summary>DOVDU19: change the palette of the space view (0 = space, 16 = chart, 32 = title, 48 = trade).</summary>
    private void DOVDU19(int offset) => _screen.PaletteOffset = offset;

    /// <summary>TRADEMODE: clear the screen and set up a trading screen with the given view type.</summary>
    private void TRADEMODE(int view)
    {
        TT66(view);
        TRADEMODE2();
    }

    /// <summary>TRADEMODE2: switch to the trading screen palette and cyan text.</summary>
    private void TRADEMODE2()
    {
        DOVDU19(48);
        COL = CYAN;
    }

    // ------------------------------------------------------------------------
    // Status Mode
    // ------------------------------------------------------------------------

    /// <summary>STATUS: show the Status Mode screen (red key f8).</summary>
    private void STATUS()
    {
        TRADEMODE(8);
        TT111();
        XC = 7;
        NLIN3(126);

        if (QQ12 != 0)
        {
            // wearedocked
            DETOK(205);
            TT67K();
        }
        else
        {
            int a = 230;
            if (SlotType(2 + Junk) != 0)
            {
                a += 1 + (ENERGY >= 128 ? 1 : 0);
            }

            // st6
            plf(a);
        }

        spc(125);

        int legal = 19;
        if (FIST != 0)
        {
            legal += 1 + (FIST >= 50 ? 1 : 0);
        }

        // st5
        plf(legal);
        spc(16);

        int rating;
        int tallyHi = TALLY >> 8;
        if (tallyHi != 0)
        {
            // st4
            rating = tallyHi >= 25 ? 9 : tallyHi >= 10 ? 8 : tallyHi >= 2 ? 7 : 6;
        }
        else
        {
            rating = 0;
            int a = (TALLY & 0xFF) >> 2;
            do
            {
                rating++;
                a >>= 1;
            }
            while (a != 0);
        }

        // st3
        plf(rating + 21);
        plf2(18);

        if (ESCP != 0)
        {
            plf2(112);
        }

        if (BST != 0)
        {
            plf2(111);
        }

        if (ECM != 0)
        {
            plf2(108);
        }

        // stqv: the energy bomb, energy unit, docking computer and galactic hyperdrive
        int[] equipment = [BOMB, ENGY, DKCMP, GHYP];
        for (int i = 0; i < 4; i++)
        {
            if (equipment[i] != 0)
            {
                plf2(113 + i);
            }
        }

        for (int x = 0; x < 4; x++)
        {
            int laser = LASER[x];
            if (laser == 0)
            {
                continue;
            }

            spc(96 + x);
            int token = 103;
            if (laser == 128 + POW)
            {
                token = 104;
            }

            if (laser == Armlas)
            {
                token = 117;
            }

            if (laser == Mlas)
            {
                token = 118;
            }

            plf2(token);
        }
    }

    // ------------------------------------------------------------------------
    // Galaxy and system seeds
    // ------------------------------------------------------------------------

    /// <summary>TT81: set the selected system's seeds to those of system 0 in the current galaxy.</summary>
    private void TT81()
    {
        for (int x = 5; x >= 0; x--)
        {
            QQ15[x] = QQ21[x];
        }
    }

    /// <summary>TT20: twist the selected system's seeds four times, to move to the next system.</summary>
    private void TT20()
    {
        for (int i = 0; i < 4; i++)
        {
            TT54();
        }
    }

    /// <summary>TT54: twist the selected system's seeds once (s0 = s1, s1 = s2, s2 = s0 + s1 + s2).</summary>
    private void TT54()
    {
        int lo = QQ15[0] + QQ15[2];
        int hi = QQ15[1] + QQ15[3] + (lo > 0xFF ? 1 : 0);
        lo &= 0xFF;
        hi &= 0xFF;

        QQ15[0] = QQ15[2];
        QQ15[1] = QQ15[3];
        QQ15[3] = QQ15[5];
        QQ15[2] = QQ15[4];

        int newLo = lo + QQ15[2];
        QQ15[4] = newLo & 0xFF;
        QQ15[5] = (hi + QQ15[3] + (newLo > 0xFF ? 1 : 0)) & 0xFF;
    }

    /// <summary>TT24: calculate the selected system's economy, government, tech level, population and productivity.</summary>
    private void TT24()
    {
        QQ3 = QQ15[1] & 7;
        QQ4 = (QQ15[2] >> 3) & 7;
        if ((QQ4 >> 1) == 0)
        {
            QQ3 |= 2;
        }

        // TT77
        int qq5 = (QQ3 ^ 7) + (QQ15[3] & 3);
        qq5 += (QQ4 >> 1) + (QQ4 & 1);
        QQ5 = qq5 & 0xFF;
        QQ6 = (QQ5 * 4 + QQ3 + QQ4 + 1) & 0xFF;

        int p = ((QQ3 ^ 7) + 3) * (QQ4 + 4);
        int product = (p & 0xFF) * QQ6;
        QQ7 = (product * 8) & 0xFFFF;
    }

    /// <summary>
    /// TT111: select the system closest to galactic coordinates (QQ9, QQ10),
    /// and calculate its distance and data.
    /// </summary>
    private void TT111()
    {
        TT81();
        int best = 127;
        int u = 0;
        do
        {
            // TT130
            int s = Math.Abs(QQ15[3] - QQ9) >> 1;
            int distance = (Math.Abs(QQ15[1] - QQ10) >> 1) + s;
            if (distance < best)
            {
                best = distance;
                for (int x = 5; x >= 0; x--)
                {
                    QQ19[x] = QQ15[x];
                }

                SystemNumber = u;
            }

            // TT135
            TT20();
            u = (u + 1) & 0xFF;
        }
        while (u != 0);

        for (int x = 5; x >= 0; x--)
        {
            QQ15[x] = QQ19[x];
        }

        QQ10 = QQ15[1];
        QQ9 = QQ15[3];
        readdistnce(QQ9);
    }

    /// <summary>
    /// readdistnce: calculate the distance in QQ8 from the current system to
    /// the system at galactic x-coordinate A (and y-coordinate QQ15+1), and
    /// then calculate the system data with TT24.
    /// </summary>
    private void readdistnce(int x)
    {
        int dx = Math.Abs(x - QQ0) & 0xFF;
        int dx2 = dx * dx;
        int dy = (Math.Abs(QQ15[1] - QQ1) & 0xFF) >> 1;
        int dy2 = dy * dy;
        int sum = dx2 + dy2;
        int q = sum & 0xFF;
        int r = sum >> 8;
        if (r > 0xFF)
        {
            r = 255;
        }

        int root = EliteMaths.Ll5((r << 8) | q);
        QQ8 = (root << 2) & 0x3FF;
        TT24();
    }

    /// <summary>cpl: print the selected system's name.</summary>
    private void cpl()
    {
        var saved = (int[])QQ15.Clone();
        int pairs = (QQ15[0] & 0x40) != 0 ? 3 : 2;
        for (int t = pairs; t >= 0; t--)
        {
            // TT55
            int a = QQ15[5] & 31;
            if (a != 0)
            {
                TT27(a | 0x80);
            }

            TT54();
        }

        Array.Copy(saved, QQ15, 6);
    }

    // ------------------------------------------------------------------------
    // Data on System
    // ------------------------------------------------------------------------

    /// <summary>TT25: show the Data on System screen (red key f6).</summary>
    private void TT25()
    {
        TRADEMODE(1);
        XC = 9;
        NLIN3(163);
        TTX69();
        TT146();

        TT68(194);
        int a = QQ3;
        if (((a + 1) >> 1) == 2)
        {
            // TT70: economies 3 and 4 are "Mainly"
            TT27(173);
        }
        else
        {
            // TT71: the C flag from the CMP #2 is set for economies 5-7
            TT27((a >= 5 ? a - 5 : a) + 170);
        }

        // TT72
        TT60((QQ3 >> 2) + 168);

        TT68(162);
        TT60(QQ4 + 177);

        TT68(196);
        pr2(QQ5 + 1);
        TTX69();

        TT68(192);
        pr2(QQ6, true);
        TT60(198);

        TT27('(');
        if ((QQ15[4] & 0x80) == 0)
        {
            TT27(188);
        }
        else
        {
            // TT75: the species description
            int b = QQ15[5] >> 2;
            if ((b & 7) < 3)
            {
                spc((b & 7) + 227);
            }

            // TT205
            b >>= 3;
            if (b < 6)
            {
                spc(b + 230);
            }

            // TT206
            int c = (QQ15[3] ^ QQ15[1]) & 7;
            QQ19[0] = c;
            if (c < 6)
            {
                spc(c + 236);
            }

            // TT207
            int species = ((QQ15[5] & 3) + QQ19[0]) & 7;
            TT27(species + 242);
        }

        // TT76
        TT27('S');
        TT60(')');

        TT68(193);
        pr6(QQ7);
        TT162();
        QQ17 = 0;
        TT27('M');
        TT60(226);

        TT68(250);
        int radius = (((QQ15[5] & 15) + 11) << 8) | QQ15[3];
        pr5(radius, false);
        TT162();
        TT26('k');
        TT26('m');
        TTX69();

        PDESC();
    }

    /// <summary>TT146: print the distance to the selected system, if it isn't the current system.</summary>
    private void TT146()
    {
        if (QQ8 == 0)
        {
            YC++;
            return;
        }

        // TT63
        TT68(191);
        pr5(QQ8, true);
        TT60(195);
    }

    /// <summary>PDESC: print the system's extended description, or a mission 1 directive.</summary>
    private void PDESC()
    {
        if (QQ8 == 0 && (QQ12 & 0x80) != 0)
        {
            // NRU% is 0 in the original source, which is a bug that makes the
            // game crash for some systems, so we use the intended table size
            int count = GameData.ExtendedDescriptionSystems.Length;
            for (int y = count; y > 0; y--)
            {
                if (GameData.ExtendedDescriptionSystems[y - 1] != SystemNumber)
                {
                    continue;
                }

                int galaxy = GameData.ExtendedDescriptionGalaxies[y - 1];
                if ((galaxy & 0x7F) != GCNT)
                {
                    continue;
                }

                if ((galaxy & 0x80) != 0)
                {
                    // PD3
                    DETOK2(176);
                }
                else
                {
                    if ((TP & 1) == 0)
                    {
                        break;
                    }

                    MT14();
                    DETOK2(1);
                }

                DETOK3(y);
                DETOK(177);
                return;
            }
        }

        // PD1: the "goat soup" description, seeded from the system's seeds
        for (int x = 3; x >= 0; x--)
        {
            RAND[x] = QQ15[2 + x];
        }

        DETOK(5);
    }

    // ------------------------------------------------------------------------
    // Charts
    // ------------------------------------------------------------------------

    /// <summary>TT22: show the Long-range Chart (red key f4).</summary>
    private void TT22()
    {
        TT66(64);
        DOVDU19(16);
        COL = CYAN;
        XC = 7;
        TT81();
        TT27(199);
        NLIN();
        NLIN5(GCYB + 1);
        TT14();

        for (int x = 0; x < 256; x++)
        {
            // TT83: plot each system as a dot
            int sx = QQ15[3];
            int zz = QQ15[4] | 0x50;
            int sy = (QQ15[1] >> 1) + GCYT;
            foreach (var rect in PixelRects(sx, sy, zz, YELLOW))
            {
                _screen.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Colour);
            }

            TT20();
        }

        QQ19[0] = QQ9;
        QQ19[1] = QQ10 >> 1;
        QQ19[2] = 4;
        COL = GREEN;
        TT15();
    }

    /// <summary>TT15: draw a set of crosshairs at (QQ19, QQ19+1) with size QQ19+2.</summary>
    private void TT15()
    {
        int offset = (QQ11 & 0x80) != 0 ? 0 : GCYT;
        QQ19[5] = offset;

        int x1 = QQ19[0] - QQ19[2];
        if ((QQ11 & 0x80) == 0)
        {
            if (x1 < 2)
            {
                x1 = 2;
            }
        }
        else
        {
            x1 &= 0xFF;
        }

        int x2 = QQ19[0] + QQ19[2];
        if (x2 > 0xFF || x2 >= 254)
        {
            x2 = 254;
        }

        int y = (QQ19[1] + offset) & 0xFF;
        HLOIN3(x1, x2, y);

        int y1 = QQ19[1] - QQ19[2];
        if (y1 < 0)
        {
            y1 = 0;
        }

        y1 = (y1 + offset) & 0xFF;
        int y2 = QQ19[1] + QQ19[2] + offset;
        if (y2 >= GCYB && (QQ11 & 0x80) == 0)
        {
            y2 = GCYB;
        }

        y2 &= 0xFF;
        LOIN(QQ19[0], y1, QQ19[0], y2);
    }

    /// <summary>TT14: draw the fuel range circle and the crosshairs on the current system.</summary>
    private void TT14()
    {
        if ((QQ11 & 0x80) != 0)
        {
            // TT126: the short-range chart
            QQ19[0] = 104;
            QQ19[1] = 90;
            QQ19[2] = 16;
            COL = GREEN;
            TT15();
            KRadius = QQ14;
            TT128();
            return;
        }

        KRadius = QQ14 >> 2;
        QQ19[0] = QQ0;
        QQ19[1] = QQ1 >> 1;
        QQ19[2] = 7;
        COL = CYAN;
        TT15();
        QQ19[1] += GCYT;
        TT128();
    }

    /// <summary>TT128: draw a red circle of radius K centred on (QQ19, QQ19+1).</summary>
    private void TT128()
    {
        CircleX = QQ19[0];
        CircleY = QQ19[1];
        LSP = 1;
        STP = 2;
        COL = RED;
        _ballLines.Clear();
        CIRCLE2();
        foreach (var line in _ballLines)
        {
            _screen.DrawLine(line.X1, line.Y1, line.X2, line.Y2, COL);
        }
    }

    /// <summary>TT103: draw a small set of crosshairs on a chart at the selected system (EOR, so it also erases).</summary>
    private void TT103()
    {
        COL = GREEN;
        if ((QQ11 & 0x80) != 0)
        {
            TT105();
            return;
        }

        QQ19[0] = QQ9;
        QQ19[1] = QQ10 >> 1;
        QQ19[2] = 4;
        TT15();
    }

    /// <summary>TT105: draw the crosshairs on the short-range chart, if the selected system is in range.</summary>
    private void TT105()
    {
        int dx = (QQ9 - QQ0) & 0xFF;
        int adx = Math.Abs(QQ9 - QQ0) & 0xFF;
        if (adx >= 29)
        {
            return;
        }

        if ((dx & 0x80) != 0 && dx < 233)
        {
            return;
        }

        // TT179
        QQ19[0] = ((dx << 2) + 104) & 0xFF;

        int dy = (QQ10 - QQ1) & 0xFF;
        int ady = Math.Abs(QQ10 - QQ1) & 0xFF;
        if (ady >= 35)
        {
            return;
        }

        QQ19[1] = ((dy << 1) + 90) & 0xFF;
        QQ19[2] = 8;
        COL = GREEN;
        TT15();
    }

    /// <summary>TT16: move the crosshairs on a chart by the given amounts.</summary>
    private void TT16(int dx, int dy)
    {
        WSCAN();
        TT103();
        QQ10 = TT123(QQ10, -dy);
        QQ19[1] = QQ10;
        QQ9 = TT123(QQ9, dx);
        QQ19[0] = QQ9;
        TT103();
    }

    /// <summary>TT123: add a delta to a coordinate, without going past 0 or 255.</summary>
    private static int TT123(int value, int delta)
    {
        int result = value + delta;
        return result is < 0 or > 255 ? value : result;
    }

    /// <summary>TT23: show the Short-range Chart (red key f5).</summary>
    private void TT23()
    {
        TT66(128);
        DOVDU19(16);
        COL = CYAN;
        XC = 7;
        NLIN3(190);
        TT14();
        TT103();
        TT81();
        COL = CYAN;

        // Zero the label row table (INWK in the original)
        var rowsUsed = new bool[32];
        for (int count = 0; count < 256; count++)
        {
            // TT182
            int dx = (QQ15[3] - QQ0) & 0xFF;
            int dy = (QQ15[1] - QQ1) & 0xFF;
            if ((Math.Abs(QQ15[3] - QQ0) & 0xFF) < 29 && (Math.Abs(QQ15[1] - QQ1) & 0xFF) < 40)
            {
                // The ASL instructions leave bits 5 and 7 of the deltas in
                // the C flag, which is added in by the ADC
                int sx = ((dx << 2) + 104 + ((dx >> 5) & 1)) & 0xFF;
                XC = (sx >> 3) + 1;
                int sy = ((dy << 1) + 90 + ((dy >> 7) & 1)) & 0xFF;
                int y = sy >> 3;

                // Find a row for the label
                int labelRow = -1;
                if (!rowsUsed[y])
                {
                    labelRow = y;
                }
                else if (y + 1 < 32 && !rowsUsed[y + 1])
                {
                    labelRow = y + 1;
                }
                else if (y > 0 && !rowsUsed[y - 1])
                {
                    labelRow = y - 1;
                }

                bool skip = false;
                if (labelRow >= 0)
                {
                    // EE4
                    YC = labelRow;
                    if (labelRow < 3 || labelRow >= 21)
                    {
                        skip = true;
                    }
                    else
                    {
                        readdistnce(QQ15[3]);
                        if (QQ8 >= 70)
                        {
                            skip = true;
                        }
                        else
                        {
                            rowsUsed[labelRow] = true;
                            QQ17 = 0x80;
                            cpl();
                        }
                    }
                }

                if (!skip)
                {
                    // ee1: draw the star
                    CircleX = sx;
                    CircleY = sy;
                    KRadius = (QQ15[5] & 1) + 2;
                    FLFLLS();
                    _sunToCanvas = true;
                    SUN();
                    _sunToCanvas = false;
                    FLFLLS();
                    COL = CYAN;
                }
            }

            // TT187
            TT20();
        }
    }

    /// <summary>True if SUN should draw on the canvas (for the stars on the short-range chart).</summary>
    private bool _sunToCanvas;

    /// <summary>hm: move the chart crosshairs to the nearest system.</summary>
    private void hm()
    {
        TT103();
        TT111();
        TT103();
        CLYNS();
    }

    /// <summary>HME2: search the galaxy for a system by name (the "F" key on the charts).</summary>
    private void HME2()
    {
        COL = CYAN;
        DETOK(14);
        TT103();
        TT81();

        string wanted = _lastInput.ToUpperInvariant();
        for (int i = 0; i < 256; i++)
        {
            // HME3: compare the name with this system's name
            if (SystemName() == wanted && wanted.Length > 0)
            {
                // HME5
                QQ9 = QQ15[3];
                QQ10 = QQ15[1];
                TT111();
                TT103();
                MT15();
                T95();
                return;
            }

            TT20();
        }

        TT111();
        TT103();
        BOOP();
        DETOK(215);
    }

    /// <summary>The name of the selected system, in capitals.</summary>
    private string SystemName()
    {
        var name = new System.Text.StringBuilder();
        var saved = (int[])QQ15.Clone();
        int pairs = (QQ15[0] & 0x40) != 0 ? 3 : 2;
        for (int t = pairs; t >= 0; t--)
        {
            int a = QQ15[5] & 31;
            if (a != 0)
            {
                int y = a << 1;
                name.Append((char)GameData.TwoLetterTokens[y]);
                if (GameData.TwoLetterTokens[y + 1] != '?')
                {
                    name.Append((char)GameData.TwoLetterTokens[y + 1]);
                }
            }

            TT54();
        }

        Array.Copy(saved, QQ15, 6);
        return name.ToString();
    }

    // ------------------------------------------------------------------------
    // Hyperspace
    // ------------------------------------------------------------------------

    /// <summary>hyp: start the hyperspace process (the "H" key).</summary>
    private void hyp()
    {
        if (QQ12 != 0)
        {
            // dockEd
            CLYNS();
            XC = 15;
            COL = RED;
            DETOK(205);
            return;
        }

        if (QQ22Hi != 0)
        {
            return;
        }

        COL = CYAN;
        if ((CtrlPressed() & 0x80) != 0)
        {
            Ghy();
            return;
        }

        if (QQ11 == 0)
        {
            // TTX110
            TT111();
        }
        else
        {
            if ((QQ11 & 0b11000000) == 0)
            {
                return;
            }

            hm();
        }

        // TTX111
        if (QQ8 == 0)
        {
            return;
        }

        for (int x = 5; x >= 0; x--)
        {
            safehouse[x] = QQ15[x];
        }

        XC = 7;
        YC = 22;
        QQ17 = 0;
        TT27(189);
        if ((QQ8 >> 8) != 0 || QQ14 < QQ8)
        {
            // TT147
            prq(202);
            return;
        }

        TT27('-');
        cpl();

        // wW
        wW2(15);
    }

    /// <summary>wW2: start the hyperspace countdown from the given value.</summary>
    private void wW2(int a)
    {
        QQ22Hi = a;
        QQ22 = a;
        ee3(a);
    }

    /// <summary>Ghy: perform a galactic hyperspace jump.</summary>
    private void Ghy()
    {
        if (GHYP == 0)
        {
            return;
        }

        GHYP = 0;
        FIST = 0;
        wW2(2);
        GCNT = (GCNT + 1) & 0b11110111;

        // G1: rotate each seed byte left
        for (int x = 5; x >= 0; x--)
        {
            int value = QQ21[x];
            QQ21[x] = ((value << 1) | (value >> 7)) & 0xFF;
        }

        // zZ
        QQ9 = 96;
        QQ10 = 96;
        TT110();
        TT111();
        for (int x = 5; x >= 0; x--)
        {
            safehouse[x] = QQ15[x];
        }

        QQ8 = 0;
        MESS(116);
        jmp();
    }

    // ------------------------------------------------------------------------
    // Buying and selling cargo
    // ------------------------------------------------------------------------

    /// <summary>TT163: print the headers for the market prices table.</summary>
    private void TT163()
    {
        XC = 17;
        TT27(255);
    }

    /// <summary>TT167: show the Market Price screen (red key f7).</summary>
    private void TT167()
    {
        TRADEMODE(16);
        XC = 5;
        NLIN3(167);
        YC = 3;
        TT163();
        YC = 6;
        for (QQ29 = 0; QQ29 < 17; QQ29++)
        {
            QQ17 = 0x80;
            TT151(QQ29);
            YC++;
        }
    }

    /// <summary>TT151: print the name, price and availability of a market item.</summary>
    private void TT151(int item)
    {
        QQ19[4] = item;
        QQ19[0] = (item << 2) & 0xFF;
        if (MJ != 0)
        {
            return;
        }

        XC = 1;
        TT27(item + 208);
        XC = 14;

        int x = QQ19[0];
        QQ19[1] = GameData.MarketPrices[x + 1];
        QQ24 = ((QQ26 & GameData.MarketPrices[x + 3]) + GameData.MarketPrices[x]) & 0xFF;
        TT152();
        var_();

        if ((QQ19[1] & 0x80) != 0)
        {
            // TT155
            QQ24 = (QQ24 - QQ19[3]) & 0xFF;
        }
        else
        {
            QQ24 = (QQ24 + QQ19[3]) & 0xFF;
        }

        // TT156
        pr5((QQ24 << 2) & 0xFFFF, true);

        int available = AVL[QQ19[4]];
        QQ25 = available;
        if (available == 0)
        {
            // TT172
            XC = 25;
            TT27('-');
            return;
        }

        TT11(available, 5, false);
        TT152();
    }

    /// <summary>TT152: print the units for the current market item ("t", "kg" or "g").</summary>
    private void TT152()
    {
        int units = QQ19[1] & 96;
        if (units == 0)
        {
            // TT160
            TT26('t');
            TT162();
        }
        else if (units == 32)
        {
            // TT161
            TT26('k');
            TT26('g');
        }
        else
        {
            TT26('g');
            TT162();
        }
    }

    /// <summary>TT219: show the Buy Cargo screen (red key f1).</summary>
    private void TT219()
    {
        TRADEMODE(2);
        TT163();
        QQ17 = 0x80;
        QQ29 = 0;

        while (true)
        {
            // TT220
            TT151(QQ29);
            if (QQ25 != 0)
            {
                BuyItem();
            }

            // TT222
            YC = QQ29 + 5;
            XC = 0;
            QQ29++;
            if (QQ29 >= 17)
            {
                // BAY2
                throw new GameJumpException(GameJump.ForceKey, f9);
            }
        }
    }

    /// <summary>TT224: ask how many of the current item to buy, and buy them.</summary>
    private void BuyItem()
    {
        while (true)
        {
            // TT224
            CLYNS();
            TT27(204);
            TT27(QQ29 + 208);
            TT27('/');
            TT152();
            TT27('?');
            TT67();

            var (r, error) = gnum();
            if (error)
            {
                // TQ4
                Tc(176);
                continue;
            }

            if (r != 0 && tnpr(r))
            {
                Tc(206);
                continue;
            }

            if (!LCASH(GCASH(r, QQ24)))
            {
                Tc(197);
                continue;
            }

            QQ20[QQ29] = (QQ20[QQ29] + r) & 0xFF;
            AVL[QQ29] = (AVL[QQ29] - r) & 0xFF;
            if (r != 0)
            {
                dn();
            }

            return;
        }
    }

    /// <summary>Tc: print a space, a token and a question mark, and beep.</summary>
    private void Tc(int token)
    {
        TT162();
        prq(token);

        // TTX224
        dn2();
    }

    /// <summary>
    /// gnum: get a number from the keyboard, returning the number and whether
    /// the number was too large (the C flag).
    /// </summary>
    private (int Value, bool Error) gnum()
    {
        COL = MAGENTA;
        int r = 0;
        for (int t1 = 12; t1 > 0; t1--)
        {
            // TT223
            int a = TT217();
            if (r == 0)
            {
                if (a == 'Y')
                {
                    // NWDAV1
                    TT26(a);
                    r = QQ25;
                    COL = CYAN;
                    return (r, false);
                }

                if (a == 'N')
                {
                    // NWDAV3
                    TT26(a);
                    COL = CYAN;
                    return (0, false);
                }
            }

            // NWDAV2
            int digit = a - '0';
            if (digit < 0)
            {
                break;
            }

            if (digit >= 10)
            {
                // BAY2
                throw new GameJumpException(GameJump.ForceKey, f9);
            }

            if (r >= 26)
            {
                return OUTK(a);
            }

            int value = r * 10 + digit;
            if (value > 0xFF)
            {
                return OUTK(a);
            }

            r = value;
            if (r > QQ25)
            {
                return OUTK(a);
            }

            // TT226
            TT26(a);
        }

        // OUT
        COL = CYAN;
        return (r, false);

        (int, bool) OUTK(int key)
        {
            TT26(key);
            COL = CYAN;
            return (r, true);
        }
    }

    /// <summary>
    /// tnpr: work out whether there is room in the cargo hold for the given
    /// quantity of item QQ29, returning true (C set) if there isn't.
    /// </summary>
    private bool tnpr(int quantity)
    {
        if (QQ29 > 12)
        {
            // kg: items measured in kg or g are limited to 200 each
            return quantity + QQ20[QQ29] >= 200;
        }

        // The C flag is set by the CPX, so the first addition adds an extra 1
        int total = quantity + 1;
        for (int x = 12; x >= 0; x--)
        {
            total += QQ20[x];
        }

        return total >= CRGO;
    }

    /// <summary>tnpr1: work out whether there is room for one tonne of the given item (setting QQ29).</summary>
    private bool tnpr1(int item)
    {
        QQ29 = item;
        return tnpr(1);
    }

    /// <summary>GCASH: calculate the cost of a quantity at a price (in Cr * 10), i.e. quantity * price * 4.</summary>
    private static int GCASH(int quantity, int price) => (quantity * price * 4) & 0xFFFF;

    /// <summary>TT208: show the Sell Cargo screen (red key f2).</summary>
    private void TT208()
    {
        TRADEMODE(4);
        XC = 10;
        TT27(205);
        NLIN3(206);
        TT67();
        TT210();
    }

    /// <summary>TT213: show the Inventory screen (red key f9).</summary>
    private void TT213()
    {
        TRADEMODE(8);
        XC = 11;
        TT60(164);
        NLIN4();
        fwl();
        if (CRGO >= 26)
        {
            TT27(107);
        }

        TT210();
    }

    /// <summary>TT210: show a list of the cargo in the hold, and sell it if this is the Sell Cargo screen.</summary>
    private void TT210()
    {
        for (int y = 0; y < 17; y++)
        {
            // TT211
            QQ29 = y;
            while (true)
            {
                // NWDAVxx
                int amount = QQ20[y];
                if (amount == 0)
                {
                    break;
                }

                QQ19[1] = GameData.MarketPrices[y * 4 + 1];
                TT69();
                TT27(QQ29 + 208);
                XC = 14;
                QQ25 = amount;
                pr2(amount);
                TT152();

                if (QQ11 != 4)
                {
                    break;
                }

                // Sell this item?
                TT27(205);
                DETOK(206);
                var (r, error) = gnum();
                if (r == 0)
                {
                    break;
                }

                if (error)
                {
                    // NWDAV4
                    TT67();
                    prq(176);
                    dn2();
                    continue;
                }

                // Work out the price without printing anything
                QQ17 = 0xFF;
                TT151(QQ29);
                QQ20[QQ29] = (QQ20[QQ29] - r) & 0xFF;
                MCASH(GCASH(r, QQ24));
                QQ17 = 0;
                break;
            }
        }

        if (QQ11 == 4)
        {
            dn2();
            throw new GameJumpException(GameJump.ForceKey, f9);
        }

        TT69();
    }

    // ------------------------------------------------------------------------
    // Buying equipment
    // ------------------------------------------------------------------------

    /// <summary>PRXS: the price of each item of equipment.</summary>
    private int PRXS(int item) => GameData.EquipmentPrices[item * 2] | (GameData.EquipmentPrices[item * 2 + 1] << 8);

    /// <summary>The fuel price, which EQSHP stores in PRXS+0.</summary>
    private int _fuelPrice = 1;

    /// <summary>prx: the price of an item of equipment.</summary>
    private int prx(int item) => item == 0 ? _fuelPrice : PRXS(item);

    /// <summary>EQSHP: show the Equip Ship screen (red key f3).</summary>
    private void EQSHP()
    {
        while (true)
        {
            TRADEMODE(32);
            XC = 12;
            spc(207);
            NLIN3(185);
            QQ17 = 0x80;
            YC++;

            int a = tek + 3;
            if (a >= 12)
            {
                a = 14;
            }

            QQ25 = a;
            int q = a + 1;
            _fuelPrice = ((70 - QQ14) << 1) & 0xFFFF;

            for (int x = 1; x < q; x++)
            {
                // EQL1
                TT67();
                pr2(x);
                TT162();
                TT27(x + 104);
                int price = prx(x - 1);
                XC = 25;
                TT11(price, 6, true);
            }

            CLYNS();
            prq(127);
            var (item, error) = gnum();
            if (item == 0 || error)
            {
                BAY();
            }

            item--;
            XC = 2;
            YC++;
            eq(item);

            if (!BuyEquipment(item))
            {
                return;
            }

            // et11
            dn();
        }
    }

    /// <summary>
    /// Fit a newly bought item of equipment, returning false if the item was
    /// already present (in which case we have jumped to BAY).
    /// </summary>
    private bool BuyEquipment(int a)
    {
        switch (a)
        {
            case 0:
                QQ14 = 70;
                return true;
            case 1:
                if (NOMSL + 1 >= 5)
                {
                    Present(a, 124);
                }

                NOMSL++;
                msblob();
                return true;
            case 2:
                if (CRGO == 37)
                {
                    Present(a, 107);
                }

                CRGO = 37;
                return true;
            case 3:
                if (ECM != 0)
                {
                    Present(a, 108);
                }

                ECM = 0xFF;
                return true;
            case 4:
                refund(qv(), POW);
                return true;
            case 5:
                refund(qv(), POW + 128);
                return true;
            case 6:
                if (BST != 0)
                {
                    Present(a, 111);
                }

                BST = 0xFF;
                return true;
            case 7:
                if (ESCP != 0)
                {
                    Present(a, 112);
                }

                ESCP = 0xFF;
                return true;
            case 8:
                if (BOMB != 0)
                {
                    Present(a, 113);
                }

                BOMB = 0x7F;
                return true;
            case 9:
                if (ENGY != 0)
                {
                    Present(a, 114);
                }

                ENGY = 1;
                return true;
            case 10:
                if (DKCMP != 0)
                {
                    Present(a, 115);
                }

                DKCMP = 0xFF;
                return true;
            case 11:
                if (GHYP != 0)
                {
                    Present(a, 116);
                }

                GHYP = 0xFF;
                return true;
            case 12:
                refund(qv(), Armlas);
                return true;
            case 13:
                refund(qv(), Mlas);
                return true;
        }

        return true;
    }

    /// <summary>pres: the item is already fitted, so refund the price and say so.</summary>
    private void Present(int item, int token)
    {
        MCASH(prx(item));
        spc(token);
        TT27(31);

        // err
        dn2();
        BAY();
    }

    /// <summary>eq: subtract the price of an item from our cash, jumping to BAY if we can't afford it.</summary>
    private void eq(int item)
    {
        if (LCASH(prx(item)))
        {
            return;
        }

        prq(197);
        dn2();
        BAY();
    }

    /// <summary>dn: print the amount of cash left, and beep.</summary>
    private void dn()
    {
        TT162();
        spc(119);
        dn2();
    }

    /// <summary>dn2: make a short, high beep and wait a moment.</summary>
    private void dn2()
    {
        BEEP();
        DELAY(25);
    }

    /// <summary>qv: ask which view to fit a laser to, returning the view number.</summary>
    private int qv()
    {
        if (tek >= 8)
        {
            TT66(32);
        }

        int y = 16;
        YC = y;
        do
        {
            // qv1
            XC = 12;
            spc(y + '0' - 16);
            TT27(YC + 80);
            YC++;
            y = YC;
        }
        while (y < 20);

        CLYNS();
        while (true)
        {
            // qv2
            prq(175);
            int a = TT217() - '0';
            if (a >= 0 && a < 4)
            {
                return a;
            }

            CLYNS();
        }
    }

    /// <summary>refund: fit a laser to the given view, refunding the price of any laser already there.</summary>
    private void refund(int view, int laser)
    {
        int old = LASER[view];
        if (old != 0)
        {
            int item = old switch
            {
                POW => 4,
                POW + 128 => 5,
                Armlas => 12,
                _ => 13,
            };

            MCASH(prx(item));
        }

        LASER[view] = laser;
    }
}

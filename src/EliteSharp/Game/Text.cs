using EliteSharp.Data;

namespace EliteSharp.Game;

/// <summary>
/// Text: the recursive and extended token systems, printing characters and
/// numbers, justified text and in-flight messages.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>XC and YC: the text cursor.</summary>
    private int XC = 1, YC = 1;

    /// <summary>QQ17: the text case flags (bit 7 = Sentence Case, bit 6 = lower case next, &amp;FF = don't print).</summary>
    private int QQ17;

    // The DTW flags used by the extended token system
    private int DTW1 = 0b00100000;
    private int DTW2 = 0b11111111;
    private int DTW3;
    private int DTW4;
    private int DTW5;
    private int DTW6;
    private int DTW8 = 0b11111111;

    /// <summary>DTW7: the character printed by MT16 (the drive number in the catalogue).</summary>
    private int DTW7 = 'A';

    /// <summary>LL: the line length for justified text.</summary>
    private const int LL = 30;

    /// <summary>
    /// TKN2: the two-letter token table for extended tokens. The QQ16 table
    /// follows TKN2 in memory, and higher token numbers read into it, so the
    /// two tables are joined here.
    /// </summary>
    private static readonly byte[] TKN2 = [.. GameData.ExtendedTwoLetterTokens, .. GameData.TwoLetterTokens, .. new byte[128]];

    /// <summary>BUF: the line buffer for justified text.</summary>
    private readonly int[] BUF = new int[256];

    // ------------------------------------------------------------------------
    // Printing characters
    // ------------------------------------------------------------------------

    /// <summary>CHPR: print a character at the text cursor.</summary>
    private void CHPR(int a)
    {
        if (QQ17 == 0xFF || a == 0 || a >= 128)
        {
            return;
        }

        if (a == 11)
        {
            // cls
            TTX66();
            return;
        }

        if (a == 7)
        {
            // R5
            BEEP();
            return;
        }

        if (a < 32)
        {
            if (a != 10)
            {
                XC = 1;
            }

            // RRX1
            if (a != 13)
            {
                YC++;
            }

            return;
        }

        // RR1: in the catalogue, skip spaces at column 17
        if (CATF != 0 && a == ' ' && XC == 17)
        {
            return;
        }

        if (a == 127)
        {
            // Delete the character to the left of the cursor
            XC--;
            _screen.EraseCharacter(XC, YC);
            return;
        }

        // RR2
        int column = XC;
        XC++;
        if (YC >= 24)
        {
            TTX66();
            XC = 1;
            YC = 1;
            return;
        }

        // RR3
        _screen.PrintCharacter(column, YC, (char)a, COL);
    }

    /// <summary>
    /// TT26 (DASC): print a character, taking into account justification and
    /// the in-flight message buffer.
    /// </summary>
    private void TT26(int a)
    {
        DTW8 = 0xFF;
        DTW2 = a is '.' or ':' or 10 or 12 or ' ' ? 0xFF : 0;

        if ((DTW4 & 0x80) == 0)
        {
            CHPR(a);
            return;
        }

        if ((DTW4 & 0x40) == 0 && a == 12)
        {
            DA1();
            return;
        }

        BUF[DTW5] = a;
        DTW5 = (DTW5 + 1) & 0xFF;
    }

    /// <summary>DA1: print the contents of the line buffer, justifying it into lines of LL characters.</summary>
    private void DA1()
    {
        int sc1 = 0;
        while (true)
        {
            // DA5
            int x = DTW5;
            if (x == 0)
            {
                // DA6+3
                DTW5 = 0;
                CHPR(12);
                return;
            }

            if (x < LL + 1)
            {
                // DA6
                DAS1(x);
                DTW5 = 0;
                CHPR(12);
                return;
            }

            // Justify the first line by inserting spaces until the character
            // at position LL is a space
            sc1 >>= 1;
            Justify(ref sc1);

            // DA2: print the first line
            DAS1(LL);
            CHPR(12);

            // The C flag is clear from CHPR, so this is DTW5 - LL - 1
            int remaining = DTW5 - LL - 1;
            DTW5 = remaining & 0xFF;
            if (DTW5 == 0)
            {
                CHPR(12);
                return;
            }

            for (int y = 0; y <= DTW5; y++)
            {
                BUF[y] = BUF[LL + 1 + y];
            }
        }
    }

    /// <summary>DA11 to DAL3: insert spaces into the buffer until BUF+LL is a space.</summary>
    private void Justify(ref int sc1)
    {
        // The original loops forever if there are no spaces to expand, so we
        // give up after a while instead
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            // DA11
            if ((sc1 & 0x80) == 0)
            {
                sc1 = 0b01000000;
            }

            int y = LL - 1;
            while (true)
            {
                // DAL1
                if (BUF[LL] == ' ')
                {
                    return;
                }

                // DAL2: find the next space to the left
                bool restart = false;
                while (true)
                {
                    y--;
                    if (y <= 0)
                    {
                        restart = true;
                        break;
                    }

                    if (BUF[y] != ' ')
                    {
                        continue;
                    }

                    sc1 = (sc1 << 1) & 0xFF;
                    if ((sc1 & 0x80) != 0)
                    {
                        continue;
                    }

                    break;
                }

                if (restart)
                {
                    break;
                }

                // Insert a space at position y by shifting the rest right
                int sc = y;
                for (int i = DTW5; i >= sc; i--)
                {
                    BUF[i + 1] = BUF[i];
                }

                DTW5++;

                // DAL3: skip back past any consecutive spaces
                y = sc - 1;
                while (y >= 0 && BUF[y] == ' ')
                {
                    y--;
                }

                if (y < 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>DAS1: print the first x characters of the line buffer.</summary>
    private void DAS1(int x)
    {
        for (int y = 0; y < x; y++)
        {
            CHPR(BUF[y]);
        }
    }

    // ------------------------------------------------------------------------
    // Recursive tokens (TT27)
    // ------------------------------------------------------------------------

    /// <summary>TT27: print a recursive token, a two-letter token, a control code or a character.</summary>
    private void TT27(int a)
    {
        a &= 0xFF;
        if (a == 0)
        {
            csh();
            return;
        }

        if (a >= 128)
        {
            TT43(a);
            return;
        }

        switch (a)
        {
            case 1:
                tal();
                return;
            case 2:
                ypl();
                return;
            case 3:
                cpl();
                return;
            case 4:
                cmn();
                return;
            case 5:
                fwl();
                return;
            case 6:
                QQ17 = 0x80;
                return;
            case 7:
                // X is not zero here, so fall through to the character checks
                break;
            case 8:
                QQ17 = 0;
                return;
            case 9:
                crlf();
                return;
        }

        if (a >= 96)
        {
            ex(a);
            return;
        }

        if (a >= 14 && a < 32)
        {
            // qw: tokens 14-31 are recursive tokens 128-145
            ex(a + 114);
            return;
        }

        int x = QQ17;
        if (x == 0)
        {
            // TT74
            TT26(a);
            return;
        }

        if ((x & 0x80) != 0)
        {
            // TT41: Sentence Case
            if ((x & 0x40) != 0)
            {
                // TT45
                if (x == 0xFF)
                {
                    return;
                }

                if (a >= 'A')
                {
                    TT42(a);
                    return;
                }

                // TT46
                QQ17 = x & 0b10111111;
                TT26(a);
                return;
            }

            if (a < 'A')
            {
                TT26(a);
                return;
            }

            QQ17 = x | 0b01000000;
            TT26(a);
            return;
        }

        if ((x & 0x40) != 0)
        {
            // TT46
            QQ17 = x & 0b10111111;
            TT26(a);
            return;
        }

        TT42(a);
    }

    /// <summary>TT42: print a letter in lower case.</summary>
    private void TT42(int a)
    {
        if (a >= 'A' && a <= 'Z')
        {
            a += 32;
        }

        TT26(a);
    }

    /// <summary>TT43: print a two-letter token (128-159) or a recursive token (160-255).</summary>
    private void TT43(int a)
    {
        if (a >= 160)
        {
            // TT47
            ex(a - 160);
            return;
        }

        int y = (a & 127) << 1;
        TT27(GameData.TwoLetterTokens[y]);
        int second = GameData.TwoLetterTokens[y + 1];
        if (second != '?')
        {
            TT27(second);
        }
    }

    /// <summary>ex: print recursive token A from QQ18.</summary>
    private void ex(int a)
    {
        var table = GameData.RecursiveTokens;
        int position = 0;
        int x = a & 0xFF;
        while (x != 0)
        {
            while (table[position] != 0)
            {
                position++;
            }

            position++;
            x--;
        }

        while (table[position] != 0)
        {
            TT27(table[position] ^ 0x23);
            position++;
        }
    }

    // ------------------------------------------------------------------------
    // Extended tokens (DETOK)
    // ------------------------------------------------------------------------

    /// <summary>DETOK: print extended token A.</summary>
    private void DETOK(int a) => PrintExtendedToken(GameData.ExtendedTokens, a);

    /// <summary>DETOK3: print extended token A from the RUTOK table.</summary>
    private void DETOK3(int a) => PrintExtendedToken(GameData.ExtendedDescriptionTokens, a);

    private void PrintExtendedToken(byte[] table, int a)
    {
        int position = 0;
        int x = a & 0xFF;

        // Find the start of the token by counting separators (bytes that
        // decode to zero)
        while (true)
        {
            if (position >= table.Length)
            {
                return;
            }

            if ((table[position] ^ 0x57) == 0)
            {
                x = (x - 1) & 0xFF;
                if (x == 0)
                {
                    break;
                }
            }

            position++;
        }

        // DTL2
        while (true)
        {
            position++;
            if (position >= table.Length)
            {
                return;
            }

            int c = table[position] ^ 0x57;
            if (c == 0)
            {
                return;
            }

            DETOK2(c);
        }
    }

    /// <summary>DETOK2: print an extended text token character or control code.</summary>
    private void DETOK2(int a)
    {
        if (a < 32)
        {
            DT3(a);
            return;
        }

        if ((DTW3 & 0x80) != 0)
        {
            TT27(a);
            return;
        }

        // DT8
        if (a < '[')
        {
            DTS(a);
            return;
        }

        if (a < 129)
        {
            DT6(a);
            return;
        }

        if (a < 215)
        {
            DETOK(a);
            return;
        }

        int x = (a - 215) << 1;
        DTS(TKN2[x]);
        DTS(TKN2[x + 1]);
    }

    /// <summary>DTS: print a letter in the correct case.</summary>
    private void DTS(int a)
    {
        if (a >= 'A')
        {
            if ((DTW6 & 0x80) != 0 || (DTW2 & 0x80) == 0)
            {
                // DT10
                a |= DTW1;
            }

            // DT5
            a &= DTW8;
        }

        // DT9
        TT26(a);
    }

    /// <summary>DT6: print a random token from the MTIN table.</summary>
    private void DT6(int a)
    {
        int x = DORND();
        int index = (x >= 51 ? 1 : 0) + (x >= 102 ? 1 : 0) + (x >= 153 ? 1 : 0) + (x >= 204 ? 1 : 0);
        DETOK(GameData.RandomTokenBases[a - 91] + index);
    }

    /// <summary>DT3: process a control code in an extended token (via the JMTB jump table).</summary>
    private void DT3(int a)
    {
        switch (a)
        {
            case 1:
                MT1();
                break;
            case 2:
                MT2();
                break;
            case 3:
            case 4:
                TT27(a);
                break;
            case 5:
                DTW3 = 0;
                break;
            case 6:
                QQ17 = 0x80;
                DTW3 = 0xFF;
                break;
            case 8:
                XC = 6;
                DTW2 = 0xFF;
                break;
            case 9:
                XC = 1;
                TT66(1);
                break;
            case 11:
                NLIN4();
                break;
            case 13:
                DTW6 = 0x80;
                DTW1 = 0b00100000;
                break;
            case 14:
                MT14();
                break;
            case 15:
                MT15();
                break;
            case 16:
                TT26(DTW7);
                break;
            case 17:
                MT17();
                break;
            case 18:
                MT18();
                break;
            case 19:
                DTW8 = 0b11011111;
                break;
            case 21:
                CLYNS();
                break;
            case 22:
                PAUSE();
                break;
            case 23:
                MT29(10);
                break;
            case 24:
                PAUSE2();
                break;
            case 25:
                BRIS();
                break;
            case 26:
                MT26();
                break;
            case 27:
                DETOK(217 + GCNT);
                break;
            case 28:
                DETOK(220 + GCNT);
                break;
            case 29:
                MT29(6);
                break;
            case 30:
                FILEPR();
                break;
            case 31:
                OTHERFILEPR();
                break;
            default:
                // 7, 10, 12, 20 and 32 print the character
                TT26(a);
                break;
        }
    }

    /// <summary>MT1: switch to ALL CAPS.</summary>
    private void MT1()
    {
        DTW1 = 0;
        DTW6 = 0;
    }

    /// <summary>MT2: switch to Sentence Case.</summary>
    private void MT2()
    {
        DTW1 = 0b00100000;
        DTW6 = 0;
    }

    /// <summary>MT14: switch to justified text.</summary>
    private void MT14()
    {
        DTW4 = 0x80;
        DTW5 = 0;
    }

    /// <summary>MT15: switch to left-aligned text.</summary>
    private void MT15()
    {
        DTW4 = 0;
        DTW5 = 0;
    }

    /// <summary>MT17: print the selected system's adjective (e.g. "Lavian").</summary>
    private void MT17()
    {
        QQ17 &= 0b10111111;
        TT27(3);
        int last = DTW5 > 0 ? BUF[DTW5 - 1] : 0;
        if (VOWEL(last))
        {
            DTW5--;
        }

        // MT171
        DETOK(153);
    }

    /// <summary>MT18: print a random 1-8 letter word.</summary>
    private void MT18()
    {
        DTW8 = 0b11011111;
        int y = DORND() & 3;
        do
        {
            int x = DORND() & 62;
            DTS(TKN2[x + 2]);
            DTS(TKN2[x + 3]);
            y--;
        }
        while (y >= 0);
    }

    /// <summary>VOWEL: returns true if the character is a vowel.</summary>
    private static bool VOWEL(int a)
    {
        a |= 0b00100000;
        return a is 'a' or 'e' or 'i' or 'o' or 'u';
    }

    /// <summary>MT23 and MT29: move to the given row, switch to cyan and lower case.</summary>
    private void MT29(int row)
    {
        YC = row;
        COL = CYAN;
        DTW6 = 0x80;
        DTW1 = 0b00100000;
    }

    // ------------------------------------------------------------------------
    // Printing numbers
    // ------------------------------------------------------------------------

    /// <summary>
    /// BPRNT: print a number to a specific number of digits, right-aligned
    /// with leading spaces, optionally with a decimal point before the last
    /// digit.
    /// </summary>
    private void BPRNT(long number, int digits, bool decimalPoint)
    {
        int t = decimalPoint ? 10 : 11;
        int u = decimalPoint ? digits - 1 : digits;
        u = 12 - u;

        string text = (number % 1_000_000_000_000L).ToString("D12");
        for (int i = 0; i < 12; i++)
        {
            int digit = text[i] - '0';
            if (digit != 0 || t == 0)
            {
                // TT32
                t = 0;
                TT26('0' + digit);
            }
            else
            {
                u--;
                if (u < 0)
                {
                    TT26(' ');
                }
            }

            // TT34
            t = Math.Max(t - 1, 0);
            if (i == 10 && decimalPoint)
            {
                TT26('.');
            }
        }
    }

    /// <summary>TT11: print a 16-bit number to the given number of digits.</summary>
    private void TT11(int value, int digits, bool decimalPoint) => BPRNT(value & 0xFFFF, digits, decimalPoint);

    /// <summary>pr2: print an 8-bit number to 3 digits.</summary>
    private void pr2(int value, bool decimalPoint = false) => TT11(value, 3, decimalPoint);

    /// <summary>pr5: print a 16-bit number to 5 digits.</summary>
    private void pr5(int value, bool decimalPoint) => TT11(value, 5, decimalPoint);

    /// <summary>pr6: print a 16-bit number to 5 digits, without a decimal point.</summary>
    private void pr6(int value) => pr5(value, false);

    /// <summary>csh: print our cash with one decimal place, then " CR" and a newline.</summary>
    private void csh()
    {
        BPRNT(CASH, 9, true);
        plf(226);
    }

    /// <summary>tal: print the galaxy number.</summary>
    private void tal() => pr2(GCNT + 1);

    /// <summary>ypl: print the current system's name.</summary>
    private void ypl()
    {
        if ((MJ & 0x80) != 0)
        {
            return;
        }

        TT62();
        cpl();
        TT62();
    }

    /// <summary>TT62: swap the current system's seeds with the selected system's seeds.</summary>
    private void TT62()
    {
        for (int x = 5; x >= 0; x--)
        {
            (QQ2[x], QQ15[x]) = (QQ15[x], QQ2[x]);
        }
    }

    /// <summary>cmn: print the commander's name.</summary>
    private void cmn()
    {
        foreach (char c in CommanderName)
        {
            TT26(c);
        }
    }

    /// <summary>fwl: print fuel and cash levels.</summary>
    private void fwl()
    {
        TT68(105);
        pr2(QQ14, true);
        plf(195);

        // PCASH
        TT27(119);
    }

    /// <summary>crlf: tab to column 21 and print a colon.</summary>
    private void crlf()
    {
        XC = 21;
        TT73();
    }

    /// <summary>plf: print a token followed by a newline.</summary>
    private void plf(int a)
    {
        TT27(a);
        TT67();
    }

    /// <summary>plf2: print a token followed by a newline, and indent the next line to column 6.</summary>
    private void plf2(int a)
    {
        plf(a);
        XC = 6;
    }

    /// <summary>TT68: print a token followed by a colon.</summary>
    private void TT68(int a)
    {
        TT27(a);
        TT73();
    }

    /// <summary>TT73: print a colon.</summary>
    private void TT73() => TT27(':');

    /// <summary>TT162: print a space.</summary>
    private void TT162() => TT27(' ');

    /// <summary>TT67: print a newline.</summary>
    private void TT67() => TT27(12);

    /// <summary>TT67K: print a newline using CHPR directly.</summary>
    private void TT67K() => CHPR(12);

    /// <summary>TT69: switch to Sentence Case and print a newline.</summary>
    private void TT69()
    {
        QQ17 = 0x80;
        TT67();
    }

    /// <summary>TTX69: print a paragraph break (a blank line) in Sentence Case.</summary>
    private void TTX69()
    {
        YC++;
        TT69();
    }

    /// <summary>TT60: print a token and a paragraph break.</summary>
    private void TT60(int a)
    {
        TT27(a);
        TTX69();
    }

    /// <summary>spc: print a token followed by a space.</summary>
    private void spc(int a)
    {
        TT27(a);
        TT162();
    }

    /// <summary>prq: print a token followed by a question mark.</summary>
    private void prq(int a)
    {
        TT27(a);
        TT27('?');
    }

    /// <summary>NLIN3: print a title and draw a horizontal line at row 19.</summary>
    private void NLIN3(int a)
    {
        TT27(a);
        NLIN4();
    }

    /// <summary>NLIN4: draw a horizontal line at pixel row 19.</summary>
    private void NLIN4() => NLIN2(19);

    /// <summary>NLIN: draw a horizontal line at pixel row 23 and move the text cursor down a line.</summary>
    private void NLIN() => NLIN5(23);

    /// <summary>NLIN5: move the text cursor down a line and draw a horizontal line at the given row.</summary>
    private void NLIN5(int row)
    {
        YC++;
        NLIN2(row);
    }

    /// <summary>NLIN2: draw a horizontal yellow line across the screen at the given pixel row.</summary>
    private void NLIN2(int row)
    {
        COL = YELLOW;
        HLOIN3(2, 254, row);
        COL = CYAN;
    }

    /// <summary>HLOIN3: draw a horizontal line from x1 to x2 - 1 in the current colour.</summary>
    private void HLOIN3(int x1, int x2, int y)
    {
        if (x1 == x2)
        {
            return;
        }

        if (x1 > x2)
        {
            (x1, x2) = (x2, x1);
        }

        _screen.DrawLine(x1, y, x2 - 1, y, COL);
    }

    /// <summary>LOIN: draw a line in the current colour (using EOR logic, so drawing it twice removes it).</summary>
    private void LOIN(int x1, int y1, int x2, int y2) => _screen.DrawLine(x1, y1, x2, y2, COL);

    /// <summary>BELL: make a beep.</summary>
    private void BELL() => CHPR(7);

    // ------------------------------------------------------------------------
    // Screen clearing
    // ------------------------------------------------------------------------

    /// <summary>TT66: clear the screen and set the current view type in QQ11.</summary>
    private void TT66(int a)
    {
        QQ11 = a;
        TTX66K();
    }

    /// <summary>TTX66K: clear the screen, draw a border box, and print the view name if this is a space view.</summary>
    private void TTX66K()
    {
        TTX66();
        MT2();
        LSP = 0;
        QQ17 = 0x80;
        DTW2 = 0x80;
        FLFLLS();
        LAS2 = 0;
        DLY = 0;
        de = 0;
        if (QQ22Hi != 0)
        {
            ee3(QQ22Hi);
        }

        if (QQ11 == 0)
        {
            XC = 11;
            COL = CYAN;
            TT27(VIEW | 0x60);
            TT162();
            TT27(175);
        }

        // tt66
        QQ17 = 0;
    }

    /// <summary>TTX66: clear the top part of the screen and draw a border box.</summary>
    private void TTX66()
    {
        _screen.ClearSpaceView();
        LSX2Empty = true;
        _sunImage = null;
        BOX();
    }

    /// <summary>BOX: draw the border box around the space view (using EOR logic).</summary>
    private void BOX()
    {
        YC = 1;
        XC = 1;
        const int colour = YELLOW;
        _screen.DrawLine(0, 0, 255, 0, colour);
        _screen.DrawLine(1, 0, 1, 2 * CentreY - 1, colour);
        _screen.DrawLine(0, 0, 0, 2 * CentreY - 1, colour);
        _screen.DrawLine(255, 0, 255, 2 * CentreY - 1, colour);
        _screen.DrawLine(254, 0, 254, 2 * CentreY - 1, colour);
    }

    /// <summary>CLYNS: clear the bottom three text rows of the space view.</summary>
    private void CLYNS()
    {
        DLY = 0;
        de = 0;
        DTW2 = 0xFF;
        QQ17 = 0x80;
        YC = 20;
        TT67K();
        _screen.ClearRows(21 * 8, 23 * 8 + 7);
    }

    // ------------------------------------------------------------------------
    // In-flight messages
    // ------------------------------------------------------------------------

    /// <summary>MESS: display an in-flight message in capitals at the bottom of the space view.</summary>
    private void MESS(int a)
    {
        while (true)
        {
            if (QQ11 != 0)
            {
                CLYNS();
            }

            // infrontvw
            YC = 21;
            COL = YELLOW;
            QQ17 = 0;
            XC = messXC;

            if (DLY != 0)
            {
                // me1: erase the existing message by printing it again
                DLY = 0;
                COL = YELLOW;
                mes9(MCH);
                continue;
            }

            DLY = 20;
            MCH = a;

            // Work out the length of the message so we can centre it
            DTW4 = 0b11000000;
            DTW5 = (de & 1) != 0 ? 10 : 0;
            TT27(MCH);
            messXC = (32 - DTW5) >> 1;
            XC = messXC;
            MT15();
            mes9(MCH);
            return;
        }
    }

    /// <summary>mes9: print a message token, followed by " DESTROYED" if bit 0 of de is set.</summary>
    private void mes9(int a)
    {
        TT27(a);
        bool destroyed = (de & 1) != 0;
        de >>= 1;
        if (destroyed)
        {
            TT27(253);
        }
    }

    /// <summary>me2: remove an in-flight message from the space view.</summary>
    private void me2()
    {
        if (QQ11 != 0)
        {
            // clynsneed
            CLYNS();
            return;
        }

        MESS(MCH);
        DLY = 0;
    }

    /// <summary>OUCH: potentially lose cargo or equipment following damage.</summary>
    private void OUCH()
    {
        int a = DORND();
        int x = _randX;
        if ((a & 0x80) != 0 || x >= 22)
        {
            return;
        }

        if (GetCargoOrEquipment(x) == 0 || DLY != 0)
        {
            return;
        }

        de = 3;
        SetCargoOrEquipment(x, 0);
        if (x < 17)
        {
            MESS(x + 208);
        }
        else if (x == 17)
        {
            // ou2
            MESS(108);
        }
        else if (x == 18)
        {
            // ou3
            MESS(111);
        }
        else
        {
            MESS(x + 113 - 20 + 1);
        }
    }

    /// <summary>The cargo hold and the equipment that follows it in memory (QQ20,X for X = 0-21).</summary>
    private int GetCargoOrEquipment(int x) => x switch
    {
        < 17 => QQ20[x],
        17 => ECM,
        18 => BST,
        19 => BOMB,
        20 => ENGY,
        _ => DKCMP,
    };

    private void SetCargoOrEquipment(int x, int value)
    {
        switch (x)
        {
            case < 17:
                QQ20[x] = value;
                break;
            case 17:
                ECM = value;
                break;
            case 18:
                BST = value;
                break;
            case 19:
                BOMB = value;
                break;
            case 20:
                ENGY = value;
                break;
            default:
                DKCMP = value;
                break;
        }
    }

    /// <summary>ee3: print the hyperspace countdown in the top-left of the screen.</summary>
    private void ee3(int x)
    {
        COL = RED;
        XC = 1;
        YC = 1;
        TT11(x & 0xFF, 3, false);
    }
}

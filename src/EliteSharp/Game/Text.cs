using EliteSharp.Data;
using EliteSharp.Game.Missions;

namespace EliteSharp.Game;

/// <summary>
/// Text: printing characters, the game's text (see Assets/Strings), the system
/// descriptions, numbers, justified text and in-flight messages.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>XC and YC: the text cursor.</summary>
    private int _cursorX = 1, _cursorY = 1;

    /// <summary>QQ17: the text case flags (bit 7 = Sentence Case, bit 6 = lower case next, &amp;FF = don't print).</summary>
    private int _textCase;

    // The flags used by the extended text token system

    /// <summary>
    /// DTW1: the mask for applying the lower case part of Sentence Case
    /// (%00100000 to apply lower case to the second letter of a word onwards,
    /// or 0 to leave the case alone).
    /// </summary>
    private int _lowerCaseMask = 0b00100000;

    /// <summary>DTW2: 0 if we are currently printing a word, non-zero if we are between words.</summary>
    private int _notPrintingWord = 0b11111111;

    /// <summary>
    /// DTW4: the justification flags (bit 7 set to justify text, and bit 6 set
    /// to buffer the text without printing it, as used to measure messages).
    /// </summary>
    private int _justifyFlags;

    /// <summary>DTW5: the number of characters in the justified text buffer at BUF.</summary>
    private int _lineBufferSize;

    /// <summary>DTW6: %10000000 if lower case is enabled for extended tokens, or 0 if it isn't.</summary>
    private int _lowerCaseEnabled;

    /// <summary>DTW8: the mask for capitalising the next letter (%11011111 to capitalise, %11111111 to leave it alone).</summary>
    private int _capitaliseMask = 0b11111111;

    /// <summary>DTW7: the character printed by MT16 (the drive number in the catalogue).</summary>
    private int _catalogueDriveCharacter = 'A';

    /// <summary>LL: the line length for justified text.</summary>
    private const int LineLength = 30;

    /// <summary>BUF: the line buffer for justified text.</summary>
    private readonly int[] _lineBuffer = new int[256];

    // ------------------------------------------------------------------------
    // Printing characters
    // ------------------------------------------------------------------------

    /// <summary>
    /// Whether text is being printed to erase it rather than show it. The
    /// original prints with EOR logic, and erases text (such as in-flight
    /// messages and the hyperspace countdown) by printing it again; this game
    /// does the same printing, so the cursor and the other state change just as
    /// they do in the original, but takes the characters out of the HUD.
    /// </summary>
    private bool _erasingText;

    /// <summary>Print something to erase it from the screen (see <see cref="_erasingText"/>).</summary>
    private void EraseText(Action print)
    {
        _erasingText = true;
        try
        {
            print();
        }
        finally
        {
            _erasingText = false;
        }
    }

    /// <summary>CHPR: print a character at the text cursor.</summary>
    private void PutCharacter(int character)
    {
        if (_textCase == 0xFF || character == 0 || character >= 128)
        {
            return;
        }

        if (character == 11)
        {
            // cls
            ClearSpaceView();
            return;
        }

        if (character == 7)
        {
            // R5
            Beep();
            return;
        }

        if (character < 32)
        {
            if (character != 10)
            {
                _cursorX = 1;
            }

            // RRX1
            if (character != 13)
            {
                _cursorY++;
            }

            return;
        }

        // RR1: in the catalogue, skip spaces at column 17
        if (_printingCatalogue != 0 && character == ' ' && _cursorX == 17)
        {
            return;
        }

        if (character == 127)
        {
            // Delete the character to the left of the cursor
            _cursorX--;
            _hud.EraseCharacter(_cursorX, _cursorY);
            return;
        }

        // RR2
        int column = _cursorX;
        _cursorX++;
        if (_cursorY >= 24)
        {
            ClearSpaceView();
            _cursorX = 1;
            _cursorY = 1;
            return;
        }

        // RR3
        if (_erasingText)
        {
            _hud.EraseCharacter(column, _cursorY, (char)character);
        }
        else
        {
            _hud.Print(column, _cursorY, (char)character, _colour);
        }
    }

    /// <summary>
    /// TT26 (DASC): print a character, taking into account justification and
    /// the in-flight message buffer.
    /// </summary>
    private void PrintCharacter(int character)
    {
        _capitaliseMask = 0xFF;
        _notPrintingWord = character is '.' or ':' or 10 or 12 or ' ' ? 0xFF : 0;

        if ((_justifyFlags & 0x80) == 0)
        {
            PutCharacter(character);
            return;
        }

        if ((_justifyFlags & 0x40) == 0 && character == 12)
        {
            PrintJustifiedLines();
            return;
        }

        _lineBuffer[_lineBufferSize] = character;
        _lineBufferSize = (_lineBufferSize + 1) & 0xFF;
    }

    /// <summary>DA1: print the contents of the line buffer, justifying it into lines of LL characters.</summary>
    private void PrintJustifiedLines()
    {
        int spacingPattern = 0;
        while (true)
        {
            // DA5
            int x = _lineBufferSize;
            if (x == 0)
            {
                // DA6+3
                _lineBufferSize = 0;
                PutCharacter(12);
                return;
            }

            if (x < LineLength + 1)
            {
                // DA6
                PrintBufferStart(x);
                _lineBufferSize = 0;
                PutCharacter(12);
                return;
            }

            // Justify the first line by inserting spaces until the character
            // at position LL is a space
            spacingPattern >>= 1;
            Justify(ref spacingPattern);

            // DA2: print the first line
            PrintBufferStart(LineLength);
            PutCharacter(12);

            // The C flag is clear from CHPR, so this is DTW5 - LL - 1
            int remaining = _lineBufferSize - LineLength - 1;
            _lineBufferSize = remaining & 0xFF;
            if (_lineBufferSize == 0)
            {
                PutCharacter(12);
                return;
            }

            for (int y = 0; y <= _lineBufferSize; y++)
            {
                _lineBuffer[y] = _lineBuffer[LineLength + 1 + y];
            }
        }
    }

    /// <summary>DA11 to DAL3: insert spaces into the buffer until BUF+LL is a space.</summary>
    private void Justify(ref int spacingPattern)
    {
        // The original loops forever if there are no spaces to expand, so we
        // give up after a while instead
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            // DA11
            if ((spacingPattern & 0x80) == 0)
            {
                spacingPattern = 0b01000000;
            }

            int y = LineLength - 1;
            while (true)
            {
                // DAL1
                if (_lineBuffer[LineLength] == ' ')
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

                    if (_lineBuffer[y] != ' ')
                    {
                        continue;
                    }

                    spacingPattern = (spacingPattern << 1) & 0xFF;
                    if ((spacingPattern & 0x80) != 0)
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
                int insertAt = y;
                for (int i = _lineBufferSize; i >= insertAt; i--)
                {
                    _lineBuffer[i + 1] = _lineBuffer[i];
                }

                _lineBufferSize++;

                // DAL3: skip back past any consecutive spaces
                y = insertAt - 1;
                while (y >= 0 && _lineBuffer[y] == ' ')
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
    private void PrintBufferStart(int x)
    {
        for (int y = 0; y < x; y++)
        {
            PutCharacter(_lineBuffer[y]);
        }
    }

    // ------------------------------------------------------------------------
    // Printing tokens (TT27)
    // ------------------------------------------------------------------------

    /// <summary>
    /// TT27: print a two-letter token (128-159, as in the system names), a
    /// control code or a character. The original's recursive tokens are now
    /// the game's text (see Assets/Strings), so there are none to print.
    /// </summary>
    private void PrintToken(int token)
    {
        token &= 0xFF;
        if (token == 0)
        {
            PrintCash();
            return;
        }

        if (token >= 128)
        {
            PrintTwoLetterToken(token);
            return;
        }

        switch (token)
        {
            case 1:
                PrintGalaxyNumber();
                return;
            case 2:
                PrintCurrentSystemName();
                return;
            case 3:
                PrintSystemName();
                return;
            case 4:
                PrintCommanderName();
                return;
            case 5:
                PrintFuelAndCash();
                return;
            case 6:
                _textCase = 0x80;
                return;
            case 7:
                // X is not zero here, so fall through to the character checks
                break;
            case 8:
                _textCase = 0;
                return;
            case 9:
                PrintColumnColon();
                return;
        }

        if (token >= 96 || (token >= 14 && token < 32))
        {
            throw new ArgumentOutOfRangeException(nameof(token), token, "The original's recursive tokens are now the game's text (see Assets/Strings)");
        }

        int textCase = _textCase;
        if (textCase == 0)
        {
            // TT74
            PrintCharacter(token);
            return;
        }

        if ((textCase & 0x80) != 0)
        {
            // TT41: Sentence Case
            if ((textCase & 0x40) != 0)
            {
                // TT45
                if (textCase == 0xFF)
                {
                    return;
                }

                if (token >= 'A')
                {
                    PrintLowerCase(token);
                    return;
                }

                // TT46
                _textCase = textCase & 0b10111111;
                PrintCharacter(token);
                return;
            }

            if (token < 'A')
            {
                PrintCharacter(token);
                return;
            }

            _textCase = textCase | 0b01000000;
            PrintCharacter(token);
            return;
        }

        if ((textCase & 0x40) != 0)
        {
            // TT46
            _textCase = textCase & 0b10111111;
            PrintCharacter(token);
            return;
        }

        PrintLowerCase(token);
    }

    /// <summary>TT42: print a letter in lower case.</summary>
    private void PrintLowerCase(int character)
    {
        if (character >= 'A' && character <= 'Z')
        {
            character += 32;
        }

        PrintCharacter(character);
    }

    /// <summary>TT43: print a two-letter token (128-159), the letters of part of a system's name (see <see cref="SystemNames"/>).</summary>
    private void PrintTwoLetterToken(int token)
    {
        if (token >= 160)
        {
            throw new ArgumentOutOfRangeException(nameof(token), token, "The original's recursive tokens are now the game's text (see Assets/Strings)");
        }

        foreach (char letter in SystemNames.LetterPairs[token & 31])
        {
            PrintToken(letter);
        }
    }

    // ------------------------------------------------------------------------
    // Extended text (DETOK)
    // ------------------------------------------------------------------------

    /// <summary>
    /// DETOK2: print a character or control code of extended text (the game's
    /// text and the system descriptions). The original's extended tokens are
    /// now the system descriptions (see Assets/Strings), so there are none to
    /// print.
    /// </summary>
    private void PrintExtendedCharacter(int character)
    {
        if (character < 32)
        {
            ProcessControlCode(character);
            return;
        }

        // DT8
        if (character >= '[')
        {
            throw new ArgumentOutOfRangeException(nameof(character), character, "The original's extended tokens are now the system descriptions (see Assets/Strings)");
        }

        PrintLetter(character);
    }

    /// <summary>DTS: print a letter in the correct case.</summary>
    private void PrintLetter(int character)
    {
        if (character >= 'A')
        {
            if ((_lowerCaseEnabled & 0x80) != 0 || (_notPrintingWord & 0x80) == 0)
            {
                // DT10
                character |= _lowerCaseMask;
            }

            // DT5
            character &= _capitaliseMask;
        }

        // DT9
        PrintCharacter(character);
    }

    /// <summary>
    /// DT3: process a control code in the system descriptions (from the
    /// original's JMTB jump table). The original's other control codes
    /// position the text, clear the screen and so on in its fixed text, which
    /// the code that prints the game's text does itself.
    /// </summary>
    private void ProcessControlCode(int code)
    {
        switch (code)
        {
            case 1:
                SetAllCaps();
                break;
            case 2:
                SetSentenceCase();
                break;
            case 3:
                PrintSystemName();
                break;
            case 13:
                SetLowerCase();
                break;
            case 14:
                SetJustified();
                break;
            case 15:
                SetLeftAligned();
                break;
            case 17:
                PrintSystemAdjective();
                break;
            case 18:
                PrintRandomWord();
                break;
            case 19:
                // MT19: a capital
                _capitaliseMask = 0b11011111;
                break;
            default:
                // 7, 10 and 12 print the character
                PrintCharacter(code);
                break;
        }
    }

    /// <summary>MT1: switch to ALL CAPS.</summary>
    private void SetAllCaps()
    {
        _lowerCaseMask = 0;
        _lowerCaseEnabled = 0;
    }

    /// <summary>MT2: switch to Sentence Case.</summary>
    private void SetSentenceCase()
    {
        _lowerCaseMask = 0b00100000;
        _lowerCaseEnabled = 0;
    }

    /// <summary>MT8: move the text cursor to column 6, between words.</summary>
    private void MoveToColumn6()
    {
        _cursorX = 6;
        _notPrintingWord = 0xFF;
    }

    /// <summary>MT13: switch to lower case.</summary>
    private void SetLowerCase()
    {
        _lowerCaseEnabled = 0x80;
        _lowerCaseMask = 0b00100000;
    }

    /// <summary>MT14: switch to justified text.</summary>
    private void SetJustified()
    {
        _justifyFlags = 0x80;
        _lineBufferSize = 0;
    }

    /// <summary>MT15: switch to left-aligned text.</summary>
    private void SetLeftAligned()
    {
        _justifyFlags = 0;
        _lineBufferSize = 0;
    }

    /// <summary>MT17: print the selected system's adjective (e.g. "Lavian").</summary>
    private void PrintSystemAdjective()
    {
        _textCase &= 0b10111111;
        PrintToken(3);
        int last = _lineBufferSize > 0 ? _lineBuffer[_lineBufferSize - 1] : 0;
        if (IsVowel(last))
        {
            _lineBufferSize--;
        }

        // MT171
        PrintDescriptionText(_descriptions.AdjectiveSuffix);
    }

    /// <summary>
    /// MT18: print a random 1-8 letter word. The C flag is clear when DT3
    /// calls this, the first letter pair's random number gets the C flag from
    /// the random number before it, and the other pairs' random numbers get
    /// it clear, as TT26 clears it after printing each letter.
    /// </summary>
    private void PrintRandomWord()
    {
        _capitaliseMask = 0b11011111;
        int pairs = NextRandomRepeatable() & 3;
        bool first = true;
        do
        {
            int index = (first ? NextRandom() : NextRandomRepeatable()) & 62;
            first = false;
            foreach (char letter in _descriptions.RandomWordPairs[index / 2])
            {
                PrintLetter(letter);
            }
            pairs--;
        }
        while (pairs >= 0);
    }

    /// <summary>VOWEL: returns true if the character is a vowel.</summary>
    private static bool IsVowel(int character)
    {
        character |= 0b00100000;
        return character is 'a' or 'e' or 'i' or 'o' or 'u';
    }

    /// <summary>MT23 and MT29: move to the given row, switch to cyan and lower case.</summary>
    private void MoveToRowInCyan(int row)
    {
        _cursorY = row;
        _colour = Cyan;
        _lowerCaseEnabled = 0x80;
        _lowerCaseMask = 0b00100000;
    }

    // ------------------------------------------------------------------------
    // System descriptions
    // ------------------------------------------------------------------------

    /// <summary>The text that the game generates for the systems, in the chosen language (see Assets/Strings).</summary>
    private readonly DescriptionGrammar _descriptions;

    /// <summary>Print some description text, as DETOK prints an extended token.</summary>
    private void PrintDescriptionText(DescriptionText text)
    {
        foreach (var part in text.Parts)
        {
            switch (part)
            {
                case DescriptionCharacter character:
                    PrintExtendedCharacter(character.Code);
                    break;
                case DescriptionReference reference:
                    PrintDescriptionRule(_descriptions.Rules[reference.Rule]);
                    break;
            }
        }
    }

    /// <summary>
    /// DT6: print a description rule: its text, or if it's a random rule, one
    /// of its choices, chosen as the original chooses a random token (with a
    /// random number drawn with the C flag clear, as DETOK2 only gets to DT6
    /// with it clear, so the choice doesn't depend on what used the random
    /// number generator before).
    /// </summary>
    private void PrintDescriptionRule(DescriptionRule rule)
    {
        int choice = rule.Random ? MissionRuntime.ChooseRandomly(rule.Choices.Count, NextRandomRepeatable()) : 0;
        PrintDescriptionText(rule.Choices[choice]);
    }

    /// <summary>Print a word of a species' name, as TT27 prints a token.</summary>
    private void PrintSpeciesWord(string word)
    {
        foreach (char c in word)
        {
            PrintToken(c);
        }
    }

    // ------------------------------------------------------------------------
    // Fixed text
    // ------------------------------------------------------------------------

    /// <summary>
    /// The game's fixed text, in the chosen language (see Assets/Strings). The
    /// original keeps this text in its token tables, along with the text that
    /// it generates (see <see cref="_descriptions"/>).
    /// </summary>
    private readonly GameStrings _strings;

    /// <summary>The market items, in the order of the market table (QQ23), which the original prints as tokens 208 onwards.</summary>
    private static readonly string[] CommodityKeys =
    [
        "commodities.food", "commodities.textiles", "commodities.radioactives", "commodities.slaves",
        "commodities.liquor_wines", "commodities.luxuries", "commodities.narcotics", "commodities.computers",
        "commodities.machinery", "commodities.alloys", "commodities.firearms", "commodities.furs",
        "commodities.minerals", "commodities.gold", "commodities.platinum", "commodities.gem_stones",
        "commodities.alien_items",
    ];

    /// <summary>The equipment on the Equip Ship screen, in the order of the price table (PRXS), which the original prints as tokens 105 onwards.</summary>
    private static readonly string[] EquipmentKeys =
    [
        "equipment.fuel", "equipment.missile", "equipment.large_cargo_bay", "equipment.ecm",
        "equipment.extra_pulse_lasers", "equipment.extra_beam_lasers", "equipment.fuel_scoops", "equipment.escape_pod",
        "equipment.energy_bomb", "equipment.energy_unit", "equipment.docking_computers", "equipment.galactic_hyperspace",
        "equipment.military_laser", "equipment.mining_laser",
    ];

    /// <summary>The space views (front, rear, left and right), which the original prints as tokens 96 onwards.</summary>
    private static readonly string[] ViewKeys = ["views.front", "views.rear", "views.left", "views.right"];

    /// <summary>
    /// Print a string of fixed text, as it is written, in the text case that
    /// the screen's text (TT27) is in: capitals if QQ17 is 0, and otherwise
    /// as written (see <see cref="PrintWrittenText"/>).
    /// </summary>
    private void PrintText(string key)
    {
        foreach (var part in _strings.GetParts(key))
        {
            switch (part)
            {
                case LiteralText literal:
                    PrintWrittenText(literal.Text);
                    break;
                case Placeholder placeholder:
                    PrintPlaceholder(placeholder.Name);
                    break;
            }
        }
    }

    /// <summary>
    /// Print a string of fixed text, as it is written, in the text case that
    /// the menus and prompts (DETOK) are in: capitals if DTW1 is 0, and
    /// otherwise as written (see <see cref="PrintWrittenExtendedText"/>).
    /// </summary>
    private void PrintExtendedText(string key)
    {
        foreach (var part in _strings.GetParts(key))
        {
            switch (part)
            {
                case LiteralText literal:
                    PrintWrittenExtendedText(literal.Text);
                    break;
                case Placeholder placeholder:
                    PrintPlaceholder(placeholder.Name);
                    break;
            }
        }
    }

    /// <summary>Print the value of a placeholder in the fixed text, such as {cash}.</summary>
    private void PrintPlaceholder(string name)
    {
        switch (name)
        {
            case "cash":
                PrintCash();
                break;
            case "galaxy":
                PrintGalaxyNumber();
                break;
            case "current_system":
                PrintCurrentSystemName();
                break;
            case "system":
                PrintSystemName();
                break;
            case "commander":
                PrintCommanderName();
                break;
            case "default_commander":
                // As it is, as the original prints it in capitals
                foreach (char c in DefaultCommander.Name)
                {
                    PrintCharacter(c);
                }

                break;
            case "drive":
                // MT16
                PrintCharacter(_catalogueDriveCharacter);
                break;
            default:
                throw new InvalidOperationException($"Unknown placeholder '{{{name}}}'");
        }
    }

    /// <summary>
    /// Print some text as it is written, following the text case in QQ17 as
    /// TT27 does: in capitals if QQ17 is 0 (or when it's printing the one
    /// letter that bit 6 on its own asks for), not at all if it's &amp;FF, in
    /// lower case for the other values without bit 7, and otherwise (in
    /// Sentence Case) as written. QQ17 changes just as it does when TT27
    /// prints the original's capitals, so the generated text that follows
    /// (such as a system name) is printed in the same case as before. If the
    /// text before ended in the middle of a word (bit 6 of QQ17), the text
    /// carries on that word, as it does in the original, so its first letter
    /// is in lower case.
    /// </summary>
    private void PrintWrittenText(string text)
    {
        bool carryingOnWord = _textCase != 0xFF && (_textCase & 0xC0) == 0xC0;
        foreach (char c in text)
        {
            int character = c == '\n' ? 12 : c;
            int upper = char.ToUpperInvariant(c);
            bool letter = upper >= 'A';
            int textCase = _textCase;
            if (textCase == 0)
            {
                // TT74: capitals
                PrintCharacter(c == '\n' ? 12 : upper);
                continue;
            }

            if ((textCase & 0x80) != 0)
            {
                // TT41: Sentence Case
                if ((textCase & 0x40) != 0)
                {
                    // TT45: in a word
                    if (textCase == 0xFF)
                    {
                        continue;
                    }

                    if (letter)
                    {
                        PrintCharacter(carryingOnWord ? char.ToLowerInvariant(c) : character);
                        carryingOnWord = false;
                        continue;
                    }

                    // TT46: the end of a word
                    _textCase = textCase & 0b10111111;
                    carryingOnWord = false;
                    PrintCharacter(character);
                    continue;
                }

                if (letter)
                {
                    _textCase = textCase | 0b01000000;
                }

                carryingOnWord = false;
                PrintCharacter(character);
                continue;
            }

            if ((textCase & 0x40) != 0)
            {
                // TT46: one letter as it is, and then capitals
                _textCase = textCase & 0b10111111;
                PrintCharacter(c == '\n' ? 12 : upper);
                continue;
            }

            // TT42: lower case
            PrintCharacter(c == '\n' ? 12 : char.ToLowerInvariant(c));
        }
    }

    /// <summary>
    /// Print some text as it is written, following the text case that DTS
    /// applies to the original's capitals: in capitals if DTW1 is 0, and
    /// otherwise as written, except for a letter that starts one of the text's
    /// words, which is in lower case if DTW6 has turned lower case on, or if
    /// the text before ended in the middle of a word (DTW2), so the text
    /// carries on that word, as it does in the original. A capital in the
    /// middle of a word (such as the C in "(C)", where only spaces, full stops,
    /// colons and newlines end words) is always a capital, as it is where the
    /// original asks for one (MT19), and so is a letter that DTW8 asks for.
    /// </summary>
    private void PrintWrittenExtendedText(string text)
    {
        bool startOfWord = true;
        foreach (char c in text)
        {
            int character = c == '\n' ? 12 : c;
            if (char.ToUpperInvariant(c) >= 'A')
            {
                if (_lowerCaseMask == 0)
                {
                    character = char.ToUpperInvariant(c);
                }
                else if (startOfWord && ((_lowerCaseEnabled & 0x80) != 0 || (_notPrintingWord & 0x80) == 0))
                {
                    character = char.ToLowerInvariant(c);
                }

                if (_capitaliseMask != 0xFF)
                {
                    // DT5: a capital
                    character = char.ToUpperInvariant((char)character);
                }
            }

            PrintCharacter(character);
            startOfWord = c is ' ' or '.' or ':' or '\n';
        }
    }

    // ------------------------------------------------------------------------
    // Printing numbers
    // ------------------------------------------------------------------------

    /// <summary>
    /// BPRNT: print a number to a specific number of digits, right-aligned
    /// with leading spaces, optionally with a decimal point before the last
    /// digit.
    /// </summary>
    private void PrintNumber(long number, int digits, bool decimalPoint)
    {
        int digitsBeforeForced = decimalPoint ? 10 : 11;
        int hiddenDigits = decimalPoint ? digits - 1 : digits;
        hiddenDigits = 12 - hiddenDigits;

        string text = (number % 1_000_000_000_000L).ToString("D12");
        for (int i = 0; i < 12; i++)
        {
            int digit = text[i] - '0';
            if (digit != 0 || digitsBeforeForced == 0)
            {
                // TT32
                digitsBeforeForced = 0;
                PrintCharacter('0' + digit);
            }
            else
            {
                hiddenDigits--;
                if (hiddenDigits < 0)
                {
                    PrintCharacter(' ');
                }
            }

            // TT34
            digitsBeforeForced = Math.Max(digitsBeforeForced - 1, 0);
            if (i == 10 && decimalPoint)
            {
                PrintCharacter('.');
            }
        }
    }

    /// <summary>TT11: print a 16-bit number to the given number of digits.</summary>
    private void PrintNumber16(int value, int digits, bool decimalPoint) => PrintNumber(value & 0xFFFF, digits, decimalPoint);

    /// <summary>pr2: print an 8-bit number to 3 digits.</summary>
    private void PrintNumber3(int value, bool decimalPoint = false) => PrintNumber16(value, 3, decimalPoint);

    /// <summary>pr5: print a 16-bit number to 5 digits.</summary>
    private void PrintNumber5(int value, bool decimalPoint) => PrintNumber16(value, 5, decimalPoint);

    /// <summary>pr6: print a 16-bit number to 5 digits, without a decimal point.</summary>
    private void PrintNumber5WithoutPoint(int value) => PrintNumber5(value, false);

    /// <summary>csh: print our cash with one decimal place, then " CR" and a newline.</summary>
    private void PrintCash()
    {
        PrintNumber(_cash, 9, true);
        PrintTextLine("market.credits");
    }

    /// <summary>tal: print the galaxy number.</summary>
    private void PrintGalaxyNumber() => PrintNumber3(_galaxyNumber + 1);

    /// <summary>ypl: print the current system's name.</summary>
    private void PrintCurrentSystemName()
    {
        if ((_inWitchspace & 0x80) != 0)
        {
            return;
        }

        SwapSystemSeeds();
        PrintSystemName();
        SwapSystemSeeds();
    }

    /// <summary>TT62: swap the current system's seeds with the selected system's seeds.</summary>
    private void SwapSystemSeeds()
    {
        for (int x = 5; x >= 0; x--)
        {
            (_currentSystemSeeds[x], _selectedSeeds[x]) = (_selectedSeeds[x], _currentSystemSeeds[x]);
        }
    }

    /// <summary>cmn: print the commander's name.</summary>
    private void PrintCommanderName()
    {
        foreach (char c in CommanderName)
        {
            PrintCharacter(c);
        }
    }

    /// <summary>fwl: print fuel and cash levels.</summary>
    private void PrintFuelAndCash()
    {
        PrintTextColon("equipment.fuel");
        PrintNumber3(_fuel, true);
        PrintTextLine("system_data.light_years");

        // PCASH
        PrintText("market.cash_balance");
    }

    /// <summary>crlf: tab to column 21 and print a colon.</summary>
    private void PrintColumnColon()
    {
        _cursorX = 21;
        PrintColon();
    }

    /// <summary>plf: print a string of fixed text followed by a newline.</summary>
    private void PrintTextLine(string key)
    {
        PrintText(key);
        PrintNewline();
    }

    /// <summary>plf2: print a string of fixed text followed by a newline, and indent the next line to column 6.</summary>
    private void PrintTextLineIndented(string key)
    {
        PrintTextLine(key);
        _cursorX = 6;
    }

    /// <summary>TT68: print a string of fixed text followed by a colon.</summary>
    private void PrintTextColon(string key)
    {
        PrintText(key);
        PrintColon();
    }

    /// <summary>TT73: print a colon.</summary>
    private void PrintColon() => PrintToken(':');

    /// <summary>TT162: print a space.</summary>
    private void PrintSpace() => PrintToken(' ');

    /// <summary>TT67: print a newline.</summary>
    private void PrintNewline() => PrintToken(12);

    /// <summary>TT67K: print a newline using CHPR directly.</summary>
    private void PutNewline() => PutCharacter(12);

    /// <summary>TT69: switch to Sentence Case and print a newline.</summary>
    private void PrintSentenceNewline()
    {
        _textCase = 0x80;
        PrintNewline();
    }

    /// <summary>TTX69: print a paragraph break (a blank line) in Sentence Case.</summary>
    private void PrintParagraphBreak()
    {
        _cursorY++;
        PrintSentenceNewline();
    }

    /// <summary>TT60: print a token and a paragraph break.</summary>
    private void PrintTokenParagraph(int token)
    {
        PrintToken(token);
        PrintParagraphBreak();
    }

    /// <summary>TT60: print a string of fixed text and a paragraph break.</summary>
    private void PrintTextParagraph(string key)
    {
        PrintText(key);
        PrintParagraphBreak();
    }

    /// <summary>spc: print a token followed by a space.</summary>
    private void PrintTokenSpace(int token)
    {
        PrintToken(token);
        PrintSpace();
    }

    /// <summary>spc: print a string of fixed text followed by a space.</summary>
    private void PrintTextSpace(string key)
    {
        PrintText(key);
        PrintSpace();
    }

    /// <summary>Print "(Y/N)?", in capitals.</summary>
    private void PrintYesNo()
    {
        SetAllCaps();
        PrintExtendedText("prompts.yes_no");
    }

    /// <summary>prq: print a string of fixed text followed by a question mark.</summary>
    private void PrintTextQuestion(string key)
    {
        PrintText(key);
        PrintToken('?');
    }

    /// <summary>NLIN3: print a title and draw a horizontal line at row 19.</summary>
    private void PrintTitle(string key)
    {
        PrintText(key);
        DrawTitleLine();
    }

    /// <summary>NLIN4: draw a horizontal line at pixel row 19.</summary>
    private void DrawTitleLine() => DrawHorizontalLine(19);

    /// <summary>NLIN: draw a horizontal line at pixel row 23 and move the text cursor down a line.</summary>
    private void DrawTitleLineAndNewline() => NewlineAndDrawLine(23);

    /// <summary>NLIN5: move the text cursor down a line and draw a horizontal line at the given row.</summary>
    private void NewlineAndDrawLine(int row)
    {
        _cursorY++;
        DrawHorizontalLine(row);
    }

    /// <summary>NLIN2: draw a horizontal yellow line across the screen at the given pixel row.</summary>
    private void DrawHorizontalLine(int row)
    {
        _colour = Yellow;
        DrawHorizontalSegment(2, 254, row);
        _colour = Cyan;
    }

    /// <summary>HLOIN3: draw a horizontal line from x1 to x2 - 1 in the current colour.</summary>
    private void DrawHorizontalSegment(int x1, int x2, int y)
    {
        if (x1 == x2)
        {
            return;
        }

        if (x1 > x2)
        {
            (x1, x2) = (x2, x1);
        }

        _hud.DrawLine(x1, y, x2 - 1, y, _colour);
    }

    /// <summary>LOIN: draw a line in the current colour.</summary>
    private void DrawLine(int x1, int y1, int x2, int y2) => _hud.DrawLine(x1, y1, x2, y2, _colour);

    /// <summary>BELL: make a beep.</summary>
    private void Bell() => PutCharacter(7);

    // ------------------------------------------------------------------------
    // Screen clearing
    // ------------------------------------------------------------------------

    /// <summary>TT66: clear the screen and set the current view type in QQ11.</summary>
    private void ClearScreen(int viewType)
    {
        _viewType = viewType;
        ClearScreenAndShowView();
    }

    /// <summary>TTX66K: clear the screen, draw a border box, and print the view name if this is a space view.</summary>
    private void ClearScreenAndShowView()
    {
        ClearSpaceView();
        SetSentenceCase();
        _textCase = 0x80;
        _notPrintingWord = 0x80;
        ResetSunLines();
        _laserBeamPower = 0;
        _messageDelay = 0;
        _messageDestroyed = 0;
        if (_hyperspaceCountdown != 0)
        {
            PrintHyperspaceCountdown(_hyperspaceCountdown);
        }

        if (_viewType == 0)
        {
            _cursorX = 11;
            _colour = Cyan;
            PrintText(ViewKeys[_view]);
            PrintSpace();
            PrintText("views.view");
        }

        // tt66
        _textCase = 0;
    }

    /// <summary>TTX66: clear the top part of the screen and draw a border box.</summary>
    private void ClearSpaceView()
    {
        _hud.ClearSpaceView();
        _world.Clear();
        DrawBorderBox();
    }

    /// <summary>BOX: draw the border box around the space view.</summary>
    private void DrawBorderBox()
    {
        _cursorY = 1;
        _cursorX = 1;
        _hud.Border = true;
    }

    /// <summary>CLYNS: clear the bottom three text rows of the space view.</summary>
    private void ClearBottomRows()
    {
        _messageDelay = 0;
        _messageDestroyed = 0;
        _notPrintingWord = 0xFF;
        _textCase = 0x80;
        _cursorY = 20;
        PutNewline();
        _hud.ClearRows(21 * 8, 23 * 8 + 7);
    }

    // ------------------------------------------------------------------------
    // In-flight messages
    // ------------------------------------------------------------------------

    /// <summary>MESS: display an in-flight message in capitals at the bottom of the space view.</summary>
    private void ShowMessage(string key)
    {
        while (true)
        {
            if (_viewType != 0)
            {
                ClearBottomRows();
            }

            // infrontvw
            _cursorY = 21;
            _colour = Yellow;
            _textCase = 0;
            _cursorX = _messageX;

            if (_messageDelay != 0)
            {
                // me1: erase the existing message by printing it again
                _messageDelay = 0;
                _colour = Yellow;
                EraseText(() => PrintMessage(_messageKey));
                continue;
            }

            _messageDelay = 20;
            _messageKey = key;

            // Work out the length of the message so we can centre it
            _justifyFlags = 0b11000000;
            _lineBufferSize = (_messageDestroyed & 1) != 0 ? 10 : 0;
            PrintText(_messageKey);
            _messageX = (32 - _lineBufferSize) >> 1;
            _cursorX = _messageX;
            SetLeftAligned();
            PrintMessage(_messageKey);
            return;
        }
    }

    /// <summary>mes9: print a message, followed by " DESTROYED" if bit 0 of de is set.</summary>
    private void PrintMessage(string key)
    {
        PrintText(key);
        bool destroyed = (_messageDestroyed & 1) != 0;
        _messageDestroyed >>= 1;
        if (destroyed)
        {
            PrintText("messages.destroyed");
        }
    }

    /// <summary>me2: remove an in-flight message from the space view.</summary>
    private void RemoveMessage()
    {
        if (_viewType != 0)
        {
            // clynsneed
            ClearBottomRows();
            return;
        }

        // The message is erased by showing it again
        EraseText(() => ShowMessage(_messageKey));
        _messageDelay = 0;
    }

    /// <summary>OUCH: potentially lose cargo or equipment following damage.</summary>
    private void LoseCargoOrEquipment()
    {
        int random = NextRandom();
        int item = _randomX;
        if ((random & 0x80) != 0 || item >= 22)
        {
            return;
        }

        if (GetCargoOrEquipment(item) == 0 || _messageDelay != 0)
        {
            return;
        }

        _messageDestroyed = 3;
        SetCargoOrEquipment(item, 0);
        if (item < 17)
        {
            ShowMessage(CommodityKeys[item]);
        }
        else if (item == 17)
        {
            // ou2
            ShowMessage("equipment.ecm");
        }
        else if (item == 18)
        {
            // ou3
            ShowMessage("equipment.fuel_scoops");
        }
        else
        {
            // The energy bomb, energy unit and docking computers (tokens 113-115)
            ShowMessage(EquipmentKeys[item - 11]);
        }
    }

    /// <summary>The cargo hold and the equipment that follows it in memory (QQ20,X for X = 0-21).</summary>
    private int GetCargoOrEquipment(int item) => item switch
    {
        < 17 => _cargo[item],
        17 => _ecm,
        18 => _fuelScoops,
        19 => _energyBomb,
        20 => _energyUnit,
        _ => _dockingComputer,
    };

    /// <summary>Set an item in the cargo hold, or an item of equipment (see <see cref="GetCargoOrEquipment"/>).</summary>
    private void SetCargoOrEquipment(int item, int value)
    {
        switch (item)
        {
            case < 17:
                _cargo[item] = value;
                break;
            case 17:
                _ecm = value;
                break;
            case 18:
                _fuelScoops = value;
                break;
            case 19:
                _energyBomb = value;
                break;
            case 20:
                _energyUnit = value;
                break;
            default:
                _dockingComputer = value;
                break;
        }
    }

    /// <summary>ee3: print the hyperspace countdown in the top-left of the screen.</summary>
    private void PrintHyperspaceCountdown(int countdown)
    {
        _colour = Red;
        _cursorX = 1;
        _cursorY = 1;
        PrintNumber16(countdown & 0xFF, 3, false);
    }
}

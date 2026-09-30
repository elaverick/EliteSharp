using EliteSharp.Data;
using EliteSharp.Rendering;

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
    private void SetSpacePalette(SpacePalette palette) => _hud.Palette = palette;

    /// <summary>TRADEMODE: clear the screen and set up a trading screen with the given view type.</summary>
    private void ShowTradingScreen(int view)
    {
        ClearScreen(view);
        SetTradingPalette();
    }

    /// <summary>TRADEMODE2: switch to the trading screen palette and cyan text.</summary>
    private void SetTradingPalette()
    {
        SetSpacePalette(SpacePalette.Trade);
        _colour = Cyan;
    }

    // ------------------------------------------------------------------------
    // Status Mode
    // ------------------------------------------------------------------------

    /// <summary>STATUS: show the Status Mode screen (red key f8).</summary>
    private void ShowStatus()
    {
        ShowTradingScreen(8);
        SelectNearestSystem();
        _cursorX = 7;
        PrintTitle(126);

        if (_docked != 0)
        {
            // wearedocked
            PrintExtendedToken(205);
            PutNewline();
        }
        else
        {
            int conditionToken = 230;
            if (SlotType(2 + _junkCount) != 0)
            {
                conditionToken += 1 + (_energy >= 128 ? 1 : 0);
            }

            // st6
            PrintTokenLine(conditionToken);
        }

        PrintTokenSpace(125);

        int legal = 19;
        if (_legalStatus != 0)
        {
            legal += 1 + (_legalStatus >= 50 ? 1 : 0);
        }

        // st5
        PrintTokenLine(legal);
        PrintTokenSpace(16);

        int rating;
        int tallyHi = _killTally >> 8;
        if (tallyHi != 0)
        {
            // st4
            rating = tallyHi >= 25 ? 9 : tallyHi >= 10 ? 8 : tallyHi >= 2 ? 7 : 6;
        }
        else
        {
            rating = 0;
            int conditionToken = (_killTally & 0xFF) >> 2;
            do
            {
                rating++;
                conditionToken >>= 1;
            }
            while (conditionToken != 0);
        }

        // st3
        PrintTokenLine(rating + 21);
        PrintTokenLineIndented(18);

        if (_escapePod != 0)
        {
            PrintTokenLineIndented(112);
        }

        if (_fuelScoops != 0)
        {
            PrintTokenLineIndented(111);
        }

        if (_ecm != 0)
        {
            PrintTokenLineIndented(108);
        }

        // stqv: the energy bomb, energy unit, docking computer and galactic hyperdrive
        int[] equipment = [_energyBomb, _energyUnit, _dockingComputer, _galacticHyperdrive];
        for (int i = 0; i < 4; i++)
        {
            if (equipment[i] != 0)
            {
                PrintTokenLineIndented(113 + i);
            }
        }

        for (int x = 0; x < 4; x++)
        {
            int laser = _lasers[x];
            if (laser == 0)
            {
                continue;
            }

            PrintTokenSpace(96 + x);
            int token = 103;
            if (laser == 128 + PulseLaserPower)
            {
                token = 104;
            }

            if (laser == MilitaryLaserPower)
            {
                token = 117;
            }

            if (laser == MiningLaserPower)
            {
                token = 118;
            }

            PrintTokenLineIndented(token);
        }
    }

    // ------------------------------------------------------------------------
    // Galaxy and system seeds
    // ------------------------------------------------------------------------

    /// <summary>TT81: set the selected system's seeds to those of system 0 in the current galaxy.</summary>
    private void SelectFirstSystem()
    {
        for (int x = 5; x >= 0; x--)
        {
            _selectedSeeds[x] = _galaxySeeds[x];
        }
    }

    /// <summary>TT20: twist the selected system's seeds four times, to move to the next system.</summary>
    private void NextSystem()
    {
        for (int i = 0; i < 4; i++)
        {
            TwistSeeds();
        }
    }

    /// <summary>TT54: twist the selected system's seeds once (s0 = s1, s1 = s2, s2 = s0 + s1 + s2).</summary>
    private void TwistSeeds()
    {
        int lo = _selectedSeeds[0] + _selectedSeeds[2];
        int hi = _selectedSeeds[1] + _selectedSeeds[3] + (lo > 0xFF ? 1 : 0);
        lo &= 0xFF;
        hi &= 0xFF;

        _selectedSeeds[0] = _selectedSeeds[2];
        _selectedSeeds[1] = _selectedSeeds[3];
        _selectedSeeds[3] = _selectedSeeds[5];
        _selectedSeeds[2] = _selectedSeeds[4];

        int newLo = lo + _selectedSeeds[2];
        _selectedSeeds[4] = newLo & 0xFF;
        _selectedSeeds[5] = (hi + _selectedSeeds[3] + (newLo > 0xFF ? 1 : 0)) & 0xFF;
    }

    /// <summary>TT24: calculate the selected system's economy, government, tech level, population and productivity.</summary>
    private void CalculateSystemData()
    {
        _selectedEconomy = _selectedSeeds[1] & 7;
        _selectedGovernment = (_selectedSeeds[2] >> 3) & 7;
        if ((_selectedGovernment >> 1) == 0)
        {
            _selectedEconomy |= 2;
        }

        // TT77
        int techLevel = (_selectedEconomy ^ 7) + (_selectedSeeds[3] & 3);
        techLevel += (_selectedGovernment >> 1) + (_selectedGovernment & 1);
        _selectedTechLevel = techLevel & 0xFF;
        _selectedPopulation = (_selectedTechLevel * 4 + _selectedEconomy + _selectedGovernment + 1) & 0xFF;

        int productivityFactor = ((_selectedEconomy ^ 7) + 3) * (_selectedGovernment + 4);
        int product = (productivityFactor & 0xFF) * _selectedPopulation;
        _selectedProductivity = (product * 8) & 0xFFFF;
    }

    /// <summary>
    /// TT111: select the system closest to galactic coordinates (QQ9, QQ10),
    /// and calculate its distance and data.
    /// </summary>
    private void SelectNearestSystem()
    {
        SelectFirstSystem();
        int best = 127;
        int systemIndex = 0;
        do
        {
            // TT130
            int xDistance = Math.Abs(_selectedSeeds[3] - _crosshairX) >> 1;
            int distance = (Math.Abs(_selectedSeeds[1] - _crosshairY) >> 1) + xDistance;
            if (distance < best)
            {
                best = distance;
                for (int x = 5; x >= 0; x--)
                {
                    _scratch[x] = _selectedSeeds[x];
                }

                SystemNumber = systemIndex;
            }

            // TT135
            NextSystem();
            systemIndex = (systemIndex + 1) & 0xFF;
        }
        while (systemIndex != 0);

        for (int x = 5; x >= 0; x--)
        {
            _selectedSeeds[x] = _scratch[x];
        }

        _crosshairY = _selectedSeeds[1];
        _crosshairX = _selectedSeeds[3];
        CalculateDistance(_crosshairX);
    }

    /// <summary>
    /// readdistnce: calculate the distance in QQ8 from the current system to
    /// the system at galactic x-coordinate A (and y-coordinate QQ15+1), and
    /// then calculate the system data with TT24.
    /// </summary>
    private void CalculateDistance(int systemX)
    {
        // The galaxy's y-coordinates are at twice the scale of its
        // x-coordinates, and each unit is 0.4 light years
        float dx = systemX - _currentSystemX;
        float dy = (_selectedSeeds[1] - _currentSystemY) / 2f;
        _selectedDistance = (int)(4 * MathF.Sqrt(dx * dx + dy * dy));
        CalculateSystemData();
    }

    /// <summary>cpl: print the selected system's name.</summary>
    private void PrintSystemName()
    {
        var saved = (int[])_selectedSeeds.Clone();
        int pairs = (_selectedSeeds[0] & 0x40) != 0 ? 3 : 2;
        for (int pair = pairs; pair >= 0; pair--)
        {
            // TT55
            int token = _selectedSeeds[5] & 31;
            if (token != 0)
            {
                PrintToken(token | 0x80);
            }

            TwistSeeds();
        }

        Array.Copy(saved, _selectedSeeds, 6);
    }

    // ------------------------------------------------------------------------
    // Data on System
    // ------------------------------------------------------------------------

    /// <summary>TT25: show the Data on System screen (red key f6).</summary>
    private void ShowSystemData()
    {
        ShowTradingScreen(1);
        _cursorX = 9;
        PrintTitle(163);
        PrintParagraphBreak();
        PrintDistance();

        PrintTokenColon(194);
        int economy = _selectedEconomy;
        if (((economy + 1) >> 1) == 2)
        {
            // TT70: economies 3 and 4 are "Mainly"
            PrintToken(173);
        }
        else
        {
            // TT71: the C flag from the CMP #2 is set for economies 5-7
            PrintToken((economy >= 5 ? economy - 5 : economy) + 170);
        }

        // TT72
        PrintTokenParagraph((_selectedEconomy >> 2) + 168);

        PrintTokenColon(162);
        PrintTokenParagraph(_selectedGovernment + 177);

        PrintTokenColon(196);
        PrintNumber3(_selectedTechLevel + 1);
        PrintParagraphBreak();

        PrintTokenColon(192);
        PrintNumber3(_selectedPopulation, true);
        PrintTokenParagraph(198);

        PrintToken('(');
        if ((_selectedSeeds[4] & 0x80) == 0)
        {
            PrintToken(188);
        }
        else
        {
            // TT75: the species description
            int b = _selectedSeeds[5] >> 2;
            if ((b & 7) < 3)
            {
                PrintTokenSpace((b & 7) + 227);
            }

            // TT205
            b >>= 3;
            if (b < 6)
            {
                PrintTokenSpace(b + 230);
            }

            // TT206
            int c = (_selectedSeeds[3] ^ _selectedSeeds[1]) & 7;
            _scratch[0] = c;
            if (c < 6)
            {
                PrintTokenSpace(c + 236);
            }

            // TT207
            int species = ((_selectedSeeds[5] & 3) + _scratch[0]) & 7;
            PrintToken(species + 242);
        }

        // TT76
        PrintToken('S');
        PrintTokenParagraph(')');

        PrintTokenColon(193);
        PrintNumber5WithoutPoint(_selectedProductivity);
        PrintSpace();
        _textCase = 0;
        PrintToken('M');
        PrintTokenParagraph(226);

        PrintTokenColon(250);
        int radius = (((_selectedSeeds[5] & 15) + 11) << 8) | _selectedSeeds[3];
        PrintNumber5(radius, false);
        PrintSpace();
        PrintCharacter('k');
        PrintCharacter('m');
        PrintParagraphBreak();

        PrintSystemDescription();
    }

    /// <summary>TT146: print the distance to the selected system, if it isn't the current system.</summary>
    private void PrintDistance()
    {
        if (_selectedDistance == 0)
        {
            _cursorY++;
            return;
        }

        // TT63
        PrintTokenColon(191);
        PrintNumber5(_selectedDistance, true);
        PrintTokenParagraph(195);
    }

    /// <summary>PDESC: print the system's extended description, or a mission's description (such as a clue in mission 1).</summary>
    private void PrintSystemDescription()
    {
        if (_selectedDistance == 0 && (_docked & 0x80) != 0)
        {
            // The missions' descriptions are in Assets/Missions
            if (_missions.TryShowSystemDescription(this, _galaxyNumber, SystemNumber))
            {
                return;
            }

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
                if ((galaxy & 0x7F) != _galaxyNumber)
                {
                    continue;
                }

                // PD3 (the original also keeps the mission 1 clues in these
                // tables, but those are now in the mission files)
                PrintExtendedCharacter(176);
                PrintDescriptionToken(y);
                PrintExtendedToken(177);
                return;
            }
        }

        // PD1: the "goat soup" description, seeded from the system's seeds
        for (int x = 3; x >= 0; x--)
        {
            _randomSeeds[x] = _selectedSeeds[2 + x];
        }

        PrintExtendedToken(5);
    }

    // ------------------------------------------------------------------------
    // Charts
    // ------------------------------------------------------------------------

    /// <summary>TT22: show the Long-range Chart (red key f4).</summary>
    private void ShowLongRangeChart()
    {
        ClearScreen(64);
        SetSpacePalette(SpacePalette.Chart);
        _colour = Cyan;
        _cursorX = 7;
        SelectFirstSystem();
        PrintToken(199);
        DrawTitleLineAndNewline();
        NewlineAndDrawLine(GalacticChartBottom + 1);
        DrawFuelRange();

        for (int system = 0; system < 256; system++)
        {
            // TT83: plot each system as a dot
            int dotX = _selectedSeeds[3];
            int dotSize = _selectedSeeds[4] | 0x50;
            int dotY = (_selectedSeeds[1] >> 1) + GalacticChartTop;
            foreach (var rect in PixelRects(dotX, dotY, dotSize, Yellow))
            {
                _hud.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Ink);
            }

            NextSystem();
        }

        _scratch[0] = _crosshairX;
        _scratch[1] = _crosshairY >> 1;
        _scratch[2] = 4;
        _colour = Green;
        DrawChartCrosshairs();
    }

    /// <summary>TT15: draw a set of crosshairs at (QQ19, QQ19+1) with size QQ19+2.</summary>
    private void DrawChartCrosshairs()
    {
        int offset = (_viewType & 0x80) != 0 ? 0 : GalacticChartTop;
        _scratch[5] = offset;

        int x1 = _scratch[0] - _scratch[2];
        if ((_viewType & 0x80) == 0)
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

        int x2 = _scratch[0] + _scratch[2];
        if (x2 > 0xFF || x2 >= 254)
        {
            x2 = 254;
        }

        int y = (_scratch[1] + offset) & 0xFF;
        DrawHorizontalSegment(x1, x2, y);

        int y1 = _scratch[1] - _scratch[2];
        if (y1 < 0)
        {
            y1 = 0;
        }

        y1 = (y1 + offset) & 0xFF;
        int y2 = _scratch[1] + _scratch[2] + offset;
        if (y2 >= GalacticChartBottom && (_viewType & 0x80) == 0)
        {
            y2 = GalacticChartBottom;
        }

        y2 &= 0xFF;
        DrawLine(_scratch[0], y1, _scratch[0], y2);
    }

    /// <summary>TT14: draw the fuel range circle and the crosshairs on the current system.</summary>
    private void DrawFuelRange()
    {
        if ((_viewType & 0x80) != 0)
        {
            // TT126: the short-range chart
            _scratch[0] = 104;
            _scratch[1] = 90;
            _scratch[2] = 16;
            _colour = Green;
            DrawChartCrosshairs();
            _circleRadius = _fuel;
            DrawChartCircle();
            return;
        }

        _circleRadius = _fuel >> 2;
        _scratch[0] = _currentSystemX;
        _scratch[1] = _currentSystemY >> 1;
        _scratch[2] = 7;
        _colour = Cyan;
        DrawChartCrosshairs();
        _scratch[1] += GalacticChartTop;
        DrawChartCircle();
    }

    /// <summary>TT128: draw a red circle of radius K centred on (QQ19, QQ19+1).</summary>
    private void DrawChartCircle()
    {
        _circleX = _scratch[0];
        _circleY = _scratch[1];
        _circleStep = 2;
        _colour = Red;
        _circleLines.Clear();
        DrawCircle();
        foreach (var line in _circleLines)
        {
            _hud.DrawLine(line.X1, line.Y1, line.X2, line.Y2, _colour);
        }
    }

    /// <summary>
    /// TT103: draw a small set of crosshairs on a chart at the selected system.
    /// The original draws these with EOR logic, and moves them by drawing them
    /// again (to erase them), moving the selection, and drawing them once more;
    /// here they are a group in the HUD, which is replaced each time it's drawn,
    /// so the crosshairs are simply wherever the selection is.
    /// </summary>
    private void DrawSmallCrosshairs()
    {
        using var group = _hud.Group(HudGroup.ChartSelection);
        _colour = Green;
        if ((_viewType & 0x80) != 0)
        {
            DrawShortRangeCrosshairs();
            return;
        }

        _scratch[0] = _crosshairX;
        _scratch[1] = _crosshairY >> 1;
        _scratch[2] = 4;
        DrawChartCrosshairs();
    }

    /// <summary>TT105: draw the crosshairs on the short-range chart, if the selected system is in range.</summary>
    private void DrawShortRangeCrosshairs()
    {
        int dx = (_crosshairX - _currentSystemX) & 0xFF;
        int adx = Math.Abs(_crosshairX - _currentSystemX) & 0xFF;
        if (adx >= 29)
        {
            return;
        }

        if ((dx & 0x80) != 0 && dx < 233)
        {
            return;
        }

        // TT179
        _scratch[0] = ((dx << 2) + 104) & 0xFF;

        int dy = (_crosshairY - _currentSystemY) & 0xFF;
        int ady = Math.Abs(_crosshairY - _currentSystemY) & 0xFF;
        if (ady >= 35)
        {
            return;
        }

        _scratch[1] = ((dy << 1) + 90) & 0xFF;
        _scratch[2] = 8;
        _colour = Green;
        DrawChartCrosshairs();
    }

    /// <summary>TT16: move the crosshairs on a chart by the given amounts.</summary>
    private void MoveCrosshairs(int dx, int dy)
    {
        WaitForVsync();
        DrawSmallCrosshairs();
        _crosshairY = AddClamped(_crosshairY, -dy);
        _scratch[1] = _crosshairY;
        _crosshairX = AddClamped(_crosshairX, dx);
        _scratch[0] = _crosshairX;
        DrawSmallCrosshairs();
    }

    /// <summary>TT123: add a delta to a coordinate, without going past 0 or 255.</summary>
    private static int AddClamped(int value, int delta)
    {
        int result = value + delta;
        return result is < 0 or > 255 ? value : result;
    }

    /// <summary>TT23: show the Short-range Chart (red key f5).</summary>
    private void ShowShortRangeChart()
    {
        ClearScreen(128);
        SetSpacePalette(SpacePalette.Chart);
        _colour = Cyan;
        _cursorX = 7;
        PrintTitle(190);
        DrawFuelRange();
        DrawSmallCrosshairs();
        SelectFirstSystem();
        _colour = Cyan;

        // Zero the label row table (INWK in the original)
        var rowsUsed = new bool[32];
        for (int count = 0; count < 256; count++)
        {
            // TT182
            int dx = (_selectedSeeds[3] - _currentSystemX) & 0xFF;
            int dy = (_selectedSeeds[1] - _currentSystemY) & 0xFF;
            if ((Math.Abs(_selectedSeeds[3] - _currentSystemX) & 0xFF) < 29 && (Math.Abs(_selectedSeeds[1] - _currentSystemY) & 0xFF) < 40)
            {
                // The ASL instructions leave bits 5 and 7 of the deltas in
                // the C flag, which is added in by the ADC
                int screenX = ((dx << 2) + 104 + ((dx >> 5) & 1)) & 0xFF;
                _cursorX = (screenX >> 3) + 1;
                int screenY = ((dy << 1) + 90 + ((dy >> 7) & 1)) & 0xFF;
                int textRow = screenY >> 3;

                // Find a row for the label
                int labelRow = -1;
                if (!rowsUsed[textRow])
                {
                    labelRow = textRow;
                }
                else if (textRow + 1 < 32 && !rowsUsed[textRow + 1])
                {
                    labelRow = textRow + 1;
                }
                else if (textRow > 0 && !rowsUsed[textRow - 1])
                {
                    labelRow = textRow - 1;
                }

                bool skip = false;
                if (labelRow >= 0)
                {
                    // EE4
                    _cursorY = labelRow;
                    if (labelRow < 3 || labelRow >= 21)
                    {
                        skip = true;
                    }
                    else
                    {
                        CalculateDistance(_selectedSeeds[3]);
                        if (_selectedDistance >= 70)
                        {
                            skip = true;
                        }
                        else
                        {
                            rowsUsed[labelRow] = true;
                            _textCase = 0x80;
                            PrintSystemName();
                        }
                    }
                }

                if (!skip)
                {
                    // ee1: draw the star
                    _circleX = screenX;
                    _circleY = screenY;
                    _circleRadius = (_selectedSeeds[5] & 1) + 2;
                    ResetSunLines();
                    _sunToCanvas = true;
                    DrawSun();
                    _sunToCanvas = false;
                    ResetSunLines();
                    _colour = Cyan;
                }
            }

            // TT187
            NextSystem();
        }
    }

    /// <summary>True if SUN should draw on the canvas (for the stars on the short-range chart).</summary>
    private bool _sunToCanvas;

    /// <summary>hm: move the chart crosshairs to the nearest system.</summary>
    private void MoveCrosshairsToNearestSystem()
    {
        DrawSmallCrosshairs();
        SelectNearestSystem();
        DrawSmallCrosshairs();
        ClearBottomRows();
    }

    /// <summary>HME2: search the galaxy for a system by name (the "F" key on the charts).</summary>
    private void FindSystem()
    {
        _colour = Cyan;
        PrintExtendedToken(14);
        DrawSmallCrosshairs();
        SelectFirstSystem();

        string wanted = _lastInput.ToUpperInvariant();
        for (int i = 0; i < 256; i++)
        {
            // HME3: compare the name with this system's name
            if (SystemName() == wanted && wanted.Length > 0)
            {
                // HME5
                _crosshairX = _selectedSeeds[3];
                _crosshairY = _selectedSeeds[1];
                SelectNearestSystem();
                DrawSmallCrosshairs();
                SetLeftAligned();
                PrintDistanceToSystem();
                return;
            }

            NextSystem();
        }

        SelectNearestSystem();
        DrawSmallCrosshairs();
        Boop();
        PrintExtendedToken(215);
    }

    /// <summary>The name of the selected system, in capitals.</summary>
    private string SystemName()
    {
        var name = new System.Text.StringBuilder();
        var saved = (int[])_selectedSeeds.Clone();
        int pairs = (_selectedSeeds[0] & 0x40) != 0 ? 3 : 2;
        for (int pair = pairs; pair >= 0; pair--)
        {
            int token = _selectedSeeds[5] & 31;
            if (token != 0)
            {
                int index = token << 1;
                name.Append((char)GameData.TwoLetterTokens[index]);
                if (GameData.TwoLetterTokens[index + 1] != '?')
                {
                    name.Append((char)GameData.TwoLetterTokens[index + 1]);
                }
            }

            TwistSeeds();
        }

        Array.Copy(saved, _selectedSeeds, 6);
        return name.ToString();
    }

    // ------------------------------------------------------------------------
    // Hyperspace
    // ------------------------------------------------------------------------

    /// <summary>hyp: start the hyperspace process (the "H" key).</summary>
    private void StartHyperspace()
    {
        if (_docked != 0)
        {
            // dockEd
            ClearBottomRows();
            _cursorX = 15;
            _colour = Red;
            PrintExtendedToken(205);
            return;
        }

        if (_hyperspaceCountdown != 0)
        {
            return;
        }

        _colour = Cyan;
        if ((CtrlPressed() & 0x80) != 0)
        {
            GalacticHyperspace();
            return;
        }

        if (_viewType == 0)
        {
            // TTX110
            SelectNearestSystem();
        }
        else
        {
            if ((_viewType & 0b11000000) == 0)
            {
                return;
            }

            MoveCrosshairsToNearestSystem();
        }

        // TTX111
        if (_selectedDistance == 0)
        {
            return;
        }

        for (int x = 5; x >= 0; x--)
        {
            _destinationSeeds[x] = _selectedSeeds[x];
        }

        _cursorX = 7;
        _cursorY = 22;
        _textCase = 0;
        PrintToken(189);
        if ((_selectedDistance >> 8) != 0 || _fuel < _selectedDistance)
        {
            // TT147
            PrintTokenQuestion(202);
            return;
        }

        PrintToken('-');
        PrintSystemName();

        // wW
        StartHyperspaceCountdown(15);
    }

    /// <summary>wW2: start the hyperspace countdown from the given value.</summary>
    private void StartHyperspaceCountdown(int countdown)
    {
        _hyperspaceCountdown = countdown;
        _hyperspaceTicks = countdown;
        PrintHyperspaceCountdown(countdown);
    }

    /// <summary>Ghy: perform a galactic hyperspace jump.</summary>
    private void GalacticHyperspace()
    {
        if (_galacticHyperdrive == 0)
        {
            return;
        }

        _galacticHyperdrive = 0;
        _legalStatus = 0;
        StartHyperspaceCountdown(2);
        _galaxyNumber = (_galaxyNumber + 1) & 0b11110111;

        // G1: rotate each seed byte left
        for (int x = 5; x >= 0; x--)
        {
            int value = _galaxySeeds[x];
            _galaxySeeds[x] = ((value << 1) | (value >> 7)) & 0xFF;
        }

        // zZ
        _crosshairX = 96;
        _crosshairY = 96;
        Launch();
        SelectNearestSystem();
        for (int x = 5; x >= 0; x--)
        {
            _destinationSeeds[x] = _selectedSeeds[x];
        }

        _selectedDistance = 0;
        ShowMessage(116);
        SetCurrentSystem();
    }

    // ------------------------------------------------------------------------
    // Buying and selling cargo
    // ------------------------------------------------------------------------

    /// <summary>TT163: print the headers for the market prices table.</summary>
    private void PrintMarketHeaders()
    {
        _cursorX = 17;
        PrintToken(255);
    }

    /// <summary>TT167: show the Market Price screen (red key f7).</summary>
    private void ShowMarketPrices()
    {
        ShowTradingScreen(16);
        _cursorX = 5;
        PrintTitle(167);
        _cursorY = 3;
        PrintMarketHeaders();
        _cursorY = 6;
        for (_itemNumber = 0; _itemNumber < 17; _itemNumber++)
        {
            _textCase = 0x80;
            PrintMarketItem(_itemNumber);
            _cursorY++;
        }
    }

    /// <summary>TT151: print the name, price and availability of a market item.</summary>
    private void PrintMarketItem(int item)
    {
        _scratch[4] = item;
        _scratch[0] = (item << 2) & 0xFF;
        if (_inWitchspace != 0)
        {
            return;
        }

        _cursorX = 1;
        PrintToken(item + 208);
        _cursorX = 14;

        int x = _scratch[0];
        _scratch[1] = GameData.MarketPrices[x + 1];
        _itemPrice = ((_marketRandom & GameData.MarketPrices[x + 3]) + GameData.MarketPrices[x]) & 0xFF;
        PrintUnits();
        CalculateEconomicFactor();

        if ((_scratch[1] & 0x80) != 0)
        {
            // TT155
            _itemPrice = (_itemPrice - _scratch[3]) & 0xFF;
        }
        else
        {
            _itemPrice = (_itemPrice + _scratch[3]) & 0xFF;
        }

        // TT156
        PrintNumber5((_itemPrice << 2) & 0xFFFF, true);

        int available = _marketAvailability[_scratch[4]];
        _itemAvailability = available;
        if (available == 0)
        {
            // TT172
            _cursorX = 25;
            PrintToken('-');
            return;
        }

        PrintNumber16(available, 5, false);
        PrintUnits();
    }

    /// <summary>TT152: print the units for the current market item ("t", "kg" or "g").</summary>
    private void PrintUnits()
    {
        int units = _scratch[1] & 96;
        if (units == 0)
        {
            // TT160
            PrintCharacter('t');
            PrintSpace();
        }
        else if (units == 32)
        {
            // TT161
            PrintCharacter('k');
            PrintCharacter('g');
        }
        else
        {
            PrintCharacter('g');
            PrintSpace();
        }
    }

    /// <summary>TT219: show the Buy Cargo screen (red key f1).</summary>
    private void ShowBuyCargo()
    {
        ShowTradingScreen(2);
        PrintMarketHeaders();
        _textCase = 0x80;
        _itemNumber = 0;

        while (true)
        {
            // TT220
            PrintMarketItem(_itemNumber);
            if (_itemAvailability != 0)
            {
                BuyItem();
            }

            // TT222
            _cursorY = _itemNumber + 5;
            _cursorX = 0;
            _itemNumber++;
            if (_itemNumber >= 17)
            {
                // BAY2
                throw new GameJumpException(GameJump.ForceKey, FunctionKey9);
            }
        }
    }

    /// <summary>TT224: ask how many of the current item to buy, and buy them.</summary>
    private void BuyItem()
    {
        while (true)
        {
            // TT224
            ClearBottomRows();
            PrintToken(204);
            PrintToken(_itemNumber + 208);
            PrintToken('/');
            PrintUnits();
            PrintToken('?');
            PrintNewline();

            var (quantity, error) = ReadNumber();
            if (error)
            {
                // TQ4
                AskQuestion(176);
                continue;
            }

            if (quantity != 0 && HasRoomInHold(quantity))
            {
                AskQuestion(206);
                continue;
            }

            if (!SpendCash(CalculateCost(quantity, _itemPrice)))
            {
                AskQuestion(197);
                continue;
            }

            _cargo[_itemNumber] = (_cargo[_itemNumber] + quantity) & 0xFF;
            _marketAvailability[_itemNumber] = (_marketAvailability[_itemNumber] - quantity) & 0xFF;
            if (quantity != 0)
            {
                PrintCashLeft();
            }

            return;
        }
    }

    /// <summary>Tc: print a space, a token and a question mark, and beep.</summary>
    private void AskQuestion(int token)
    {
        PrintSpace();
        PrintTokenQuestion(token);

        // TTX224
        BeepAndWait();
    }

    /// <summary>
    /// gnum: get a number from the keyboard, returning the number and whether
    /// the number was too large (the C flag).
    /// </summary>
    private (int Value, bool Error) ReadNumber()
    {
        _colour = Magenta;
        int number = 0;
        for (int digitsLeft = 12; digitsLeft > 0; digitsLeft--)
        {
            // TT223
            int key = WaitForKey();
            if (number == 0)
            {
                if (key == 'Y')
                {
                    // NWDAV1
                    PrintCharacter(key);
                    number = _itemAvailability;
                    _colour = Cyan;
                    return (number, false);
                }

                if (key == 'N')
                {
                    // NWDAV3
                    PrintCharacter(key);
                    _colour = Cyan;
                    return (0, false);
                }
            }

            // NWDAV2
            int digit = key - '0';
            if (digit < 0)
            {
                break;
            }

            if (digit >= 10)
            {
                // BAY2
                throw new GameJumpException(GameJump.ForceKey, FunctionKey9);
            }

            if (number >= 26)
            {
                return Reject(key);
            }

            int value = number * 10 + digit;
            if (value > 0xFF)
            {
                return Reject(key);
            }

            number = value;
            if (number > _itemAvailability)
            {
                return Reject(key);
            }

            // TT226
            PrintCharacter(key);
        }

        // OUT
        _colour = Cyan;
        return (number, false);

        (int, bool) Reject(int key)
        {
            PrintCharacter(key);
            _colour = Cyan;
            return (number, true);
        }
    }

    /// <summary>
    /// tnpr: work out whether there is room in the cargo hold for the given
    /// quantity of item QQ29, returning true (C set) if there isn't.
    /// </summary>
    private bool HasRoomInHold(int quantity)
    {
        if (_itemNumber > 12)
        {
            // kg: items measured in kg or g are limited to 200 each
            return quantity + _cargo[_itemNumber] >= 200;
        }

        // The C flag is set by the CPX, so the first addition adds an extra 1
        int total = quantity + 1;
        for (int x = 12; x >= 0; x--)
        {
            total += _cargo[x];
        }

        return total >= _cargoCapacity;
    }

    /// <summary>tnpr1: work out whether there is room for one tonne of the given item (setting QQ29).</summary>
    private bool HasRoomForOne(int item)
    {
        _itemNumber = item;
        return HasRoomInHold(1);
    }

    /// <summary>GCASH: calculate the cost of a quantity at a price (in Cr * 10), i.e. quantity * price * 4.</summary>
    private static int CalculateCost(int quantity, int price) => (quantity * price * 4) & 0xFFFF;

    /// <summary>TT208: show the Sell Cargo screen (red key f2).</summary>
    private void ShowSellCargo()
    {
        ShowTradingScreen(4);
        _cursorX = 10;
        PrintToken(205);
        PrintTitle(206);
        PrintNewline();
        ListCargo();
    }

    /// <summary>TT213: show the Inventory screen (red key f9).</summary>
    private void ShowInventory()
    {
        ShowTradingScreen(8);
        _cursorX = 11;
        PrintTokenParagraph(164);
        DrawTitleLine();
        PrintFuelAndCash();
        if (_cargoCapacity >= 26)
        {
            PrintToken(107);
        }

        ListCargo();
    }

    /// <summary>TT210: show a list of the cargo in the hold, and sell it if this is the Sell Cargo screen.</summary>
    private void ListCargo()
    {
        for (int item = 0; item < 17; item++)
        {
            // TT211
            _itemNumber = item;
            while (true)
            {
                // NWDAVxx
                int amount = _cargo[item];
                if (amount == 0)
                {
                    break;
                }

                _scratch[1] = GameData.MarketPrices[item * 4 + 1];
                PrintSentenceNewline();
                PrintToken(_itemNumber + 208);
                _cursorX = 14;
                _itemAvailability = amount;
                PrintNumber3(amount);
                PrintUnits();

                if (_viewType != 4)
                {
                    break;
                }

                // Sell this item?
                PrintToken(205);
                PrintExtendedToken(206);
                var (quantity, error) = ReadNumber();
                if (quantity == 0)
                {
                    break;
                }

                if (error)
                {
                    // NWDAV4
                    PrintNewline();
                    PrintTokenQuestion(176);
                    BeepAndWait();
                    continue;
                }

                // Work out the price without printing anything
                _textCase = 0xFF;
                PrintMarketItem(_itemNumber);
                _cargo[_itemNumber] = (_cargo[_itemNumber] - quantity) & 0xFF;
                AddCash(CalculateCost(quantity, _itemPrice));
                _textCase = 0;
                break;
            }
        }

        if (_viewType == 4)
        {
            BeepAndWait();
            throw new GameJumpException(GameJump.ForceKey, FunctionKey9);
        }

        PrintSentenceNewline();
    }

    // ------------------------------------------------------------------------
    // Buying equipment
    // ------------------------------------------------------------------------

    /// <summary>PRXS: the price of each item of equipment.</summary>
    private int ListedEquipmentPrice(int item) => GameData.EquipmentPrices[item * 2] | (GameData.EquipmentPrices[item * 2 + 1] << 8);

    /// <summary>The fuel price, which EQSHP stores in PRXS+0.</summary>
    private int _fuelPrice = 1;

    /// <summary>prx: the price of an item of equipment.</summary>
    private int EquipmentPrice(int item) => item == 0 ? _fuelPrice : ListedEquipmentPrice(item);

    /// <summary>EQSHP: show the Equip Ship screen (red key f3).</summary>
    private void ShowEquipShip()
    {
        while (true)
        {
            ShowTradingScreen(32);
            _cursorX = 12;
            PrintTokenSpace(207);
            PrintTitle(185);
            _textCase = 0x80;
            _cursorY++;

            int itemCount = _techLevel + 3;
            if (itemCount >= 12)
            {
                itemCount = 14;
            }

            _itemAvailability = itemCount;
            int itemLimit = itemCount + 1;
            _fuelPrice = ((70 - _fuel) << 1) & 0xFFFF;

            for (int number = 1; number < itemLimit; number++)
            {
                // EQL1
                PrintNewline();
                PrintNumber3(number);
                PrintSpace();
                PrintToken(number + 104);
                int price = EquipmentPrice(number - 1);
                _cursorX = 25;
                PrintNumber16(price, 6, true);
            }

            ClearBottomRows();
            PrintTokenQuestion(127);
            var (item, error) = ReadNumber();
            if (item == 0 || error)
            {
                GoToDockingBay();
            }

            item--;
            _cursorX = 2;
            _cursorY++;
            PayForEquipment(item);

            if (!BuyEquipment(item))
            {
                return;
            }

            // et11
            PrintCashLeft();
        }
    }

    /// <summary>
    /// Fit a newly bought item of equipment, returning false if the item was
    /// already present (in which case we have jumped to BAY).
    /// </summary>
    private bool BuyEquipment(int item)
    {
        switch (item)
        {
            case 0:
                _fuel = 70;
                return true;
            case 1:
                if (_missiles + 1 >= 5)
                {
                    Present(item, 124);
                }

                _missiles++;
                ResetMissileIndicators();
                return true;
            case 2:
                if (_cargoCapacity == 37)
                {
                    Present(item, 107);
                }

                _cargoCapacity = 37;
                return true;
            case 3:
                if (_ecm != 0)
                {
                    Present(item, 108);
                }

                _ecm = 0xFF;
                return true;
            case 4:
                Refund(AskForLaserView(), PulseLaserPower);
                return true;
            case 5:
                Refund(AskForLaserView(), PulseLaserPower + 128);
                return true;
            case 6:
                if (_fuelScoops != 0)
                {
                    Present(item, 111);
                }

                _fuelScoops = 0xFF;
                return true;
            case 7:
                if (_escapePod != 0)
                {
                    Present(item, 112);
                }

                _escapePod = 0xFF;
                return true;
            case 8:
                if (_energyBomb != 0)
                {
                    Present(item, 113);
                }

                _energyBomb = 0x7F;
                return true;
            case 9:
                if (_energyUnit != 0)
                {
                    Present(item, 114);
                }

                _energyUnit = 1;
                return true;
            case 10:
                if (_dockingComputer != 0)
                {
                    Present(item, 115);
                }

                _dockingComputer = 0xFF;
                return true;
            case 11:
                if (_galacticHyperdrive != 0)
                {
                    Present(item, 116);
                }

                _galacticHyperdrive = 0xFF;
                return true;
            case 12:
                Refund(AskForLaserView(), MilitaryLaserPower);
                return true;
            case 13:
                Refund(AskForLaserView(), MiningLaserPower);
                return true;
        }

        return true;
    }

    /// <summary>pres: the item is already fitted, so refund the price and say so.</summary>
    private void Present(int item, int token)
    {
        AddCash(EquipmentPrice(item));
        PrintTokenSpace(token);
        PrintToken(31);

        // err
        BeepAndWait();
        GoToDockingBay();
    }

    /// <summary>eq: subtract the price of an item from our cash, jumping to BAY if we can't afford it.</summary>
    private void PayForEquipment(int item)
    {
        if (SpendCash(EquipmentPrice(item)))
        {
            return;
        }

        PrintTokenQuestion(197);
        BeepAndWait();
        GoToDockingBay();
    }

    /// <summary>dn: print the amount of cash left, and beep.</summary>
    private void PrintCashLeft()
    {
        PrintSpace();
        PrintTokenSpace(119);
        BeepAndWait();
    }

    /// <summary>dn2: make a short, high beep and wait a moment.</summary>
    private void BeepAndWait()
    {
        Beep();
        Delay(25);
    }

    /// <summary>qv: ask which view to fit a laser to, returning the view number.</summary>
    private int AskForLaserView()
    {
        if (_techLevel >= 8)
        {
            ClearScreen(32);
        }

        int y = 16;
        _cursorY = y;
        do
        {
            // qv1
            _cursorX = 12;
            PrintTokenSpace(y + '0' - 16);
            PrintToken(_cursorY + 80);
            _cursorY++;
            y = _cursorY;
        }
        while (y < 20);

        ClearBottomRows();
        while (true)
        {
            // qv2
            PrintTokenQuestion(175);
            int view = WaitForKey() - '0';
            if (view >= 0 && view < 4)
            {
                return view;
            }

            ClearBottomRows();
        }
    }

    /// <summary>refund: fit a laser to the given view, refunding the price of any laser already there.</summary>
    private void Refund(int view, int laser)
    {
        int old = _lasers[view];
        if (old != 0)
        {
            int item = old switch
            {
                PulseLaserPower => 4,
                PulseLaserPower + 128 => 5,
                MilitaryLaserPower => 12,
                _ => 13,
            };

            AddCash(EquipmentPrice(item));
        }

        _lasers[view] = laser;
    }
}

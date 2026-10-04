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
        PrintStatusTitle();

        if (_docked != 0)
        {
            // wearedocked
            PrintExtendedText("status.docked");
            PutNewline();
        }
        else
        {
            string condition = "status.condition_green";
            if (SlotType(2 + _junkCount) != 0)
            {
                condition = _energy >= 128 ? "status.condition_yellow" : "status.condition_red";
            }

            // st6
            PrintTextLine(condition);
        }

        PrintLegalStatusLabel();

        string legal = "status.clean";
        if (_legalStatus != 0)
        {
            legal = _legalStatus >= 50 ? "status.fugitive" : "status.offender";
        }

        // st5
        PrintTextLine(legal);
        PrintTextSpace("status.rating");

        // st3
        PrintTextLine(RatingKeys[RatingFor(_killTally) - 1]);
        PrintEquipmentHeading();

        if (_escapePod != 0)
        {
            PrintTextLineIndented("equipment.escape_pod");
        }

        if (_fuelScoops != 0)
        {
            PrintTextLineIndented("equipment.fuel_scoops");
        }

        if (_ecm != 0)
        {
            PrintTextLineIndented("equipment.ecm");
        }

        // stqv: the energy bomb, energy unit, docking computer and galactic hyperdrive
        int[] equipment = [_energyBomb, _energyUnit, _dockingComputer, _galacticHyperdrive];
        string[] equipmentKeys = ["equipment.energy_bomb", "equipment.energy_unit", "equipment.docking_computers", "equipment.galactic_hyperspace"];
        for (int i = 0; i < 4; i++)
        {
            if (equipment[i] != 0)
            {
                PrintTextLineIndented(equipmentKeys[i]);
            }
        }

        for (int x = 0; x < 4; x++)
        {
            int laser = _lasers[x];
            if (laser == 0)
            {
                continue;
            }

            PrintTextSpace(ViewKeys[x]);
            string name = "equipment.pulse_laser";
            if (laser == 128 + PulseLaserPower)
            {
                name = "equipment.beam_laser";
            }

            if (laser == MilitaryLaserPower)
            {
                name = "equipment.military_laser";
            }

            if (laser == MiningLaserPower)
            {
                name = "equipment.mining_laser";
            }

            PrintTextLineIndented(name);
        }

        if ((_docked & 0x80) != 0 && _cursorY <= 23)
        {
            // Not in the original: the keys for saving and loading, which
            // work on this screen while we're docked (shown on the bottom
            // row, unless the equipment reaches down to it), centred
            string help = _strings.Get("status.save_load");
            _cursorX = Math.Max(1, (32 - help.Length) / 2);
            _cursorY = 23;
            PrintRaw(help);
        }
    }

    /// <summary>
    /// Print the Status screen's title: the commander's name, then the present
    /// and hyperspace systems and the label for the condition, each with a
    /// colon in column 21, and the line under the title.
    /// </summary>
    private void PrintStatusTitle()
    {
        PrintText("status.commander");
        PrintNewline();
        PrintNewline();
        PrintNewline();
        _textCase = 0x80;
        PrintText("status.present_system");
        PrintColumnColon();
        PrintCurrentSystemName();
        PrintNewline();
        PrintText("status.hyperspace_system");
        PrintColumnColon();
        PrintSystemName();
        PrintNewline();
        PrintText("status.condition");
        PrintColumnColon();
        DrawTitleLine();
    }

    /// <summary>Print our fuel and cash, and the label for our legal status.</summary>
    private void PrintLegalStatusLabel()
    {
        PrintFuelAndCash();
        PrintTextSpace("status.legal_status");
    }

    /// <summary>Print the heading of the equipment list, in capitals, and indent the next line.</summary>
    private void PrintEquipmentHeading()
    {
        PrintNewline();
        _textCase = 0;
        PrintText("status.equipment");
        _textCase = 0x80;
        PrintNewline();
        _cursorX = 6;
    }

    /// <summary>The combat rating for a kill tally, from Harmless (1) to Elite (9).</summary>
    private static int RatingFor(int killTally)
    {
        int tallyHi = killTally >> 8;
        if (tallyHi != 0)
        {
            // st4
            return tallyHi >= 25 ? 9 : tallyHi >= 10 ? 8 : tallyHi >= 2 ? 7 : 6;
        }

        int rating = 0;
        int conditionToken = (killTally & 0xFF) >> 2;
        do
        {
            rating++;
            conditionToken >>= 1;
        }
        while (conditionToken != 0);

        return rating;
    }

    /// <summary>The combat ratings, from Harmless (1) to Elite (9), which the original prints as tokens 22 onwards.</summary>
    private static readonly string[] RatingKeys =
    [
        "ratings.harmless", "ratings.mostly_harmless", "ratings.poor", "ratings.average", "ratings.above_average",
        "ratings.competent", "ratings.dangerous", "ratings.deadly", "ratings.elite",
    ];

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
        // x-coordinates, and each unit is 0.4 light years. As in the
        // original, the y difference is halved and the square root rounded
        // down before multiplying by 4, so the distances (and so which
        // systems are within range) are the original's
        int dx = Math.Abs(systemX - _currentSystemX);
        int dy = Math.Abs(_selectedSeeds[1] - _currentSystemY) >> 1;
        _selectedDistance = 4 * (int)Math.Sqrt(dx * dx + dy * dy);
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
        PrintTitle("system_data.title");
        PrintParagraphBreak();
        PrintDistance();

        PrintTextColon("system_data.economy");
        int economy = _selectedEconomy;
        if (((economy + 1) >> 1) == 2)
        {
            // TT70: economies 3 and 4 are "Mainly"
            PrintText("economy.mainly");
        }
        else
        {
            // TT71: the C flag from the CMP #2 is set for economies 5-7
            PrintText(ProsperityKeys[economy >= 5 ? economy - 5 : economy]);
        }

        // TT72
        PrintTextParagraph((_selectedEconomy >> 2) == 0 ? "economy.industrial" : "economy.agricultural");

        PrintTextColon("system_data.government");
        PrintTextParagraph(GovernmentKeys[_selectedGovernment]);

        PrintTextColon("system_data.tech_level");
        PrintNumber3(_selectedTechLevel + 1);
        PrintParagraphBreak();

        PrintTextColon("system_data.population");
        PrintNumber3(_selectedPopulation, true);
        PrintTextParagraph("system_data.billion");

        PrintToken('(');
        if ((_selectedSeeds[4] & 0x80) == 0)
        {
            PrintSpeciesWord(_descriptions.Species.HumanColonials);
        }
        else
        {
            // TT75: the species description
            var names = _descriptions.Species;
            int b = _selectedSeeds[5] >> 2;
            if ((b & 7) < 3)
            {
                PrintSpeciesWord(names.Size[b & 7]);
                PrintSpace();
            }

            // TT205
            b >>= 3;
            if (b < 6)
            {
                PrintSpeciesWord(names.Colour[b]);
                PrintSpace();
            }

            // TT206
            int c = (_selectedSeeds[3] ^ _selectedSeeds[1]) & 7;
            _scratch[0] = c;
            if (c < 6)
            {
                PrintSpeciesWord(names.Appearance[c]);
                PrintSpace();
            }

            // TT207
            int species = ((_selectedSeeds[5] & 3) + _scratch[0]) & 7;
            PrintSpeciesWord(names.Kind[species]);
        }

        // TT76
        PrintToken('S');
        PrintTokenParagraph(')');

        PrintTextColon("system_data.gross_productivity");
        PrintNumber5WithoutPoint(_selectedProductivity);
        PrintSpace();
        _textCase = 0;
        PrintToken('M');
        PrintTextParagraph("market.credits");

        PrintTextColon("system_data.average_radius");
        int radius = (((_selectedSeeds[5] & 15) + 11) << 8) | _selectedSeeds[3];
        PrintNumber5(radius, false);
        PrintSpace();
        PrintCharacter('k');
        PrintCharacter('m');
        PrintParagraphBreak();

        PrintSystemDescription();
    }

    /// <summary>The prosperity part of the economy (rich, average or poor), which the original prints as tokens 170 onwards.</summary>
    private static readonly string[] ProsperityKeys = ["economy.rich", "economy.average", "economy.poor"];

    /// <summary>The governments, which the original prints as tokens 177 onwards.</summary>
    private static readonly string[] GovernmentKeys =
    [
        "government.anarchy", "government.feudal", "government.multi_government", "government.dictatorship",
        "government.communist", "government.confederacy", "government.democracy", "government.corporate_state",
    ];

    /// <summary>TT146: print the distance to the selected system, if it isn't the current system.</summary>
    private void PrintDistance()
    {
        if (_selectedDistance == 0)
        {
            _cursorY++;
            return;
        }

        // TT63
        PrintTextColon("system_data.distance");
        PrintNumber5(_selectedDistance, true);
        PrintTextParagraph("system_data.light_years");
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

            // PD2: the special descriptions (checked from the last to the
            // first, as in the original, which keeps them in the RUPLA, RUGAL
            // and RUTOK tables)
            for (int y = _descriptions.SpecialDescriptions.Count - 1; y >= 0; y--)
            {
                var special = _descriptions.SpecialDescriptions[y];
                if (special.System == SystemNumber && special.Galaxy == _galaxyNumber)
                {
                    // PD3
                    PrintDescriptionText(special.Text);
                    return;
                }
            }
        }

        // PD1: the "goat soup" description, seeded from the system's seeds
        for (int x = 3; x >= 0; x--)
        {
            _randomSeeds[x] = _selectedSeeds[2 + x];
        }

        PrintDescriptionText(_descriptions.Description);
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
        PrintText("charts.galactic");
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
        PrintTitle("charts.short_range");
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
        PrintPlanetNamePrompt();
        ReadLine();
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
        PrintUnknownPlanet();
    }

    /// <summary>Clear the bottom of the screen and ask for a system's name.</summary>
    private void PrintPlanetNamePrompt()
    {
        ClearBottomRows();
        PrintExtendedText("charts.planet_name");
    }

    /// <summary>Say that there is no system with the name we asked for.</summary>
    private void PrintUnknownPlanet()
    {
        SetLeftAligned();
        PrintExtendedText("charts.unknown_planet");
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
                name.Append(SystemNames.LetterPairs[token]);
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
            PrintExtendedText("status.docked");
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
        PrintText("charts.hyperspace");
        if ((_selectedDistance >> 8) != 0 || _fuel < _selectedDistance)
        {
            // TT147
            PrintTextQuestion("charts.range");
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
        ShowMessage("equipment.galactic_hyperspace");
        SetCurrentSystem();
    }

    // ------------------------------------------------------------------------
    // Buying and selling cargo
    // ------------------------------------------------------------------------

    /// <summary>TT163: print the headers for the market prices table.</summary>
    private void PrintMarketHeaders(string key = "market.headers")
    {
        _cursorX = 17;
        PrintText(key);
        PrintToken(10);
    }

    /// <summary>TT167: show the Market Price screen (red key f7).</summary>
    private void ShowMarketPrices()
    {
        ShowTradingScreen(16);
        _cursorX = 5;
        PrintTitle("market.title");
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
    private void PrintMarketItem(int item) => PrintMarketItem(item, () => _marketAvailability[item]);

    /// <summary>
    /// TT151: print the name and price of a market item, and the quantity
    /// given (the availability on the market screens, or what's left after
    /// the basket on the trading screens).
    /// </summary>
    private void PrintMarketItem(int item, Func<int> quantity)
    {
        _scratch[4] = item;
        _scratch[0] = (item << 2) & 0xFF;
        if (_inWitchspace != 0)
        {
            return;
        }

        _cursorX = 1;
        PrintText(CommodityKeys[item]);
        _cursorX = 14;

        _itemPrice = CalculateItemPrice(item);
        PrintUnits();

        // TT156
        PrintNumber5((_itemPrice << 2) & 0xFFFF, true);

        int available = quantity();
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

    /// <summary>
    /// TT151 to TT156: work out the price of a market item, leaving its
    /// economic factor and units in QQ19+1 (for TT152).
    /// </summary>
    private int CalculateItemPrice(int item)
    {
        var commodity = _trading.Commodities[item];
        _scratch[1] = commodity.FactorAndUnit;
        int price = ((_marketRandom & commodity.Fluctuation) + commodity.BasePrice) & 0xFF;
        CalculateEconomicFactor();

        if ((_scratch[1] & 0x80) != 0)
        {
            // TT155
            return (price - _scratch[3]) & 0xFF;
        }

        return (price + _scratch[3]) & 0xFF;
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

    /// <summary>
    /// TT219: show the Buy Cargo screen (red key f1), which in this version
    /// lists every item in the market for filling a basket (see TradeScreen.cs).
    /// </summary>
    private void ShowBuyCargo() => ShowTradeScreen(selling: false);

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

    /// <summary>
    /// TT208: show the Sell Cargo screen (red key f2), which in this version
    /// lists every item in the market for filling a basket (see TradeScreen.cs).
    /// </summary>
    private void ShowSellCargo() => ShowTradeScreen(selling: true);

    /// <summary>TT213: show the Inventory screen (red key f9).</summary>
    private void ShowInventory()
    {
        ShowTradingScreen(8);
        _cursorX = 11;
        PrintTextParagraph("market.inventory");
        DrawTitleLine();
        PrintFuelAndCash();
        if (_cargoCapacity >= 26)
        {
            PrintText("equipment.large_cargo_bay");
        }

        ListCargo();
    }

    /// <summary>
    /// TT210: show a list of the cargo in the hold (the original also sells
    /// it here on the Sell Cargo screen, which now has its own basket).
    /// </summary>
    private void ListCargo()
    {
        for (int item = 0; item < 17; item++)
        {
            // TT211
            _itemNumber = item;

            // NWDAVxx
            int amount = _cargo[item];
            if (amount == 0)
            {
                continue;
            }

            _scratch[1] = _trading.Commodities[item].FactorAndUnit;
            PrintSentenceNewline();
            PrintText(CommodityKeys[_itemNumber]);
            _cursorX = 14;
            _itemAvailability = amount;
            PrintNumber3(amount);
            PrintUnits();
        }

        PrintSentenceNewline();
    }

    // ------------------------------------------------------------------------
    // Buying equipment
    // ------------------------------------------------------------------------

    /// <summary>The goods in the markets and the prices of the equipment for sale (see Assets/trading.yml).</summary>
    private readonly TradingData _trading;

    /// <summary>
    /// Check that the trading data has the commodities and equipment that the
    /// game has names for, in the same order.
    /// </summary>
    private static void CheckTradingNames(TradingData trading)
    {
        var commodities = trading.Commodities.Select(c => "commodities." + c.Name);
        var equipment = trading.EquipmentPrices.Select(e => "equipment." + e.Name);
        if (!commodities.SequenceEqual(CommodityKeys) || !equipment.SequenceEqual(EquipmentKeys.Skip(1)))
        {
            throw new InvalidDataException(
                "The trading data must have these commodities and equipment, in this order: "
                + string.Join(", ", CommodityKeys.Concat(EquipmentKeys.Skip(1))));
        }
    }

    /// <summary>PRXS: the price of an item of equipment after fuel (see Assets/trading.yml), in tenths of a credit.</summary>
    private int ListedEquipmentPrice(int item) => _trading.EquipmentPrices[item - 1].Price;

    /// <summary>The fuel price, which EQSHP stores in PRXS+0.</summary>
    private int _fuelPrice = 1;

    /// <summary>prx: the price of an item of equipment.</summary>
    private int EquipmentPrice(int item) => item == 0 ? _fuelPrice : ListedEquipmentPrice(item);

    /// <summary>
    /// EQSHP: show the Equip Ship screen (red key f3), which in this version
    /// has a highlighted list to buy from (see EquipScreen.cs).
    /// </summary>
    private void ShowEquipShip() => ShowEquipScreen();

    /// <summary>EQSHP: the number of items of equipment for sale, which depends on the tech level.</summary>
    private int EquipmentForSale()
    {
        int itemCount = _techLevel + 3;
        return itemCount >= 12 ? 14 : itemCount;
    }

    /// <summary>
    /// The pres checks in EQSHP: the text key for an item that is already
    /// fitted (or "all" for missiles when there are four), or null if the item
    /// can be bought.
    /// </summary>
    private string? EquipmentAlreadyPresent(int item) => item switch
    {
        1 when _missiles + 1 >= 5 => "equipment.all",
        2 when _cargoCapacity == 37 => "equipment.large_cargo_bay",
        3 when _ecm != 0 => "equipment.ecm",
        6 when _fuelScoops != 0 => "equipment.fuel_scoops",
        7 when _escapePod != 0 => "equipment.escape_pod",
        8 when _energyBomb != 0 => "equipment.energy_bomb",
        9 when _energyUnit != 0 => "equipment.energy_unit",
        10 when _dockingComputer != 0 => "equipment.docking_computers",
        11 when _galacticHyperdrive != 0 => "equipment.galactic_hyperspace",
        _ => null,
    };

    /// <summary>Whether an item of equipment is a laser, which is fitted to one of the views.</summary>
    private static bool IsLaser(int item) => item is 4 or 5 or 12 or 13;

    /// <summary>EQSHP: fit a newly bought item of equipment (a laser goes on the given view).</summary>
    private void FitEquipment(int item, int view)
    {
        switch (item)
        {
            case 0:
                _fuel = 70;
                break;
            case 1:
                _missiles++;
                ResetMissileIndicators();
                break;
            case 2:
                _cargoCapacity = 37;
                break;
            case 3:
                _ecm = 0xFF;
                break;
            case 4:
                Refund(view, PulseLaserPower);
                break;
            case 5:
                Refund(view, PulseLaserPower + 128);
                break;
            case 6:
                _fuelScoops = 0xFF;
                break;
            case 7:
                _escapePod = 0xFF;
                break;
            case 8:
                _energyBomb = 0x7F;
                break;
            case 9:
                _energyUnit = 1;
                break;
            case 10:
                _dockingComputer = 0xFF;
                break;
            case 11:
                _galacticHyperdrive = 0xFF;
                break;
            case 12:
                Refund(view, MilitaryLaserPower);
                break;
            case 13:
                Refund(view, MiningLaserPower);
                break;
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

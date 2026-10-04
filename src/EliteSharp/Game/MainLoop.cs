using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;
using EliteSharp.Sound;

namespace EliteSharp.Game;

/// <summary>
/// The main game loop and the start and end of the game.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>BEGIN: initialise the configuration variables and the commander, and start the game.</summary>
    private void Begin()
    {
        // Zero the configuration variables from COMC to DISK
        Array.Clear(ToggleOptions);
        _soundDisabled = 0;
        FilingSystemToggle = 0;
        JoystickEnabled = 0;
        _compassColour = Ink.None;
        Array.Clear(_missileColours);

        RestoreDefaultCommander();
    }

    /// <summary>TT170: the entry point for the start of the game (and after pressing ESCAPE while paused).</summary>
    private void StartGame()
    {
        ResetShipAndUniverse();
        RestartAfterDeath();
    }

    /// <summary>DEATH2: reset most of the game and restart from the title screen.</summary>
    private void RestartAfterDeath()
    {
        ResetFlight();
        ShowLoadCommanderTitle();
    }

    /// <summary>BR1 (part 1): show the "Load New Commander (Y/N)?" title screen.</summary>
    private void ShowLoadCommanderTitle()
    {
        ClearKeyLogger();
        _cursorX = 3;
        int key = ShowTitleScreen("title.load_new_commander", ShipType.CobraMkIII, 200, askYesNo: true);
        if (key == 'Y')
        {
            ApplySavedCommander();
            ShowLoadCommander();
        }

        LoadDefaultCommander();
    }

    /// <summary>QU5 and BR1 (part 2): show the "Press Fire or Space, Commander" title screen and start the game docked.</summary>
    private void LoadDefaultCommander()
    {
        ApplySavedCommander();
        ResetMissileIndicators();
        ShowTitleScreen("title.press_space", ShipType.Cougar, 100);
        MoveCrosshairsHome();
        SelectNearestSystem();
        SetCurrentSystem();

        // likeTT112: copy the current system's seeds into QQ2
        for (int i = 5; i >= 0; i--)
        {
            _currentSystemSeeds[i] = _selectedSeeds[i];
        }

        _extraVesselsDelay = 0;
        _currentEconomy = _selectedEconomy;
        _techLevel = _selectedTechLevel;
        _government = _selectedGovernment;
        GoToDockingBay();
    }

    /// <summary>BAY: go to the docking bay (i.e. show the Status Mode screen).</summary>
    private void GoToDockingBay()
    {
        _docked = 0xFF;
        throw new GameJumpException(GameJump.ForceKey, FunctionKey8);
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
                        MainLoopIteration();
                    }

                    startAtMloop = false;

                    // MLOOP: cool the lasers and update the dashboard
                    if (_laserTemperature != 0)
                    {
                        _laserTemperature--;
                    }

                    if (_laserPulseCounter != 0)
                    {
                        int counter = _laserPulseCounter - 1;
                        if (counter != 0)
                        {
                            counter--;
                        }

                        _laserPulseCounter = counter;
                    }

                    UpdateDashboard();

                    if (_viewType != 0 && ((_viewType & AuthorNamesShown) & 1) == 0)
                    {
                        Delay(2);
                    }
                    else if (_docked != 0)
                    {
                        // The docked loop doesn't go through TT100, so make
                        // sure it doesn't spin without sending frames
                        WaitForVsync();
                    }

                    key = ReadCursorKeys(out cursorX, out cursorY);
                }

                startAtFrce = false;

                // FRCE: process the key
                ProcessKey(key, cursorX, cursorY);
                cursorX = cursorY = 0;

                // If we are docked, loop back to MLOOP, otherwise TT100
                startAtMloop = _docked != 0;
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
    private void MainLoopIteration()
    {
        ThrottleMainLoop();
        RunDebugCommands();
        MainFlightLoop();

        // As in the Commodore 64 version, The Blue Danube plays while the
        // docking computer is on (and once the game is under way, the title
        // theme stops)
        _sound?.PlayMusic(_autoDocking != 0 ? Music.Docking : Music.None);

        // Count down the in-flight message delay
        _messageDelay = (_messageDelay - 1) & 0xFF;
        if (_messageDelay == 0)
        {
            RemoveMessage();
        }
        else if ((_messageDelay & 0x80) != 0)
        {
            _messageDelay = (_messageDelay + 1) & 0xFF;
        }

        // me3
        _mainLoopCounter = (_mainLoopCounter - 1) & 0xFF;
        if (_mainLoopCounter != 0)
        {
            return;
        }

        SpawnShips();
    }

    /// <summary>Main game loop parts 1 to 4: spawn traders, junk, cops, pirates, bounty hunters and Thargoids.</summary>
    private void SpawnShips()
    {
        if (_inWitchspace != 0)
        {
            return;
        }

        int random = NextRandom();
        if (random < 35 && _junkCount < 3)
        {
            // Spawn a trader, asteroid or cargo canister
            ResetWorkspace();
            _currentShip.Position.Z = 38 * 256;
            random = NextRandom();
            int randomX = _randomX;

            // ROL x_hi twice sets bit 1 of x_hi to the C flag
            int xHi = _carry ? 2 : 0;
            _currentShip.Position.X = ComposeCoordinate(random, xHi, random & 0x80);
            _currentShip.Position.Y = ComposeCoordinate(randomX, 0, randomX & 0x80);

            random = NextRandom();
            randomX = _randomX;
            if (_overflow)
            {
                // MTT4: spawn a trader, and then fall through into TT100 to
                // run the main flight loop again
                SpawnTrader();
                MainLoopIteration();
                return;
            }

            _currentShip.RollCounter = random | 0b01101111;
            if (InSafeZone != 0)
            {
                return;
            }

            random = randomX;
            if (_carry)
            {
                // MTT2
                _currentShip.PitchCounter = random | 0b01111111;
            }
            else
            {
                _currentShip.Speed = (random & 31) | 16;
            }

            // MTT3
            random = NextRandom();
            int type;
            if (random >= 252)
            {
                type = ShipType.RockHermit;
                _currentShip.Ai = ShipType.RockHermit;
            }
            else
            {
                // thongs: C is set if A >= 10
                type = (random & 1) + ShipType.CargoCanister + (random >= 10 ? 1 : 0);
            }

            AddShip(type);
        }

        // MTT1 (part 3): potentially spawn a cop
        if (InSafeZone != 0)
        {
            return;
        }

        int badness = (ContrabandBadness() << 1) & 0xFF;
        if (_shipCounts[ShipType.Viper] != 0)
        {
            badness |= _legalStatus;
        }

        random = SetUpDistantShip();
        if (random == 136)
        {
            // fothg: spawn a Thargoid, or very rarely a Cougar (depending on
            // the planet's z_lo)
            if ((LowByte(Planet.Position.Z) & 0b00111110) == 0)
            {
                _currentShip.Speed = 18;
                _currentShip.Ai = 0b01111001;
                AddShip(ShipType.Cougar);
                return;
            }

            // fothg2
            SpawnThargoid();
            SpawnPiratesOrBountyHunter();
            return;
        }

        if (random < badness)
        {
            AddShip(ShipType.Viper);
        }

        if (_shipCounts[ShipType.Viper] != 0)
        {
            return;
        }

        // Part 4
        _extraVesselsDelay = (_extraVesselsDelay - 1) & 0xFF;
        if ((_extraVesselsDelay & 0x80) == 0)
        {
            return;
        }

        _extraVesselsDelay = (_extraVesselsDelay + 1) & 0xFF;

        // Ships that missions send after us, such as the Thargoids that chase
        // the plans in mission 2 (fothg2)
        _missions.RunEncounters(this);

        SpawnPiratesOrBountyHunter();
    }

    /// <summary>
    /// MTT4: spawn a trader (a Cobra Mk III, Python, Boa or Anaconda) using the
    /// position already set up in INWK.
    /// </summary>
    private void SpawnTrader()
    {
        int random = NextRandom() >> 1;
        _currentShip.Ai = random;
        _currentShip.RollCounter = random;
        _currentShip.Speed = (random & 31) | 16;

        random = NextRandom();
        if ((random & 0x80) == 0)
        {
            // Spawn a ship that is trying to dock
            _currentShip.Ai |= 0b11000000;
            _currentShip.Behaviour = 0b00010000;
        }

        // nodo: A = 0 or 2, plus the C flag from DORND
        int type = (random & 2) + (_carry ? 1 : 0) + ShipType.CobraMkIII;
        if (type != ShipType.RockHermit)
        {
            AddShip(type);
        }
    }

    /// <summary>
    /// nopl and LABEL_2 (main game loop part 4): potentially spawn a lone bounty
    /// hunter, the Constrictor, or a group of up to four pirates.
    /// </summary>
    private void SpawnPiratesOrBountyHunter()
    {
        int random = NextRandom();
        if (_government != 0)
        {
            if (random >= 90 || (random & 7) < _government)
            {
                return;
            }
        }

        // LABEL_2
        random = SetUpDistantShip();
        if (random >= 100)
        {
            // mt1: spawn a group of pirates
            random &= 3;
            _extraVesselsDelay = random;
            int count = random;
            do
            {
                int mask = NextRandom();
                random = NextRandom() & mask & 7;
                AddShip(random + ShipType.PackHunters + (_carry ? 1 : 0));
                count--;
            }
            while (count >= 0);

            return;
        }

        _extraVesselsDelay = (_extraVesselsDelay + 1) & 0xFF;

        // The C flag is clear here as we passed through the BCS above
        int type = (random & 3) + ShipType.CobraMkIIIPirate;
        if (_missions.TrySpawnTarget(this))
        {
            // YESCON: a mission's target (such as the Constrictor) appears instead
            return;
        }

        // NOCON
        _currentShip.Behaviour = 0b00000100;
        random = NextRandom();
        _currentShip.Ai = (((random << 1) | (random >= 200 ? 1 : 0)) & 0xFF) | 0b11000000;
        AddShip(type);
    }

    /// <summary>
    /// Compose a coordinate from sign-magnitude bytes (low, high and sign), as
    /// the original sets up new ships' positions from random numbers.
    /// </summary>
    private static int ComposeCoordinate(int lo, int hi, int sign)
    {
        int magnitude = (lo & 0xFF) | ((hi & 0xFF) << 8) | ((sign & 0x7F) << 16);
        return (sign & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// TT102: process function keys, the save and load keys, hyperspace and chart keys,
    /// and update the hyperspace countdown.
    /// </summary>
    private void ProcessKey(int key, int cursorX, int cursorY)
    {
        switch (key)
        {
            case FunctionKey8:
                ShowStatus();
                return;
            case FunctionKey4:
                ShowLongRangeChart();
                return;
            case FunctionKey5:
                ShowShortRangeChart();
                return;
            case FunctionKey6:
                SelectNearestSystem();
                ShowSystemData();
                return;
            case FunctionKey9:
                ShowInventory();
                return;
            case FunctionKey7:
                ShowMarketPrices();
                return;
            case FunctionKey0:
                Launch();
                return;
        }

        // fvw
        if ((_docked & 0x80) != 0)
        {
            // We are docked
            if (key == FunctionKey3)
            {
                ShowEquipShip();
                return;
            }

            if (key == FunctionKey1)
            {
                ShowBuyCargo();
                return;
            }

            // Not in the original (which has "@" for its disc access menu):
            // L and S on the Status screen load and save commanders
            if (_viewType == 8 && key == 'L')
            {
                if (ShowLoadCommander())
                {
                    throw new GameJumpException(GameJump.LoadDefaultCommander);
                }

                GoToDockingBay();
            }

            if (_viewType == 8 && key == 'S')
            {
                ShowSaveCommander();
                GoToDockingBay();
            }

            if (key == FunctionKey2)
            {
                ShowSellCargo();
                return;
            }
        }
        else
        {
            // INSP: change the space view
            switch (key)
            {
                case FunctionKey1:
                    SwitchView(1);
                    return;
                case FunctionKey2:
                    SwitchView(2);
                    return;
                case FunctionKey3:
                    SwitchView(3);
                    return;
            }
        }

        // LABEL_3
        if (_keyPressed == 'H')
        {
            StartHyperspace();
            return;
        }

        if (_keyPressed == 'D')
        {
            PrintDistanceToSystem();
            return;
        }

        if (_keyPressed == 'F')
        {
            if (_docked != 0 && (_viewType & 0b11000000) != 0)
            {
                FindSystem();
            }

            return;
        }

        // HME1
        if ((_viewType & 0b11000000) != 0 && _hyperspaceCountdown == 0)
        {
            if (_keyPressed == 'O')
            {
                DrawSmallCrosshairs();
                MoveCrosshairsHome();
                DrawSmallCrosshairs();
                return;
            }

            // ee2: move the crosshairs
            MoveCrosshairs(cursorX, cursorY);
        }

        // TT107: update the hyperspace countdown
        if (_hyperspaceCountdown == 0)
        {
            return;
        }

        _hyperspaceTicks = (_hyperspaceTicks - 1) & 0xFF;
        if (_hyperspaceTicks != 0)
        {
            return;
        }

        // Erase the old count (by printing it again) and print the new one
        EraseText(() => PrintHyperspaceCountdown(_hyperspaceCountdown));
        _hyperspaceTicks = 5;
        PrintHyperspaceCountdown(_hyperspaceCountdown - 1);
        _hyperspaceCountdown--;
        if (_hyperspaceCountdown != 0)
        {
            return;
        }

        Hyperspace();
    }

    /// <summary>T95: print the distance to the selected system.</summary>
    private void PrintDistanceToSystem()
    {
        if ((_viewType & 0b11000000) == 0)
        {
            return;
        }

        MoveCrosshairsToNearestSystem();
        PrintSystemName();
        _textCase = 0x80;
        PrintCharacter(12);
        PrintDistance();
    }

    /// <summary>BAD: calculate how bad we have been from the amount of contraband in our hold.</summary>
    private int ContrabandBadness() => ((_cargo[3] + _cargo[6]) * 2 + _cargo[10]) & 0xFF;

    /// <summary>
    /// FAROF: returns true if INWK is within 57,600 of us in all three axes
    /// (x_hi, y_hi and z_hi are all 224 or less); ships any further away leave
    /// the local bubble.
    /// </summary>
    private bool IsNearby() => IsWithin(_currentShip.Position, 225 * 256);

    /// <summary>Print "GAME OVER" in capitals.</summary>
    private void PrintGameOver()
    {
        _textCase = 0;
        PrintText("messages.game_over");
    }

    /// <summary>DEATH: display the death screen.</summary>
    private void ShowDeathScreen()
    {
        MakeSound(SoundExplosion);
        ResetFlight();
        _speed = (_speed << 2) & 0xFF;
        SetDashboardRows(24);
        ClearScreen(13);
        _viewType = 0;

        // The original draws the border box again, which erases it (with EOR
        // logic), as the death screen has no border
        _cursorY = 1;
        _cursorX = 1;
        _hud.Border = false;
        CreateStardust();
        _colour = Cyan;
        _cursorX = 12;
        _cursorY = 12;
        PrintGameOver();

        do
        {
            // D1
            int random = SetUpDistantShip();
            int randomX = _randomX;
            random >>= 2;
            int xLo = random;
            _mainLoopCounter = 0xFF;
            int yLo = random ^ 0b00101010;
            int zLo = yLo | 0b01010000;
            // Keep the signs of x and y from Ze
            _currentShip.Position = new Vector3(
                _currentShip.Position.X < 0 ? -xLo : xLo,
                _currentShip.Position.Y < 0 ? -yLo : yLo,
                zLo);
            _currentShip.Ai = 0;
            int roll = randomX & 0b10001111;
            _currentShip.RollCounter = roll;
            _laserPulseCounter = 64;

            // SEC, ROR A
            _currentShip.PitchCounter = ((roll >> 1) | 0x80) & 0b10000111;

            // The byte at XX21 + 7 is always non-zero, so this depends on
            // the C flag, which was set by the ROR above to bit 0 of the roll
            int type = (roll & 1) != 0 ? ShipType.AlloyPlate : ShipType.CargoCanister;
            LaunchFromShip(type);

            int killed = NextRandom() & 0x80;
            if (_slotShip != null)
            {
                _slotShip.Flags = killed;
            }
        }
        while (Slots[4] == null);

        _speed = 0;
        ThrottleMainLoop();
        MainFlightLoop();

        do
        {
            ThrottleMainLoop();
            MainFlightLoop();
            _laserPulseCounter = (_laserPulseCounter - 1) & 0xFF;
        }
        while (_laserPulseCounter != 0);

        SetDashboardRows(31);
        throw new GameJumpException(GameJump.RestartAfterDeath);
    }

    /// <summary>DOENTRY: dock at the space station, show the ship hangar and work out any mission progression.</summary>
    private void DockAtStation()
    {
        // The docking computer's music stops once we're docked (the main
        // loop, which would otherwise stop it, doesn't run while we're docked)
        _sound?.PlayMusic(Music.None);
        ResetFlight();
        LaunchTunnel();
        _speed = 0;
        _laserTemperature = 0;
        _hyperspaceCountdown = 0;
        _forwardShield = 0xFF;
        _aftShield = 0xFF;
        _energy = 0xFF;
        DrawHangar();
        Delay(44);

        // Any mission briefings and debriefings (see Assets/Missions), and
        // then the docking bay (EN4)
        _missions.OnDocked(this);
        GoToDockingBay();
    }

    /// <summary>
    /// ESCAPE: launch our escape pod, watch our Cobra fly off, and dock at the
    /// station with our cargo and legal status wiped.
    /// </summary>
    private void LaunchEscapePod()
    {
        ResetFlight();
        _shipType = ShipType.CobraMkIII;
        if (!LaunchFromUs(ShipType.CobraMkIII))
        {
            LaunchFromUs(ShipType.CobraMkIIIPirate);
        }

        // ES1: set up the Cobra that we just launched from (the new ship is
        // still in INWK)
        _currentShip.Speed = 8;
        _currentShip.PitchCounter = 194;
        _currentShip.Ai = 194 >> 1;

        do
        {
            // ESL1
            MoveShip();
            if ((_viewType | _view) == 0)
            {
                DrawShip();
            }

            _currentShip.Ai = (_currentShip.Ai - 1) & 0xFF;
            ThrottleMainLoop();
        }
        while (_currentShip.Ai != 0);

        DrawOnScanner();

        Array.Clear(_cargo);
        _legalStatus = 0;
        _escapePod = 0;
        _fuel = 70;
        DockAtStation();
    }
}

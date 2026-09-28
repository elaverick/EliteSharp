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
    private int _titleShipDistance;

    /// <summary>
    /// TITLE: display a title screen with a rotating ship and a recursive
    /// token, and wait for a key press, returning its ASCII code.
    /// </summary>
    private int ShowTitleScreen(int token, int type, int distance)
    {
        _padContextOverride = PadContext.Screen;
        try
        {
            return RunTitleScreen(token, type, distance);
        }
        finally
        {
            _padContextOverride = null;
        }
    }

    /// <summary>The body of <see cref="ShowTitleScreen"/>.</summary>
    private int RunTitleScreen(int token, int type, int distance)
    {
        _titleShipDistance = distance;
        _shipType = type;
        ResetShipAndUniverse();
        ClearKeyLogger();
        ResetWorkspace();
        SetSpacePalette(32);
        ClearScreen(13);
        _colour = Red;
        _viewType = 0;
        _currentShip.Nose.Z = 96 << 8;
        _currentShip.Z = 96 << 8;
        _currentShip.RollCounter = 127;
        _currentShip.PitchCounter = 127;
        _textCase = 128;
        AddShip(_shipType);

        _cursorX = 6;
        PrintTokenLine(30);
        PrintCharacter(10);
        _cursorX = 6;
        if (AuthorNamesShown != 0)
        {
            PrintExtendedToken(13);
        }

        // awe
        _speed = 0;
        JoystickEnabled = 0;
        _cursorY = 20;
        _cursorX = 1;
        PrintExtendedToken(token);
        _cursorX = 7;
        PrintExtendedToken(12);
        _turnAngleLimit = 12;
        _mainLoopCounter = 5;
        JoystickEnabled = 0;

        while (true)
        {
            // TLL2: move the ship towards us
            if (_currentShip.ZHi != 1)
            {
                _currentShip.Z -= 256;
            }

            // TL1
            MoveShip();
            _currentShip.Z = ComposeCoordinate(_titleShipDistance, _currentShip.ZHi, _currentShip.ZSign);
            _currentShip.X = ComposeCoordinate(0, _currentShip.XHi, _currentShip.XSign);
            _currentShip.Y = ComposeCoordinate(0, _currentShip.YHi, _currentShip.YSign);
            DrawShip();
            _mainLoopCounter = (_mainLoopCounter - 1) & 0xFF;

            ThrottleMainLoop();
            int key = ReadKey();
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
    private void StartMission1()
    {
        _missionStatus |= 1;
        ShowIncomingMessage();
        ResetWorkspace();
        _shipType = ShipType.Constrictor;
        AddShip(_shipType);
        _cursorX = 1;
        _currentShip.Z = 1 << 8;
        ClearScreen(13);
        _mainLoopCounter = 64;

        do
        {
            // BRL1: spin the Constrictor in front of us
            _currentShip.RollCounter = 0x7F;
            _currentShip.PitchCounter = 0x7F;
            DrawShip();
            MoveShip();
            ThrottleMainLoop();
            _mainLoopCounter = (_mainLoopCounter - 1) & 0xFF;
        }
        while (_mainLoopCounter != 0);

        while (true)
        {
            // BRL2: fly the Constrictor away
            int xLo = _currentShip.XLo >> 1;
            _currentShip.X = ComposeCoordinate(xLo, _currentShip.XHi, _currentShip.XSign);
            int zLo = _currentShip.ZLo;
            zLo = (zLo + 1) & 0xFF;
            if (zLo == 0)
            {
                break;
            }

            zLo = (zLo + 1) & 0xFF;
            _currentShip.Z = ComposeCoordinate(zLo, _currentShip.ZHi, _currentShip.ZSign);
            if (zLo == 0)
            {
                break;
            }

            int yLo = Math.Min(_currentShip.YLo + 1, 120);
            _currentShip.Y = ComposeCoordinate(yLo, _currentShip.YHi, _currentShip.YSign);
            DrawShip();
            MoveShip();
            ThrottleMainLoop();
            _mainLoopCounter = (_mainLoopCounter - 1) & 0xFF;
        }

        // BR2
        _currentShip.Z = ComposeCoordinate(0, (_currentShip.ZHi + 1) & 0xFF, _currentShip.ZSign);
        ShowRotatingShip();
        PrintTokenAndDock(10);
    }

    /// <summary>BRP: print an extended token and go to the docking bay.</summary>
    private void PrintTokenAndDock(int token)
    {
        _colour = Cyan;
        PrintExtendedToken(token);
        GoToDockingBay();
    }

    /// <summary>BRIEF2: start mission 2.</summary>
    private void StartMission2()
    {
        _missionStatus |= 0b00000100;
        PrintTokenAndDock(11);
    }

    /// <summary>BRIEF3: receive the briefing and plans for mission 2.</summary>
    private void ShowMission2Briefing()
    {
        _missionStatus = (_missionStatus & 0b11110000) | 0b00001010;
        PrintTokenAndDock(222);
    }

    /// <summary>DEBRIEF2: finish mission 2.</summary>
    private void FinishMission2()
    {
        _missionStatus |= 0b00000100;
        _energyUnit = 2;
        _killTally = (_killTally + 0x100) & 0xFFFF;
        PrintTokenAndDock(223);
    }

    /// <summary>DEBRIEF: finish mission 1.</summary>
    private void FinishMission1()
    {
        _missionStatus &= 0xFE;
        AddCash(50000);
        PrintTokenAndDock(15);
    }

    /// <summary>BRIS: clear the screen, show "Incoming Message" and wait.</summary>
    private void ShowIncomingMessage()
    {
        PrintExtendedToken(216);
        Delay(100);
    }

    /// <summary>PAUSE: display the rotating ship and wait for a key, then clear the screen.</summary>
    private void ShowShipAndWait()
    {
        while (ShowRotatingShip() != 0)
        {
        }

        // PAL1
        while (ShowRotatingShip() == 0)
        {
        }

        _currentShip.Flags = 0;
        ClearScreen(1);
        DrawShip();

        // Fall through into MT23
        MoveToRowInCyan(10);
    }

    /// <summary>PAUSE2: wait for a key to be released and then pressed.</summary>
    private void WaitForKeyPress()
    {
        while (ReadKey() != 0)
        {
            WaitForVsync();
        }

        while (ReadKey() == 0)
        {
            WaitForVsync();
        }
    }

    /// <summary>PAS1: display a rotating ship at the top of the screen and read the keyboard.</summary>
    private int ShowRotatingShip()
    {
        _currentShip.Y = ComposeCoordinate(120, _currentShip.YHi, _currentShip.YSign);
        _currentShip.X = ComposeCoordinate(0, _currentShip.XHi, _currentShip.XSign);
        _currentShip.Z = ComposeCoordinate(0, 2, _currentShip.ZSign);
        DrawShip();
        MoveShip();
        ThrottleMainLoop();
        return ReadKey();
    }

    // ------------------------------------------------------------------------
    // The ship hangar
    // ------------------------------------------------------------------------

    /// <summary>HANGFLAG: non-zero if there are multiple ships in the hangar.</summary>
    private int _hangarHasManyShips;

    /// <summary>HALL: draw the ships in the hangar, then the hangar itself.</summary>
    private void DrawHangar()
    {
        SetSpacePalette(0);
        ClearScreen(0);
        int random = NextRandom();
        if ((random & 0x80) != 0)
        {
            // Draw a group of three ships from HATB
            random &= 3;
            int offset = random * 9;
            for (int i = 0; i < 3; i++)
            {
                // HAL8/HAL9
                _unitVector[2] = GameData.HangarGroups[offset];
                _unitVector[1] = GameData.HangarGroups[offset + 1];
                _unitVector[0] = GameData.HangarGroups[offset + 2];
                offset += 3;
                DrawHangarShip();
            }

            _hangarHasManyShips = 128;
        }
        else
        {
            // HA7: draw a single random ship
            _unitVector[1] = random >> 1;
            _unitVector[0] = NextRandom();
            int type = (NextRandom() & 3) + ShipType.Sidewinder + (_carry ? 1 : 0);
            _unitVector[2] = type;
            DrawHangarShip();
            _hangarHasManyShips = 0;
        }

        // HA9
        DrawHangarBackground();
    }

    /// <summary>HAS1: draw a ship in the hangar, using the type and position bytes in XX15.</summary>
    private void DrawHangarShip()
    {
        // Each ship in the hangar gets its own on-screen image
        _currentShip = Ship.Workspace();
        _currentShip.ResetOrientationAndPosition();

        int zLo = _unitVector[0];
        int xSign = (zLo & 1) != 0 ? 0x80 : 0;
        int xLo = _unitVector[1];
        int zHi = 1 + (xLo & 1);
        _currentShip.Z = (zHi << 8) | zLo;
        _currentShip.X = xSign != 0 ? -xLo : xLo;
        _rotationTemp2 = 0x80;

        int rotations = NextRandom();
        do
        {
            // HAL5: rotate the ship around its roof axis
            RotateVectorPair(ref _currentShip.Side.X, ref _currentShip.Nose.X);
            RotateVectorPair(ref _currentShip.Side.Y, ref _currentShip.Nose.Y);
            RotateVectorPair(ref _currentShip.Side.Z, ref _currentShip.Nose.Z);
            rotations = (rotations - 1) & 0xFF;
        }
        while (rotations != 0);

        int type = _unitVector[2];
        if (type == 0)
        {
            return;
        }

        var blueprint = BlueprintFor(type);
        if (blueprint == null)
        {
            return;
        }

        _blueprint = blueprint;
        _shipType = type;

        // Sit the ship on the hangar floor, using its size
        int size = EliteMaths.SquareRoot(blueprint.TargetableArea & 0xFFFF);
        int yLo = ((100 - size) & 0xFF) >> 1;
        _currentShip.Y = -yLo;

        OrthonormaliseOrientation();
        DrawShip();
    }

    /// <summary>
    /// HANGER: draw the hangar floor and back wall, with the lines stopping
    /// when they bump into the ships that are already on-screen.
    /// </summary>
    private void DrawHangarBackground()
    {
        var occupied = _screen.RasterizeSpaceView();

        void Draw(int x1, int y1, int x2, int y2)
        {
            _screen.DrawLine(x1, y1, x2, y2, Red, toggle: false);
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
        for (int divisor = 2; divisor < 13; divisor++)
        {
            int y = CentreY + 130 / divisor;
            Scan(y, 2, 1, 256);
            Scan(y, 253, -1, -1);
            if (_hangarHasManyShips != 0)
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

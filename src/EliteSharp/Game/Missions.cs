using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering.Scene;

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

    /// <summary>
    /// How far below us the hangar floor is: HAS1 puts each ship's centre
    /// (100 - size) / 2 below us, where size is roughly its radius, so the
    /// ships' undersides are this far down.
    /// </summary>
    private const float HangarFloorDepth = 50;

    /// <summary>
    /// How far the hangar's floor and back wall reach to each side, as a
    /// multiple of their distance (enough to fill any view).
    /// </summary>
    private const float HangarSideReach = 4;

    /// <summary>The owner of the hangar's floor and back wall in the 3D world.</summary>
    private readonly object _hangarOwner = new();

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
        }
        else
        {
            // HA7: draw a single random ship
            _unitVector[1] = random >> 1;
            _unitVector[0] = NextRandom();
            int type = (NextRandom() & 3) + ShipType.Sidewinder + (_carry ? 1 : 0);
            _unitVector[2] = type;
            DrawHangarShip();
        }

        // HA9
        DrawHangarBackground();
    }

    /// <summary>HAS1: draw a ship in the hangar, using the type and position bytes in XX15.</summary>
    private void DrawHangarShip()
    {
        // Each ship in the hangar is a separate object on the screen
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
    /// HANGER: draw the hangar floor and back wall. The original draws these in
    /// 2D, stopping each line where it bumps into a ship that is already on the
    /// screen; here they are real lines in 3D, so the depth buffer hides the
    /// parts that are behind the ships.
    ///
    /// The original draws the floor's lines across the screen at rows
    /// CentreY + 130 / d for d = 2 to 12, which is where lines across a floor
    /// <see cref="HangarFloorDepth"/> below us project to at evenly spaced
    /// distances (the ships sit on the floor, as HAS1 puts them this far below
    /// us, less their size). The back wall stands at the far end of the floor,
    /// with a vertical line every eight pixels. The floor and wall carry on out
    /// to the sides of the view, however wide it is.
    /// </summary>
    private void DrawHangarBackground()
    {
        BeginWorldDrawing(0);
        var lines = new List<LineSegment>();
        LineSegment Line(float x1, float y1, float x2, float y2, float z1, float z2) =>
            new(ViewToWorld(x1, y1, z1), ViewToWorld(x2, y2, z2), Red);

        // The floor
        for (int divisor = 2; divisor < 13; divisor++)
        {
            float z = FloorLineDistance(divisor);
            lines.Add(Line(-z * HangarSideReach, -HangarFloorDepth, z * HangarSideReach, -HangarFloorDepth, z, z));
        }

        // The back wall, from the floor up past the top of the view, with the
        // lines eight pixels apart at the wall's distance
        float wall = FloorLineDistance(12);
        float spacing = 8 * wall / 256;
        int columns = (int)(HangarSideReach * wall / spacing);
        for (int column = -columns; column <= columns; column++)
        {
            float x = column * spacing;
            lines.Add(Line(x, -HangarFloorDepth, x, wall, wall, wall));
        }

        _world.SetLines(_hangarOwner, lines);
    }

    /// <summary>
    /// The distance to the floor line that the original draws at row
    /// CentreY + 130 / divisor, which is where a line across the floor at that
    /// distance projects to (as the projection is 256 * y / z).
    /// </summary>
    private static float FloorLineDistance(int divisor) => 256f * HangarFloorDepth / (130 / divisor);
}

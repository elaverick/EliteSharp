using System.Numerics;
using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering.Scene;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// The title screens and the ship hangar.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>distaway: the distance of the ship on the title screen.</summary>
    private int _titleShipDistance;

    /// <summary>
    /// TITLE: display a title screen with a rotating ship and a prompt (a
    /// string of fixed text), and wait for a key press, returning its ASCII
    /// code.
    /// </summary>
    private int ShowTitleScreen(string prompt, int type, int distance)
    {
        _padContextOverride = PadContext.Screen;
        try
        {
            return RunTitleScreen(prompt, type, distance);
        }
        finally
        {
            _padContextOverride = null;
        }
    }

    /// <summary>The body of <see cref="ShowTitleScreen"/>.</summary>
    private int RunTitleScreen(string prompt, int type, int distance)
    {
        _titleShipDistance = distance;
        _shipType = type;
        ResetShipAndUniverse();
        ClearKeyLogger();
        ResetWorkspace();
        SetSpacePalette(SpacePalette.Title);
        ClearScreen(13);
        _colour = Red;
        _viewType = 0;
        // The ship starts far away (z_hi = 96), pointing away from us
        _currentShip.Nose.Z = 1;
        _currentShip.Position.Z = 96 * 256 + distance;
        _currentShip.RollCounter = 127;
        _currentShip.PitchCounter = 127;
        _textCase = 128;
        AddShip(_shipType);

        _cursorX = 6;
        PrintTextLine("title.banner");
        PrintCharacter(10);
        _cursorX = 6;
        if (AuthorNamesShown != 0)
        {
            PrintExtendedText("title.authors");
        }

        // awe
        _speed = 0;
        JoystickEnabled = 0;
        _cursorY = 20;
        _cursorX = 1;
        PrintExtendedText(prompt);
        _cursorX = 7;
        PrintExtendedText("title.copyright");
        _turnAngleLimit = 12 / 36f;
        _mainLoopCounter = 5;
        JoystickEnabled = 0;

        while (true)
        {
            // TLL2: move the ship towards us by 256 each time, until it is
            // 256 plus the title screen's distance away (the original sets
            // z_lo to the distance, and stops when z_hi is 1)
            _currentShip.Position.Z = MathF.Max(_currentShip.Position.Z - 256, 256 + _titleShipDistance);

            // TL1
            MoveShip();
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
        SetSpacePalette(SpacePalette.Space);
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
                DrawHangarShip(GameData.HangarGroups[offset], GameData.HangarGroups[offset + 1], GameData.HangarGroups[offset + 2]);
                offset += 3;
            }
        }
        else
        {
            // HA7: draw a single random ship
            int position = random >> 1;
            int depth = NextRandom();
            int type = (NextRandom() & 3) + ShipType.Sidewinder + (_carry ? 1 : 0);
            DrawHangarShip(type, position, depth);
        }

        // HA9
        DrawHangarBackground();
    }

    /// <summary>
    /// HAS1: draw a ship in the hangar. The position bytes (which the original
    /// passes in XX15) are x_lo, with bit 0 giving z_hi, and z_lo, with bit 0
    /// giving the sign of x.
    /// </summary>
    private void DrawHangarShip(int type, int xLo, int zLo)
    {
        // Each ship in the hangar is a separate object on the screen
        _currentShip = Ship.Workspace();
        _currentShip.ResetOrientationAndPosition();

        int zHi = 1 + (xLo & 1);
        _currentShip.Position = new Vector3((zLo & 1) != 0 ? -xLo : xLo, 0, (zHi << 8) | zLo);

        // HAL5: rotate the ship around its roof axis by a random multiple of
        // 1/16 radian
        int rotations = NextRandom();
        do
        {
            RotatePair(ref _currentShip.Side, ref _currentShip.Nose, -ShipTurnAngle);
            rotations = (rotations - 1) & 0xFF;
        }
        while (rotations != 0);

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

        // Sit the ship on the hangar floor, using its size (the square root
        // of its targetable area)
        float size = MathF.Sqrt(blueprint.TargetableArea);
        _currentShip.Position.Y = -(100 - size) / 2;

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

        _world.SetLines(_hangarOwner, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines));
    }

    /// <summary>
    /// The distance to the floor line that the original draws at row
    /// CentreY + 130 / divisor, which is where a line across the floor at that
    /// distance projects to (as the projection is 256 * y / z).
    /// </summary>
    private static float FloorLineDistance(int divisor) => 256f * HangarFloorDepth / (130 / divisor);
}

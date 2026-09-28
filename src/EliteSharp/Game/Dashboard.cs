using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// The dashboard. The original updates the dashboard in screen memory as the
/// values change; here the dashboard is built for each frame from the game's
/// state, using the same positions, sizes and colours as the original.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The top of the dashboard in logical pixels.</summary>
    private const int DashTop = 2 * CentreY;

    /// <summary>A ship's dot and stick on the scanner, as drawn by SCAN.</summary>
    private readonly record struct ScannerBlip(int X, int DotY, int BaseY, int Colour);

    /// <summary>The ships currently shown on the scanner (SCAN draws and erases using EOR logic).</summary>
    private readonly Dictionary<object, ScannerBlip> _blips = [];

    /// <summary>COMX, COMY, COMC: the compass dot's position and colour.</summary>
    private int _compassX, _compassY, _compassColour;

    /// <summary>mscol: the colour of each missile indicator.</summary>
    private readonly int[] _missileColours = new int[5];

    /// <summary>Whether the E.C.M. and space station bulbs are lit (they are toggled with EOR logic).</summary>
    private bool _ecmBulb, _stationBulb;

    /// <summary>The dashboard bitmap, decoded into rectangles once.</summary>
    private static readonly List<ScreenRect> DashboardBitmap = DecodeDashboard();

    /// <summary>Decode the dashboard image (P.DIALS2P, in screen mode 2) into rectangles of identical bytes.</summary>
    private static List<ScreenRect> DecodeDashboard()
    {
        var rects = new List<ScreenRect>();
        var data = GameData.Dashboard;
        for (int row = 0; row < data.Length / 512; row++)
        {
            for (int line = 0; line < 8; line++)
            {
                int column = 0;
                while (column < 64)
                {
                    int value = data[row * 512 + column * 8 + line];
                    if (value == 0)
                    {
                        column++;
                        continue;
                    }

                    // Merge runs of identical bytes into one rectangle
                    int start = column;
                    while (column < 64 && data[row * 512 + column * 8 + line] == value)
                    {
                        column++;
                    }

                    rects.Add(new ScreenRect(start * 4, DashTop + row * 8 + line, (column - start) * 4, 1, value));
                }
            }
        }

        return rects;
    }

    /// <summary>DIALS: update the dashboard (in the original this redraws the bars and the compass).</summary>
    private void UpdateDashboard() => UpdateCompass();

    /// <summary>Build the dashboard for the current frame.</summary>
    private void DrawDashboard(FrameBuilder builder)
    {
        const uint flags = VertexFlags.Dashboard | VertexFlags.Mode2;
        foreach (var rect in DashboardBitmap)
        {
            builder.Rect(rect.X, rect.Y, rect.Width, rect.Height, rect.Colour, flags);
        }

        // PZW2 and PZW: the colours for the bars, flashing if FLH is set
        int danger = ((_mainLoopCounter & 8) & FlashingBars) != 0 ? DashboardGreen : DashboardRed;

        // The right-hand side: speed, roll, pitch and energy banks
        DrawBar(builder, 208, 0, _speed >> 1, 14, danger, DashboardWhite);
        DrawIndicator(builder, 208, 1, AddSignMagnitudeHighBytes(8, ((_rollMagnitude >> 2) | _rollSign) ^ 0x80));
        int pitch = _pitchAngle;
        if (_pitchMagnitude != 0)
        {
            pitch = (pitch - 1) & 0xFF;
        }

        DrawIndicator(builder, 208, 2, AddSignMagnitudeHighBytes(8, pitch));

        int[] banks = new int[4];
        int remaining = _energy >> 2;
        for (int x = 3; x >= 0; x--)
        {
            if (remaining - 16 < 0)
            {
                banks[x] = remaining;
                break;
            }

            remaining -= 16;
            banks[x] = 16;
        }

        for (int y = 0; y < 4; y++)
        {
            DrawBar(builder, 208, 3 + y, banks[y], 3, DashboardStripe, danger);
        }

        // The left-hand side: shields, fuel, temperatures and altitude
        DrawBar(builder, 16, 0, _forwardShield >> 4, 3, DashboardStripe, danger);
        DrawBar(builder, 16, 1, _aftShield >> 4, 3, DashboardStripe, danger);
        DrawBar(builder, 16, 2, _fuel >> 2, 3, DashboardYellow, DashboardYellow);
        DrawBar(builder, 16, 3, _cabinTemperature >> 4, 11, danger, DashboardWhite);
        DrawBar(builder, 16, 4, _laserTemperature >> 4, 11, danger, DashboardWhite);
        DrawBar(builder, 16, 5, _altitude >> 4, 240, DashboardYellow, DashboardYellow);

        // MSBAR: the missile indicators
        for (int x = 1; x <= 4; x++)
        {
            int colour = _missileColours[x];
            if (colour == 0)
            {
                continue;
            }

            int left = 48 - 8 * x;
            builder.Rect(left, DashTop + 48 + 1, 4, 5, colour, flags);
            builder.Rect(left + 4, DashTop + 48 + 1, 2, 5, colour & 0b10101010, flags);
        }

        // ECBLB and SPBLB: the E.C.M. and space station bulbs
        if (_ecmBulb)
        {
            DrawBulb(builder, 56, GameData.EcmBulb);
        }

        if (_stationBulb)
        {
            DrawBulb(builder, 192, GameData.StationBulb);
        }

        // DOT: the compass
        if (_compassColour != 0)
        {
            DrawDash(builder, _compassX, _compassY, _compassColour);
            if (_compassColour == DashboardYellow)
            {
                DrawDash(builder, _compassX, _compassY - 1, _compassColour);
            }
        }

        // SCAN: the ships on the scanner
        foreach (var blip in _blips.Values)
        {
            DrawDash(builder, blip.X, blip.DotY, blip.Colour);
            int length = blip.DotY - blip.BaseY;
            if (length > 0)
            {
                builder.Rect(blip.X + 2, blip.BaseY, 2, length, blip.Colour, flags);
            }
            else if (length < 0)
            {
                builder.Rect(blip.X + 2, blip.DotY + 1, 2, -length, blip.Colour, flags);
            }
        }
    }

    /// <summary>
    /// DIL: draw a bar of the given length (in mode 2 pixels, up to 16) on
    /// the given dashboard row, in the danger colour if the value is at least
    /// the threshold, or in the safe colour otherwise.
    /// </summary>
    private static void DrawBar(FrameBuilder builder, int x, int row, int value, int threshold, int danger, int safe)
    {
        int colour = value >= threshold || safe == 0 ? danger : safe;
        int length = Math.Min(value, 16);
        if (length > 0)
        {
            builder.Rect(x, DashTop + row * 8 + 2, length * 2, 3, colour, VertexFlags.Dashboard | VertexFlags.Mode2);
        }
    }

    /// <summary>
    /// DIL2: draw the vertical bar of the roll or pitch indicator. Each
    /// character block holds two pixels, and the bar is always drawn in the
    /// left pixel of the block (as in the original).
    /// </summary>
    private static void DrawIndicator(FrameBuilder builder, int x, int row, int value)
    {
        int block = value >> 1;
        if (block < 8)
        {
            builder.Rect(x + block * 4, DashTop + row * 8 + 1, 2, 4, DashboardWhite, VertexFlags.Dashboard | VertexFlags.Mode2);
        }
    }

    /// <summary>ADDK: (A X) = (A 0) + (S 0) with sign-magnitude arithmetic, returning the high byte.</summary>
    private static int AddSignMagnitudeHighBytes(int addend, int value)
    {
        int result = EliteMaths.Add16(EliteMaths.FromSignMagnitude(value) << 8, addend << 8);
        return (result >> 8) & 0xFF;
    }

    /// <summary>Draw a bulb bitmap (two columns of eight mode 2 bytes) at the given x on dashboard row 5.</summary>
    private static void DrawBulb(FrameBuilder builder, int x, byte[] bitmap)
    {
        for (int i = 0; i < 16; i++)
        {
            int value = bitmap[i];
            if (value != 0)
            {
                builder.Rect(x + (i >> 3) * 4, DashTop + 40 + (i & 7), 4, 1, value, VertexFlags.Dashboard | VertexFlags.Mode2);
            }
        }
    }

    /// <summary>CPIXK: draw a four-pixel dash (two mode 2 pixels) at (x, y).</summary>
    private static void DrawDash(FrameBuilder builder, int x, int y, int colour)
    {
        builder.Rect(x & 0xFE, y, 4, 1, colour, VertexFlags.Dashboard | VertexFlags.Mode2);
    }

    /// <summary>SCAN: draw (or erase) the ship in INWK on the scanner.</summary>
    private void DrawOnScanner()
    {
        if ((_currentShip.Flags & Ship.FlagScanner) == 0 || _shipType >= 128)
        {
            return;
        }

        int colour = ShipCatalogue.Get(_shipType).ScannerColour;
        if (((_currentShip.XHi | _currentShip.YHi | _currentShip.ZHi) & 0b11000000) != 0)
        {
            return;
        }

        var owner = _currentShip.DisplayOwner;
        if (_blips.Remove(owner))
        {
            // The ship was on the scanner, so this call erases it
            return;
        }

        // X1 = 125 + x_hi (made even)
        int offset = _currentShip.XHi;
        if (_currentShip.X < 0)
        {
            offset = (-offset) & 0xFF;
        }

        int x1 = (offset + 125) & 0xFE;

        // Y2 = 220 - z_hi / 4
        offset = _currentShip.ZHi >> 2;
        offset = _currentShip.Z < 0 ? (~offset + 35 + 1) & 0xFF : (offset + 35) & 0xFF;
        int y2 = offset ^ 0xFF;

        // The dot's y-coordinate is Y2 - y_hi / 2, clipped to the scanner
        offset = _currentShip.YHi >> 1;
        int dot = _currentShip.Y < 0 ? (offset + y2) & 0xFF : ((~offset & 0xFF) + y2 + 1) & 0xFF;
        if ((dot & 0x80) == 0 || dot >= 247)
        {
            dot = 246;
        }
        else if (dot < 194)
        {
            dot = 194;
        }

        _blips[owner] = new ScannerBlip(x1, dot, y2, colour);
    }

    /// <summary>COMPAS: update the compass to point to the planet or the space station.</summary>
    private void UpdateCompass()
    {
        if (InSafeZone != 0)
        {
            // SP1: point to the station
            CalculateStationVector();
        }
        else
        {
            CalculatePlanetVector();
        }

        // SP2
        _compassX = (CompassOffset(_unitVector[0]) + 195) & 0xFF;
        _compassY = (204 - CompassOffset(_unitVector[1]) - 1) & 0xFF;
        _compassColour = _unitVector[2] < 0 ? DashboardGreen : DashboardYellow;
    }

    /// <summary>SPS4: calculate the normalised vector to the space station in XX15.</summary>
    private void CalculateStationVector()
    {
        var station = Slots[1];
        SetVectorCoordinate(0, station?.X ?? 0);
        SetVectorCoordinate(1, station?.Y ?? 0);
        SetVectorCoordinate(2, station?.Z ?? 0);
        NormaliseVector();
    }

    /// <summary>SPS2: X = A / 10, for a signed value A (the compass offset).</summary>
    private static int CompassOffset(int value)
    {
        int magnitude = (Math.Abs(value) << 1) & 0xFF;
        EliteMaths.DivideWithRemainder(magnitude, 20, out int offset, out _);
        return value < 0 ? -offset : offset;
    }

    /// <summary>MSBAR: set the colour of a missile indicator.</summary>
    private void SetMissileIndicator(int missile, int colour)
    {
        if (missile >= 1 && missile <= 4)
        {
            _missileColours[missile] = colour;
        }
    }

    /// <summary>msblob: display the dashboard's missile indicators in green.</summary>
    private void ResetMissileIndicators()
    {
        for (int x = 4; x > 0; x--)
        {
            SetMissileIndicator(x, x <= _missiles ? DashboardGreen : 0);
        }
    }

    /// <summary>ABORT: disarm the missiles and update the indicators.</summary>
    private void DisarmMissile(int colour) => SetMissileTarget(0xFF, colour);

    /// <summary>ABORT2: set the missile target and update the current missile indicator.</summary>
    private void SetMissileTarget(int target, int colour)
    {
        _missileTarget = target;
        SetMissileIndicator(_missiles, colour);
        _missileArmed = colour;
    }

    /// <summary>ECBLB: toggle the E.C.M. bulb.</summary>
    private void ToggleEcmBulb() => _ecmBulb = !_ecmBulb;

    /// <summary>SPBLB: toggle the space station bulb.</summary>
    private void ToggleStationBulb() => _stationBulb = !_stationBulb;

    /// <summary>DET1: show or hide the dashboard (by setting the number of character rows shown).</summary>
    private void SetDashboardRows(int rows) => _screen.DashboardVisible = rows > 24;
}

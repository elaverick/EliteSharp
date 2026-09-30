using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// The dashboard. The original updates the dashboard in screen memory as the
/// values change; here the dashboard is built for each frame from the game's
/// state, as the dashboard image with the bars, indicators and scanner drawn
/// over it, in the same positions, sizes and colours as the original.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The top of the dashboard in logical pixels.</summary>
    private const int DashTop = 2 * CentreY;

    /// <summary>A ship's dot and stick on the scanner, as drawn by SCAN.</summary>
    private readonly record struct ScannerBlip(int X, int DotY, int BaseY, Ink Colour);

    /// <summary>
    /// The ships currently shown on the scanner. The original's SCAN draws and
    /// erases each ship with EOR logic, and the game calls it once to draw a
    /// ship and again to erase it, so here each call adds or removes the ship.
    /// </summary>
    private readonly Dictionary<object, ScannerBlip> _blips = [];

    /// <summary>COMX, COMY, COMC: the compass dot's position and colour.</summary>
    private int _compassX, _compassY;

    /// <summary>COMC: the compass dot's colour (or none).</summary>
    private Ink _compassColour;

    /// <summary>mscol: the colour of each missile indicator.</summary>
    private readonly Ink[] _missileColours = new Ink[5];

    /// <summary>Whether the E.C.M. and space station bulbs are lit (they are toggled with EOR logic).</summary>
    private bool _ecmBulb, _stationBulb;

    /// <summary>DIALS: update the dashboard (in the original this redraws the bars and the compass).</summary>
    private void UpdateDashboard() => UpdateCompass();

    /// <summary>Build the dashboard for the current frame.</summary>
    private void DrawDashboard(HudBuilder builder)
    {
        builder.Image(HudAtlas.Dashboard, 0, DashTop);

        // PZW2 and PZW: the colours for the bars, flashing if FLH is set
        var danger = ((_mainLoopCounter & 8) & FlashingBars) != 0 ? DashboardGreen : DashboardRed;

        // The right-hand side: speed, roll, pitch and energy banks
        DrawBar(builder, 208, 0, _speed >> 1, 14, danger, DashboardWhite);
        // The roll indicator moves a quarter as far as the roll, and the pitch
        // indicator one less than the pitch
        DrawIndicator(builder, 208, 1, 8 - Math.Sign(_roll) * (Math.Abs(_roll) >> 2));
        DrawIndicator(builder, 208, 2, 8 + Math.Sign(_pitch) * Math.Max(Math.Abs(_pitch) - 1, 0));

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
            var colour = _missileColours[x];
            if (colour != Ink.None)
            {
                // Three of the dashboard's pixels (six of the space view's) wide
                builder.Rect(48 - 8 * x, DashTop + 48 + 1, 6, 5, colour);
            }
        }

        // ECBLB and SPBLB: the E.C.M. and space station bulbs
        if (_ecmBulb)
        {
            builder.Image(HudAtlas.EcmBulb, 56, DashTop + 40);
        }

        if (_stationBulb)
        {
            builder.Image(HudAtlas.StationBulb, 192, DashTop + 40);
        }

        // DOT: the compass
        if (_compassColour != Ink.None)
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
                builder.Rect(blip.X + 2, blip.BaseY, 2, length, blip.Colour);
            }
            else if (length < 0)
            {
                builder.Rect(blip.X + 2, blip.DotY + 1, 2, -length, blip.Colour);
            }
        }
    }

    /// <summary>
    /// DIL: draw a bar of the given length (up to 16 of the dashboard's pixels,
    /// which are two of the space view's wide) on the given dashboard row, in
    /// the danger colour if the value is at least the threshold, or in the
    /// safe colour otherwise.
    /// </summary>
    private static void DrawBar(HudBuilder builder, int x, int row, int value, int threshold, Ink danger, Ink safe)
    {
        var colour = value >= threshold || safe == Ink.None ? danger : safe;
        int length = Math.Min(value, 16);
        if (length > 0)
        {
            builder.Rect(x, DashTop + row * 8 + 2, length * 2, 3, colour);
        }
    }

    /// <summary>
    /// DIL2: draw the vertical bar of the roll or pitch indicator. Each
    /// character block holds two pixels, and the bar is always drawn in the
    /// left pixel of the block (as in the original).
    /// </summary>
    private static void DrawIndicator(HudBuilder builder, int x, int row, int value)
    {
        int block = value >> 1;
        if (block < 8)
        {
            builder.Rect(x + block * 4, DashTop + row * 8 + 1, 2, 4, DashboardWhite);
        }
    }

    /// <summary>CPIXK: draw a four-pixel dash (two of the dashboard's pixels) at (x, y).</summary>
    private static void DrawDash(HudBuilder builder, int x, int y, Ink colour)
    {
        builder.Rect(x & 0xFE, y, 4, 1, colour);
    }

    /// <summary>SCAN: draw (or erase) the ship in INWK on the scanner.</summary>
    private void DrawOnScanner()
    {
        if ((_currentShip.Flags & Ship.FlagScanner) == 0 || _shipType >= 128)
        {
            return;
        }

        // Only ships within 16,384 in each axis are shown (x_hi, y_hi and z_hi
        // are less than 64)
        var colour = ShipCatalogue.Get(_shipType).ScannerColour;
        var position = _currentShip.Position;
        if (!IsWithin(position, 16384))
        {
            return;
        }

        var owner = _currentShip.DisplayOwner;
        if (_blips.Remove(owner))
        {
            // The ship was on the scanner, so this call erases it
            return;
        }

        // The scanner shows the ship's position on the ellipse (x and z, with
        // the stick's base at X1 = 125 + x / 256, made even, and
        // Y2 = 220 - z / 1024) and its height (y) as the length of the stick
        // (Y2 - y / 512, clipped to the scanner)
        int x1 = (125 + (int)(position.X / 256)) & ~1;
        int y2 = 220 - (int)(position.Z / 1024);
        int dot = Math.Clamp(y2 - (int)(position.Y / 512), 194, 246);

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

        // SP2: the dot is up to 9 pixels from the compass's centre (the
        // original divides the 96ths of the unit vector by 10), and is green
        // if the planet or station is behind us
        _compassX = 195 + CompassOffset(_unitVector.X);
        _compassY = 204 - CompassOffset(_unitVector.Y) - 1;
        _compassColour = _unitVector.Z < 0 ? DashboardGreen : DashboardYellow;
    }

    /// <summary>SPS4: calculate the unit vector to the space station in XX15.</summary>
    private void CalculateStationVector()
    {
        _tacticsVector = Slots[1]?.Position ?? Vector3.Zero;
        NormaliseVector();
    }

    /// <summary>SPS2: the compass dot's offset from the compass's centre for a coordinate of a unit vector.</summary>
    private static int CompassOffset(float coordinate) => (int)(coordinate * 96 / 10);

    /// <summary>MSBAR: set the colour of a missile indicator.</summary>
    private void SetMissileIndicator(int missile, Ink colour)
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
            SetMissileIndicator(x, x <= _missiles ? DashboardGreen : Ink.None);
        }
    }

    /// <summary>ABORT: disarm the missiles and update the indicators.</summary>
    private void DisarmMissile(Ink colour) => SetMissileTarget(0xFF, colour);

    /// <summary>ABORT2: set the missile target and update the current missile indicator.</summary>
    private void SetMissileTarget(int target, Ink colour)
    {
        _missileTarget = target;
        SetMissileIndicator(_missiles, colour);

        // MSAR is set to the indicator's colour, so it is non-zero unless the
        // indicator is blank
        _missileArmed = colour != Ink.None;
    }

    /// <summary>ECBLB: toggle the E.C.M. bulb.</summary>
    private void ToggleEcmBulb() => _ecmBulb = !_ecmBulb;

    /// <summary>SPBLB: toggle the space station bulb.</summary>
    private void ToggleStationBulb() => _stationBulb = !_stationBulb;

    /// <summary>DET1: show or hide the dashboard (by setting the number of character rows shown).</summary>
    private void SetDashboardRows(int rows) => _hud.DashboardVisible = rows > 24;
}

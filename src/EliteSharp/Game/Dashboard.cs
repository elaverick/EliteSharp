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
    private int COMX, COMY, COMC;

    /// <summary>mscol: the colour of each missile indicator.</summary>
    private readonly int[] mscol = new int[5];

    /// <summary>Whether the E.C.M. and space station bulbs are lit (they are toggled with EOR logic).</summary>
    private bool _ecmBulb, _stationBulb;

    /// <summary>The dashboard bitmap, decoded into rectangles once.</summary>
    private static readonly List<ScreenRect> DashboardBitmap = DecodeDashboard();

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
    private void DIALS() => COMPAS();

    /// <summary>Build the dashboard for the current frame.</summary>
    private void DrawDashboard(FrameBuilder builder)
    {
        const uint flags = VertexFlags.Dashboard | VertexFlags.Mode2;
        foreach (var rect in DashboardBitmap)
        {
            builder.Rect(rect.X, rect.Y, rect.Width, rect.Height, rect.Colour, flags);
        }

        // PZW2 and PZW: the colours for the bars, flashing if FLH is set
        int danger = ((MCNT & 8) & FLH) != 0 ? GREEN2 : RED2;

        // The right-hand side: speed, roll, pitch and energy banks
        DrawBar(builder, 208, 0, DELTA >> 1, 14, danger, WHITE2);
        DrawIndicator(builder, 208, 1, AddK(8, ((ALP1 >> 2) | ALP2) ^ 0x80));
        int pitch = BETA;
        if (BET1 != 0)
        {
            pitch = (pitch - 1) & 0xFF;
        }

        DrawIndicator(builder, 208, 2, AddK(8, pitch));

        int[] banks = new int[4];
        int remaining = ENERGY >> 2;
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
            DrawBar(builder, 208, 3 + y, banks[y], 3, STRIPE, danger);
        }

        // The left-hand side: shields, fuel, temperatures and altitude
        DrawBar(builder, 16, 0, FSH >> 4, 3, STRIPE, danger);
        DrawBar(builder, 16, 1, ASH >> 4, 3, STRIPE, danger);
        DrawBar(builder, 16, 2, QQ14 >> 2, 3, YELLOW2, YELLOW2);
        DrawBar(builder, 16, 3, CABTMP >> 4, 11, danger, WHITE2);
        DrawBar(builder, 16, 4, GNTMP >> 4, 11, danger, WHITE2);
        DrawBar(builder, 16, 5, ALTIT >> 4, 240, YELLOW2, YELLOW2);

        // MSBAR: the missile indicators
        for (int x = 1; x <= 4; x++)
        {
            int colour = mscol[x];
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
        if (COMC != 0)
        {
            DrawDash(builder, COMX, COMY, COMC);
            if (COMC == YELLOW2)
            {
                DrawDash(builder, COMX, COMY - 1, COMC);
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
            builder.Rect(x + block * 4, DashTop + row * 8 + 1, 2, 4, WHITE2, VertexFlags.Dashboard | VertexFlags.Mode2);
        }
    }

    /// <summary>ADDK: (A X) = (A 0) + (S 0) with sign-magnitude arithmetic, returning the high byte.</summary>
    private static int AddK(int s, int a)
    {
        int result = EliteMaths.Add16(EliteMaths.FromSignMagnitude(a) << 8, s << 8);
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
    private void SCAN()
    {
        if ((INWK.Flags & Ship.FlagScanner) == 0 || TYPE >= 128)
        {
            return;
        }

        int colour = ShipCatalogue.Get(TYPE).ScannerColour;
        if (((INWK.XHi | INWK.YHi | INWK.ZHi) & 0b11000000) != 0)
        {
            return;
        }

        var owner = INWK.DisplayOwner;
        if (_blips.Remove(owner))
        {
            // The ship was on the scanner, so this call erases it
            return;
        }

        // X1 = 125 + x_hi (made even)
        int a = INWK.XHi;
        if (INWK.X < 0)
        {
            a = (-a) & 0xFF;
        }

        int x1 = (a + 125) & 0xFE;

        // Y2 = 220 - z_hi / 4
        a = INWK.ZHi >> 2;
        a = INWK.Z < 0 ? (~a + 35 + 1) & 0xFF : (a + 35) & 0xFF;
        int y2 = a ^ 0xFF;

        // The dot's y-coordinate is Y2 - y_hi / 2, clipped to the scanner
        a = INWK.YHi >> 1;
        int dot = INWK.Y < 0 ? (a + y2) & 0xFF : ((~a & 0xFF) + y2 + 1) & 0xFF;
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
    private void COMPAS()
    {
        if (SSPR != 0)
        {
            // SP1: point to the station
            SPS4();
        }
        else
        {
            SPS1();
        }

        // SP2
        COMX = (SPS2(XX15[0]) + 195) & 0xFF;
        COMY = (204 - SPS2(XX15[1]) - 1) & 0xFF;
        COMC = XX15[2] < 0 ? GREEN2 : YELLOW2;
    }

    /// <summary>SPS4: calculate the normalised vector to the space station in XX15.</summary>
    private void SPS4()
    {
        var station = Slots[1];
        SetK3Coord(0, station?.X ?? 0);
        SetK3Coord(1, station?.Y ?? 0);
        SetK3Coord(2, station?.Z ?? 0);
        TAS2();
    }

    /// <summary>SPS2: X = A / 10, for a signed value A (the compass offset).</summary>
    private static int SPS2(int a)
    {
        int magnitude = (Math.Abs(a) << 1) & 0xFF;
        EliteMaths.Dvid4(magnitude, 20, out int p, out _);
        return a < 0 ? -p : p;
    }

    /// <summary>MSBAR: set the colour of a missile indicator.</summary>
    private void MSBAR(int missile, int colour)
    {
        if (missile >= 1 && missile <= 4)
        {
            mscol[missile] = colour;
        }
    }

    /// <summary>msblob: display the dashboard's missile indicators in green.</summary>
    private void msblob()
    {
        for (int x = 4; x > 0; x--)
        {
            MSBAR(x, x <= NOMSL ? GREEN2 : 0);
        }
    }

    /// <summary>ABORT: disarm the missiles and update the indicators.</summary>
    private void ABORT(int colour) => ABORT2(0xFF, colour);

    /// <summary>ABORT2: set the missile target and update the current missile indicator.</summary>
    private void ABORT2(int target, int colour)
    {
        MSTG = target;
        MSBAR(NOMSL, colour);
        MSAR = colour;
    }

    /// <summary>ECBLB: toggle the E.C.M. bulb.</summary>
    private void ECBLB() => _ecmBulb = !_ecmBulb;

    /// <summary>SPBLB: toggle the space station bulb.</summary>
    private void SPBLB() => _stationBulb = !_stationBulb;

    /// <summary>DET1: show or hide the dashboard (by setting the number of character rows shown).</summary>
    private void DET1(int rows) => _screen.DashboardVisible = rows > 24;
}

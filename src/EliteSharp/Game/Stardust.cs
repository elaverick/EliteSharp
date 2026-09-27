using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// The stardust particles. The particles' coordinates are stored as in the
/// original, with sign-magnitude high bytes in SX, SY and SZ, and low bytes in
/// SXL, SYL and SZL.
/// </summary>
public sealed partial class EliteGame
{
    private readonly object _dustOwner = new();

    /// <summary>A 16-bit sign-magnitude value from a sign-magnitude high byte and a low byte.</summary>
    private static int Sm16(int hi, int lo)
    {
        int magnitude = ((hi & 0x7F) << 8) | (lo & 0xFF);
        return (hi & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>The sign-magnitude high byte of a 16-bit value.</summary>
    private static int SmHi(int value) => ((Math.Abs(value) >> 8) & 0x7F) | (value < 0 ? 0x80 : 0);

    /// <summary>The low byte of a 16-bit sign-magnitude value.</summary>
    private static int SmLo(int value) => Math.Abs(value) & 0xFF;

    /// <summary>MULTS: (A P) = P * |A| with the sign of A, for a sign-magnitude byte A.</summary>
    private static int MULTS(int p, int a)
    {
        int magnitude = (a & 0x7F) * (p & 0xFF);
        return (a & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>The high byte of a 16-bit result as a sign-magnitude byte (as stored in A).</summary>
    private static int HiByte(int value) => SmHi(value);

    /// <summary>DV42 then (P R) >> 2 | 1: the stardust speed factor for a particle.</summary>
    private int DustSpeedFactor(int z)
    {
        EliteMaths.Dvid4(DELTA, z, out int p, out int r);
        return ((((p << 8) | r) >> 2) & 0xFF) | 1;
    }

    /// <summary>STARS: move the stardust for the current view.</summary>
    private void STARS()
    {
        switch (VIEW)
        {
            case 0:
                STARS1();
                break;
            case 1:
                STARS6();
                break;
            default:
                STARS2();
                break;
        }

        UpdateStardustImage();
    }

    /// <summary>STARS1: process the stardust for the front view.</summary>
    private void STARS1()
    {
        for (int y = NOSTM; y > 0; y--)
        {
            int q = DustSpeedFactor(SZ[y]);

            // Move the particle towards us
            int z = ((SZ[y] << 8) | SZL[y]) - DELT4;
            SZL[y] = z & 0xFF;
            SZ[y] = (z >> 8) & 0xFF;

            // Move the particle away from the centre
            int yy = Sm16(SY[y], SYL[y]);
            yy = MoveOutwards(yy, SY[y], q);
            int xx = Sm16(SX[y], SXL[y]);
            xx = MoveOutwards(xx, SX[y], q);

            // Roll: y = y - alpha * x_hi, x = x + alpha * y_hi
            yy = EliteMaths.Add16(MULTS(ALP1, HiByte(xx) ^ ALP2Flipped), yy);
            xx = EliteMaths.Add16(MULTS(ALP1, HiByte(yy) ^ ALP2), xx);

            // Pitch: x = x + 2 * (beta * y / 256)^2, y = y - beta * 256
            int qb = HiByte(MULTS(BET1, HiByte(yy) ^ BET2Flipped));
            int square = EliteMaths.Mult1(EliteMaths.FromSignMagnitude(qb), EliteMaths.FromSignMagnitude(qb));
            xx = EliteMaths.Add16(square * 2, xx);
            SXL[y] = SmLo(xx);

            yy = EliteMaths.Add16(EliteMaths.FromSignMagnitude(BETA ^ 0x80) << 8, yy);
            SYL[y] = SmLo(yy);

            SX[y] = HiByte(xx);
            SY[y] = HiByte(yy);
            if ((SX[y] & 0x7F) >= 120 || (SY[y] & 0x7F) >= 120 || SZ[y] < 16)
            {
                // KILL1: recycle the particle in the distance
                SY[y] = DORND() | 4;
                SX[y] = DORND() | 8;
                SZ[y] = DORND() | 144;
            }
        }
    }

    /// <summary>Add |hi| * q to the magnitude of a 16-bit sign-magnitude value.</summary>
    private static int MoveOutwards(int value, int hi, int q)
    {
        int magnitude = (Math.Abs(value) + (hi & 0x7F) * q) & 0x7FFF;
        return value < 0 || (value == 0 && (hi & 0x80) != 0) ? -magnitude : magnitude;
    }

    /// <summary>Subtract |hi| * q from the magnitude of a 16-bit sign-magnitude value.</summary>
    private static int MoveInwards(int value, int hi, int q)
    {
        int magnitude = Math.Abs(value) - (hi & 0x7F) * q;
        bool negative = value < 0 || (value == 0 && (hi & 0x80) != 0);
        if (magnitude < 0)
        {
            magnitude = -magnitude;
            negative = !negative;
        }

        magnitude &= 0x7FFF;
        return negative ? -magnitude : magnitude;
    }

    /// <summary>STARS6: process the stardust for the rear view.</summary>
    private void STARS6()
    {
        for (int y = NOSTM; y > 0; y--)
        {
            int q = DustSpeedFactor(SZ[y]);

            int xx = MoveInwards(Sm16(SX[y], SXL[y]), SX[y], q);
            int yy = MoveInwards(Sm16(SY[y], SYL[y]), SY[y], q);

            // Move the particle away from us
            int z = ((SZ[y] << 8) | SZL[y]) + DELT4;
            SZL[y] = z & 0xFF;
            SZ[y] = (z >> 8) & 0xFF;

            // Roll
            yy = EliteMaths.Add16(MULTS(ALP1, HiByte(xx) ^ ALP2), yy);
            xx = EliteMaths.Add16(MULTS(ALP1, HiByte(yy) ^ ALP2Flipped), xx);

            // Pitch: x = x - 2 * (beta * y / 256) * x_hi, y = y + beta * 256
            int qb = HiByte(MULTS(BET1, HiByte(yy) ^ BET2Flipped));
            int product = EliteMaths.Mult1(EliteMaths.FromSignMagnitude(qb), EliteMaths.FromSignMagnitude(HiByte(xx) ^ 0x80));
            xx = EliteMaths.Add16(product * 2, xx);
            SXL[y] = SmLo(xx);

            yy = EliteMaths.Add16(EliteMaths.FromSignMagnitude(BETA) << 8, yy);
            SYL[y] = SmLo(yy);

            SX[y] = HiByte(xx);
            SY[y] = HiByte(yy);
            if ((SY[y] & 0x7F) >= 110 || SZ[y] >= 160)
            {
                // KILL6: recycle the particle at the edge of the screen
                int a = DORND() & 0x7F;
                a = (a + 10 + (_carry ? 1 : 0)) & 0xFF;
                SZ[y] = a;
                if ((a & 1) != 0)
                {
                    // ST4: along the top or bottom edge
                    int r = DORND();
                    SX[y] = r;
                    SY[y] = (230 >> 1) | ((r & 1) << 7);
                }
                else
                {
                    // Along the left or right edge
                    int carry = (a >> 1) & 1;
                    SX[y] = (252 >> 1) | (carry << 7);
                    SY[y] = DORND();
                }
            }
        }
    }

    /// <summary>STARS2: process the stardust for the left or right view.</summary>
    private void STARS2()
    {
        int rat = VIEW == 3 ? 0x80 : 0;
        int rat2 = rat ^ 0x80;
        ST2(rat);

        for (int y = NOSTM; y > 0; y--)
        {
            int zz = SZ[y];
            EliteMaths.Dvid4(DELTA, zz >> 3, out int p, out int r);
            int newzp = p;

            // x = x + speed factor (in the direction of the view)
            int xx = EliteMaths.Add16(Sm16(newzp ^ rat2, r), Sm16(SX[y], SXL[y]));

            // x = x + beta * y_hi
            xx = EliteMaths.Add16(MULTS(BET1, SY[y] ^ BET2), xx);

            // y = y - beta * x_hi
            int yy = EliteMaths.Add16(MULTS(BET1, HiByte(xx) ^ BET2Flipped), Sm16(SY[y], SYL[y]));

            // Roll
            int q = HiByte(MULTS(ALP1, HiByte(yy) ^ ALP2));
            int qs = EliteMaths.FromSignMagnitude(q);
            xx = EliteMaths.Mad(qs, EliteMaths.FromSignMagnitude(HiByte(xx) ^ 0x80), xx);
            SXL[y] = SmLo(xx);
            yy = EliteMaths.Mad(qs, EliteMaths.FromSignMagnitude(HiByte(xx)), yy);
            yy = EliteMaths.Add16(EliteMaths.FromSignMagnitude(ALPHA) << 8, yy);
            SYL[y] = SmLo(yy);

            SX[y] = HiByte(xx);
            int limit = (SX[y] & 0x7F) ^ 0x7F;
            if (limit <= newzp)
            {
                // KILL2
                SY[y] = DORND();
                SX[y] = 115 | rat;
                SZ[y] = DORND() | 8;
                continue;
            }

            SY[y] = HiByte(yy);
            if ((SY[y] & 0x7F) >= 116)
            {
                // ST5
                SX[y] = DORND();
                SY[y] = 110 | ALP2Flipped;
                SZ[y] = DORND() | 8;
            }
        }

        ST2(rat);
    }

    /// <summary>ST2: flip the signs of alpha and beta for the side views.</summary>
    private void ST2(int rat)
    {
        ALPHA ^= rat;
        ALP2 ^= rat;
        ALP2Flipped = ALP2 ^ 0x80;
        BET2 ^= rat;
        BET2Flipped = BET2 ^ 0x80;
    }

    /// <summary>FLIP: swap the x and y coordinates of the stardust (when changing view).</summary>
    private void FLIP()
    {
        for (int y = NOSTM; y > 0; y--)
        {
            (SX[y], SY[y]) = (SY[y], SX[y]);
        }

        UpdateStardustImage();
    }

    /// <summary>
    /// Build the on-screen image of the stardust from the particles' current
    /// positions (PIXEL2 for each particle).
    /// </summary>
    private void UpdateStardustImage()
    {
        if (QQ11 != 0)
        {
            return;
        }

        var image = new ObjectImage();
        for (int y = NOSTM; y > 0; y--)
        {
            int sy = SY[y];
            if ((sy & 0x7F) >= CentreY)
            {
                continue;
            }

            int x = CentreX + EliteMaths.FromSignMagnitude(SX[y]);
            int row = CentreY - EliteMaths.FromSignMagnitude(sy);
            image.Rects.AddRange(PixelRects(x & 0xFF, row, SZ[y], DUST));
        }

        _screen.SetImage(_dustOwner, image);
    }
}

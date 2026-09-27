using EliteSharp.Data;

namespace EliteSharp.Game;

/// <summary>
/// Ports of the original's arithmetic routines. Elite stores most numbers in
/// sign-magnitude form; here those values are passed around as ordinary signed
/// integers, but the routines reproduce the original's truncation, rounding and
/// table-driven approximations exactly, so the game behaves identically.
/// </summary>
public static class EliteMaths
{
    /// <summary>The sign bit of a sign-magnitude byte for a signed value.</summary>
    public static int SignBit(int value) => value < 0 ? 0x80 : 0;

    /// <summary>Convert a signed value to a sign-magnitude byte (magnitude clamped to 7 bits).</summary>
    public static int ToSignMagnitude(int value) => (Math.Abs(value) & 0x7F) | SignBit(value);

    /// <summary>Convert a sign-magnitude byte to a signed value.</summary>
    public static int FromSignMagnitude(int value) => (value & 0x80) != 0 ? -(value & 0x7F) : value & 0x7F;

    /// <summary>Apply the sign of a sign bit (bit 7) to a magnitude.</summary>
    public static int WithSign(int magnitude, int signBit) => (signBit & 0x80) != 0 ? -magnitude : magnitude;

    /// <summary>
    /// FMLTU: A = A * Q / 256 for unsigned 8-bit values, using the logarithm
    /// tables. Also returns the C flag, which is set if the result came from the
    /// antilog table.
    /// </summary>
    public static int Fmltu(int a, int q, out bool carry)
    {
        carry = false;
        if (a == 0 || q == 0)
        {
            return 0;
        }

        int low = GameData.LogLow[a] + GameData.LogLow[q];
        int high = GameData.LogHigh[q] + GameData.LogHigh[a] + (low > 0xFF ? 1 : 0);
        if (high <= 0xFF)
        {
            return 0;
        }

        carry = true;
        return GameData.AntiLog[high & 0xFF];
    }

    public static int Fmltu(int a, int q) => Fmltu(a, q, out _);

    /// <summary>
    /// LL28: R = 256 * A / Q using the logarithm tables, returning 255 and
    /// setting the overflow flag if A >= Q.
    /// </summary>
    public static int Ll28(int a, int q, out bool overflow)
    {
        overflow = false;
        if (a >= q)
        {
            overflow = true;
            return 255;
        }

        if (a == 0)
        {
            return 0;
        }

        return Ll28NoCheck(a, q, out overflow);
    }

    public static int Ll28(int a, int q) => Ll28(a, q, out _);

    /// <summary>LL28+4: the log division without the A &gt;= Q check.</summary>
    private static int Ll28NoCheck(int a, int q, out bool overflow)
    {
        overflow = false;
        if (a == 0)
        {
            return 0;
        }

        int low = GameData.LogLow[a] - GameData.LogLow[q];
        int borrow = low < 0 ? 1 : 0;
        int high = GameData.LogHigh[a] - GameData.LogHigh[q] - borrow;
        if (high >= 0)
        {
            overflow = true;
            return 255;
        }

        return GameData.AntiLog[high & 0xFF];
    }

    /// <summary>
    /// DVID4: (P R) = 256 * A / Q, where P is the integer part of A / Q (from an
    /// 8-bit shift-and-subtract loop, including its 8-bit overflow behaviour) and
    /// R is the remainder as a fraction of Q (from the logarithm tables).
    /// </summary>
    public static void Dvid4(int a, int q, out int p, out int r)
    {
        a &= 0xFF;
        q &= 0xFF;
        int carry = (a >> 7) & 1;
        p = (a << 1) & 0xFF;
        int acc = 0;
        for (int i = 0; i < 8; i++)
        {
            // ROL A
            int newCarry = (acc >> 7) & 1;
            acc = ((acc << 1) | carry) & 0xFF;
            carry = newCarry;

            // CMP Q / SBC Q (the carry from the ROL is overwritten by the CMP)
            if (acc >= q)
            {
                acc = (acc - q) & 0xFF;
                carry = 1;
            }
            else
            {
                carry = 0;
            }

            // ROL P
            newCarry = (p >> 7) & 1;
            p = ((p << 1) | carry) & 0xFF;
            carry = newCarry;
        }

        r = acc == 0 ? 0 : Ll28NoCheck(acc, q, out _);
    }

    /// <summary>
    /// DVID4K (and DVID4 without the log remainder): P = A / Q with the remainder
    /// kept, using the version with the overflow check (BCS DV8K).
    /// </summary>
    public static int Dvid4K(int a, int q)
    {
        a &= 0xFF;
        int carry = (a >> 7) & 1;
        int p = (a << 1) & 0xFF;
        int acc = 0;
        for (int i = 0; i < 8; i++)
        {
            int newCarry = (acc >> 7) & 1;
            acc = ((acc << 1) | carry) & 0xFF;
            if (newCarry == 1 || acc >= q)
            {
                acc = (acc - q) & 0xFF;
                carry = 1;
            }
            else
            {
                carry = 0;
            }

            newCarry = (p >> 7) & 1;
            p = ((p << 1) | carry) & 0xFF;
            carry = newCarry;
        }

        return p;
    }

    /// <summary>
    /// DVID3B2: K(3 2 1 0) = (A P+1 P) / (z_sign z_hi z_lo), returning the
    /// result as a signed integer. The numerator and denominator are signed
    /// 24-bit values (the sign byte may contain magnitude bits), and the result
    /// is 256 * numerator / denominator, computed with the original's
    /// normalise-and-divide-top-bytes approach.
    /// </summary>
    public static int Dvid3B2(int numerator, int denominator)
    {
        int nMag = Math.Abs(numerator);
        int p = nMag & 0xFF;
        int p1 = (nMag >> 8) & 0xFF;
        int p2 = ((nMag >> 16) & 0x7F) | SignBit(numerator);

        int dMag = Math.Abs(denominator);
        int q = (dMag & 0xFF) | 1;
        int r = (dMag >> 8) & 0xFF;
        int s = ((dMag >> 16) & 0x7F) | SignBit(denominator);

        // DVID3B
        p |= 1;
        int t = (p2 ^ s) & 0x80;
        int y = 0;
        int a = p2 & 0x7F;

        // DVL9: shift (A P+1 P) left until A >= 64
        while (a < 64)
        {
            int c0 = (p >> 7) & 1;
            p = (p << 1) & 0xFF;
            int c1 = (p1 >> 7) & 1;
            p1 = ((p1 << 1) | c0) & 0xFF;
            a = ((a << 1) | c1) & 0xFF;
            y++;
        }

        p2 = a;

        // DVL6: shift (|S| R Q) left until bit 7 of the top byte is set
        a = s & 0x7F;
        do
        {
            y--;
            int c0 = (q >> 7) & 1;
            q = (q << 1) & 0xFF;
            int c1 = (r >> 7) & 1;
            r = ((r << 1) | c0) & 0xFF;
            a = ((a << 1) | c1) & 0xFF;
        }
        while ((a & 0x80) == 0);

        q = a;

        // LL31: R = 256 * A / Q
        int result = 254;
        a = p2;
        while (true)
        {
            int carryOut = (a >> 7) & 1;
            a = (a << 1) & 0xFF;
            int bit;
            if (carryOut == 1)
            {
                a = (a - q) & 0xFF;
                bit = 1;
            }
            else if (a >= q)
            {
                a = (a - q) & 0xFF;
                bit = 1;
            }
            else
            {
                bit = 0;
            }

            int rolCarry = (result >> 7) & 1;
            result = ((result << 1) | bit) & 0xFF;
            if (rolCarry == 0)
            {
                break;
            }
        }

        long k;
        if (y < 0)
        {
            k = (long)result << -y;
        }
        else if (y == 0)
        {
            k = result;
        }
        else
        {
            k = result >> y;
        }

        k &= 0x7FFFFFFF;
        return t != 0 ? -(int)k : (int)k;
    }

    /// <summary>LL5: Q = SQRT(R Q), the 8-bit square root of a 16-bit value.</summary>
    public static int Ll5(int value)
    {
        int y = (value >> 8) & 0xFF;
        int s = value & 0xFF;
        int x = 0;
        int q = 0;
        for (int t = 0; t < 8; t++)
        {
            int carry;
            if (x < q)
            {
                carry = 0;
            }
            else if (x > q || y >= 64)
            {
                // SBC #64 then SBC Q, both with the C flag set on entry
                int ySub = y - 64;
                int c = ySub >= 0 ? 1 : 0;
                y = ySub & 0xFF;
                int xSub = x - q - (1 - c);
                carry = xSub >= 0 ? 1 : 0;
                x = xSub & 0xFF;
            }
            else
            {
                carry = 0;
            }

            // ROL Q
            q = ((q << 1) | carry) & 0xFF;

            for (int pair = 0; pair < 2; pair++)
            {
                int c0 = (s >> 7) & 1;
                s = (s << 1) & 0xFF;
                int c1 = (y >> 7) & 1;
                y = ((y << 1) | c0) & 0xFF;
                x = ((x << 1) | c1) & 0xFF;
            }
        }

        return q;
    }

    /// <summary>SQUA2: (A P) = A * A for an unsigned 8-bit value.</summary>
    public static int Squa2(int a) => (a & 0xFF) * (a & 0xFF);

    /// <summary>
    /// MULT1: (A P) = Q * A for two sign-magnitude bytes (passed as signed
    /// values), giving an exact signed 16-bit product of the 7-bit magnitudes.
    /// </summary>
    public static int Mult1(int q, int a)
    {
        int magnitude = (Math.Abs(q) & 0x7F) * (Math.Abs(a) & 0x7F);
        return ((q < 0) ^ (a < 0)) ? -magnitude : magnitude;
    }

    /// <summary>MAD: (A X) = Q * A + (S R).</summary>
    public static int Mad(int q, int a, int sr) => Add16(Mult1(q, a), sr);

    /// <summary>
    /// ADD: (A X) = (A P) + (S R) for 16-bit sign-magnitude values. The sum is
    /// exact apart from overflow into the sign bit, which is reproduced here.
    /// </summary>
    public static int Add16(int ap, int sr)
    {
        int sum = ap + sr;
        if (Math.Abs(sum) > 0x7FFF)
        {
            // The magnitude overflows into bit 15, which the original treats as
            // the sign, so wrap the magnitude and flip the sign
            int magnitude = Math.Abs(sum) & 0x7FFF;
            return sum < 0 ? magnitude : -magnitude;
        }

        return sum;
    }

    /// <summary>
    /// MULT3: K(3 2 1 0) = (A P+1 P) * Q, a signed 24-bit value multiplied by a
    /// sign-magnitude byte, giving an exact signed 32-bit result.
    /// </summary>
    public static long Mult3(int value24, int q)
    {
        long magnitude = (long)(Math.Abs(value24) & 0x7FFFFF) * (Math.Abs(q) & 0x7F);
        return ((value24 < 0) ^ (q < 0)) ? -magnitude : magnitude;
    }

    /// <summary>
    /// TIS2: A = A / Q, where A is a sign-magnitude byte and the result uses 96 to
    /// represent 1 (the maximum returned).
    /// </summary>
    public static int Tis2(int a, int q)
    {
        int sign = a < 0 ? -1 : 1;
        int magnitude = Math.Abs(a) & 0x7F;
        if (magnitude >= q)
        {
            return sign * 96;
        }

        int t = 0xFE;
        int acc = magnitude;
        while (true)
        {
            acc = (acc << 1) & 0xFF;
            int bit = 0;
            if (acc >= q)
            {
                acc -= q;
                bit = 1;
            }

            int carry = (t >> 7) & 1;
            t = ((t << 1) | bit) & 0xFF;
            if (carry == 0)
            {
                break;
            }
        }

        t >>= 2;
        int eighth = t >> 1;
        int roundingCarry = t & 1;
        int result = eighth + t + roundingCarry;
        return sign * (result & 0x7F);
    }

    /// <summary>
    /// DVID96 (the end of TIS1): divide the magnitude of the high byte of a
    /// 16-bit sign-magnitude value by 96, returning a signed byte result.
    /// </summary>
    public static int Dvid96(int value16)
    {
        int a = (Math.Abs(value16) >> 8) & 0x7F;
        int t1 = 0xFE;
        while (true)
        {
            a = (a << 1) & 0xFF;
            int bit = 0;
            if (a >= 96)
            {
                a -= 96;
                bit = 1;
            }

            int carry = (t1 >> 7) & 1;
            t1 = ((t1 << 1) | bit) & 0xFF;
            if (carry == 0)
            {
                break;
            }
        }

        int magnitude = t1 & 0x7F;
        int result = value16 < 0 ? -magnitude : magnitude;

        // The sign bit is OR'd into the result, so a set bit 7 in T1 would
        // interfere with the sign, but the result is always < 128 here
        return result;
    }

    /// <summary>TIS1: (A ?) = (-X * A + (S R)) / 96, returning a signed byte.</summary>
    public static int Tis1(int x, int a, int sr) => Dvid96(Mad(x, -a, sr));

    /// <summary>
    /// DVIDT: (P+1 A) = (A P) / Q, dividing a 16-bit sign-magnitude value by a
    /// sign-magnitude byte, returning the low byte of the quotient with the sign.
    /// </summary>
    public static int Dvidt(int ap, int q)
    {
        // The shift-and-subtract loop divides the 15-bit magnitude by the 7-bit
        // magnitude of Q (giving all 1s if Q is zero), and the low byte of the
        // quotient is returned with the sign bit OR'd in, so bit 7 of the
        // quotient merges with the sign, just as in the original
        int sign = ((ap < 0) ^ (q < 0)) ? 0x80 : 0;
        int magnitude = Math.Abs(ap) & 0x7FFF;
        int qMag = Math.Abs(q) & 0x7F;
        int quotient = qMag == 0 ? 0xFFFF : magnitude / qMag;
        return FromSignMagnitude((quotient & 0xFF) | sign);
    }

    /// <summary>
    /// ARCTAN: A = arctan(P / Q) for sign-magnitude bytes P and Q, returning an
    /// angle in the range 0-128 where 256 is a full circle.
    /// </summary>
    public static int Arctan(int p, int q)
    {
        bool differentSigns = (p < 0) ^ (q < 0);
        int qm = Math.Abs(q) & 0x7F;
        if (qm == 0)
        {
            return 63;
        }

        int q2 = (qm << 1) & 0xFF;
        int p2 = ((Math.Abs(p) & 0x7F) << 1) & 0xFF;
        int angle;
        if (p2 >= q2)
        {
            // AR1: arctan(t) = 64 - arctan(1 / t)
            angle = 64 - ArcTanLookup(q2, p2);
        }
        else
        {
            angle = ArcTanLookup(p2, q2);
        }

        return differentSigns ? 128 - angle : angle;
    }

    /// <summary>ARS1: A = arctan(A / Q) from the ACT table, via LL28.</summary>
    private static int ArcTanLookup(int a, int q)
    {
        int r = Ll28(a, q);
        return GameData.Arctan[r >> 3];
    }

    /// <summary>
    /// NORM: normalise a vector of signed bytes (magnitudes up to 127) so that 96
    /// represents 1, returning the normalised vector and the original length.
    /// </summary>
    public static (int X, int Y, int Z) Normalise(int x, int y, int z, out int length)
    {
        int sum = Squa2(Math.Abs(x) & 0x7F) + Squa2(Math.Abs(y) & 0x7F) + Squa2(Math.Abs(z) & 0x7F);
        length = Ll5(sum & 0xFFFF);
        if (length == 0)
        {
            return (0, 0, 0);
        }

        return (Tis2(x, length), Tis2(y, length), Tis2(z, length));
    }

    /// <summary>
    /// TAS2: normalise a vector of 16-bit magnitudes with separate signs, by
    /// shifting the three coordinates left as far as possible, taking the high
    /// bytes, and then calling NORM.
    /// </summary>
    public static (int X, int Y, int Z) NormaliseLarge(int x, int y, int z, out int length)
    {
        int xm = Math.Abs(x) & 0xFFFF;
        int ym = Math.Abs(y) & 0xFFFF;
        int zm = Math.Abs(z) & 0xFFFF;

        // The sign bytes K3+2, K3+5 and K3+8 are OR'd into the results, so if they
        // contain magnitude bits (i.e. the value is 65536 or more), those bits
        // leak into the result, just as in the original
        int xs = (Math.Abs(x) >> 16) & 0x7F;
        int ys = (Math.Abs(y) >> 16) & 0x7F;
        int zs = (Math.Abs(z) >> 16) & 0x7F;

        int lowOr = ((xm | ym | zm) & 0xFF) | 1;
        int highOr = ((xm | ym | zm) >> 8) & 0xFF;

        // TAL2: shift (A K3+9) left until a 1 falls out of the top
        while (true)
        {
            int c0 = (lowOr >> 7) & 1;
            lowOr = (lowOr << 1) & 0xFF;
            int c1 = (highOr >> 7) & 1;
            highOr = ((highOr << 1) | c0) & 0xFF;
            if (c1 == 1)
            {
                break;
            }

            xm = (xm << 1) & 0xFFFF;
            ym = (ym << 1) & 0xFFFF;
            zm = (zm << 1) & 0xFFFF;
        }

        int hx = ((xm >> 8) >> 1) | xs;
        int hy = ((ym >> 8) >> 1) | ys;
        int hz = ((zm >> 8) >> 1) | zs;
        return Normalise(x < 0 ? -hx : hx, y < 0 ? -hy : hy, z < 0 ? -hz : hz, out length);
    }

    /// <summary>
    /// MVT6: (A P+2 P+1) = (x_sign x_hi x_lo) + (A P+2 P+1), adding a signed
    /// 16-bit value to a coordinate but only using the coordinate's low 16 bits
    /// of magnitude, and giving a 16-bit magnitude result with a sign.
    /// </summary>
    public static int AddCoordinate16(int coordinate, int value)
    {
        int cMag = Math.Abs(coordinate) & 0xFFFF;
        int vMag = Math.Abs(value) & 0xFFFF;
        bool cNeg = coordinate < 0;
        bool vNeg = value < 0;
        if (cNeg == vNeg)
        {
            int sum = (vMag + cMag) & 0xFFFF;
            return vNeg ? -sum : sum;
        }

        int diff = cMag - vMag;
        if (diff >= 0)
        {
            return cNeg ? -diff : diff;
        }

        return vNeg ? diff : -diff;
    }
}

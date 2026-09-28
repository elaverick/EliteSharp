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
    public static int MultiplyFraction(int value, int multiplier, out bool carry)
    {
        carry = false;
        if (value == 0 || multiplier == 0)
        {
            return 0;
        }

        int low = GameData.LogLow[value] + GameData.LogLow[multiplier];
        int high = GameData.LogHigh[multiplier] + GameData.LogHigh[value] + (low > 0xFF ? 1 : 0);
        if (high <= 0xFF)
        {
            return 0;
        }

        carry = true;
        return GameData.AntiLog[high & 0xFF];
    }

    public static int MultiplyFraction(int value, int multiplier) => MultiplyFraction(value, multiplier, out _);

    /// <summary>
    /// LL28: R = 256 * A / Q using the logarithm tables, returning 255 and
    /// setting the overflow flag if A >= Q.
    /// </summary>
    public static int DivideFraction(int dividend, int divisor, out bool overflow)
    {
        overflow = false;
        if (dividend >= divisor)
        {
            overflow = true;
            return 255;
        }

        if (dividend == 0)
        {
            return 0;
        }

        return DivideFractionUnchecked(dividend, divisor, out overflow);
    }

    public static int DivideFraction(int dividend, int divisor) => DivideFraction(dividend, divisor, out _);

    /// <summary>LL28+4: the log division without the A &gt;= Q check.</summary>
    private static int DivideFractionUnchecked(int dividend, int divisor, out bool overflow)
    {
        overflow = false;
        if (dividend == 0)
        {
            return 0;
        }

        int low = GameData.LogLow[dividend] - GameData.LogLow[divisor];
        int borrow = low < 0 ? 1 : 0;
        int high = GameData.LogHigh[dividend] - GameData.LogHigh[divisor] - borrow;
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
    public static void DivideWithRemainder(int dividend, int divisor, out int quotient, out int remainderFraction)
    {
        dividend &= 0xFF;
        divisor &= 0xFF;
        int carry = (dividend >> 7) & 1;
        quotient = (dividend << 1) & 0xFF;
        int remainder = 0;
        for (int i = 0; i < 8; i++)
        {
            // ROL A
            int newCarry = (remainder >> 7) & 1;
            remainder = ((remainder << 1) | carry) & 0xFF;
            carry = newCarry;

            // CMP Q / SBC Q (the carry from the ROL is overwritten by the CMP)
            if (remainder >= divisor)
            {
                remainder = (remainder - divisor) & 0xFF;
                carry = 1;
            }
            else
            {
                carry = 0;
            }

            // ROL P
            newCarry = (quotient >> 7) & 1;
            quotient = ((quotient << 1) | carry) & 0xFF;
            carry = newCarry;
        }

        remainderFraction = remainder == 0 ? 0 : DivideFractionUnchecked(remainder, divisor, out _);
    }

    /// <summary>
    /// DVID3B2: K(3 2 1 0) = (A P+1 P) / (z_sign z_hi z_lo), returning the
    /// result as a signed integer. The numerator and denominator are signed
    /// 24-bit values (the sign byte may contain magnitude bits), and the result
    /// is 256 * numerator / denominator, computed with the original's
    /// normalise-and-divide-top-bytes approach.
    /// </summary>
    public static int DivideScaled(int numerator, int denominator)
    {
        int numeratorMagnitude = Math.Abs(numerator);
        int numeratorLow = numeratorMagnitude & 0xFF;
        int numeratorMiddle = (numeratorMagnitude >> 8) & 0xFF;
        int numeratorHigh = ((numeratorMagnitude >> 16) & 0x7F) | SignBit(numerator);

        int denominatorMagnitude = Math.Abs(denominator);
        int denominatorLow = (denominatorMagnitude & 0xFF) | 1;
        int denominatorMiddle = (denominatorMagnitude >> 8) & 0xFF;
        int denominatorHigh = ((denominatorMagnitude >> 16) & 0x7F) | SignBit(denominator);

        // DVID3B
        numeratorLow |= 1;
        int resultSign = (numeratorHigh ^ denominatorHigh) & 0x80;
        int scale = 0;
        int top = numeratorHigh & 0x7F;

        // DVL9: shift (A P+1 P) left until A >= 64
        while (top < 64)
        {
            int carryLow = (numeratorLow >> 7) & 1;
            numeratorLow = (numeratorLow << 1) & 0xFF;
            int carryHigh = (numeratorMiddle >> 7) & 1;
            numeratorMiddle = ((numeratorMiddle << 1) | carryLow) & 0xFF;
            top = ((top << 1) | carryHigh) & 0xFF;
            scale++;
        }

        numeratorHigh = top;

        // DVL6: shift (|S| R Q) left until bit 7 of the top byte is set
        top = denominatorHigh & 0x7F;
        do
        {
            scale--;
            int carryLow = (denominatorLow >> 7) & 1;
            denominatorLow = (denominatorLow << 1) & 0xFF;
            int carryHigh = (denominatorMiddle >> 7) & 1;
            denominatorMiddle = ((denominatorMiddle << 1) | carryLow) & 0xFF;
            top = ((top << 1) | carryHigh) & 0xFF;
        }
        while ((top & 0x80) == 0);

        denominatorLow = top;

        // LL31: R = 256 * A / Q
        int result = 254;
        top = numeratorHigh;
        while (true)
        {
            int carryOut = (top >> 7) & 1;
            top = (top << 1) & 0xFF;
            int bit;
            if (carryOut == 1)
            {
                top = (top - denominatorLow) & 0xFF;
                bit = 1;
            }
            else if (top >= denominatorLow)
            {
                top = (top - denominatorLow) & 0xFF;
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

        long scaled;
        if (scale < 0)
        {
            scaled = (long)result << -scale;
        }
        else if (scale == 0)
        {
            scaled = result;
        }
        else
        {
            scaled = result >> scale;
        }

        scaled &= 0x7FFFFFFF;
        return resultSign != 0 ? -(int)scaled : (int)scaled;
    }

    /// <summary>LL5: Q = SQRT(R Q), the 8-bit square root of a 16-bit value.</summary>
    public static int SquareRoot(int value)
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
                int carryLow = (s >> 7) & 1;
                s = (s << 1) & 0xFF;
                int carryHigh = (y >> 7) & 1;
                y = ((y << 1) | carryLow) & 0xFF;
                x = ((x << 1) | carryHigh) & 0xFF;
            }
        }

        return q;
    }

    /// <summary>SQUA2: (A P) = A * A for an unsigned 8-bit value.</summary>
    public static int Square(int value) => (value & 0xFF) * (value & 0xFF);

    /// <summary>
    /// MULT1: (A P) = Q * A for two sign-magnitude bytes (passed as signed
    /// values), giving an exact signed 16-bit product of the 7-bit magnitudes.
    /// </summary>
    public static int MultiplySigned(int multiplicand, int multiplier)
    {
        int magnitude = (Math.Abs(multiplicand) & 0x7F) * (Math.Abs(multiplier) & 0x7F);
        return ((multiplicand < 0) ^ (multiplier < 0)) ? -magnitude : magnitude;
    }

    /// <summary>MAD: (A X) = Q * A + (S R).</summary>
    public static int MultiplyAdd(int multiplicand, int multiplier, int addend) => Add16(MultiplySigned(multiplicand, multiplier), addend);

    /// <summary>
    /// ADD: (A X) = (A P) + (S R) for 16-bit sign-magnitude values. The sum is
    /// exact apart from overflow into the sign bit, which is reproduced here.
    /// </summary>
    public static int Add16(int value, int addend)
    {
        int sum = value + addend;
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
    public static long Multiply24(int value, int multiplier)
    {
        long magnitude = (long)(Math.Abs(value) & 0x7FFFFF) * (Math.Abs(multiplier) & 0x7F);
        return ((value < 0) ^ (multiplier < 0)) ? -magnitude : magnitude;
    }

    /// <summary>
    /// TIS2: A = A / Q, where A is a sign-magnitude byte and the result uses 96 to
    /// represent 1 (the maximum returned).
    /// </summary>
    public static int DivideToUnit(int value, int divisor)
    {
        int sign = value < 0 ? -1 : 1;
        int magnitude = Math.Abs(value) & 0x7F;
        if (magnitude >= divisor)
        {
            return sign * 96;
        }

        int quotient = 0xFE;
        int remainder = magnitude;
        while (true)
        {
            remainder = (remainder << 1) & 0xFF;
            int bit = 0;
            if (remainder >= divisor)
            {
                remainder -= divisor;
                bit = 1;
            }

            int carry = (quotient >> 7) & 1;
            quotient = ((quotient << 1) | bit) & 0xFF;
            if (carry == 0)
            {
                break;
            }
        }

        quotient >>= 2;
        int eighth = quotient >> 1;
        int roundingCarry = quotient & 1;
        int result = eighth + quotient + roundingCarry;
        return sign * (result & 0x7F);
    }

    /// <summary>
    /// DVID96 (the end of TIS1): divide the magnitude of the high byte of a
    /// 16-bit sign-magnitude value by 96, returning a signed byte result.
    /// </summary>
    public static int DivideBy96(int value)
    {
        int remainder = (Math.Abs(value) >> 8) & 0x7F;
        int quotient = 0xFE;
        while (true)
        {
            remainder = (remainder << 1) & 0xFF;
            int bit = 0;
            if (remainder >= 96)
            {
                remainder -= 96;
                bit = 1;
            }

            int carry = (quotient >> 7) & 1;
            quotient = ((quotient << 1) | bit) & 0xFF;
            if (carry == 0)
            {
                break;
            }
        }

        int magnitude = quotient & 0x7F;
        int result = value < 0 ? -magnitude : magnitude;

        // The sign bit is OR'd into the result, so a set bit 7 in T1 would
        // interfere with the sign, but the result is always < 128 here
        return result;
    }

    /// <summary>TIS1: (A ?) = (-X * A + (S R)) / 96, returning a signed byte.</summary>
    public static int MultiplyAddDivideBy96(int multiplicand, int multiplier, int addend) => DivideBy96(MultiplyAdd(multiplicand, -multiplier, addend));

    /// <summary>
    /// DVIDT: (P+1 A) = (A P) / Q, dividing a 16-bit sign-magnitude value by a
    /// sign-magnitude byte, returning the low byte of the quotient with the sign.
    /// </summary>
    public static int DivideSigned(int dividend, int divisor)
    {
        // The shift-and-subtract loop divides the 15-bit magnitude by the 7-bit
        // magnitude of Q (giving all 1s if Q is zero), and the low byte of the
        // quotient is returned with the sign bit OR'd in, so bit 7 of the
        // quotient merges with the sign, just as in the original
        int sign = ((dividend < 0) ^ (divisor < 0)) ? 0x80 : 0;
        int magnitude = Math.Abs(dividend) & 0x7FFF;
        int divisorMagnitude = Math.Abs(divisor) & 0x7F;
        int quotient = divisorMagnitude == 0 ? 0xFFFF : magnitude / divisorMagnitude;
        return FromSignMagnitude((quotient & 0xFF) | sign);
    }

    /// <summary>
    /// ARCTAN: A = arctan(P / Q) for sign-magnitude bytes P and Q, returning an
    /// angle in the range 0-128 where 256 is a full circle.
    /// </summary>
    public static int Arctan(int numerator, int denominator)
    {
        bool differentSigns = (numerator < 0) ^ (denominator < 0);
        int denominatorMagnitude = Math.Abs(denominator) & 0x7F;
        if (denominatorMagnitude == 0)
        {
            return 63;
        }

        int doubledDenominator = (denominatorMagnitude << 1) & 0xFF;
        int doubledNumerator = ((Math.Abs(numerator) & 0x7F) << 1) & 0xFF;
        int angle;
        if (doubledNumerator >= doubledDenominator)
        {
            // AR1: arctan(t) = 64 - arctan(1 / t)
            angle = 64 - ArcTanLookup(doubledDenominator, doubledNumerator);
        }
        else
        {
            angle = ArcTanLookup(doubledNumerator, doubledDenominator);
        }

        return differentSigns ? 128 - angle : angle;
    }

    /// <summary>ARS1: A = arctan(A / Q) from the ACT table, via LL28.</summary>
    private static int ArcTanLookup(int numerator, int denominator)
    {
        int ratio = DivideFraction(numerator, denominator);
        return GameData.Arctan[ratio >> 3];
    }

    /// <summary>
    /// NORM: normalise a vector of signed bytes (magnitudes up to 127) so that 96
    /// represents 1, returning the normalised vector and the original length.
    /// </summary>
    public static (int X, int Y, int Z) Normalise(int x, int y, int z, out int length)
    {
        int sum = Square(Math.Abs(x) & 0x7F) + Square(Math.Abs(y) & 0x7F) + Square(Math.Abs(z) & 0x7F);
        length = SquareRoot(sum & 0xFFFF);
        if (length == 0)
        {
            return (0, 0, 0);
        }

        return (DivideToUnit(x, length), DivideToUnit(y, length), DivideToUnit(z, length));
    }

    /// <summary>
    /// TAS2: normalise a vector of 16-bit magnitudes with separate signs, by
    /// shifting the three coordinates left as far as possible, taking the high
    /// bytes, and then calling NORM.
    /// </summary>
    public static (int X, int Y, int Z) NormaliseLarge(int x, int y, int z, out int length)
    {
        int xMagnitude = Math.Abs(x) & 0xFFFF;
        int yMagnitude = Math.Abs(y) & 0xFFFF;
        int zMagnitude = Math.Abs(z) & 0xFFFF;

        // The sign bytes K3+2, K3+5 and K3+8 are OR'd into the results, so if they
        // contain magnitude bits (i.e. the value is 65536 or more), those bits
        // leak into the result, just as in the original
        int xHighBits = (Math.Abs(x) >> 16) & 0x7F;
        int yHighBits = (Math.Abs(y) >> 16) & 0x7F;
        int zHighBits = (Math.Abs(z) >> 16) & 0x7F;

        int lowOr = ((xMagnitude | yMagnitude | zMagnitude) & 0xFF) | 1;
        int highOr = ((xMagnitude | yMagnitude | zMagnitude) >> 8) & 0xFF;

        // TAL2: shift (A K3+9) left until a 1 falls out of the top
        while (true)
        {
            int carryLow = (lowOr >> 7) & 1;
            lowOr = (lowOr << 1) & 0xFF;
            int carryHigh = (highOr >> 7) & 1;
            highOr = ((highOr << 1) | carryLow) & 0xFF;
            if (carryHigh == 1)
            {
                break;
            }

            xMagnitude = (xMagnitude << 1) & 0xFFFF;
            yMagnitude = (yMagnitude << 1) & 0xFFFF;
            zMagnitude = (zMagnitude << 1) & 0xFFFF;
        }

        int xHigh = ((xMagnitude >> 8) >> 1) | xHighBits;
        int yHigh = ((yMagnitude >> 8) >> 1) | yHighBits;
        int zHigh = ((zMagnitude >> 8) >> 1) | zHighBits;
        return Normalise(x < 0 ? -xHigh : xHigh, y < 0 ? -yHigh : yHigh, z < 0 ? -zHigh : zHigh, out length);
    }

    /// <summary>
    /// MVT6: (A P+2 P+1) = (x_sign x_hi x_lo) + (A P+2 P+1), adding a signed
    /// 16-bit value to a coordinate but only using the coordinate's low 16 bits
    /// of magnitude, and giving a 16-bit magnitude result with a sign.
    /// </summary>
    public static int AddCoordinate16(int coordinate, int value)
    {
        int coordinateMagnitude = Math.Abs(coordinate) & 0xFFFF;
        int valueMagnitude = Math.Abs(value) & 0xFFFF;
        bool cNeg = coordinate < 0;
        bool vNeg = value < 0;
        if (cNeg == vNeg)
        {
            int sum = (valueMagnitude + coordinateMagnitude) & 0xFFFF;
            return vNeg ? -sum : sum;
        }

        int diff = coordinateMagnitude - valueMagnitude;
        if (diff >= 0)
        {
            return cNeg ? -diff : diff;
        }

        return vNeg ? diff : -diff;
    }
}

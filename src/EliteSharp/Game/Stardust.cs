using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Game;

/// <summary>
/// The stardust particles. The particles' coordinates are stored as in the
/// original, with sign-magnitude high bytes in SX, SY and SZ, and low bytes in
/// SXL, SYL and SZL.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The owner of the stardust in the 3D world.</summary>
    private readonly object _dustOwner = new();

    /// <summary>A 16-bit sign-magnitude value from a sign-magnitude high byte and a low byte.</summary>
    private static int SignMagnitude16(int hi, int lo)
    {
        int magnitude = ((hi & 0x7F) << 8) | (lo & 0xFF);
        return (hi & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>The sign-magnitude high byte of a 16-bit value.</summary>
    private static int SignMagnitudeHigh(int value) => ((Math.Abs(value) >> 8) & 0x7F) | (value < 0 ? 0x80 : 0);

    /// <summary>The low byte of a 16-bit sign-magnitude value.</summary>
    private static int SignMagnitudeLow(int value) => Math.Abs(value) & 0xFF;

    /// <summary>MULTS: (A P) = P * |A| with the sign of A, for a sign-magnitude byte A.</summary>
    private static int MultiplyBySignMagnitude(int value, int signMagnitude)
    {
        int magnitude = (signMagnitude & 0x7F) * (value & 0xFF);
        return (signMagnitude & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>The high byte of a 16-bit result as a sign-magnitude byte (as stored in A).</summary>
    private static int HiByte(int value) => SignMagnitudeHigh(value);

    /// <summary>DV42 then (P R) >> 2 | 1: the stardust speed factor for a particle.</summary>
    private int DustSpeedFactor(int z)
    {
        EliteMaths.DivideWithRemainder(_speed, z, out int quotient, out int remainder);
        return ((((quotient << 8) | remainder) >> 2) & 0xFF) | 1;
    }

    /// <summary>STARS: move the stardust for the current view.</summary>
    private void MoveStardust()
    {
        switch (_view)
        {
            case 0:
                MoveStardustFront();
                break;
            case 1:
                MoveStardustRear();
                break;
            default:
                MoveStardustSide();
                break;
        }

        UpdateStardustImage();
    }

    /// <summary>STARS1: process the stardust for the front view.</summary>
    private void MoveStardustFront()
    {
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            int speedFactor = DustSpeedFactor(_dustZ[particle]);

            // Move the particle towards us
            int z = ((_dustZ[particle] << 8) | _dustZLow[particle]) - _speedTimes64;
            _dustZLow[particle] = z & 0xFF;
            _dustZ[particle] = (z >> 8) & 0xFF;

            // Move the particle away from the centre
            int positionY = SignMagnitude16(_dustY[particle], _dustYLow[particle]);
            positionY = MoveOutwards(positionY, _dustY[particle], speedFactor);
            int positionX = SignMagnitude16(_dustX[particle], _dustXLow[particle]);
            positionX = MoveOutwards(positionX, _dustX[particle], speedFactor);

            // Roll: y = y - alpha * x_hi, x = x + alpha * y_hi
            positionY = EliteMaths.Add16(MultiplyBySignMagnitude(_rollMagnitude, HiByte(positionX) ^ _rollSignFlipped), positionY);
            positionX = EliteMaths.Add16(MultiplyBySignMagnitude(_rollMagnitude, HiByte(positionY) ^ _rollSign), positionX);

            // Pitch: x = x + 2 * (beta * y / 256)^2, y = y - beta * 256
            int pitchTerm = HiByte(MultiplyBySignMagnitude(_pitchMagnitude, HiByte(positionY) ^ _pitchSignFlipped));
            int square = EliteMaths.MultiplySigned(EliteMaths.FromSignMagnitude(pitchTerm), EliteMaths.FromSignMagnitude(pitchTerm));
            positionX = EliteMaths.Add16(square * 2, positionX);
            _dustXLow[particle] = SignMagnitudeLow(positionX);

            positionY = EliteMaths.Add16(EliteMaths.FromSignMagnitude(_pitchAngle ^ 0x80) << 8, positionY);
            _dustYLow[particle] = SignMagnitudeLow(positionY);

            _dustX[particle] = HiByte(positionX);
            _dustY[particle] = HiByte(positionY);
            if ((_dustX[particle] & 0x7F) >= 120 || (_dustY[particle] & 0x7F) >= 120 || _dustZ[particle] < 16)
            {
                // KILL1: recycle the particle in the distance
                _dustY[particle] = NextRandom() | 4;
                _dustX[particle] = NextRandom() | 8;
                _dustZ[particle] = NextRandom() | 144;
            }
        }
    }

    /// <summary>Add |hi| * q to the magnitude of a 16-bit sign-magnitude value.</summary>
    private static int MoveOutwards(int value, int high, int speedFactor)
    {
        int magnitude = (Math.Abs(value) + (high & 0x7F) * speedFactor) & 0x7FFF;
        return value < 0 || (value == 0 && (high & 0x80) != 0) ? -magnitude : magnitude;
    }

    /// <summary>Subtract |hi| * q from the magnitude of a 16-bit sign-magnitude value.</summary>
    private static int MoveInwards(int value, int high, int speedFactor)
    {
        int magnitude = Math.Abs(value) - (high & 0x7F) * speedFactor;
        bool negative = value < 0 || (value == 0 && (high & 0x80) != 0);
        if (magnitude < 0)
        {
            magnitude = -magnitude;
            negative = !negative;
        }

        magnitude &= 0x7FFF;
        return negative ? -magnitude : magnitude;
    }

    /// <summary>STARS6: process the stardust for the rear view.</summary>
    private void MoveStardustRear()
    {
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            int speedFactor = DustSpeedFactor(_dustZ[particle]);

            int positionX = MoveInwards(SignMagnitude16(_dustX[particle], _dustXLow[particle]), _dustX[particle], speedFactor);
            int positionY = MoveInwards(SignMagnitude16(_dustY[particle], _dustYLow[particle]), _dustY[particle], speedFactor);

            // Move the particle away from us
            int z = ((_dustZ[particle] << 8) | _dustZLow[particle]) + _speedTimes64;
            _dustZLow[particle] = z & 0xFF;
            _dustZ[particle] = (z >> 8) & 0xFF;

            // Roll
            positionY = EliteMaths.Add16(MultiplyBySignMagnitude(_rollMagnitude, HiByte(positionX) ^ _rollSign), positionY);
            positionX = EliteMaths.Add16(MultiplyBySignMagnitude(_rollMagnitude, HiByte(positionY) ^ _rollSignFlipped), positionX);

            // Pitch: x = x - 2 * (beta * y / 256) * x_hi, y = y + beta * 256
            int pitchTerm = HiByte(MultiplyBySignMagnitude(_pitchMagnitude, HiByte(positionY) ^ _pitchSignFlipped));
            int product = EliteMaths.MultiplySigned(EliteMaths.FromSignMagnitude(pitchTerm), EliteMaths.FromSignMagnitude(HiByte(positionX) ^ 0x80));
            positionX = EliteMaths.Add16(product * 2, positionX);
            _dustXLow[particle] = SignMagnitudeLow(positionX);

            positionY = EliteMaths.Add16(EliteMaths.FromSignMagnitude(_pitchAngle) << 8, positionY);
            _dustYLow[particle] = SignMagnitudeLow(positionY);

            _dustX[particle] = HiByte(positionX);
            _dustY[particle] = HiByte(positionY);
            if ((_dustY[particle] & 0x7F) >= 110 || _dustZ[particle] >= 160)
            {
                // KILL6: recycle the particle at the edge of the screen
                int depth = NextRandom() & 0x7F;
                depth = (depth + 10 + (_carry ? 1 : 0)) & 0xFF;
                _dustZ[particle] = depth;
                if ((depth & 1) != 0)
                {
                    // ST4: along the top or bottom edge
                    int random = NextRandom();
                    _dustX[particle] = random;
                    _dustY[particle] = (230 >> 1) | ((random & 1) << 7);
                }
                else
                {
                    // Along the left or right edge
                    int carry = (depth >> 1) & 1;
                    _dustX[particle] = (252 >> 1) | (carry << 7);
                    _dustY[particle] = NextRandom();
                }
            }
        }
    }

    /// <summary>STARS2: process the stardust for the left or right view.</summary>
    private void MoveStardustSide()
    {
        int viewSign = _view == 3 ? 0x80 : 0;
        int viewSignFlipped = viewSign ^ 0x80;
        FlipAnglesForSideView(viewSign);

        for (int particle = _stardustCount; particle > 0; particle--)
        {
            int depth = _dustZ[particle];
            EliteMaths.DivideWithRemainder(_speed, depth >> 3, out int quotient, out int remainder);
            int step = quotient;

            // x = x + speed factor (in the direction of the view)
            int positionX = EliteMaths.Add16(SignMagnitude16(step ^ viewSignFlipped, remainder), SignMagnitude16(_dustX[particle], _dustXLow[particle]));

            // x = x + beta * y_hi
            positionX = EliteMaths.Add16(MultiplyBySignMagnitude(_pitchMagnitude, _dustY[particle] ^ _pitchSign), positionX);

            // y = y - beta * x_hi
            int positionY = EliteMaths.Add16(MultiplyBySignMagnitude(_pitchMagnitude, HiByte(positionX) ^ _pitchSignFlipped), SignMagnitude16(_dustY[particle], _dustYLow[particle]));

            // Roll
            int rollTerm = HiByte(MultiplyBySignMagnitude(_rollMagnitude, HiByte(positionY) ^ _rollSign));
            int rollTermSigned = EliteMaths.FromSignMagnitude(rollTerm);
            positionX = EliteMaths.MultiplyAdd(rollTermSigned, EliteMaths.FromSignMagnitude(HiByte(positionX) ^ 0x80), positionX);
            _dustXLow[particle] = SignMagnitudeLow(positionX);
            positionY = EliteMaths.MultiplyAdd(rollTermSigned, EliteMaths.FromSignMagnitude(HiByte(positionX)), positionY);
            positionY = EliteMaths.Add16(EliteMaths.FromSignMagnitude(_rollAngle) << 8, positionY);
            _dustYLow[particle] = SignMagnitudeLow(positionY);

            _dustX[particle] = HiByte(positionX);
            int limit = (_dustX[particle] & 0x7F) ^ 0x7F;
            if (limit <= step)
            {
                // KILL2
                _dustY[particle] = NextRandom();
                _dustX[particle] = 115 | viewSign;
                _dustZ[particle] = NextRandom() | 8;
                continue;
            }

            _dustY[particle] = HiByte(positionY);
            if ((_dustY[particle] & 0x7F) >= 116)
            {
                // ST5
                _dustX[particle] = NextRandom();
                _dustY[particle] = 110 | _rollSignFlipped;
                _dustZ[particle] = NextRandom() | 8;
            }
        }

        FlipAnglesForSideView(viewSign);
    }

    /// <summary>ST2: flip the signs of alpha and beta for the side views.</summary>
    private void FlipAnglesForSideView(int viewSign)
    {
        _rollAngle ^= viewSign;
        _rollSign ^= viewSign;
        _rollSignFlipped = _rollSign ^ 0x80;
        _pitchSign ^= viewSign;
        _pitchSignFlipped = _pitchSign ^ 0x80;
    }

    /// <summary>FLIP: swap the x and y coordinates of the stardust (when changing view).</summary>
    private void FlipStardust()
    {
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            (_dustX[particle], _dustY[particle]) = (_dustY[particle], _dustX[particle]);
        }

        UpdateStardustImage();
    }

    /// <summary>
    /// Put the stardust into the 3D world at the particles' current positions
    /// (PIXEL2 for each particle).
    /// </summary>
    private void UpdateStardustImage()
    {
        if (_viewType != 0)
        {
            return;
        }

        BeginWorldDrawing(_view);
        var particles = new List<Particle>();
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            // The stardust's coordinates are its position on the screen (in
            // pixels from the centre) and its distance, so in space it is at
            // that distance along the line of sight through that point
            int screenX = EliteMaths.FromSignMagnitude(_dustX[particle]);
            int screenY = EliteMaths.FromSignMagnitude(_dustY[particle]);
            float distance = Math.Max(_dustZ[particle], 4);
            var position = ViewToWorld(screenX * distance / 256, screenY * distance / 256, distance);
            particles.Add(new Particle(position, 2, _dustZ[particle] >= 80 ? 1 : 2, DustColour, Stardust: true));
        }

        _world.SetParticles(_dustOwner, particles);
    }
}

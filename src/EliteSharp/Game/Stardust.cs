using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Game;

/// <summary>
/// The stardust particles. Each particle has a position on the screen (x and
/// y, in pixels from the centre of the original's screen, with y up) and a
/// distance (z), and moves across the screen as we fly and turn.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The owner of the stardust in the 3D world.</summary>
    private readonly object _dustOwner = new();

    /// <summary>
    /// The signed value of a sign-magnitude byte (bit 7 is the sign), as the
    /// original places new stardust particles using random numbers.
    /// </summary>
    private static int SignedByte(int value) => (value & 0x80) != 0 ? -(value & 0x7F) : value & 0x7F;

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

    /// <summary>
    /// STARS1: process the stardust for the front view. The particles come
    /// towards us at a quarter of our speed, and move away from the centre of
    /// the screen as they get closer.
    /// </summary>
    private void MoveStardustFront()
    {
        float roll = _roll / 256f;
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            float x = _dustX[particle], y = _dustY[particle], z = _dustZ[particle];

            // Move the particle towards us, and away from the centre
            float outwards = _speed / (4 * z);
            z -= _speed / 4f;
            y += y * outwards;
            x += x * outwards;

            // Roll
            y -= roll * x;
            x += roll * y;

            // Pitch, which moves the particle up or down the screen (and
            // slightly sideways)
            float pitchTerm = _pitch * y / 256;
            x += 2 * pitchTerm * pitchTerm / 256;
            y -= _pitch;

            if (MathF.Abs(x) >= 120 || MathF.Abs(y) >= 120 || z < 16)
            {
                // KILL1: recycle the particle in the distance
                y = SignedByte(NextRandom() | 4);
                x = SignedByte(NextRandom() | 8);
                z = NextRandom() | 144;
            }

            (_dustX[particle], _dustY[particle], _dustZ[particle]) = (x, y, z);
        }
    }

    /// <summary>
    /// STARS6: process the stardust for the rear view. The particles move away
    /// from us at a quarter of our speed, and towards the centre of the
    /// screen as they get further away.
    /// </summary>
    private void MoveStardustRear()
    {
        float roll = _roll / 256f;
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            float x = _dustX[particle], y = _dustY[particle], z = _dustZ[particle];

            // Move the particle towards the centre, and away from us
            float inwards = _speed / (4 * z);
            x -= x * inwards;
            y -= y * inwards;
            z += _speed / 4f;

            // Roll
            y += roll * x;
            x -= roll * y;

            // Pitch
            x += 2 * (_pitch * y / 256) * x / 256;
            y += _pitch;

            if (MathF.Abs(y) >= 110 || z >= 160)
            {
                // KILL6: recycle the particle at the edge of the screen
                int depth = NextRandom() & 0x7F;
                depth = (depth + 10 + (_carry ? 1 : 0)) & 0xFF;
                z = depth;
                if ((depth & 1) != 0)
                {
                    // ST4: along the top or bottom edge
                    int random = NextRandom();
                    x = SignedByte(random);
                    y = (random & 1) != 0 ? -115 : 115;
                }
                else
                {
                    // Along the left or right edge
                    x = ((depth >> 1) & 1) != 0 ? -126 : 126;
                    y = SignedByte(NextRandom());
                }
            }

            (_dustX[particle], _dustY[particle], _dustZ[particle]) = (x, y, z);
        }
    }

    /// <summary>
    /// STARS2: process the stardust for the left or right view. The particles
    /// move across the screen, faster the closer they are, and our pitch and
    /// roll act as roll and pitch in these views.
    /// </summary>
    private void MoveStardustSide()
    {
        // ST2: in the right view, the particles move to the left, and our
        // pitch and roll turn the view the other way
        int direction = _view == 3 ? -1 : 1;
        float pitch = direction * _pitch;
        float roll = direction * _roll;

        for (int particle = _stardustCount; particle > 0; particle--)
        {
            float x = _dustX[particle], y = _dustY[particle], z = _dustZ[particle];

            // Move the particle across the screen
            float step = _speed / (z / 8);
            x -= direction * step;

            // Pitch rolls the view
            x += pitch * y / 256;
            y -= pitch * x / 256;

            // Roll pitches the view
            float rollTerm = roll * y / 256;
            x -= rollTerm * x / 256;
            y += rollTerm * x / 256;
            y += roll;

            if (MathF.Abs(x) >= 127 - step)
            {
                // KILL2: recycle the particle at the side of the screen it
                // comes in from
                _dustY[particle] = SignedByte(NextRandom());
                _dustX[particle] = 115 * direction;
                _dustZ[particle] = NextRandom() | 8;
                continue;
            }

            if (MathF.Abs(y) >= 116)
            {
                // ST5: recycle the particle at the top or bottom of the
                // screen, depending on which way we are turning
                x = SignedByte(NextRandom());
                y = roll >= 0 ? -110 : 110;
                z = NextRandom() | 8;
            }

            (_dustX[particle], _dustY[particle], _dustZ[particle]) = (x, y, z);
        }
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
        Span<Particle> particles = stackalloc Particle[_stardustCount];
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            // The stardust's coordinates are its position on the screen (in
            // pixels from the centre) and its distance, so in space it is at
            // that distance along the line of sight through that point
            float distance = MathF.Max(_dustZ[particle], 4);
            var position = ViewToWorld(_dustX[particle] * distance / 256, _dustY[particle] * distance / 256, distance);
            particles[particle - 1] = new Particle(position, 2, _dustZ[particle] >= 80 ? 1 : 2, DustColour, Stardust: true);
        }

        _world.SetParticles(_dustOwner, particles);
    }
}

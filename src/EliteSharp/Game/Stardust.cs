using System.Numerics;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Game;

/// <summary>
/// The stardust particles. Each particle has a position on the screen (x and
/// y, in pixels from the centre of the original's screen, with y up) and a
/// distance (z), and moves across the screen as we fly and turn.
///
/// The original's stardust only fills its own screen, so a space view that is
/// wider than the original's shows another field of stardust in its place (the
/// wide stardust), which fills the whole width. It moves just as the game's
/// stardust does, by the same rules, but with its own random numbers, so it
/// doesn't change the game (which still moves its own stardust as the
/// original does).
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The owner of the stardust in the 3D world.</summary>
    private readonly object _dustOwner = new();

    /// <summary>A particle of the wide stardust, in the same coordinates as the game's stardust.</summary>
    private struct WideDust
    {
        public float X, Y, Z;

        /// <summary>The particle's id in the 3D world (negative, so it is never one of the game's), which is new each time it is recycled.</summary>
        public int Id;

        /// <summary>Where it moves in from, if it was recycled in the latest move (see <see cref="_dustEntries"/>).</summary>
        public (float X, float Y, float Z)? Entry;
    }

    /// <summary>The wide stardust (empty if the space view is no wider than the original's).</summary>
    private readonly List<WideDust> _wideDust = [];

    /// <summary>The random numbers for the wide stardust, which are separate from the game's.</summary>
    private readonly Random _wideDustRandom = new(1984);

    /// <summary>
    /// The half-width of the wide stardust's field, in the original's pixels
    /// (see <see cref="StardustHalfWidth"/>), or 0 if it isn't shown.
    /// </summary>
    private float _wideDustHalfWidth;

    /// <summary>The id of the wide stardust particle that was recycled most recently.</summary>
    private int _lastWideDustId;

    /// <summary>
    /// The signed value of a sign-magnitude byte (bit 7 is the sign), as the
    /// original places new stardust particles using random numbers.
    /// </summary>
    private static int SignedByte(int value) => (value & 0x80) != 0 ? -(value & 0x7F) : value & 0x7F;

    /// <summary>
    /// Note that a stardust particle has been recycled (given a new place to
    /// start from), which gives it a new id in the 3D world, so the renderer
    /// doesn't move it smoothly from where it was.
    /// </summary>
    private void RecycleStardust(int particle)
    {
        _dustRecycles[particle]++;
        _dustEntries[particle] = null;
    }

    /// <summary>
    /// Note that a stardust particle has been recycled to start again at the
    /// given position, given where the next move would take it from there.
    /// Had it been there all along, a move earlier it would have been about as
    /// far back the other way, so that is where the renderer moves it in from
    /// (see <see cref="_dustEntries"/>).
    /// </summary>
    private void RecycleStardust(int particle, (float X, float Y, float Z) position, (float X, float Y, float Z) next)
    {
        RecycleStardust(particle);
        _dustEntries[particle] = (2 * position.X - next.X, 2 * position.Y - next.Y, 2 * position.Z - next.Z);
    }

    /// <summary>
    /// The id of a stardust particle in the 3D world, which changes each time
    /// the particle is recycled (see <see cref="_dustRecycles"/>).
    /// </summary>
    private int StardustId(int particle) => unchecked(particle + _dustRecycles[particle] * (NormalStardustCount + 1));

    /// <summary>STARS: move the stardust for the current view.</summary>
    private void MoveStardust()
    {
        Array.Clear(_dustEntries);
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

        MoveWideStardust();
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
            var (x, y, z) = StepStardustFront(_dustX[particle], _dustY[particle], _dustZ[particle], roll);
            if (MathF.Abs(x) >= 120 || MathF.Abs(y) >= 120 || z < 16)
            {
                // KILL1: recycle the particle in the distance
                y = SignedByte(NextRandom() | 4);
                x = SignedByte(NextRandom() | 8);
                z = NextRandom() | 144;
                RecycleStardust(particle, (x, y, z), StepStardustFront(x, y, z, roll));
            }

            (_dustX[particle], _dustY[particle], _dustZ[particle]) = (x, y, z);
        }
    }

    /// <summary>One move of a stardust particle in the front view (STARS1, without recycling it).</summary>
    private (float X, float Y, float Z) StepStardustFront(float x, float y, float z, float roll)
    {
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
        return (x, y, z);
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
            var (x, y, z) = StepStardustRear(_dustX[particle], _dustY[particle], _dustZ[particle], roll);
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

                RecycleStardust(particle, (x, y, z), StepStardustRear(x, y, z, roll));
            }

            (_dustX[particle], _dustY[particle], _dustZ[particle]) = (x, y, z);
        }
    }

    /// <summary>One move of a stardust particle in the rear view (STARS6, without recycling it).</summary>
    private (float X, float Y, float Z) StepStardustRear(float x, float y, float z, float roll)
    {
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
        return (x, y, z);
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
            var (x, y, z) = StepStardustSide(_dustX[particle], _dustY[particle], _dustZ[particle], direction, pitch, roll, out float step);
            if (MathF.Abs(x) >= 127 - step)
            {
                // KILL2: recycle the particle at the side of the screen it
                // comes in from
                float newY = SignedByte(NextRandom());
                float newX = 115 * direction;
                float newZ = NextRandom() | 8;
                (_dustX[particle], _dustY[particle], _dustZ[particle]) = (newX, newY, newZ);
                RecycleStardust(particle, (newX, newY, newZ), StepStardustSide(newX, newY, newZ, direction, pitch, roll, out _));
                continue;
            }

            if (MathF.Abs(y) >= 116)
            {
                // ST5: recycle the particle at the top or bottom of the
                // screen, depending on which way we are turning
                x = SignedByte(NextRandom());
                y = roll >= 0 ? -110 : 110;
                z = NextRandom() | 8;
                RecycleStardust(particle, (x, y, z), StepStardustSide(x, y, z, direction, pitch, roll, out _));
            }

            (_dustX[particle], _dustY[particle], _dustZ[particle]) = (x, y, z);
        }
    }

    /// <summary>
    /// One move of a stardust particle in the left or right view (STARS2,
    /// without recycling it), and how far it moved across the screen.
    /// </summary>
    private (float X, float Y, float Z) StepStardustSide(float x, float y, float z, int direction, float pitch, float roll, out float step)
    {
        // Move the particle across the screen
        step = _speed / (z / 8);
        x -= direction * step;

        // Pitch rolls the view
        x += pitch * y / 256;
        y -= pitch * x / 256;

        // Roll pitches the view
        float rollTerm = roll * y / 256;
        x -= rollTerm * x / 256;
        y += rollTerm * x / 256;
        y += roll;
        return (x, y, z);
    }

    /// <summary>FLIP: swap the x and y coordinates of the stardust (when changing view).</summary>
    private void FlipStardust()
    {
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            (_dustX[particle], _dustY[particle]) = (_dustY[particle], _dustX[particle]);
        }

        // The wide stardust's field isn't square, so it is scattered afresh
        // for the new view instead
        ScatterWideStardust();
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
        if (_wideDustHalfWidth > 0)
        {
            // The wide stardust is shown in place of the game's
            Span<Particle> wide = stackalloc Particle[_wideDust.Count];
            for (int i = 0; i < wide.Length; i++)
            {
                var dust = _wideDust[i];
                var entry = dust.Entry is { } from ? StardustPosition(from.X, from.Y, from.Z) : (Vector3?)null;
                wide[i] = new Particle(StardustPosition(dust.X, dust.Y, dust.Z), 2, dust.Z >= 80 ? 1 : 2, DustColour, Stardust: true, Id: dust.Id, Entry: entry);
            }

            _world.SetParticles(_dustOwner, wide);
            return;
        }

        Span<Particle> particles = stackalloc Particle[_stardustCount];
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            var position = StardustPosition(_dustX[particle], _dustY[particle], _dustZ[particle]);
            var entry = _dustEntries[particle] is { } from ? StardustPosition(from.X, from.Y, from.Z) : (Vector3?)null;
            particles[particle - 1] = new Particle(position, 2, _dustZ[particle] >= 80 ? 1 : 2, DustColour, Stardust: true, Id: StardustId(particle), Entry: entry);
        }

        _world.SetParticles(_dustOwner, particles);
    }

    /// <summary>
    /// The position in space of stardust at the given position on the screen
    /// (in pixels from the centre) and distance: it is at that distance along
    /// the line of sight through that point.
    /// </summary>
    private Vector3 StardustPosition(float x, float y, float z)
    {
        float distance = MathF.Max(z, 4);
        return ViewToWorld(x * distance / 256, y * distance / 256, distance);
    }
    // ------------------------------------------------------------------------
    // The wide stardust (not in the original)
    // ------------------------------------------------------------------------

    /// <summary>
    /// The half-width of the stardust's field, in the original's pixels, in a
    /// space view that is widened by the given margin (see Hud.SideMargin).
    /// The game's own field reaches 120 pixels either side of the centre (8
    /// short of the original's edges), and a widened field reaches as far
    /// beyond that as the view does.
    /// </summary>
    private static float StardustHalfWidth(float margin) => 120 + margin;

    /// <summary>
    /// The number of particles in the wide stardust, for a field of the given
    /// half-width: as many as the game's, in proportion to the width, so the
    /// stardust is just as dense.
    /// </summary>
    private int WideStardustCount(float halfWidth) => (int)MathF.Round(_stardustCount * halfWidth / 120);

    /// <summary>
    /// Scatter the wide stardust afresh over a field as wide as the space view
    /// (or remove it, if the view is no wider than the original's), as the
    /// game scatters its own stardust (nWq).
    /// </summary>
    private void ScatterWideStardust()
    {
        float halfWidth = StardustHalfWidth(_hud.SideMargin);
        _wideDust.Clear();
        _wideDustHalfWidth = 0;
        if (halfWidth <= StardustHalfWidth(0))
        {
            return;
        }

        _wideDustHalfWidth = halfWidth;
        for (int i = WideStardustCount(halfWidth); i > 0; i--)
        {
            _wideDust.Add(new WideDust
            {
                Z = WideRandomByte() | 8,
                X = WideRandomAcross(halfWidth + 7),
                Y = SignedByte(WideRandomByte()),
                Id = --_lastWideDustId,
            });
        }
    }

    /// <summary>
    /// Move the wide stardust for the current view, by the same rules as the
    /// game's stardust (STARS1, STARS6 and STARS2) in its wider field. If the
    /// view has changed width (or the game has changed how much stardust there
    /// is), it is scattered afresh first.
    /// </summary>
    private void MoveWideStardust()
    {
        float halfWidth = StardustHalfWidth(_hud.SideMargin);
        bool widened = halfWidth > StardustHalfWidth(0);
        if (halfWidth != _wideDustHalfWidth && (widened || _wideDustHalfWidth > 0) ||
            widened && WideStardustCount(halfWidth) != _wideDust.Count)
        {
            ScatterWideStardust();
        }

        float w = _wideDustHalfWidth;
        for (int i = 0; i < _wideDust.Count; i++)
        {
            var dust = _wideDust[i];
            dust.Entry = null;
            var next = _view switch
            {
                0 => MoveWideStardustFront(ref dust, w),
                1 => MoveWideStardustRear(ref dust, w),
                _ => MoveWideStardustSide(ref dust, w),
            };

            if (next is { } after)
            {
                // The particle was recycled, so it moves in from as far back
                // as its next move would take it forwards
                dust.Id = --_lastWideDustId;
                dust.Entry = (2 * dust.X - after.X, 2 * dust.Y - after.Y, 2 * dust.Z - after.Z);
            }

            _wideDust[i] = dust;
        }
    }

    /// <summary>
    /// Move a particle of the wide stardust in the front view (as STARS1, in
    /// the wider field), returning where its next move would take it if it was
    /// recycled, or null.
    /// </summary>
    private (float X, float Y, float Z)? MoveWideStardustFront(ref WideDust dust, float halfWidth)
    {
        float roll = _roll / 256f;
        (dust.X, dust.Y, dust.Z) = StepStardustFront(dust.X, dust.Y, dust.Z, roll);
        if (MathF.Abs(dust.X) < halfWidth && MathF.Abs(dust.Y) < 120 && dust.Z >= 16)
        {
            return null;
        }

        // KILL1: recycle the particle in the distance
        dust.Y = SignedByte(WideRandomByte() | 4);
        dust.X = WideRandomAcross(halfWidth + 7, 8);
        dust.Z = WideRandomByte() | 144;
        return StepStardustFront(dust.X, dust.Y, dust.Z, roll);
    }

    /// <summary>
    /// Move a particle of the wide stardust in the rear view (as STARS6, in the
    /// wider field), returning where its next move would take it if it was
    /// recycled, or null.
    /// </summary>
    private (float X, float Y, float Z)? MoveWideStardustRear(ref WideDust dust, float halfWidth)
    {
        float roll = _roll / 256f;
        (dust.X, dust.Y, dust.Z) = StepStardustRear(dust.X, dust.Y, dust.Z, roll);
        if (MathF.Abs(dust.Y) < 110 && dust.Z < 160)
        {
            return null;
        }

        // KILL6: recycle the particle at the edge of the screen
        int depth = (WideRandomByte() & 0x7F) + 10;
        dust.Z = depth;
        if ((depth & 1) != 0)
        {
            // ST4: along the top or bottom edge
            dust.X = WideRandomAcross(halfWidth + 7);
            dust.Y = (WideRandomByte() & 1) != 0 ? -115 : 115;
        }
        else
        {
            // Along the left or right edge
            dust.X = ((depth >> 1) & 1) != 0 ? -(halfWidth + 6) : halfWidth + 6;
            dust.Y = SignedByte(WideRandomByte());
        }

        return StepStardustRear(dust.X, dust.Y, dust.Z, roll);
    }

    /// <summary>
    /// Move a particle of the wide stardust in the left or right view (as
    /// STARS2, in the wider field), returning where its next move would take
    /// it if it was recycled, or null.
    /// </summary>
    private (float X, float Y, float Z)? MoveWideStardustSide(ref WideDust dust, float halfWidth)
    {
        int direction = _view == 3 ? -1 : 1;
        float pitch = direction * _pitch;
        float roll = direction * _roll;
        (dust.X, dust.Y, dust.Z) = StepStardustSide(dust.X, dust.Y, dust.Z, direction, pitch, roll, out float step);
        if (MathF.Abs(dust.X) >= halfWidth + 7 - step)
        {
            // KILL2: recycle the particle at the side of the screen it comes
            // in from
            dust.Y = SignedByte(WideRandomByte());
            dust.X = (halfWidth - 5) * direction;
            dust.Z = WideRandomByte() | 8;
        }
        else if (MathF.Abs(dust.Y) >= 116)
        {
            // ST5: recycle the particle at the top or bottom of the screen,
            // depending on which way we are turning
            dust.X = WideRandomAcross(halfWidth + 7);
            dust.Y = roll >= 0 ? -110 : 110;
            dust.Z = WideRandomByte() | 8;
        }
        else
        {
            return null;
        }

        return StepStardustSide(dust.X, dust.Y, dust.Z, direction, pitch, roll, out _);
    }

    /// <summary>A random byte for the wide stardust.</summary>
    private int WideRandomByte() => _wideDustRandom.Next(256);

    /// <summary>
    /// A random x-coordinate for the wide stardust, at least the given
    /// distance from the centre and less than the given limit, on either side
    /// (as the game's stardust uses a random signed byte).
    /// </summary>
    private float WideRandomAcross(float limit, float nearest = 0) =>
        (_wideDustRandom.Next(2) == 0 ? -1 : 1) * (nearest + _wideDustRandom.NextSingle() * (limit - nearest));
}

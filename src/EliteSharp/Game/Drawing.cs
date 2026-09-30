using System.Runtime.InteropServices;
using System.Numerics;
using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Game;

/// <summary>
/// Drawing ships, explosions, planets and the sun. Each object is described to
/// the 3D world (see WorldScene.cs) as what it is and where it is, and the
/// renderer decides what can be seen and how to draw it. The original's
/// drawing routines also make decisions that the rest of the game depends on,
/// because they take random numbers (whether an exploding ship is in the
/// original's field of view, which decides whether its explosion cloud is
/// drawn, and the explosion clouds and the sun's fringe themselves), and those
/// parts are exact ports of the original.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>COL: the current colour.</summary>
    private Ink _colour;

    // ------------------------------------------------------------------------
    // LL9: drawing ships
    // ------------------------------------------------------------------------

    /// <summary>
    /// LL9: draw the ship in INWK. The ship goes into the 3D world as its model,
    /// position and orientation, and the renderer decides whether it can be
    /// seen and how much detail to draw, however far away it is. An exploding
    /// ship's cloud goes into the world too, and the game works out whether
    /// the original would draw it, which decides whether it takes random
    /// numbers.
    /// </summary>
    private void DrawShip()
    {
        // Work out which view INWK is in (see _drawView)
        BeginWorldDrawing(_plutView ?? 0);
        _plutView = null;

        if (_shipType >= 128)
        {
            // LL25
            DrawPlanetOrSun();
            return;
        }

        var owner = _currentShip.DisplayOwner;
        _colour = ShipCatalogue.Get(_shipType).Colour;

        if ((_currentShip.Behaviour & 0x80) != 0)
        {
            // The ship has been scooped or has docked
            RemoveFromScreen(owner);
            return;
        }

        if ((_currentShip.Flags & Ship.FlagExploding) == 0 && (_currentShip.Flags & Ship.FlagKilled) != 0)
        {
            // The ship has just been killed, so start the explosion
            _currentShip.Flags = (_currentShip.Flags | Ship.FlagExploding) & 0b00111111;
            if (_slotShip != null)
            {
                _slotShip.Acceleration = 0;
                _slotShip.PitchCounter = 0;
            }

            RemoveFromScreen(owner);
            var cloud = _currentShip.Explosion;
            cloud.Counter = 18;
            cloud.CountByte = _blueprint!.ExplosionCountByte;
            for (int i = 0; i < 4; i++)
            {
                cloud.Seeds[i] = (byte)NextRandom();
            }
        }

        if ((_currentShip.Flags & Ship.FlagExploding) != 0)
        {
            // LL72 and LL14
            DrawExplosion(owner, InOriginalFieldOfView());
            return;
        }

        var beam = default(LineSegment);
        bool firing = (_currentShip.Flags & Ship.FlagFiring) != 0;
        if (firing)
        {
            // The ship's laser beam goes from its gun to one of the bottom
            // corners of the screen (or thereabouts)
            _currentShip.Flags &= ~Ship.FlagFiring;
            var gun = _blueprint!.Vertices[_blueprint.GunVertex];
            int cornerX = _currentShip.X < 0 ? 255 : 0;
            int cornerY = _currentShip.ZLo;
            beam = new LineSegment(Vector3.Transform(gun, CurrentShipTransform()), ScreenPointToWorld(cornerX, cornerY, ScreenEdgeDistance), _colour);
        }

        ShowShip(owner, firing ? new ReadOnlySpan<LineSegment>(in beam) : []);
    }

    /// <summary>
    /// Put the ship in INWK into the 3D world (with its laser beam, if it's
    /// firing), where the renderer decides whether and how it can be seen.
    /// </summary>
    private void ShowShip(object owner, ReadOnlySpan<LineSegment> beam = default) =>
        _world.SetShip(owner, new ShipInstance(_blueprint!.Model.Name, CurrentShipTransform(), _colour), beam);

    /// <summary>
    /// EE28 and LL10: whether the ship in INWK is in the original's field of
    /// view (in front of us, not too far away, and within 45 degrees of
    /// straight ahead). This doesn't decide what can be seen, which is up to
    /// the renderer; it decides whether the original draws an explosion's
    /// cloud, and so whether the cloud takes random numbers.
    /// </summary>
    private bool InOriginalFieldOfView()
    {
        int zMagnitude = Math.Abs(_currentShip.Z) & 0xFFFF;
        return _currentShip.Z >= 0 && _currentShip.ZHi < 192
            && (Math.Abs(_currentShip.X) & 0xFFFF) < zMagnitude && (Math.Abs(_currentShip.Y) & 0xFFFF) < zMagnitude;
    }

    /// <summary>Convert a 16-bit two's complement word to a signed value.</summary>
    private static int ToSigned16(int value) => (short)(value & 0xFFFF);

    /// <summary>
    /// The LL145 clipping test: returns true if the line from (x1, y1) to
    /// (x2, y2) intersects the space view (0-255, 0-191), widened by the given
    /// number of pixels on each side.
    /// </summary>
    private static bool LineOnScreen(int x1, int y1, int x2, int y2, int margin = 0)
    {
        double entry = 0, exit = 1;
        double dx = x2 - x1, dy = y2 - y1;

        bool Clip(double direction, double distance)
        {
            if (direction == 0)
            {
                return distance >= 0;
            }

            double ratio = distance / direction;
            if (direction < 0)
            {
                if (ratio > exit)
                {
                    return false;
                }

                if (ratio > entry)
                {
                    entry = ratio;
                }
            }
            else
            {
                if (ratio < entry)
                {
                    return false;
                }

                if (ratio < exit)
                {
                    exit = ratio;
                }
            }

            return true;
        }

        return Clip(-dx, x1 + margin) && Clip(dx, 255 + margin - x1) && Clip(-dy, y1 - 0) && Clip(dy, 2 * CentreY - 1 - y1);
    }

    /// <summary>
    /// PROJ: project the centre of the planet or sun in INWK onto the
    /// original's screen, returning false (C set) if it's too far off-screen.
    /// The results are K3 (x) and K4 (y) as 16-bit two's complement values.
    /// The planet and sun routines still work on the original's screen, as
    /// the sun's fringe takes random numbers for each of its lines.
    /// </summary>
    private bool ProjectToScreen(out int screenX, out int screenY)
    {
        screenX = 0;
        screenY = 0;
        if (!DivideByDistance(_currentShip.X, out int x))
        {
            return false;
        }

        screenX = (x + CentreX) & 0xFFFF;
        if (!DivideByDistance(-_currentShip.Y, out int y))
        {
            return false;
        }

        screenY = (y + CentreY) & 0xFFFF;
        return true;
    }

    /// <summary>PLS6: calculate 256 * value / z, returning false if the result is 1024 or more.</summary>
    private bool DivideByDistance(int value, out int result)
    {
        int scaled = EliteMaths.DivideScaled(value, _currentShip.Z);
        result = 0;
        if (Math.Abs(scaled) >= 1024)
        {
            return false;
        }

        result = scaled;
        return true;
    }

    // ------------------------------------------------------------------------
    // Explosions
    // ------------------------------------------------------------------------

    /// <summary>The particles of the explosion cloud being drawn (reused for each cloud).</summary>
    private readonly List<Particle> _cloudParticles = [];

    /// <summary>
    /// DOEXP: draw an exploding ship. The original erases and redraws the
    /// explosion cloud, which takes random numbers, only while the ship is in
    /// its field of view; the game does the same, and the 3D world gets the
    /// cloud wherever the ship is.
    /// </summary>
    /// <param name="owner">The ship's owner in the 3D world.</param>
    /// <param name="inOriginalFieldOfView">Whether the ship is in the original's field of view (see <see cref="InOriginalFieldOfView"/>).</param>
    private void DrawExplosion(object owner, bool inOriginalFieldOfView)
    {
        var cloud = _currentShip.Explosion;
        if ((_currentShip.Flags & Ship.FlagOnScreenCloud) != 0)
        {
            // Erase the existing cloud (which, as in the original, reseeds the
            // random number generator)
            DrawExplosionCloud(null);
        }

        // Work out the cloud's size from its distance and counter
        int zHi = _currentShip.ZHi;
        int distanceFactor;
        if (zHi >= 32)
        {
            distanceFactor = 0xFE;
        }
        else
        {
            int z = ((zHi << 8) | _currentShip.ZLo) >> 6;
            distanceFactor = ((z << 1) | 1) & 0xFF;
        }

        // The ADC #4 includes the C flag, which is set if z_hi >= 32 (from the
        // CMP) and clear otherwise (from the ROL, as z / 64 &lt; 128)
        int counter = cloud.Counter + 4 + (zHi >= 32 ? 1 : 0);
        if (counter > 0xFF)
        {
            // EX2: the explosion has finished
            _currentShip.Flags |= 0b10100000;
            RemoveFromScreen(owner);
            return;
        }

        cloud.Counter = counter;
        EliteMaths.DivideWithRemainder(counter, distanceFactor, out int sizeInteger, out int sizeFraction);
        int size;
        if (sizeInteger >= 0x1C)
        {
            size = 0xFE;
        }
        else
        {
            size = ((sizeInteger << 3) | (sizeFraction >> 5)) & 0xFF;
        }

        cloud.Size = size;
        _currentShip.Flags &= ~Ship.FlagOnScreenCloud;
        _cloudParticles.Clear();
        if (inOriginalFieldOfView)
        {
            _currentShip.Flags |= Ship.FlagOnScreenCloud;
            DrawExplosionCloud(_cloudParticles);
        }
        else
        {
            // The original doesn't draw the cloud when the ship is out of its
            // field of view, and so doesn't take the random numbers for it, but
            // the 3D view may still show it
            DrawExplosionCloudWithoutSideEffects(_cloudParticles);
        }

        _world.SetParticles(owner, CollectionsMarshal.AsSpan(_cloudParticles));
    }

    /// <summary>
    /// Work out the explosion cloud's particles without changing any of the
    /// game's state, by putting back the random number generator (and the
    /// current colour) afterwards.
    /// </summary>
    private void DrawExplosionCloudWithoutSideEffects(List<Particle> particles)
    {
        Span<int> seeds = stackalloc int[4];
        _randomSeeds.CopyTo(seeds);
        var (carry, overflow, randomX, colour) = (_carry, _overflow, _randomX, _colour);
        DrawExplosionCloud(particles);
        seeds.CopyTo(_randomSeeds);
        (_carry, _overflow, _randomX, _colour) = (carry, overflow, randomX, colour);
    }

    /// <summary>
    /// PTCLS: draw (or erase) the explosion cloud. The random number generator
    /// is seeded from the cloud data so the same cloud is produced each time.
    ///
    /// Each particle is added to the list of particles for the 3D world (if
    /// one is given, as erasing the cloud doesn't need them). The original
    /// scatters the particles around the ship's vertices on the screen, and
    /// here each is at the same offset from its vertex, at the vertex's
    /// distance. This takes exactly the same random numbers as the original,
    /// so the game's random number sequence is unaffected.
    /// </summary>
    private void DrawExplosionCloud(List<Particle>? particles)
    {
        var cloud = _currentShip.Explosion;
        int counter = cloud.Counter;
        if ((counter & 0x80) != 0)
        {
            counter ^= 0xFF;
        }

        int particleCount = (counter >> 4) | 1;
        int savedSeed1 = _randomSeeds[1];

        var vertices = _blueprint!.Vertices;
        for (int v = 0; v < cloud.VertexCount; v++)
        {
            // The explosion count can be more than the number of vertices (the
            // rock hermit's is), in which case the original uses whatever is
            // left over in its heap; here the vertices are used again
            var viewOrigin = particles != null ? ShipPointInView(vertices[v % vertices.Count]) : default;
            int heapOffset = 6 + 4 * (v + 1);

            // Seed the random number generator from the cloud's seeds
            for (int i = 0; i < 4; i++)
            {
                _randomSeeds[i] = cloud.Seeds[i] ^ heapOffset;
            }

            for (int n = particleCount; n >= 0; n--)
            {
                int random = NextCloudRandom();
                _colour = ExplosionColours[random & 3];

                // The original skips the x-coordinate if the y-coordinate is off
                // the bottom of the screen, but still takes a random number
                // (EX11), which is the only random number RandomCloudCoordinate
                // takes, so we can work out the x-coordinate either way (the
                // 3D view can show particles that the original's screen can't).
                // The offsets from the vertex don't depend on where the vertex
                // is on the screen, so they are worked out from the origin.
                int y = RandomCloudCoordinate(0, cloud.Size);
                int x = RandomCloudCoordinate(0, cloud.Size);

                if (particles != null && viewOrigin.Z > 0)
                {
                    // Convert the particle's offset on the screen into an offset in
                    // space at the vertex's distance
                    float scale = viewOrigin.Z / 256;
                    float dx = ToSigned16(x) * scale;
                    float dy = ToSigned16(y) * scale;
                    particles.Add(new Particle(ViewToWorld(viewOrigin.X + dx, viewOrigin.Y - dy, viewOrigin.Z), 2, random >= 80 ? 1 : 2, _colour));
                }
            }
        }

        _randomSeeds[1] = savedSeed1;
        _randomSeeds[3] = Planet.ZLo;
    }

    /// <summary>The inline random number generator used by PTCLS (DORND with the C flag clear).</summary>
    private int NextCloudRandom()
    {
        _carry = false;
        return NextRandom();
    }

    /// <summary>
    /// EXS1: return a random coordinate within the cloud's size of the given
    /// origin coordinate (a 16-bit word), as a 16-bit value.
    /// </summary>
    private int RandomCloudCoordinate(int origin, int size)
    {
        int originHigh = (origin >> 8) & 0xFF;
        int originLow = origin & 0xFF;
        int random = NextCloudRandom();
        bool negative = (random & 0x80) != 0;
        random = (random << 1) & 0xFF;
        int product = EliteMaths.MultiplyFraction(random, size, out bool carry);
        if (!negative)
        {
            int sum = originLow + product + (carry ? 1 : 0);
            int lo = sum & 0xFF;
            int hi = (originHigh + (sum > 0xFF ? 1 : 0)) & 0xFF;
            return (hi << 8) | lo;
        }

        int difference = originLow - product - (carry ? 0 : 1);
        int low = difference & 0xFF;
        int high = (originHigh - (difference < 0 ? 1 : 0)) & 0xFF;
        return (high << 8) | low;
    }

    /// <summary>A point on the ship in INWK (in the ship's own coordinates), in the space of the view it is in.</summary>
    private Vector3 ShipPointInView(Vector3 point)
    {
        static Vector3 Unit(IntVector3 v) => Vector3.Normalize(new Vector3(v.X, v.Y, v.Z));
        return new Vector3(_currentShip.X, _currentShip.Y, _currentShip.Z)
            + Unit(_currentShip.Side) * point.X + Unit(_currentShip.Roof) * point.Y + Unit(_currentShip.Nose) * point.Z;
    }

    /// <summary>
    /// PIXEL: the rectangles for a dot at (x, y), which is two pixels wide and
    /// one or two pixels high depending on the distance in zz.
    /// </summary>
    private static IEnumerable<ScreenRect> PixelRects(int x, int y, int distance, Ink colour)
    {
        // TWOS2 keeps the two pixels within the byte
        int left = (x & 3) == 3 ? x - 1 : x;
        if (distance >= 80)
        {
            yield return new ScreenRect(left, y, 2, 1, colour);
            yield break;
        }

        // PX2: a double-height dot, using the row above unless we are at the
        // top of a character row
        int other = (y & 7) == 0 ? y + 1 : y - 1;
        yield return new ScreenRect(left, Math.Min(y, other), 2, 2, colour);
    }

    // ------------------------------------------------------------------------
    // Planets
    // ------------------------------------------------------------------------

    /// <summary>STP: the step size for drawing circles.</summary>
    private int _circleStep;

    /// <summary>K: the radius of the circle being drawn.</summary>
    private int _circleRadius;

    /// <summary>K3 and K4 for circles: the centre of the circle (16-bit two's complement).</summary>
    private int _circleX, _circleY;

    /// <summary>The line segments of the circle being collected by BLINE (the ball line heap).</summary>
    private readonly List<ScreenLine> _circleLines = [];

    /// <summary>True if the next point is the start of a new line (FLAG in the original).</summary>
    private bool _circleLineFirst;

    /// <summary>The previous point on the circle being drawn.</summary>
    private int _circleLinePreviousX, _circleLinePreviousY;

    /// <summary>
    /// How far beyond each side of the original's screen the circle being
    /// drawn can reach (for the tunnels, which fill a widened space view).
    /// </summary>
    private int _circleMargin;

    /// <summary>PLANET: draw the planet or sun in INWK.</summary>
    private void DrawPlanetOrSun()
    {
        _colour = Green;
        int zSign = _currentShip.ZSign;
        if (zSign >= 48 || (zSign | _currentShip.ZHi) == 0)
        {
            RemovePlanetOrSun();
            return;
        }

        if (!ProjectToScreen(out _circleX, out _circleY))
        {
            RemovePlanetOrSun();
            return;
        }

        // The planet's radius is 96 * 256 * 256 / z
        int radius = EliteMaths.DivideScaled(96 << 8, _currentShip.Z);
        bool large = (radius >> 8) != 0;
        _circleRadius = large ? 248 : radius & 0xFF;

        if ((_shipType & 1) != 0)
        {
            // The sun's fringe takes random numbers
            DrawSun();
        }

        // Add the planet or sun to the 3D world, whether or not it is on the
        // original's screen (the 3D view can be wider)
        SetWorldPlanetOrSun(large);
    }

    /// <summary>PL2: remove the planet or sun from the screen.</summary>
    private void RemovePlanetOrSun()
    {
        if ((_shipType & 1) == 0)
        {
            RemovePlanet();
        }
        else
        {
            RemoveSun();
        }
    }

    /// <summary>
    /// BLINE: add a segment from the previous point to (x, y) to the ball line
    /// list, returning the updated segment counter (CNT + STP).
    /// </summary>
    private int AddCircleSegment(int x, int y, int count)
    {
        x = ToSigned16(x);
        y = ToSigned16(y);
        if (_circleLineFirst)
        {
            _circleLineFirst = false;
        }
        else if (LineOnScreen(_circleLinePreviousX, _circleLinePreviousY, x, y, _circleMargin))
        {
            _circleLines.Add(new ScreenLine(_circleLinePreviousX, _circleLinePreviousY, x, y, _colour));
        }

        _circleLinePreviousX = x;
        _circleLinePreviousY = y;
        return count + _circleStep;
    }

    /// <summary>CIRCLE2: draw a circle of radius K centred on K3/K4, with step size STP.</summary>
    private void DrawCircle()
    {
        _circleLineFirst = true;
        int count = 0;
        while (true)
        {
            int x = EliteMaths.MultiplyFraction(_circleRadius, GameData.Sine[count & 31]);
            if (count >= 33)
            {
                x = -x;
            }

            int y = EliteMaths.MultiplyFraction(_circleRadius, GameData.Sine[(count + 16) & 31], out bool carry);

            // The ADC #15 includes the C flag from FMLTU2
            if (((count + 15 + (carry ? 1 : 0)) & 63) >= 33)
            {
                y = -y;
            }

            int next = AddCircleSegment(_circleX + x, _circleY + y, count);
            if (next >= 65)
            {
                return;
            }

            count = next;
        }
    }

    /// <summary>
    /// CHKON: check whether the circle with centre K3/K4 and radius K is on
    /// screen, returning false (C set) if it isn't. Also returns the bottom of
    /// the circle in P+1/P+2.
    /// </summary>
    private bool IsCircleOnScreen(out int bottom, out int top)
    {
        int centreX = ToSigned16(_circleX), centreY = ToSigned16(_circleY);
        bottom = centreY + _circleRadius;
        top = centreY - _circleRadius;
        if (centreX + _circleRadius < 0)
        {
            return false;
        }

        int left = centreX - _circleRadius;
        if (left >= 256)
        {
            return false;
        }

        if (bottom < 0)
        {
            return false;
        }

        if (top < 0)
        {
            return true;
        }

        if (top >= 256)
        {
            return false;
        }

        return top < 2 * CentreY - 1;
    }

    /// <summary>WPLS2: remove the planet from the screen.</summary>
    private void RemovePlanet() => RemoveFromScreen(_currentShip.DisplayOwner);

    // ------------------------------------------------------------------------
    // The sun
    // ------------------------------------------------------------------------

    /// <summary>LSO: the sun line heap (the half-width of the sun on each pixel row).</summary>
    private readonly int[] _sunHalfWidths = new int[2 * CentreY + 8];

    /// <summary>LSX: &amp;FF if the sun is not on-screen.</summary>
    private int _sunHidden = 0xFF;

    /// <summary>The owner of the sun in the 3D world.</summary>
    private readonly object _sunOwner = new();

    /// <summary>The orange colours for each pixel row of the sun.</summary>
    private static readonly Ink[] Orange = [Ink.SunStripes, Ink.SunStripes, Ink.SunStripesShifted, Ink.SunStripesShifted];

    /// <summary>
    /// SUN: draw the sun, with its fringe of random widths. The 3D world draws
    /// the sun in space, but this still works out the width of each row, as
    /// the fringe takes a random number for each row, and the stars on the
    /// short-range chart are drawn with this routine.
    /// </summary>
    private void DrawSun()
    {
        _colour = Red;
        _sunHidden = 1;
        if (!IsCircleOnScreen(out int bottom, out _))
        {
            RemoveSun();
            return;
        }

        int radius = _circleRadius;
        int fringeMask = (radius >= 96 ? 4 : 0) | (radius >= 40 ? 2 : 0) | (radius >= 16 ? 1 : 0);

        // Work out the bottom row of the sun (TGT)
        int yMax = 2 * CentreY - 1;
        int bottomRow;
        if ((bottom >> 8) != 0 || yMax < (bottom & 0xFF))
        {
            bottomRow = yMax;
        }
        else
        {
            bottomRow = (bottom & 0xFF) != 0 ? bottom & 0xFF : 1;
        }

        // Work out V, the vertical distance from row Yx2M1 to the centre
        int centreY = ToSigned16(_circleY);
        int verticalDistance, verticalDistanceHigh;
        int distance = yMax - centreY;
        if (distance < 0)
        {
            verticalDistance = -distance & 0xFF;
            verticalDistanceHigh = 0xFF;
        }
        else if (distance >= 256 || distance >= radius)
        {
            verticalDistance = radius;
            verticalDistanceHigh = 0;
        }
        else if (distance == 0)
        {
            verticalDistance = 0;
            verticalDistanceHigh = 0xFF;
        }
        else
        {
            verticalDistance = distance;
            verticalDistanceHigh = 0;
        }

        int radiusSquared = radius * radius;

        // Rows below the sun no longer have any sun lines
        for (int row = yMax; row > bottomRow; row--)
        {
            _sunHalfWidths[row] = 0;
        }

        int centre = ToSigned16(_circleX);
        int y = bottomRow;
        bool finished = false;
        while (!finished)
        {
            // PLFL: the half-width of this row
            int halfWidthSquared = radiusSquared - verticalDistance * verticalDistance;
            int halfWidth = EliteMaths.SquareRoot(halfWidthSquared & 0xFFFF);
            int width = (NextRandom() & fringeMask) + halfWidth;
            if (width > 255)
            {
                width = 255;
            }

            _sunHalfWidths[y] = width;
            if (!SunEdges(centre, width, out _, out _))
            {
                _sunHalfWidths[y] = 0;
            }

            // PLF6
            y--;
            if (y == 0)
            {
                break;
            }

            if (verticalDistanceHigh != 0)
            {
                // PLF10: we are in the top half, moving away from the centre
                verticalDistance++;
                if (verticalDistance > radius)
                {
                    // Remove any old sun lines above the new sun
                    for (int row = y; row > 0; row--)
                    {
                        _sunHalfWidths[row] = 0;
                    }

                    finished = true;
                }
            }
            else
            {
                verticalDistance--;
                if (verticalDistance == 0)
                {
                    verticalDistanceHigh = 0xFF;
                }
            }
        }

        // PLF8: the stars on the short-range chart are drawn with the sun
        // routine, and stay on-screen
        if (!_sunToCanvas)
        {
            return;
        }

        for (int row = 1; row <= yMax; row++)
        {
            if (_sunHalfWidths[row] != 0 && SunEdges(centre, _sunHalfWidths[row], out int x1, out int x2) && x2 > x1)
            {
                _hud.DrawRect(x1, row, x2 - x1, 1, Orange[row & 3]);
            }
        }
    }

    /// <summary>EDGES: the ends of a horizontal line of half-width A centred on YY, clipped to the screen.</summary>
    private static bool SunEdges(int centre, int halfWidth, out int x1, out int x2)
    {
        x1 = centre - halfWidth;
        x2 = centre + halfWidth;
        if (x2 < 0 || x1 > 255)
        {
            return false;
        }

        x1 = Math.Max(x1, 0);
        x2 = Math.Min(x2, 255);
        return true;
    }

    /// <summary>WPLS: remove the sun from the screen.</summary>
    private void RemoveSun()
    {
        // The 3D sun can be in the world even if the sun isn't on the
        // original's screen, as the 3D view can be wider
        _world.Remove(_sunOwner);
        if ((_sunHidden & 0x80) != 0)
        {
            return;
        }

        Array.Clear(_sunHalfWidths);
        _sunHidden = 0xFF;
    }

    // ------------------------------------------------------------------------
    // The hyperspace and launch tunnels
    // ------------------------------------------------------------------------

    /// <summary>HFS2: clear the screen and draw the launch or hyperspace tunnel with the given step size.</summary>
    private void DrawTunnel(int step)
    {
        _circleStep = step;
        int view = _viewType;
        ClearScreen(0);
        _viewType = view;
        DrawTunnelCircles();
    }

    /// <summary>
    /// HFS1: draw the tunnel as eight sets of concentric circles, or erase it
    /// (the original draws it with EOR logic, and erases it by drawing it
    /// again). The circles carry on out to the sides of a space view that is
    /// wider than the original's.
    /// </summary>
    private void DrawTunnelCircles(bool erase = false)
    {
        float sideMargin = _hud.SideMargin;
        _circleMargin = (int)sideMargin;
        _circleX = CentreX;
        _circleY = CentreY;
        for (int i = 0; i < 8; i++)
        {
            // HFL1
            _circleRadius = (i & 7) + 8;
            while (true)
            {
                _circleLines.Clear();
                DrawCircle();
                foreach (var line in _circleLines)
                {
                    if (erase)
                    {
                        _hud.EraseWideLine(line.X1, line.Y1, line.X2, line.Y2, _colour);
                    }
                    else
                    {
                        _hud.DrawWideLine(line.X1, line.Y1, line.X2, line.Y2, _colour, sideMargin);
                    }
                }

                // The original draws the circles slowly enough for them to be
                // seen appearing, so we pause briefly after each one
                PresentAndPause(8);

                int doubled = _circleRadius << 1;
                if (doubled > 0xFF)
                {
                    break;
                }

                _circleRadius = doubled;
                if (_circleRadius >= 160)
                {
                    break;
                }
            }
        }

        _circleMargin = 0;
    }
}

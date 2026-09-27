using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// Drawing ships, explosions, planets and the sun. The visibility calculations
/// (which faces, vertices and edges are visible, and at what level of detail)
/// are exact ports of the original, but rather than plotting lines pixel by
/// pixel, visible edges are sent to the GPU as 3D lines, and planets, suns and
/// explosion clouds are sent as 2D lines and rectangles.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>XX3: the screen coordinates of each vertex (16-bit x and y, as raw two's complement words).</summary>
    private readonly int[] XX3X = new int[64];
    private readonly int[] XX3Y = new int[64];

    /// <summary>The 3D coordinates of each vertex, relative to our ship, as calculated in LL9.</summary>
    private readonly int[] VertexX = new int[64];
    private readonly int[] VertexY = new int[64];
    private readonly int[] VertexZ = new int[64];

    /// <summary>XX2: the visibility of each face (shares memory with K3 in the original).</summary>
    private int[] XX2 => K3Faces;

    private readonly int[] K3Faces = new int[16];

    /// <summary>XX4: the distance of the ship, used for level of detail.</summary>
    private int XX4;

    /// <summary>COL: the current colour byte.</summary>
    private int COL;

    // ------------------------------------------------------------------------
    // LL9: drawing ships
    // ------------------------------------------------------------------------

    /// <summary>A sign-magnitude byte pair used by the original's 8-bit vector maths.</summary>
    private struct SmByte(int magnitude, bool negative)
    {
        public int Magnitude = magnitude & 0xFF;
        public bool Negative = negative;
    }

    /// <summary>
    /// LL38: (S A) = (S R) + (A Q), adding two sign-magnitude bytes, returning
    /// the overflow in the C flag.
    /// </summary>
    private static SmByte LL38(SmByte first, SmByte second, out bool overflow)
    {
        overflow = false;
        if (first.Negative == second.Negative)
        {
            int sum = first.Magnitude + second.Magnitude;
            overflow = sum > 0xFF;
            return new SmByte(sum, first.Negative);
        }

        int difference = first.Magnitude - second.Magnitude;
        if (difference >= 0)
        {
            return new SmByte(difference, first.Negative);
        }

        return new SmByte(-difference, !first.Negative);
    }

    /// <summary>
    /// LL51: calculate the dot products of a vector (three sign-magnitude
    /// bytes) with the three rows of the XX16 matrix.
    /// </summary>
    private static void LL51(ReadOnlySpan<SmByte> vector, SmByte[] matrix, Span<SmByte> result)
    {
        for (int row = 0; row < 3; row++)
        {
            int m = row * 3;
            var t = new SmByte(EliteMaths.Fmltu(vector[0].Magnitude, matrix[m].Magnitude), vector[0].Negative ^ matrix[m].Negative);
            var q = new SmByte(EliteMaths.Fmltu(vector[1].Magnitude, matrix[m + 1].Magnitude), vector[1].Negative ^ matrix[m + 1].Negative);
            t = LL38(t, q, out _);
            q = new SmByte(EliteMaths.Fmltu(vector[2].Magnitude, matrix[m + 2].Magnitude), vector[2].Negative ^ matrix[m + 2].Negative);
            result[row] = LL38(t, q, out _);
        }
    }

    /// <summary>XX16: the orientation matrix, scaled for use in LL51.</summary>
    private readonly SmByte[] XX16 = new SmByte[9];

    /// <summary>LL9: draw the ship in INWK.</summary>
    private void LL9()
    {
        if (TYPE >= 128)
        {
            // LL25
            PLANET();
            return;
        }

        var owner = INWK.DisplayOwner;
        COL = GameData.ShipColours[TYPE];
        XX4 = 31;

        if ((INWK.Newb & 0x80) != 0)
        {
            // The ship has been scooped or has docked
            EE51(owner);
            return;
        }

        if ((INWK.Flags & Ship.FlagExploding) == 0 && (INWK.Flags & Ship.FlagKilled) != 0)
        {
            // The ship has just been killed, so start the explosion
            INWK.Flags = (INWK.Flags | Ship.FlagExploding) & 0b00111111;
            if (INF != null)
            {
                INF.Acceleration = 0;
                INF.PitchCounter = 0;
            }

            EE51(owner);
            var cloud = INWK.Explosion;
            cloud.Counter = 18;
            cloud.CountByte = XX0!.ExplosionCountByte;
            for (int i = 0; i < 4; i++)
            {
                cloud.Seeds[i] = (byte)DORND();
            }
        }

        // EE28
        if (INWK.Z < 0)
        {
            LL14(owner);
            return;
        }

        // LL10: check whether the ship is in the field of view
        if (INWK.ZHi >= 192)
        {
            LL14(owner);
            return;
        }

        int z16 = Math.Abs(INWK.Z) & 0xFFFF;
        if ((Math.Abs(INWK.X) & 0xFFFF) >= z16 || (Math.Abs(INWK.Y) & 0xFFFF) >= z16)
        {
            LL14(owner);
            return;
        }

        // Mark the gun vertex as not yet projected
        int gun = XX0!.GunVertex;
        XX3X[gun] = 0xFFFF;

        // Calculate the distance for the level of detail
        if ((INWK.ZHi >> 4) == 0)
        {
            XX4 = (z16 >> 7) & 0x1F;
        }
        else if (XX0.VisibilityDistance < INWK.ZHi && (INWK.Flags & Ship.FlagExploding) == 0)
        {
            // LL13: the ship is too far away, so draw it as a dot
            SHPPT(owner);
            return;
        }

        // LL17: set up the orientation matrix in XX16 (sidev, roofv, nosev as rows)
        SetUpMatrixRow(0, INWK.Side);
        SetUpMatrixRow(1, INWK.Roof);
        SetUpMatrixRow(2, INWK.Nose);

        XX2[15] = 255;
        var blueprint = XX0;
        if ((INWK.Flags & Ship.FlagExploding) != 0)
        {
            // All faces are visible when exploding
            for (int f = 0; f < blueprint.Faces.Count; f++)
            {
                XX2[f] = 255;
            }

            XX4 = 0;
        }
        else
        {
            CalculateFaceVisibility(blueprint);
        }

        // LL42: transpose the matrix so we can rotate the vertices into our frame
        (XX16[1], XX16[3]) = (XX16[3], XX16[1]);
        (XX16[2], XX16[6]) = (XX16[6], XX16[2]);
        (XX16[5], XX16[7]) = (XX16[7], XX16[5]);

        ProjectVertices(blueprint);

        // LL72
        if ((INWK.Flags & Ship.FlagExploding) != 0)
        {
            INWK.Flags |= Ship.FlagDrawn;
            DOEXP(owner);
            return;
        }

        INWK.Flags |= Ship.FlagDrawn;
        var image = new ObjectImage();
        int lsnum = 1;
        int heapSize = blueprint.LineHeapSize;

        if ((INWK.Flags & Ship.FlagFiring) != 0)
        {
            INWK.Flags &= ~Ship.FlagFiring;
            int gx = XX3X[gun], gy = XX3Y[gun];
            if ((gx & 0xFF) != 0xFF && ((gx >> 8) & 0xFF) != 0xFF)
            {
                int x2 = INWK.X < 0 ? 255 : 0;
                int y2 = INWK.ZLo;
                int sx = ToSigned16(gx), sy = ToSigned16(gy);
                if (LineOnScreen(sx, sy, x2, y2))
                {
                    image.Lines.Add(new ScreenLine(sx, sy, x2, y2, COL));
                    lsnum += 4;
                }
            }
        }

        // LL170: draw the visible edges
        foreach (var edge in blueprint.Edges)
        {
            if (lsnum >= heapSize)
            {
                break;
            }

            if (edge.Visibility < XX4)
            {
                continue;
            }

            if (XX2[edge.Face1] == 0 && XX2[edge.Face2] == 0)
            {
                continue;
            }

            int v1 = edge.Vertex1, v2 = edge.Vertex2;
            if (!LineOnScreen(ToSigned16(XX3X[v1]), ToSigned16(XX3Y[v1]), ToSigned16(XX3X[v2]), ToSigned16(XX3Y[v2])))
            {
                continue;
            }

            image.SpaceLines.Add(new SpaceLine(VertexX[v1], VertexY[v1], VertexZ[v1], VertexX[v2], VertexY[v2], VertexZ[v2], COL));
            lsnum += 4;
        }

        _screen.SetImage(owner, image);
    }

    /// <summary>Set up one row of XX16 from an orientation vector, scaling each coordinate by 256 / 197.</summary>
    private void SetUpMatrixRow(int row, IntVector3 v)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            int value = v[axis];
            int a = (Math.Abs(value) >> 7) & 0xFF;
            XX16[row * 3 + axis] = new SmByte(EliteMaths.Ll28(a, 197), value < 0);
        }
    }

    /// <summary>LL9 part 5: calculate the visibility of each of the ship's faces.</summary>
    private void CalculateFaceVisibility(ShipBlueprint blueprint)
    {
        int faceCount = blueprint.Faces.Count;
        if (faceCount == 0)
        {
            return;
        }

        // Scale the ship's position down until z_hi is zero, counting the
        // number of shifts on top of the normal scale factor
        int shifts = blueprint.NormalScale;
        int x = Math.Abs(INWK.X) & 0xFFFF;
        int y = Math.Abs(INWK.Y) & 0xFFFF;
        int z = Math.Abs(INWK.Z) & 0xFFFF;
        while ((z >> 8) != 0)
        {
            shifts++;
            x >>= 1;
            y >>= 1;
            z >>= 1;
        }

        int xx17 = shifts;

        // Rotate the ship's position into the ship's own frame of reference
        Span<SmByte> position = stackalloc SmByte[3];
        Span<SmByte> vector = stackalloc SmByte[3];
        vector[0] = new SmByte(x, INWK.X < 0);
        vector[1] = new SmByte(y, INWK.Y < 0);
        vector[2] = new SmByte(z, INWK.Z < 0);
        LL51(vector, XX16, position);

        for (int f = 0; f < faceCount; f++)
        {
            var face = blueprint.Faces[f];
            if (face.Visibility < XX4)
            {
                // The face is always visible at this distance
                XX2[f] = 255;
                continue;
            }

            // LL87
            var normal0 = new SmByte(Math.Abs(face.NormalX), face.NormalX < 0);
            var normal1 = new SmByte(Math.Abs(face.NormalY), face.NormalY < 0);
            var normal2 = new SmByte(Math.Abs(face.NormalZ), face.NormalZ < 0);

            SmByte v0, v1, v2;
            if (xx17 >= 4)
            {
                // LL143: the normal is insignificant compared to the distance
                v0 = position[0];
                v1 = position[1];
                v2 = position[2];
            }
            else
            {
                int shift = xx17;
                while (true)
                {
                    // LL92
                    int n0 = normal0.Magnitude >> shift;
                    int n1 = normal1.Magnitude >> shift;
                    int n2 = normal2.Magnitude >> shift;

                    v2 = LL38(new SmByte(n2, normal2.Negative), position[2], out bool o1);
                    if (!o1)
                    {
                        v0 = LL38(new SmByte(n0, normal0.Negative), position[0], out bool o2);
                        if (!o2)
                        {
                            v1 = LL38(new SmByte(n1, normal1.Negative), position[1], out bool o3);
                            if (!o3)
                            {
                                break;
                            }
                        }
                    }

                    // ovflw: halve the position and try again
                    position[0] = new SmByte(position[0].Magnitude >> 1, position[0].Negative);
                    position[2] = new SmByte(position[2].Magnitude >> 1, position[2].Negative);
                    position[1] = new SmByte(position[1].Magnitude >> 1, position[1].Negative);
                    shift = 1;
                }
            }

            // LL89: the dot product of the normal with the vector
            var t = new SmByte(EliteMaths.Fmltu(v0.Magnitude, normal0.Magnitude), normal0.Negative ^ v0.Negative);
            var q = new SmByte(EliteMaths.Fmltu(v1.Magnitude, normal1.Magnitude), normal1.Negative ^ v1.Negative);
            t = LL38(t, q, out _);
            q = new SmByte(EliteMaths.Fmltu(v2.Magnitude, normal2.Magnitude), v2.Negative ^ normal2.Negative);
            var dot = LL38(t, q, out _);
            XX2[f] = dot.Negative ? dot.Magnitude : 0;
        }
    }

    /// <summary>LL9 parts 6 to 8: calculate the 3D and screen coordinates of each visible vertex.</summary>
    private void ProjectVertices(ShipBlueprint blueprint)
    {
        Span<SmByte> vector = stackalloc SmByte[3];
        Span<SmByte> rotated = stackalloc SmByte[3];

        for (int i = 0; i < blueprint.Vertices.Count; i++)
        {
            var vertex = blueprint.Vertices[i];
            if (vertex.Visibility < XX4)
            {
                continue;
            }

            if (XX2[vertex.Face1] == 0 && XX2[vertex.Face2] == 0 && XX2[vertex.Face3] == 0 && XX2[vertex.Face4] == 0)
            {
                continue;
            }

            // LL49: rotate the vertex into our frame of reference
            vector[0] = new SmByte(Math.Abs(vertex.X), vertex.X < 0);
            vector[1] = new SmByte(Math.Abs(vertex.Y), vertex.Y < 0);
            vector[2] = new SmByte(Math.Abs(vertex.Z), vertex.Z < 0);
            LL51(vector, XX16, rotated);

            // Add the ship's position (as 16-bit sign-magnitude values)
            int x = AddSigned16(INWK.X, rotated[0]);
            int y = AddSigned16(INWK.Y, rotated[1]);

            // LL55: z, which is clamped to a minimum of 4
            int zPos = Math.Abs(INWK.Z) & 0xFFFF;
            int z = rotated[2].Negative ? zPos - rotated[2].Magnitude : zPos + rotated[2].Magnitude;
            if (z < 4)
            {
                z = 4;
            }

            VertexX[i] = x;
            VertexY[i] = y;
            VertexZ[i] = z;

            // LL57: scale down until everything fits into a byte
            int xm = Math.Abs(x), ym = Math.Abs(y), zm = z;
            while (((zm >> 8) | (xm >> 8) | (ym >> 8)) != 0)
            {
                xm >>= 1;
                ym >>= 1;
                zm >>= 1;
            }

            // LL60: project onto the screen
            int r = ProjectCoordinate(xm, zm, out int u);
            int value = (r | (u << 8)) & 0xFFFF;
            XX3X[i] = x < 0 ? (128 - value) & 0xFFFF : (128 + value) & 0xFFFF;

            r = ProjectCoordinate(ym, zm, out u);
            value = (r | (u << 8)) & 0xFFFF;
            XX3Y[i] = y < 0 ? (CentreY + value) & 0xFFFF : (CentreY - value) & 0xFFFF;
        }
    }

    /// <summary>(U R) = 256 * a / q, using LL28 if a &lt; q, or LL61 otherwise.</summary>
    private static int ProjectCoordinate(int a, int q, out int u)
    {
        u = 0;
        if (a < q)
        {
            return EliteMaths.Ll28(a, q);
        }

        // LL61
        if (q == 0)
        {
            u = 50;
            return 50;
        }

        int shifts = 0;
        do
        {
            a >>= 1;
            shifts++;
        }
        while (a >= q);

        int result = EliteMaths.Ll28(a, q);
        int hi = 0;
        for (int i = 0; i < shifts; i++)
        {
            int carry = (result >> 7) & 1;
            result = (result << 1) & 0xFF;
            hi = ((hi << 1) | carry) & 0xFF;
            if ((hi & 0x80) != 0)
            {
                u = 50;
                return 50;
            }
        }

        u = hi;
        return result;
    }

    /// <summary>Add a rotated vertex coordinate to a ship coordinate using 16-bit sign-magnitude arithmetic.</summary>
    private static int AddSigned16(int coordinate, SmByte offset)
    {
        int magnitude = Math.Abs(coordinate) & 0xFFFF;
        bool negative = coordinate < 0;
        if (negative == offset.Negative)
        {
            magnitude = (magnitude + offset.Magnitude) & 0xFFFF;
        }
        else
        {
            magnitude -= offset.Magnitude;
            if (magnitude < 0)
            {
                magnitude = -magnitude;
                negative = !negative;
            }
        }

        return negative ? -magnitude : magnitude;
    }

    private static int ToSigned16(int value) => (short)(value & 0xFFFF);

    /// <summary>
    /// The LL145 clipping test: returns true if the line from (x1, y1) to
    /// (x2, y2) intersects the space view (0-255, 0-191).
    /// </summary>
    private static bool LineOnScreen(int x1, int y1, int x2, int y2)
    {
        double t0 = 0, t1 = 1;
        double dx = x2 - x1, dy = y2 - y1;

        bool Clip(double p, double q)
        {
            if (p == 0)
            {
                return q >= 0;
            }

            double r = q / p;
            if (p < 0)
            {
                if (r > t1)
                {
                    return false;
                }

                if (r > t0)
                {
                    t0 = r;
                }
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }

                if (r < t1)
                {
                    t1 = r;
                }
            }

            return true;
        }

        return Clip(-dx, x1 - 0) && Clip(dx, 255 - x1) && Clip(-dy, y1 - 0) && Clip(dy, 2 * CentreY - 1 - y1);
    }

    /// <summary>LL14: the ship is not in view, so draw the explosion cloud if it's exploding, or erase it.</summary>
    private void LL14(object owner)
    {
        if ((INWK.Flags & Ship.FlagExploding) == 0)
        {
            EE51(owner);
            return;
        }

        INWK.Flags &= ~Ship.FlagDrawn;
        DOEXP(owner);
    }

    /// <summary>EE51: remove the ship from the screen if it is on-screen.</summary>
    private void EE51(object owner)
    {
        if ((INWK.Flags & Ship.FlagDrawn) != 0)
        {
            INWK.Flags &= ~Ship.FlagDrawn;
        }

        _screen.RemoveImage(owner);
    }

    /// <summary>SHPPT: draw a distant ship as a dot.</summary>
    private void SHPPT(object owner)
    {
        var image = new ObjectImage();
        if (PROJ(out int x, out int y) && (x >> 8) == 0 && (y >> 8) == 0 && y < 2 * CentreY - 2)
        {
            // Shpt: draw a four-pixel dash on two rows
            int x2 = Math.Min(x + 3, 255);
            image.Lines.Add(new ScreenLine(x, y, x2, y, COL));
            image.Lines.Add(new ScreenLine(x, y + 1, x2, y + 1, COL));
            INWK.Flags |= Ship.FlagDrawn;
        }
        else
        {
            // nono
            INWK.Flags &= ~Ship.FlagDrawn;
        }

        _screen.SetImage(owner, image);
    }

    /// <summary>
    /// PROJ: project the ship's centre onto the screen, returning false (C set)
    /// if it's too far off-screen. The results are K3 (x) and K4 (y) as 16-bit
    /// two's complement values.
    /// </summary>
    private bool PROJ(out int k3, out int k4)
    {
        k3 = 0;
        k4 = 0;
        if (!PLS6(INWK.X, out int x))
        {
            return false;
        }

        k3 = (x + CentreX) & 0xFFFF;
        if (!PLS6(-INWK.Y, out int y))
        {
            return false;
        }

        k4 = (y + CentreY) & 0xFFFF;
        return true;
    }

    /// <summary>PLS6: calculate 256 * value / z, returning false if the result is 1024 or more.</summary>
    private bool PLS6(int value, out int result)
    {
        int k = EliteMaths.Dvid3B2(value, INWK.Z);
        result = 0;
        if (Math.Abs(k) >= 1024)
        {
            return false;
        }

        result = k;
        return true;
    }

    // ------------------------------------------------------------------------
    // Explosions
    // ------------------------------------------------------------------------

    /// <summary>DOEXP: draw an exploding ship.</summary>
    private void DOEXP(object owner)
    {
        var cloud = INWK.Explosion;
        if ((INWK.Flags & Ship.FlagOnScreenCloud) != 0)
        {
            // Erase the existing cloud (which, as in the original, reseeds the
            // random number generator)
            PTCLS(null);
            _screen.RemoveImage(owner);
        }

        // Work out the cloud's size from its distance and counter
        int zHi = INWK.ZHi;
        int q;
        if (zHi >= 32)
        {
            q = 0xFE;
        }
        else
        {
            int z = ((zHi << 8) | INWK.ZLo) >> 6;
            q = ((z << 1) | 1) & 0xFF;
        }

        // The ADC #4 includes the C flag, which is set if z_hi >= 32 (from the
        // CMP) and clear otherwise (from the ROL, as z / 64 &lt; 128)
        int counter = cloud.Counter + 4 + (zHi >= 32 ? 1 : 0);
        if (counter > 0xFF)
        {
            // EX2: the explosion has finished
            INWK.Flags |= 0b10100000;
            return;
        }

        cloud.Counter = counter;
        EliteMaths.Dvid4(counter, q, out int p, out int r);
        int size;
        if (p >= 0x1C)
        {
            size = 0xFE;
        }
        else
        {
            size = ((p << 3) | (r >> 5)) & 0xFF;
        }

        cloud.Size = size;
        INWK.Flags &= ~Ship.FlagOnScreenCloud;

        if ((INWK.Flags & Ship.FlagDrawn) == 0)
        {
            return;
        }

        // Copy the screen coordinates of the explosion vertices into the heap
        cloud.Origins.Clear();
        int vertices = (cloud.CountByte - 6) / 4;
        for (int i = 0; i < vertices; i++)
        {
            cloud.Origins.Add((XX3X[i], XX3Y[i]));
        }

        INWK.Flags |= Ship.FlagOnScreenCloud;
        var image = new ObjectImage();
        PTCLS(image);
        _screen.SetImage(owner, image);
    }

    /// <summary>
    /// PTCLS: draw (or erase) the explosion cloud. The random number generator
    /// is seeded from the cloud data so the same cloud is produced each time.
    /// </summary>
    private void PTCLS(ObjectImage? image)
    {
        var cloud = INWK.Explosion;
        int counter = cloud.Counter;
        if ((counter & 0x80) != 0)
        {
            counter ^= 0xFF;
        }

        int particles = (counter >> 4) | 1;
        int savedRand1 = RAND[1];

        for (int v = 0; v < cloud.Origins.Count; v++)
        {
            var (ox, oy) = cloud.Origins[v];
            int cnt = 6 + 4 * (v + 1);

            // Seed the random number generator from the cloud's seeds
            for (int i = 0; i < 4; i++)
            {
                RAND[i] = cloud.Seeds[i] ^ cnt;
            }

            for (int n = particles; n >= 0; n--)
            {
                int zz = NextCloudRandom();
                COL = GameData.ExplosionColours[zz & 3];

                int y = EXS1(oy, cloud.Size);
                if ((y >> 8) != 0 || (y & 0xFF) >= 2 * CentreY - 1)
                {
                    // EX11
                    NextCloudRandom();
                    continue;
                }

                int x = EXS1(ox, cloud.Size);
                if ((x >> 8) != 0)
                {
                    continue;
                }

                image?.Rects.AddRange(PixelRects(x, y, zz, COL));
            }
        }

        RAND[1] = savedRand1;
        RAND[3] = Planet.ZLo;
    }

    /// <summary>The inline random number generator used by PTCLS (DORND with the C flag clear).</summary>
    private int NextCloudRandom()
    {
        _carry = false;
        return DORND();
    }

    /// <summary>
    /// EXS1: return a random coordinate within the cloud's size of the given
    /// origin coordinate (a 16-bit word), as a 16-bit value.
    /// </summary>
    private int EXS1(int origin, int size)
    {
        int s = (origin >> 8) & 0xFF;
        int r = origin & 0xFF;
        int a = NextCloudRandom();
        bool negative = (a & 0x80) != 0;
        a = (a << 1) & 0xFF;
        int product = EliteMaths.Fmltu(a, size, out bool carry);
        if (!negative)
        {
            int sum = r + product + (carry ? 1 : 0);
            int lo = sum & 0xFF;
            int hi = (s + (sum > 0xFF ? 1 : 0)) & 0xFF;
            return (hi << 8) | lo;
        }

        int difference = r - product - (carry ? 0 : 1);
        int low = difference & 0xFF;
        int high = (s - (difference < 0 ? 1 : 0)) & 0xFF;
        return (high << 8) | low;
    }

    /// <summary>
    /// PIXEL: the rectangles for a dot at (x, y), which is two pixels wide and
    /// one or two pixels high depending on the distance in zz.
    /// </summary>
    private static IEnumerable<ScreenRect> PixelRects(int x, int y, int zz, int colour)
    {
        // TWOS2 keeps the two pixels within the byte
        int left = (x & 3) == 3 ? x - 1 : x;
        if (zz >= 80)
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

    /// <summary>LSX2: true if the ball line heap is empty (no planet on-screen).</summary>
    private bool LSX2Empty = true;

    /// <summary>LSP: the ball line heap pointer.</summary>
    private int LSP;

    /// <summary>STP: the step size for drawing circles.</summary>
    private int STP;

    /// <summary>K: the radius of the circle being drawn.</summary>
    private int KRadius;

    /// <summary>K3 and K4 for circles: the centre of the circle (16-bit two's complement).</summary>
    private int CircleX, CircleY;

    /// <summary>K2 and XX16+0..3: the ellipse axes (magnitudes and signs).</summary>
    private readonly int[] K2 = new int[4];
    private readonly bool[] K2Negative = new bool[4];

    /// <summary>CNT2: the angle counter for ellipses.</summary>
    private int PlanetAngle;

    /// <summary>The segments being collected by BLINE.</summary>
    private readonly List<ScreenLine> _ballLines = [];
    private bool _ballFirst;
    private int _ballPrevX, _ballPrevY;

    /// <summary>PLANET: draw the planet or sun in INWK.</summary>
    private void PLANET()
    {
        COL = GREEN;
        int zSign = INWK.ZSign;
        if (zSign >= 48 || (zSign | INWK.ZHi) == 0)
        {
            PL2();
            return;
        }

        if (!PROJ(out CircleX, out CircleY))
        {
            PL2();
            return;
        }

        // The planet's radius is 96 * 256 * 256 / z
        int k = EliteMaths.Dvid3B2(96 << 8, INWK.Z);
        bool large = (k >> 8) != 0;
        KRadius = large ? 248 : k & 0xFF;

        if ((TYPE & 1) != 0)
        {
            SUN();
            return;
        }

        PL9(large);
    }

    /// <summary>PL2: remove the planet or sun from the screen.</summary>
    private void PL2()
    {
        if ((TYPE & 1) == 0)
        {
            WPLS2();
        }
        else
        {
            WPLS();
        }
    }

    /// <summary>PL9: draw the planet with its meridians and equator, or its crater.</summary>
    private void PL9(bool large)
    {
        WPLS2();
        _ballLines.Clear();
        if (!CIRCLE())
        {
            return;
        }

        if (!large)
        {
            if (TYPE == ShipType.Planet)
            {
                // PL9 part 2: the meridian and equator
                if (KRadius >= 6)
                {
                    PLS4(INWK.Roof.Z);
                    PLS1(INWK.Nose.X, 0);
                    PLS1(INWK.Nose.Y, 1);
                    PLS1(INWK.Roof.X, 2);
                    PLS1(INWK.Roof.Y, 3);
                    PLS2();

                    PLS4(INWK.Side.Z);
                    PLS1(INWK.Side.X, 2);
                    PLS1(INWK.Side.Y, 3);
                    PLS2();
                }
            }
            else if (INWK.Roof.Z >= 0)
            {
                // PL26: the crater
                CircleX = (CircleX + PLS3(INWK.Roof.X)) & 0xFFFF;
                CircleY = (CircleY - PLS3(INWK.Roof.Y)) & 0xFFFF;
                PLS1(INWK.Nose.X, 0, halve: true);
                PLS1(INWK.Nose.Y, 1, halve: true);
                PLS1(INWK.Side.X, 2, halve: true);
                PLS1(INWK.Side.Y, 3, halve: true);
                PlanetAngle = 0;
                PLS22(64);
            }
        }

        var image = new ObjectImage();
        image.Lines.AddRange(_ballLines);
        _screen.SetImage(INWK.DisplayOwner, image);
        LSX2Empty = false;
    }

    /// <summary>
    /// PLS1: calculate a vector coordinate * 256 / z (clamped to 254) into one
    /// of the K2 slots, with its sign.
    /// </summary>
    private void PLS1(int vector, int index, bool halve = false)
    {
        int k = EliteMaths.Dvid3B2(vector, INWK.Z);
        int a = Math.Abs(k) >= 256 ? 254 : Math.Abs(k) & 0xFF;
        if (halve)
        {
            a >>= 1;
        }

        K2[index] = a;
        K2Negative[index] = k < 0 || (k == 0 && vector < 0);
    }

    /// <summary>
    /// PLS3: calculate 222 * roofv * 256 / z / 256 as a signed 16-bit value,
    /// used to offset the crater from the planet's centre.
    /// </summary>
    private int PLS3(int vector)
    {
        int k = EliteMaths.Dvid3B2(vector, INWK.Z);
        int a = Math.Abs(k) >= 256 ? 254 : Math.Abs(k) & 0xFF;
        int product = (a * 222) >> 8;
        bool negative = k < 0 || (k == 0 && vector < 0);
        if (negative && product != 0)
        {
            return -product;
        }

        return product;
    }

    /// <summary>PLS4: CNT2 = arctan(-nosev_z / vector_z) / 4, for the starting angle of the ellipse.</summary>
    private void PLS4(int vectorZ)
    {
        int p = -Ship.VectorHi(INWK.Nose.Z);
        int q = Ship.VectorHi(vectorZ);
        int a = EliteMaths.Arctan(p, q);
        if (INWK.Nose.Z >= 0)
        {
            a ^= 0x80;
        }

        PlanetAngle = (a >> 2) & 0xFF;
    }

    /// <summary>PLS2: draw a half ellipse.</summary>
    private void PLS2() => PLS22(31);

    /// <summary>
    /// PLS22: draw an ellipse (or part of one) with axes in K2, centred on
    /// K3/K4, starting at angle CNT2 and continuing until the counter reaches
    /// the target.
    /// </summary>
    private void PLS22(int target)
    {
        int count = 0;
        _ballFirst = true;
        while (true)
        {
            int angle = PlanetAngle;
            int sine = GameData.Sine[angle & 31];
            int r = EliteMaths.Fmltu(K2[2], sine);
            int k = EliteMaths.Fmltu(K2[3], sine);
            bool sinNegative = angle >= 33;

            int cosine = GameData.Sine[(angle + 16) & 31];
            int k2 = EliteMaths.Fmltu(K2[1], cosine);
            int p = EliteMaths.Fmltu(K2[0], cosine, out bool carry);

            // The ADC #15 includes the C flag from the last FMLTU
            bool cosNegative = ((angle + 15 + (carry ? 1 : 0)) & 63) >= 33;

            // x = nosev_x * cos + roofv_x * sin
            int xs = EliteMaths.Add16(Signed16(p, cosNegative ^ K2Negative[0]), Signed16(r, sinNegative ^ K2Negative[2]));
            int ys = -EliteMaths.Add16(Signed16(k2, cosNegative ^ K2Negative[1]), Signed16(k, sinNegative ^ K2Negative[3]));

            count = BLINE(CircleX + xs, CircleY + ys, count);
            if (count > target)
            {
                return;
            }

            PlanetAngle = (PlanetAngle + STP) & 63;
        }
    }

    private static int Signed16(int magnitude, bool negative) => negative ? -magnitude : magnitude;

    /// <summary>
    /// BLINE: add a segment from the previous point to (x, y) to the ball line
    /// list, returning the updated segment counter (CNT + STP).
    /// </summary>
    private int BLINE(int x, int y, int count)
    {
        x = ToSigned16(x);
        y = ToSigned16(y);
        if (_ballFirst)
        {
            _ballFirst = false;
        }
        else if (LineOnScreen(_ballPrevX, _ballPrevY, x, y))
        {
            _ballLines.Add(new ScreenLine(_ballPrevX, _ballPrevY, x, y, COL));
        }

        _ballPrevX = x;
        _ballPrevY = y;
        return count + STP;
    }

    /// <summary>CIRCLE: draw a circle for the planet, returning false (C set) if it's off-screen.</summary>
    private bool CIRCLE()
    {
        if (!CHKON(out _, out _))
        {
            return false;
        }

        LSX2Empty = false;
        int k = KRadius;
        STP = k < 8 ? 8 : k < 60 ? 4 : 2;
        CIRCLE2();
        return true;
    }

    /// <summary>CIRCLE2: draw a circle of radius K centred on K3/K4, with step size STP.</summary>
    private void CIRCLE2()
    {
        _ballFirst = true;
        int count = 0;
        while (true)
        {
            int x = EliteMaths.Fmltu(KRadius, GameData.Sine[count & 31]);
            if (count >= 33)
            {
                x = -x;
            }

            int y = EliteMaths.Fmltu(KRadius, GameData.Sine[(count + 16) & 31], out bool carry);

            // The ADC #15 includes the C flag from FMLTU2
            if (((count + 15 + (carry ? 1 : 0)) & 63) >= 33)
            {
                y = -y;
            }

            int next = BLINE(CircleX + x, CircleY + y, count);
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
    private bool CHKON(out int bottom, out int top)
    {
        int cx = ToSigned16(CircleX), cy = ToSigned16(CircleY);
        bottom = cy + KRadius;
        top = cy - KRadius;
        if (cx + KRadius < 0)
        {
            return false;
        }

        int left = cx - KRadius;
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
    private void WPLS2()
    {
        _screen.RemoveImage(INWK.DisplayOwner);
        LSX2Empty = true;
        LSP = 1;
    }

    // ------------------------------------------------------------------------
    // The sun
    // ------------------------------------------------------------------------

    /// <summary>LSO: the sun line heap (the half-width of the sun on each pixel row).</summary>
    private readonly int[] LSO = new int[2 * CentreY + 8];

    /// <summary>LSX: &amp;FF if the sun is not on-screen.</summary>
    private int LSX = 0xFF;

    /// <summary>SUNX: the x-coordinate of the centre of the sun on-screen.</summary>
    private int SUNX;

    private readonly object _sunOwner = new();
    private ObjectImage? _sunImage;

    /// <summary>The orange colours for each pixel row of the sun.</summary>
    private static readonly int[] Orange = [0b10100101, 0b10100101, 0b01011010, 0b01011010];

    /// <summary>SUN: draw the sun, with its fringe of random widths.</summary>
    private void SUN()
    {
        COL = RED;
        LSX = 1;
        if (!CHKON(out int bottom, out _))
        {
            WPLS();
            return;
        }

        int k = KRadius;
        int cnt = (k >= 96 ? 4 : 0) | (k >= 40 ? 2 : 0) | (k >= 16 ? 1 : 0);

        // Work out the bottom row of the sun (TGT)
        int yMax = 2 * CentreY - 1;
        int tgt;
        if ((bottom >> 8) != 0 || yMax < (bottom & 0xFF))
        {
            tgt = yMax;
        }
        else
        {
            tgt = (bottom & 0xFF) != 0 ? bottom & 0xFF : 1;
        }

        // Work out V, the vertical distance from row Yx2M1 to the centre
        int cy = ToSigned16(CircleY);
        int v, vHi;
        int distance = yMax - cy;
        if (distance < 0)
        {
            v = -distance & 0xFF;
            vHi = 0xFF;
        }
        else if (distance >= 256 || distance >= k)
        {
            v = k;
            vHi = 0;
        }
        else if (distance == 0)
        {
            v = 0;
            vHi = 0xFF;
        }
        else
        {
            v = distance;
            vHi = 0;
        }

        int k2 = k * k;

        // Rows below the sun no longer have any sun lines
        for (int row = yMax; row > tgt; row--)
        {
            LSO[row] = 0;
        }

        int centre = ToSigned16(CircleX);
        int y = tgt;
        bool finished = false;
        while (!finished)
        {
            // PLFL: the half-width of this row
            int rq = k2 - v * v;
            int q = EliteMaths.Ll5(rq & 0xFFFF);
            int width = (DORND() & cnt) + q;
            if (width > 255)
            {
                width = 255;
            }

            LSO[y] = width;
            if (!SunEdges(centre, width, out _, out _))
            {
                LSO[y] = 0;
            }

            // PLF6
            y--;
            if (y == 0)
            {
                break;
            }

            if (vHi != 0)
            {
                // PLF10: we are in the top half, moving away from the centre
                v++;
                if (v > k)
                {
                    // Remove any old sun lines above the new sun
                    for (int row = y; row > 0; row--)
                    {
                        LSO[row] = 0;
                    }

                    finished = true;
                }
            }
            else
            {
                v--;
                if (v == 0)
                {
                    vHi = 0xFF;
                }
            }
        }

        // PLF8
        SUNX = CircleX;
        var image = new ObjectImage();
        for (int row = 1; row <= yMax; row++)
        {
            if (LSO[row] != 0 && SunEdges(centre, LSO[row], out int x1, out int x2) && x2 > x1)
            {
                image.Rects.Add(new ScreenRect(x1, row, x2 - x1, 1, Orange[row & 3]));
            }
        }

        if (_sunToCanvas)
        {
            // The stars on the short-range chart are drawn with the sun
            // routine, and stay on-screen
            foreach (var rect in image.Rects)
            {
                _screen.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Colour);
            }

            return;
        }

        _sunImage = image;
        _screen.SetImage(_sunOwner, image);
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
    private void WPLS()
    {
        if ((LSX & 0x80) != 0)
        {
            return;
        }

        _screen.RemoveImage(_sunOwner);
        _sunImage = null;
        Array.Clear(LSO);
        LSX = 0xFF;
    }

    // ------------------------------------------------------------------------
    // The hyperspace and launch tunnels
    // ------------------------------------------------------------------------

    /// <summary>HFS2: clear the screen and draw the launch or hyperspace tunnel with the given step size.</summary>
    private void HFS2(int step)
    {
        STP = step;
        int view = QQ11;
        TT66(0);
        QQ11 = view;
        HFS1();
    }

    /// <summary>HFS1: draw the tunnel as eight sets of concentric circles.</summary>
    private void HFS1()
    {
        CircleX = CentreX;
        CircleY = CentreY;
        for (int i = 0; i < 8; i++)
        {
            // HFL1
            KRadius = (i & 7) + 8;
            while (true)
            {
                LSP = 1;
                _ballLines.Clear();
                CIRCLE2();
                foreach (var line in _ballLines)
                {
                    _screen.DrawLine(line.X1, line.Y1, line.X2, line.Y2, COL);
                }

                // The original draws the circles slowly enough for them to be
                // seen appearing, so we pause briefly after each one
                PresentAndPause(8);

                int doubled = KRadius << 1;
                if (doubled > 0xFF)
                {
                    break;
                }

                KRadius = doubled;
                if (KRadius >= 160)
                {
                    break;
                }
            }
        }
    }
}

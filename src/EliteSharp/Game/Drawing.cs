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
    /// <summary>XX3: the screen x-coordinate of each vertex (a 16-bit two's complement word).</summary>
    private readonly int[] _projectedX = new int[64];

    /// <summary>XX3: the screen y-coordinate of each vertex (a 16-bit two's complement word).</summary>
    private readonly int[] _projectedY = new int[64];

    /// <summary>The x-coordinate of each vertex, relative to our ship, as calculated in LL9.</summary>
    private readonly int[] VertexX = new int[64];

    /// <summary>The y-coordinate of each vertex, relative to our ship, as calculated in LL9.</summary>
    private readonly int[] VertexY = new int[64];

    /// <summary>The z-coordinate of each vertex, relative to our ship, as calculated in LL9.</summary>
    private readonly int[] VertexZ = new int[64];

    /// <summary>
    /// XX2: the visibility of each face, which is zero if the face is hidden
    /// (this shares memory with K3 in the original).
    /// </summary>
    private int[] FaceVisibility => _faceVisibility;

    /// <summary>The storage for <see cref="FaceVisibility"/>.</summary>
    private readonly int[] _faceVisibility = new int[16];

    /// <summary>XX4: the distance of the ship, used for level of detail.</summary>
    private int _shipDistance;

    /// <summary>COL: the current colour byte.</summary>
    private int _colour;

    // ------------------------------------------------------------------------
    // LL9: drawing ships
    // ------------------------------------------------------------------------

    /// <summary>A sign-magnitude byte pair used by the original's 8-bit vector maths.</summary>
    private struct SignMagnitudeByte(int magnitude, bool negative)
    {
        /// <summary>The magnitude (0-255).</summary>
        public int Magnitude = magnitude & 0xFF;

        /// <summary>True if the value is negative (bit 7 of the sign byte).</summary>
        public bool Negative = negative;
    }

    /// <summary>
    /// LL38: (S A) = (S R) + (A Q), adding two sign-magnitude bytes, returning
    /// the overflow in the C flag.
    /// </summary>
    private static SignMagnitudeByte AddSignMagnitudeBytes(SignMagnitudeByte first, SignMagnitudeByte second, out bool overflow)
    {
        overflow = false;
        if (first.Negative == second.Negative)
        {
            int sum = first.Magnitude + second.Magnitude;
            overflow = sum > 0xFF;
            return new SignMagnitudeByte(sum, first.Negative);
        }

        int difference = first.Magnitude - second.Magnitude;
        if (difference >= 0)
        {
            return new SignMagnitudeByte(difference, first.Negative);
        }

        return new SignMagnitudeByte(-difference, !first.Negative);
    }

    /// <summary>
    /// LL51: calculate the dot products of a vector (three sign-magnitude
    /// bytes) with the three rows of the XX16 matrix.
    /// </summary>
    private static void MultiplyByMatrix(ReadOnlySpan<SignMagnitudeByte> vector, SignMagnitudeByte[] matrix, Span<SignMagnitudeByte> result)
    {
        for (int row = 0; row < 3; row++)
        {
            int rowStart = row * 3;
            var sum = new SignMagnitudeByte(EliteMaths.MultiplyFraction(vector[0].Magnitude, matrix[rowStart].Magnitude), vector[0].Negative ^ matrix[rowStart].Negative);
            var term = new SignMagnitudeByte(EliteMaths.MultiplyFraction(vector[1].Magnitude, matrix[rowStart + 1].Magnitude), vector[1].Negative ^ matrix[rowStart + 1].Negative);
            sum = AddSignMagnitudeBytes(sum, term, out _);
            term = new SignMagnitudeByte(EliteMaths.MultiplyFraction(vector[2].Magnitude, matrix[rowStart + 2].Magnitude), vector[2].Negative ^ matrix[rowStart + 2].Negative);
            result[row] = AddSignMagnitudeBytes(sum, term, out _);
        }
    }

    /// <summary>XX16: the orientation matrix, scaled for use in LL51.</summary>
    private readonly SignMagnitudeByte[] _orientationMatrix = new SignMagnitudeByte[9];

    /// <summary>LL9: draw the ship in INWK.</summary>
    private void DrawShip()
    {
        if (_shipType >= 128)
        {
            // LL25
            DrawPlanetOrSun();
            return;
        }

        var owner = _currentShip.DisplayOwner;
        _colour = ShipCatalogue.Get(_shipType).Colour;
        _shipDistance = 31;

        if ((_currentShip.Behaviour & 0x80) != 0)
        {
            // The ship has been scooped or has docked
            RemoveShipFromScreen(owner);
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

            RemoveShipFromScreen(owner);
            var cloud = _currentShip.Explosion;
            cloud.Counter = 18;
            cloud.CountByte = _blueprint!.ExplosionCountByte;
            for (int i = 0; i < 4; i++)
            {
                cloud.Seeds[i] = (byte)NextRandom();
            }
        }

        // EE28
        if (_currentShip.Z < 0)
        {
            DrawShipOutOfView(owner);
            return;
        }

        // LL10: check whether the ship is in the field of view
        if (_currentShip.ZHi >= 192)
        {
            DrawShipOutOfView(owner);
            return;
        }

        int zMagnitude = Math.Abs(_currentShip.Z) & 0xFFFF;
        if ((Math.Abs(_currentShip.X) & 0xFFFF) >= zMagnitude || (Math.Abs(_currentShip.Y) & 0xFFFF) >= zMagnitude)
        {
            DrawShipOutOfView(owner);
            return;
        }

        // Mark the gun vertex as not yet projected
        int gun = _blueprint!.GunVertex;
        _projectedX[gun] = 0xFFFF;

        // Calculate the distance for the level of detail
        if ((_currentShip.ZHi >> 4) == 0)
        {
            _shipDistance = (zMagnitude >> 7) & 0x1F;
        }
        else if (_blueprint.VisibilityDistance < _currentShip.ZHi && (_currentShip.Flags & Ship.FlagExploding) == 0)
        {
            // LL13: the ship is too far away, so draw it as a dot
            DrawShipAsDot(owner);
            return;
        }

        // LL17: set up the orientation matrix in XX16 (sidev, roofv, nosev as rows)
        SetUpMatrixRow(0, _currentShip.Side);
        SetUpMatrixRow(1, _currentShip.Roof);
        SetUpMatrixRow(2, _currentShip.Nose);

        FaceVisibility[15] = 255;
        var blueprint = _blueprint;
        if ((_currentShip.Flags & Ship.FlagExploding) != 0)
        {
            // All faces are visible when exploding
            for (int f = 0; f < blueprint.Faces.Count; f++)
            {
                FaceVisibility[f] = 255;
            }

            _shipDistance = 0;
        }
        else
        {
            CalculateFaceVisibility(blueprint);
        }

        // LL42: transpose the matrix so we can rotate the vertices into our frame
        (_orientationMatrix[1], _orientationMatrix[3]) = (_orientationMatrix[3], _orientationMatrix[1]);
        (_orientationMatrix[2], _orientationMatrix[6]) = (_orientationMatrix[6], _orientationMatrix[2]);
        (_orientationMatrix[5], _orientationMatrix[7]) = (_orientationMatrix[7], _orientationMatrix[5]);

        ProjectVertices(blueprint);

        // LL72
        if ((_currentShip.Flags & Ship.FlagExploding) != 0)
        {
            _currentShip.Flags |= Ship.FlagDrawn;
            DrawExplosion(owner);
            return;
        }

        _currentShip.Flags |= Ship.FlagDrawn;
        var image = new ObjectImage();
        int lineHeapUsed = 1;
        int heapSize = blueprint.LineHeapSize;

        if ((_currentShip.Flags & Ship.FlagFiring) != 0)
        {
            _currentShip.Flags &= ~Ship.FlagFiring;
            int gunX = _projectedX[gun], gunY = _projectedY[gun];
            if ((gunX & 0xFF) != 0xFF && ((gunX >> 8) & 0xFF) != 0xFF)
            {
                int x2 = _currentShip.X < 0 ? 255 : 0;
                int y2 = _currentShip.ZLo;
                int gunScreenX = ToSigned16(gunX), gunScreenY = ToSigned16(gunY);
                if (LineOnScreen(gunScreenX, gunScreenY, x2, y2))
                {
                    image.Lines.Add(new ScreenLine(gunScreenX, gunScreenY, x2, y2, _colour));
                    lineHeapUsed += 4;
                }
            }
        }

        // LL170: draw the visible edges
        foreach (var edge in blueprint.Edges)
        {
            if (lineHeapUsed >= heapSize)
            {
                break;
            }

            if (edge.Visibility < _shipDistance)
            {
                continue;
            }

            if (FaceVisibility[edge.Face1] == 0 && FaceVisibility[edge.Face2] == 0)
            {
                continue;
            }

            int vertex1 = edge.Vertex1, vertex2 = edge.Vertex2;
            if (!LineOnScreen(ToSigned16(_projectedX[vertex1]), ToSigned16(_projectedY[vertex1]), ToSigned16(_projectedX[vertex2]), ToSigned16(_projectedY[vertex2])))
            {
                continue;
            }

            image.SpaceLines.Add(new SpaceLine(VertexX[vertex1], VertexY[vertex1], VertexZ[vertex1], VertexX[vertex2], VertexY[vertex2], VertexZ[vertex2], _colour));
            lineHeapUsed += 4;
        }

        _screen.SetImage(owner, image);
    }

    /// <summary>Set up one row of XX16 from an orientation vector, scaling each coordinate by 256 / 197.</summary>
    private void SetUpMatrixRow(int row, IntVector3 v)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            int value = v[axis];
            int magnitude = (Math.Abs(value) >> 7) & 0xFF;
            _orientationMatrix[row * 3 + axis] = new SignMagnitudeByte(EliteMaths.DivideFraction(magnitude, 197), value < 0);
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
        int x = Math.Abs(_currentShip.X) & 0xFFFF;
        int y = Math.Abs(_currentShip.Y) & 0xFFFF;
        int z = Math.Abs(_currentShip.Z) & 0xFFFF;
        while ((z >> 8) != 0)
        {
            shifts++;
            x >>= 1;
            y >>= 1;
            z >>= 1;
        }

        int scaleShifts = shifts;

        // Rotate the ship's position into the ship's own frame of reference
        Span<SignMagnitudeByte> position = stackalloc SignMagnitudeByte[3];
        Span<SignMagnitudeByte> vector = stackalloc SignMagnitudeByte[3];
        vector[0] = new SignMagnitudeByte(x, _currentShip.X < 0);
        vector[1] = new SignMagnitudeByte(y, _currentShip.Y < 0);
        vector[2] = new SignMagnitudeByte(z, _currentShip.Z < 0);
        MultiplyByMatrix(vector, _orientationMatrix, position);

        for (int f = 0; f < faceCount; f++)
        {
            var face = blueprint.Faces[f];
            if (face.Visibility < _shipDistance)
            {
                // The face is always visible at this distance
                FaceVisibility[f] = 255;
                continue;
            }

            // LL87
            var normalX = new SignMagnitudeByte(Math.Abs(face.NormalX), face.NormalX < 0);
            var normalY = new SignMagnitudeByte(Math.Abs(face.NormalY), face.NormalY < 0);
            var normalZ = new SignMagnitudeByte(Math.Abs(face.NormalZ), face.NormalZ < 0);

            SignMagnitudeByte vectorX, vectorY, vectorZ;
            if (scaleShifts >= 4)
            {
                // LL143: the normal is insignificant compared to the distance
                vectorX = position[0];
                vectorY = position[1];
                vectorZ = position[2];
            }
            else
            {
                int shift = scaleShifts;
                while (true)
                {
                    // LL92
                    int scaledNormalX = normalX.Magnitude >> shift;
                    int scaledNormalY = normalY.Magnitude >> shift;
                    int scaledNormalZ = normalZ.Magnitude >> shift;

                    vectorZ = AddSignMagnitudeBytes(new SignMagnitudeByte(scaledNormalZ, normalZ.Negative), position[2], out bool overflowZ);
                    if (!overflowZ)
                    {
                        vectorX = AddSignMagnitudeBytes(new SignMagnitudeByte(scaledNormalX, normalX.Negative), position[0], out bool overflowX);
                        if (!overflowX)
                        {
                            vectorY = AddSignMagnitudeBytes(new SignMagnitudeByte(scaledNormalY, normalY.Negative), position[1], out bool overflowY);
                            if (!overflowY)
                            {
                                break;
                            }
                        }
                    }

                    // ovflw: halve the position and try again
                    position[0] = new SignMagnitudeByte(position[0].Magnitude >> 1, position[0].Negative);
                    position[2] = new SignMagnitudeByte(position[2].Magnitude >> 1, position[2].Negative);
                    position[1] = new SignMagnitudeByte(position[1].Magnitude >> 1, position[1].Negative);
                    shift = 1;
                }
            }

            // LL89: the dot product of the normal with the vector
            var sum = new SignMagnitudeByte(EliteMaths.MultiplyFraction(vectorX.Magnitude, normalX.Magnitude), normalX.Negative ^ vectorX.Negative);
            var term = new SignMagnitudeByte(EliteMaths.MultiplyFraction(vectorY.Magnitude, normalY.Magnitude), normalY.Negative ^ vectorY.Negative);
            sum = AddSignMagnitudeBytes(sum, term, out _);
            term = new SignMagnitudeByte(EliteMaths.MultiplyFraction(vectorZ.Magnitude, normalZ.Magnitude), vectorZ.Negative ^ normalZ.Negative);
            var dot = AddSignMagnitudeBytes(sum, term, out _);
            FaceVisibility[f] = dot.Negative ? dot.Magnitude : 0;
        }
    }

    /// <summary>LL9 parts 6 to 8: calculate the 3D and screen coordinates of each visible vertex.</summary>
    private void ProjectVertices(ShipBlueprint blueprint)
    {
        Span<SignMagnitudeByte> vector = stackalloc SignMagnitudeByte[3];
        Span<SignMagnitudeByte> rotated = stackalloc SignMagnitudeByte[3];

        for (int i = 0; i < blueprint.Vertices.Count; i++)
        {
            var vertex = blueprint.Vertices[i];
            if (vertex.Visibility < _shipDistance)
            {
                continue;
            }

            if (FaceVisibility[vertex.Face1] == 0 && FaceVisibility[vertex.Face2] == 0 && FaceVisibility[vertex.Face3] == 0 && FaceVisibility[vertex.Face4] == 0)
            {
                continue;
            }

            // LL49: rotate the vertex into our frame of reference
            vector[0] = new SignMagnitudeByte(Math.Abs(vertex.X), vertex.X < 0);
            vector[1] = new SignMagnitudeByte(Math.Abs(vertex.Y), vertex.Y < 0);
            vector[2] = new SignMagnitudeByte(Math.Abs(vertex.Z), vertex.Z < 0);
            MultiplyByMatrix(vector, _orientationMatrix, rotated);

            // Add the ship's position (as 16-bit sign-magnitude values)
            int x = AddSigned16(_currentShip.X, rotated[0]);
            int y = AddSigned16(_currentShip.Y, rotated[1]);

            // LL55: z, which is clamped to a minimum of 4
            int zPos = Math.Abs(_currentShip.Z) & 0xFFFF;
            int z = rotated[2].Negative ? zPos - rotated[2].Magnitude : zPos + rotated[2].Magnitude;
            if (z < 4)
            {
                z = 4;
            }

            VertexX[i] = x;
            VertexY[i] = y;
            VertexZ[i] = z;

            // LL57: scale down until everything fits into a byte
            int xScaled = Math.Abs(x), yScaled = Math.Abs(y), zScaled = z;
            while (((zScaled >> 8) | (xScaled >> 8) | (yScaled >> 8)) != 0)
            {
                xScaled >>= 1;
                yScaled >>= 1;
                zScaled >>= 1;
            }

            // LL60: project onto the screen
            int low = ProjectCoordinate(xScaled, zScaled, out int high);
            int value = (low | (high << 8)) & 0xFFFF;
            _projectedX[i] = x < 0 ? (128 - value) & 0xFFFF : (128 + value) & 0xFFFF;

            low = ProjectCoordinate(yScaled, zScaled, out high);
            value = (low | (high << 8)) & 0xFFFF;
            _projectedY[i] = y < 0 ? (CentreY + value) & 0xFFFF : (CentreY - value) & 0xFFFF;
        }
    }

    /// <summary>(U R) = 256 * a / q, using LL28 if a &lt; q, or LL61 otherwise.</summary>
    private static int ProjectCoordinate(int numerator, int denominator, out int high)
    {
        high = 0;
        if (numerator < denominator)
        {
            return EliteMaths.DivideFraction(numerator, denominator);
        }

        // LL61
        if (denominator == 0)
        {
            high = 50;
            return 50;
        }

        int shifts = 0;
        do
        {
            numerator >>= 1;
            shifts++;
        }
        while (numerator >= denominator);

        int result = EliteMaths.DivideFraction(numerator, denominator);
        int highByte = 0;
        for (int i = 0; i < shifts; i++)
        {
            int carry = (result >> 7) & 1;
            result = (result << 1) & 0xFF;
            highByte = ((highByte << 1) | carry) & 0xFF;
            if ((highByte & 0x80) != 0)
            {
                high = 50;
                return 50;
            }
        }

        high = highByte;
        return result;
    }

    /// <summary>Add a rotated vertex coordinate to a ship coordinate using 16-bit sign-magnitude arithmetic.</summary>
    private static int AddSigned16(int coordinate, SignMagnitudeByte offset)
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

    /// <summary>Convert a 16-bit two's complement word to a signed value.</summary>
    private static int ToSigned16(int value) => (short)(value & 0xFFFF);

    /// <summary>
    /// The LL145 clipping test: returns true if the line from (x1, y1) to
    /// (x2, y2) intersects the space view (0-255, 0-191).
    /// </summary>
    private static bool LineOnScreen(int x1, int y1, int x2, int y2)
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

        return Clip(-dx, x1 - 0) && Clip(dx, 255 - x1) && Clip(-dy, y1 - 0) && Clip(dy, 2 * CentreY - 1 - y1);
    }

    /// <summary>LL14: the ship is not in view, so draw the explosion cloud if it's exploding, or erase it.</summary>
    private void DrawShipOutOfView(object owner)
    {
        if ((_currentShip.Flags & Ship.FlagExploding) == 0)
        {
            RemoveShipFromScreen(owner);
            return;
        }

        _currentShip.Flags &= ~Ship.FlagDrawn;
        DrawExplosion(owner);
    }

    /// <summary>EE51: remove the ship from the screen if it is on-screen.</summary>
    private void RemoveShipFromScreen(object owner)
    {
        if ((_currentShip.Flags & Ship.FlagDrawn) != 0)
        {
            _currentShip.Flags &= ~Ship.FlagDrawn;
        }

        _screen.RemoveImage(owner);
    }

    /// <summary>SHPPT: draw a distant ship as a dot.</summary>
    private void DrawShipAsDot(object owner)
    {
        var image = new ObjectImage();
        if (ProjectToScreen(out int x, out int y) && (x >> 8) == 0 && (y >> 8) == 0 && y < 2 * CentreY - 2)
        {
            // Shpt: draw a four-pixel dash on two rows
            int x2 = Math.Min(x + 3, 255);
            image.Lines.Add(new ScreenLine(x, y, x2, y, _colour));
            image.Lines.Add(new ScreenLine(x, y + 1, x2, y + 1, _colour));
            _currentShip.Flags |= Ship.FlagDrawn;
        }
        else
        {
            // nono
            _currentShip.Flags &= ~Ship.FlagDrawn;
        }

        _screen.SetImage(owner, image);
    }

    /// <summary>
    /// PROJ: project the ship's centre onto the screen, returning false (C set)
    /// if it's too far off-screen. The results are K3 (x) and K4 (y) as 16-bit
    /// two's complement values.
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

    /// <summary>DOEXP: draw an exploding ship.</summary>
    private void DrawExplosion(object owner)
    {
        var cloud = _currentShip.Explosion;
        if ((_currentShip.Flags & Ship.FlagOnScreenCloud) != 0)
        {
            // Erase the existing cloud (which, as in the original, reseeds the
            // random number generator)
            DrawExplosionCloud(null);
            _screen.RemoveImage(owner);
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

        if ((_currentShip.Flags & Ship.FlagDrawn) == 0)
        {
            return;
        }

        // Copy the screen coordinates of the explosion vertices into the heap
        cloud.Origins.Clear();
        int vertices = (cloud.CountByte - 6) / 4;
        for (int i = 0; i < vertices; i++)
        {
            cloud.Origins.Add((_projectedX[i], _projectedY[i]));
        }

        _currentShip.Flags |= Ship.FlagOnScreenCloud;
        var image = new ObjectImage();
        DrawExplosionCloud(image);
        _screen.SetImage(owner, image);
    }

    /// <summary>
    /// PTCLS: draw (or erase) the explosion cloud. The random number generator
    /// is seeded from the cloud data so the same cloud is produced each time.
    /// </summary>
    private void DrawExplosionCloud(ObjectImage? image)
    {
        var cloud = _currentShip.Explosion;
        int counter = cloud.Counter;
        if ((counter & 0x80) != 0)
        {
            counter ^= 0xFF;
        }

        int particles = (counter >> 4) | 1;
        int savedSeed1 = _randomSeeds[1];

        for (int v = 0; v < cloud.Origins.Count; v++)
        {
            var (originX, originY) = cloud.Origins[v];
            int heapOffset = 6 + 4 * (v + 1);

            // Seed the random number generator from the cloud's seeds
            for (int i = 0; i < 4; i++)
            {
                _randomSeeds[i] = cloud.Seeds[i] ^ heapOffset;
            }

            for (int n = particles; n >= 0; n--)
            {
                int random = NextCloudRandom();
                _colour = GameData.ExplosionColours[random & 3];

                int y = RandomCloudCoordinate(originY, cloud.Size);
                if ((y >> 8) != 0 || (y & 0xFF) >= 2 * CentreY - 1)
                {
                    // EX11
                    NextCloudRandom();
                    continue;
                }

                int x = RandomCloudCoordinate(originX, cloud.Size);
                if ((x >> 8) != 0)
                {
                    continue;
                }

                image?.Rects.AddRange(PixelRects(x, y, random, _colour));
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

    /// <summary>
    /// PIXEL: the rectangles for a dot at (x, y), which is two pixels wide and
    /// one or two pixels high depending on the distance in zz.
    /// </summary>
    private static IEnumerable<ScreenRect> PixelRects(int x, int y, int distance, int colour)
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

    /// <summary>K2 and XX16+0..3: the magnitudes of the ellipse axes (x and y of the first axis, then x and y of the second).</summary>
    private readonly int[] _ellipseAxes = new int[4];

    /// <summary>The signs of the ellipse axes in <see cref="_ellipseAxes"/>.</summary>
    private readonly bool[] _ellipseAxisNegative = new bool[4];

    /// <summary>CNT2: the angle counter for ellipses.</summary>
    private int _ellipseAngle;

    /// <summary>The line segments of the planet being collected by BLINE (the ball line heap).</summary>
    private readonly List<ScreenLine> _planetLines = [];

    /// <summary>True if the next point is the start of a new line (FLAG in the original).</summary>
    private bool _planetLineFirst;

    /// <summary>The previous point in the planet line being drawn.</summary>
    private int _planetLinePreviousX, _planetLinePreviousY;

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
            DrawSun();
            return;
        }

        DrawPlanet(large);
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

    /// <summary>PL9: draw the planet with its meridians and equator, or its crater.</summary>
    private void DrawPlanet(bool large)
    {
        RemovePlanet();
        _planetLines.Clear();
        if (!DrawPlanetCircle())
        {
            return;
        }

        if (!large)
        {
            if (_shipType == ShipType.Planet)
            {
                // PL9 part 2: the meridian and equator
                if (_circleRadius >= 6)
                {
                    CalculateEllipseStartAngle(_currentShip.Roof.Z);
                    CalculateEllipseAxis(_currentShip.Nose.X, 0);
                    CalculateEllipseAxis(_currentShip.Nose.Y, 1);
                    CalculateEllipseAxis(_currentShip.Roof.X, 2);
                    CalculateEllipseAxis(_currentShip.Roof.Y, 3);
                    DrawHalfEllipse();

                    CalculateEllipseStartAngle(_currentShip.Side.Z);
                    CalculateEllipseAxis(_currentShip.Side.X, 2);
                    CalculateEllipseAxis(_currentShip.Side.Y, 3);
                    DrawHalfEllipse();
                }
            }
            else if (_currentShip.Roof.Z >= 0)
            {
                // PL26: the crater
                _circleX = (_circleX + CalculateCraterOffset(_currentShip.Roof.X)) & 0xFFFF;
                _circleY = (_circleY - CalculateCraterOffset(_currentShip.Roof.Y)) & 0xFFFF;
                CalculateEllipseAxis(_currentShip.Nose.X, 0, halve: true);
                CalculateEllipseAxis(_currentShip.Nose.Y, 1, halve: true);
                CalculateEllipseAxis(_currentShip.Side.X, 2, halve: true);
                CalculateEllipseAxis(_currentShip.Side.Y, 3, halve: true);
                _ellipseAngle = 0;
                DrawEllipse(64);
            }
        }

        var image = new ObjectImage();
        image.Lines.AddRange(_planetLines);
        _screen.SetImage(_currentShip.DisplayOwner, image);
    }

    /// <summary>
    /// PLS1: calculate a vector coordinate * 256 / z (clamped to 254) into one
    /// of the K2 slots, with its sign.
    /// </summary>
    private void CalculateEllipseAxis(int vector, int index, bool halve = false)
    {
        int scaled = EliteMaths.DivideScaled(vector, _currentShip.Z);
        int magnitude = Math.Abs(scaled) >= 256 ? 254 : Math.Abs(scaled) & 0xFF;
        if (halve)
        {
            magnitude >>= 1;
        }

        _ellipseAxes[index] = magnitude;
        _ellipseAxisNegative[index] = scaled < 0 || (scaled == 0 && vector < 0);
    }

    /// <summary>
    /// PLS3: calculate 222 * roofv * 256 / z / 256 as a signed 16-bit value,
    /// used to offset the crater from the planet's centre.
    /// </summary>
    private int CalculateCraterOffset(int vector)
    {
        int scaled = EliteMaths.DivideScaled(vector, _currentShip.Z);
        int magnitude = Math.Abs(scaled) >= 256 ? 254 : Math.Abs(scaled) & 0xFF;
        int product = (magnitude * 222) >> 8;
        bool negative = scaled < 0 || (scaled == 0 && vector < 0);
        if (negative && product != 0)
        {
            return -product;
        }

        return product;
    }

    /// <summary>PLS4: CNT2 = arctan(-nosev_z / vector_z) / 4, for the starting angle of the ellipse.</summary>
    private void CalculateEllipseStartAngle(int vectorZ)
    {
        int numerator = -Ship.VectorHi(_currentShip.Nose.Z);
        int denominator = Ship.VectorHi(vectorZ);
        int angle = EliteMaths.Arctan(numerator, denominator);
        if (_currentShip.Nose.Z >= 0)
        {
            angle ^= 0x80;
        }

        _ellipseAngle = (angle >> 2) & 0xFF;
    }

    /// <summary>PLS2: draw a half ellipse.</summary>
    private void DrawHalfEllipse() => DrawEllipse(31);

    /// <summary>
    /// PLS22: draw an ellipse (or part of one) with axes in K2, centred on
    /// K3/K4, starting at angle CNT2 and continuing until the counter reaches
    /// the target.
    /// </summary>
    private void DrawEllipse(int target)
    {
        int count = 0;
        _planetLineFirst = true;
        while (true)
        {
            int angle = _ellipseAngle;
            int sine = GameData.Sine[angle & 31];
            int roofXTerm = EliteMaths.MultiplyFraction(_ellipseAxes[2], sine);
            int roofYTerm = EliteMaths.MultiplyFraction(_ellipseAxes[3], sine);
            bool sinNegative = angle >= 33;

            int cosine = GameData.Sine[(angle + 16) & 31];
            int noseYTerm = EliteMaths.MultiplyFraction(_ellipseAxes[1], cosine);
            int noseXTerm = EliteMaths.MultiplyFraction(_ellipseAxes[0], cosine, out bool carry);

            // The ADC #15 includes the C flag from the last FMLTU
            bool cosNegative = ((angle + 15 + (carry ? 1 : 0)) & 63) >= 33;

            // x = nosev_x * cos + roofv_x * sin
            int offsetX = EliteMaths.Add16(Signed16(noseXTerm, cosNegative ^ _ellipseAxisNegative[0]), Signed16(roofXTerm, sinNegative ^ _ellipseAxisNegative[2]));
            int offsetY = -EliteMaths.Add16(Signed16(noseYTerm, cosNegative ^ _ellipseAxisNegative[1]), Signed16(roofYTerm, sinNegative ^ _ellipseAxisNegative[3]));

            count = AddPlanetLineSegment(_circleX + offsetX, _circleY + offsetY, count);
            if (count > target)
            {
                return;
            }

            _ellipseAngle = (_ellipseAngle + _circleStep) & 63;
        }
    }

    /// <summary>Apply a sign to a magnitude.</summary>
    private static int Signed16(int magnitude, bool negative) => negative ? -magnitude : magnitude;

    /// <summary>
    /// BLINE: add a segment from the previous point to (x, y) to the ball line
    /// list, returning the updated segment counter (CNT + STP).
    /// </summary>
    private int AddPlanetLineSegment(int x, int y, int count)
    {
        x = ToSigned16(x);
        y = ToSigned16(y);
        if (_planetLineFirst)
        {
            _planetLineFirst = false;
        }
        else if (LineOnScreen(_planetLinePreviousX, _planetLinePreviousY, x, y))
        {
            _planetLines.Add(new ScreenLine(_planetLinePreviousX, _planetLinePreviousY, x, y, _colour));
        }

        _planetLinePreviousX = x;
        _planetLinePreviousY = y;
        return count + _circleStep;
    }

    /// <summary>CIRCLE: draw a circle for the planet, returning false (C set) if it's off-screen.</summary>
    private bool DrawPlanetCircle()
    {
        if (!IsCircleOnScreen(out _, out _))
        {
            return false;
        }

        int radius = _circleRadius;
        _circleStep = radius < 8 ? 8 : radius < 60 ? 4 : 2;
        DrawCircle();
        return true;
    }

    /// <summary>CIRCLE2: draw a circle of radius K centred on K3/K4, with step size STP.</summary>
    private void DrawCircle()
    {
        _planetLineFirst = true;
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

            int next = AddPlanetLineSegment(_circleX + x, _circleY + y, count);
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
    private void RemovePlanet()
    {
        _screen.RemoveImage(_currentShip.DisplayOwner);
    }

    // ------------------------------------------------------------------------
    // The sun
    // ------------------------------------------------------------------------

    /// <summary>LSO: the sun line heap (the half-width of the sun on each pixel row).</summary>
    private readonly int[] _sunHalfWidths = new int[2 * CentreY + 8];

    /// <summary>LSX: &amp;FF if the sun is not on-screen.</summary>
    private int _sunHidden = 0xFF;

    /// <summary>SUNX: the x-coordinate of the centre of the sun on-screen.</summary>
    private int _sunCentreX;

    /// <summary>The owner of the sun's image on the screen.</summary>
    private readonly object _sunOwner = new();

    /// <summary>The sun's image on the screen, or null if the sun isn't shown.</summary>
    private ObjectImage? _sunImage;

    /// <summary>The orange colours for each pixel row of the sun.</summary>
    private static readonly int[] Orange = [0b10100101, 0b10100101, 0b01011010, 0b01011010];

    /// <summary>SUN: draw the sun, with its fringe of random widths.</summary>
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

        // PLF8
        _sunCentreX = _circleX;
        var image = new ObjectImage();
        for (int row = 1; row <= yMax; row++)
        {
            if (_sunHalfWidths[row] != 0 && SunEdges(centre, _sunHalfWidths[row], out int x1, out int x2) && x2 > x1)
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
    private void RemoveSun()
    {
        if ((_sunHidden & 0x80) != 0)
        {
            return;
        }

        _screen.RemoveImage(_sunOwner);
        _sunImage = null;
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

    /// <summary>HFS1: draw the tunnel as eight sets of concentric circles.</summary>
    private void DrawTunnelCircles()
    {
        _circleX = CentreX;
        _circleY = CentreY;
        for (int i = 0; i < 8; i++)
        {
            // HFL1
            _circleRadius = (i & 7) + 8;
            while (true)
            {
                _planetLines.Clear();
                DrawCircle();
                foreach (var line in _planetLines)
                {
                    _screen.DrawLine(line.X1, line.Y1, line.X2, line.Y2, _colour);
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
    }
}

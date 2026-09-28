using EliteSharp.Game.Ships;

namespace EliteSharp.Game;

/// <summary>
/// Moving ships and planets through space, and the view transformations.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The signed value of a sign-magnitude byte such as ALPHA or BETA.</summary>
    private static int Signed(int signMagnitude) => EliteMaths.FromSignMagnitude(signMagnitude);

    /// <summary>
    /// MVEIT: move the ship in INWK in space, applying its own speed and
    /// rotation, and our pitch, roll and speed.
    /// </summary>
    private void MoveShip()
    {
        // Part 1: tidy the orientation vectors every 16 iterations
        bool skipTactics = (_currentShip.Flags & 0b10100000) != 0;
        if (!skipTactics)
        {
            if (((_mainLoopCounter ^ _currentSlot) & 15) == 0)
            {
                OrthonormaliseOrientation();
            }

            // Part 2 (MV3): call the tactics routine
            if (_shipType >= 128)
            {
                MovePlanetOrSun();
                return;
            }

            if ((_currentShip.Ai & 0x80) != 0)
            {
                if (_shipType == ShipType.Missile || ((_mainLoopCounter ^ _currentSlot) & 7) == 0)
                {
                    // MV26
                    ApplyTactics();
                }
            }
        }
        else if (_shipType >= 128)
        {
            MovePlanetOrSun();
            return;
        }

        // MV30: remove the ship from the scanner, so we can move it
        DrawOnScanner();

        // Part 3: move the ship along its nose vector by its speed
        int speedFactor = (_currentShip.Speed << 2) & 0xFF;
        MoveAlongNose(ref _currentShip.X, _currentShip.Nose.X, speedFactor);
        MoveAlongNose(ref _currentShip.Y, _currentShip.Nose.Y, speedFactor);
        MoveAlongNose(ref _currentShip.Z, _currentShip.Nose.Z, speedFactor);

        // Part 4: apply acceleration
        int speed = (_currentShip.Speed + _currentShip.Acceleration) & 0xFF;
        if ((speed & 0x80) != 0)
        {
            speed = 0;
        }

        int maxSpeed = _blueprint?.MaxSpeed ?? 0;
        if (speed >= maxSpeed)
        {
            speed = maxSpeed;
        }

        _currentShip.Speed = speed;
        _currentShip.Acceleration = 0;

        // Part 5: rotate the ship's location in space by our pitch and roll
        RotateLocationByPitchAndRoll();

        // Part 6 onwards
        ApplyOurMovement();
    }

    /// <summary>
    /// MVEIT part 3: add nosev_hi * speed * 4 / 256 (using the logarithm tables)
    /// to a coordinate, with the sign of the nosev coordinate (MVT1-2).
    /// </summary>
    private static void MoveAlongNose(ref int coordinate, int nose, int speedFactor)
    {
        int hi = Ship.VectorHiByte(nose);
        int distance = EliteMaths.MultiplyFraction(hi & 0x7F, speedFactor);
        coordinate = AddToCoordinate(coordinate, hi & 0x80, distance);
    }

    /// <summary>MVT1: add (A R) to a coordinate, where A is a sign-magnitude high byte.</summary>
    private static int AddToCoordinate(int coordinate, int high, int low)
    {
        int magnitude = ((high & 0x7F) << 8) | (low & 0xFF);
        return coordinate + ((high & 0x80) != 0 ? -magnitude : magnitude);
    }

    /// <summary>MVT3: add a signed 24-bit value to a coordinate.</summary>
    private static int AddToCoordinate24(int coordinate, int value) => coordinate + value;

    /// <summary>
    /// MVEIT part 5: rotate the ship's location by our pitch and roll, using the
    /// small angle approximation (all coordinates are treated as 16-bit
    /// magnitudes by MVT6, as in the original):
    ///
    ///   K2 = y - alpha * x
    ///   z = z + beta * K2
    ///   y = K2 - beta * z
    ///   x = x + alpha * y
    /// </summary>
    private void RotateLocationByPitchAndRoll()
    {
        int alphaSign = _rollSign & 0x80;
        int betaSign = _pitchSign & 0x80;

        // K2 = y - alpha * x
        int term = (_rollMagnitude * (Math.Abs(_currentShip.X) & 0xFFFF)) >> 8;
        int sign = (alphaSign ^ 0x80) ^ SignOf(_currentShip.X);
        int intermediateY = EliteMaths.AddCoordinate16(_currentShip.Y, sign != 0 ? -term : term);

        // z = z + beta * K2
        term = (_pitchMagnitude * (Math.Abs(intermediateY) & 0xFFFF)) >> 8;
        sign = SignOf(intermediateY) ^ betaSign;
        _currentShip.Z = EliteMaths.AddCoordinate16(_currentShip.Z, sign != 0 ? -term : term);

        // y = K2 - beta * z
        term = (_pitchMagnitude * (Math.Abs(_currentShip.Z) & 0xFFFF)) >> 8;
        sign = (betaSign ^ 0x80) ^ SignOf(_currentShip.Z);
        _currentShip.Y = EliteMaths.AddCoordinate16(intermediateY, sign != 0 ? -term : term);

        // x = x + alpha * y
        term = (_rollMagnitude * (Math.Abs(_currentShip.Y) & 0xFFFF)) >> 8;
        sign = alphaSign ^ SignOf(_currentShip.Y);
        _currentShip.X = EliteMaths.AddCoordinate16(_currentShip.X, sign != 0 ? -term : term);
    }

    /// <summary>The sign bit (bit 7) of a signed value.</summary>
    private static int SignOf(int value) => value < 0 ? 0x80 : 0;

    /// <summary>
    /// MVEIT parts 6 to 9 (MV45): move the ship towards us by our speed, rotate
    /// its orientation by our pitch and roll and its own pitch and roll, and
    /// redraw it on the scanner.
    /// </summary>
    private void ApplyOurMovement()
    {
        // Part 6: move the ship in the z-axis by our speed
        _currentShip.Z = AddToCoordinate(_currentShip.Z, 0x80, _speed);

        // The sun doesn't need rotating
        if ((_shipType & 0b10000001) == 129)
        {
            return;
        }

        // Part 7: rotate the orientation vectors by our pitch and roll
        RotateByPitchAndRoll(ref _currentShip.Nose);
        RotateByPitchAndRoll(ref _currentShip.Roof);
        RotateByPitchAndRoll(ref _currentShip.Side);

        // Part 8: apply the ship's own pitch and roll
        int pitch = _currentShip.PitchCounter;
        _rotationTemp2 = pitch & 0x80;
        int magnitude = pitch & 0x7F;
        if (magnitude != 0)
        {
            // Dampen the pitch counter unless it is 127
            if (magnitude != 0x7F)
            {
                magnitude--;
            }

            _currentShip.PitchCounter = magnitude | _rotationTemp2;
            RotateVectorPair(ref _currentShip.Roof.X, ref _currentShip.Nose.X);
            RotateVectorPair(ref _currentShip.Roof.Y, ref _currentShip.Nose.Y);
            RotateVectorPair(ref _currentShip.Roof.Z, ref _currentShip.Nose.Z);
        }

        // MV8
        int roll = _currentShip.RollCounter;
        _rotationTemp2 = roll & 0x80;
        magnitude = roll & 0x7F;
        if (magnitude != 0)
        {
            if (magnitude != 0x7F)
            {
                magnitude--;
            }

            _currentShip.RollCounter = magnitude | _rotationTemp2;
            RotateVectorPair(ref _currentShip.Roof.X, ref _currentShip.Side.X);
            RotateVectorPair(ref _currentShip.Roof.Y, ref _currentShip.Side.Y);
            RotateVectorPair(ref _currentShip.Roof.Z, ref _currentShip.Side.Z);
        }

        // Part 9 (MV5): redraw on the scanner, unless the ship is exploding or killed
        if ((_currentShip.Flags & 0b10100000) != 0)
        {
            // MVD1
            _currentShip.Flags &= ~Ship.FlagScanner;
            return;
        }

        _currentShip.Flags |= Ship.FlagScanner;
        DrawOnScanner();
    }

    /// <summary>
    /// MVS4: rotate an orientation vector by our pitch and roll:
    ///
    ///   y = y - alpha * x_hi
    ///   x = x + alpha * y_hi
    ///   y = y - beta * z_hi
    ///   z = z + beta * y_hi
    /// </summary>
    private void RotateByPitchAndRoll(ref IntVector3 v)
    {
        int alpha = Signed(_rollAngle);
        v.Y = EliteMaths.MultiplyAdd(alpha, -Ship.VectorHi(v.X), v.Y);
        v.X = EliteMaths.MultiplyAdd(alpha, Ship.VectorHi(v.Y), v.X);

        int beta = Signed(_pitchAngle);
        v.Y = EliteMaths.MultiplyAdd(beta, -Ship.VectorHi(v.Z), v.Y);
        v.Z = EliteMaths.MultiplyAdd(beta, Ship.VectorHi(v.Y), v.Z);
    }

    /// <summary>
    /// MVS5: rotate a pair of orientation vector coordinates by a small angle
    /// (1/16 radians), in the direction given by RAT2:
    ///
    ///   x = x * (1 - 1/512) + y / 16
    ///   y = y * (1 - 1/512) - x / 16
    ///
    /// (with the signs of the y / 16 and x / 16 terms flipped if RAT2 is negative).
    /// </summary>
    private void RotateVectorPair(ref int x, ref int y)
    {
        int newX = EliteMaths.Add16(ScaleDown512(x), SixteenthWithSign(y, _rotationTemp2));
        int newY = EliteMaths.Add16(ScaleDown512(y), SixteenthWithSign(x, _rotationTemp2 ^ 0x80));
        x = newX;
        y = newY;
    }

    /// <summary>(S R) = (x_hi x_lo) - |x_hi| / 2, keeping the sign.</summary>
    private static int ScaleDown512(int value)
    {
        int magnitude = Math.Abs(value) & 0x7FFF;
        int correction = (magnitude >> 8) >> 1;
        magnitude -= correction;
        return value < 0 ? -magnitude : magnitude;
    }

    /// <summary>(A P) = |value| / 16 with the sign of value EOR the sign bit given.</summary>
    private static int SixteenthWithSign(int value, int signFlip)
    {
        int magnitude = (Math.Abs(value) & 0x7FFF) >> 4;
        int sign = SignOf(value) ^ (signFlip & 0x80);
        return sign != 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// MV40: rotate the planet or sun's location in space by our pitch and roll
    /// (using 24-bit coordinates and MULT3), then join MVEIT at MV45.
    /// </summary>
    private void MovePlanetOrSun()
    {
        int alpha = Signed(_rollAngle);
        int beta = Signed(_pitchAngle);

        // K2 = y - alpha * x / 256
        int term = DivideBy256(EliteMaths.Multiply24(_currentShip.X, -alpha));
        int intermediateY = AddToCoordinate24(_currentShip.Y, term);

        // z = z + beta * K2 / 256
        term = DivideBy256(EliteMaths.Multiply24(intermediateY, beta));
        _currentShip.Z = AddToCoordinate24(_currentShip.Z, term);

        // y = K2 - beta * z / 256
        term = DivideBy256(EliteMaths.Multiply24(_currentShip.Z, -beta));
        _currentShip.Y = intermediateY + term;

        // x = x + alpha * y / 256
        term = DivideBy256(EliteMaths.Multiply24(_currentShip.Y, alpha));
        _currentShip.X = AddToCoordinate24(_currentShip.X, term);

        ApplyOurMovement();
    }

    /// <summary>
    /// K(3 2 1) from a MULT3 result K(3 2 1 0): the product divided by 256 in
    /// sign-magnitude form (so the magnitude is truncated), with a 23-bit
    /// magnitude.
    /// </summary>
    private static int DivideBy256(long product)
    {
        int magnitude = (int)((Math.Abs(product) >> 8) & 0x7FFFFF);
        return product < 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// TIDY: orthonormalise the orientation vectors of the ship in INWK, so
    /// that rounding errors don't accumulate as it rotates.
    /// </summary>
    private void OrthonormaliseOrientation()
    {
        // Normalise nosev
        var (noseX, noseY, noseZ) = EliteMaths.Normalise(Ship.VectorHi(_currentShip.Nose.X), Ship.VectorHi(_currentShip.Nose.Y), Ship.VectorHi(_currentShip.Nose.Z), out _);
        _currentShip.Nose = new IntVector3(noseX << 8, noseY << 8, noseZ << 8);

        int roofX = Ship.VectorHi(_currentShip.Roof.X);
        int roofY = Ship.VectorHi(_currentShip.Roof.Y);
        int roofZ = Ship.VectorHi(_currentShip.Roof.Z);

        // Make roofv orthogonal to nosev by solving for one of its coordinates,
        // using the largest nosev coordinate as the divisor
        if ((Math.Abs(noseX) & 0b01100000) != 0)
        {
            roofX = SolveOrthogonalComponent(noseX, noseY, noseZ, roofY, roofZ);
        }
        else if ((Math.Abs(noseY) & 0b01100000) != 0)
        {
            // TI1
            roofY = SolveOrthogonalComponent(noseY, noseX, noseZ, roofX, roofZ);
        }
        else
        {
            // TI2
            roofZ = SolveOrthogonalComponent(noseZ, noseX, noseY, roofX, roofY);
        }

        // TI3: normalise roofv
        (roofX, roofY, roofZ) = EliteMaths.Normalise(roofX, roofY, roofZ, out _);

        // Set sidev to the cross product of nosev and roofv
        int sideX = -EliteMaths.MultiplyAddDivideBy96(noseZ, roofY, EliteMaths.MultiplySigned(noseY, roofZ));
        int sideY = -EliteMaths.MultiplyAddDivideBy96(noseX, roofZ, EliteMaths.MultiplySigned(noseZ, roofX));
        int sideZ = -EliteMaths.MultiplyAddDivideBy96(noseY, roofX, EliteMaths.MultiplySigned(noseX, roofY));

        // Zero the low bytes of nosev, roofv and sidev, except for sidev_z_lo
        // which the original misses
        _currentShip.Roof = new IntVector3(roofX << 8, roofY << 8, roofZ << 8);
        int sideZLo = Math.Abs(_currentShip.Side.Z) & 0xFF;
        _currentShip.Side = new IntVector3(sideX << 8, sideY << 8, sideZ < 0 ? -((-sideZ << 8) | sideZLo) : (sideZ << 8) | sideZLo);
    }

    /// <summary>
    /// TIS3: calculate -(nosev_1 * roofv_1 + nosev_2 * roofv_2) / nosev_3,
    /// the value of roofv_3 that makes roofv orthogonal to nosev.
    /// </summary>
    private static int SolveOrthogonalComponent(int nose3, int nose1, int nose2, int roof1, int roof2)
    {
        int sum = EliteMaths.MultiplyAdd(nose2, roof2, EliteMaths.MultiplySigned(nose1, roof1));
        return EliteMaths.DivideSigned(-sum, nose3);
    }

    /// <summary>
    /// PLUT: transform the ship in INWK so that it is seen from the current
    /// view (front, rear, left or right).
    /// </summary>
    private void TransformForView()
    {
        switch (_view)
        {
            case 0:
                return;
            case 1:
                // Rear view: flip the x and z axes
                _currentShip.X = -_currentShip.X;
                _currentShip.Z = -_currentShip.Z;
                _currentShip.Nose.X = -_currentShip.Nose.X;
                _currentShip.Nose.Z = -_currentShip.Nose.Z;
                _currentShip.Roof.X = -_currentShip.Roof.X;
                _currentShip.Roof.Z = -_currentShip.Roof.Z;
                _currentShip.Side.X = -_currentShip.Side.X;
                _currentShip.Side.Z = -_currentShip.Side.Z;
                return;
        }

        // Left view (RAT2 = 0): x = z, z = -x
        // Right view (RAT2 = &80): x = -z, z = x
        _rotationTemp2 = _view == 3 ? 0x80 : 0;
        _rotationTemp = _rotationTemp2 ^ 0x80;
        (_currentShip.X, _currentShip.Z) = SwapForView(_currentShip.X, _currentShip.Z);
        (_currentShip.Nose.X, _currentShip.Nose.Z) = SwapForView(_currentShip.Nose.X, _currentShip.Nose.Z);
        (_currentShip.Roof.X, _currentShip.Roof.Z) = SwapForView(_currentShip.Roof.X, _currentShip.Roof.Z);
        (_currentShip.Side.X, _currentShip.Side.Z) = SwapForView(_currentShip.Side.X, _currentShip.Side.Z);
    }

    /// <summary>
    /// PLUT for the left and right views: swap the x and z parts of a vector,
    /// negating one of them depending on the view (see <see cref="_rotationTemp"/>).
    /// </summary>
    private (int X, int Z) SwapForView(int x, int z)
    {
        int newX = _rotationTemp2 != 0 ? -z : z;
        int newZ = _rotationTemp != 0 ? -x : x;
        return (newX, newZ);
    }

    /// <summary>LOOK1: switch to a new space view.</summary>
    private void SwitchView(int view)
    {
        SetSpacePalette(0);
        if (_viewType != 0)
        {
            // LQ
            _view = view;
            ClearScreen(0);
            DrawCrosshairs();
            if ((_energyBomb & 0x80) != 0)
            {
                ToggleBombBolt();
            }

            InitialiseStardust();
            return;
        }

        if (view == _view)
        {
            return;
        }

        _view = view;
        ClearScreen(0);
        FlipStardust();
        if ((_energyBomb & 0x80) != 0)
        {
            ToggleBombBolt();
        }

        WipeScanner();
        DrawCrosshairs();
    }

    /// <summary>
    /// SIGHT: draw the laser crosshairs. The original draws two crosses of
    /// sizes 20 and 10 using EOR, so the inner parts cancel out, leaving four
    /// separate arms.
    /// </summary>
    private void DrawCrosshairs()
    {
        int laser = _lasers[_view];
        if (laser == 0)
        {
            return;
        }

        int index = laser switch
        {
            PulseLaserPower => 0,
            PulseLaserPower + 128 => 1,
            MilitaryLaserPower => 2,
            _ => 3,
        };

        int colour = Data.GameData.SightColours[index];
        int centreY = CentreY;
        _screen.DrawLine(108, centreY, 117, centreY, colour);
        _screen.DrawLine(138, centreY, 147, centreY, colour);
        _screen.DrawLine(128, 76, 128, 85, colour);
        _screen.DrawLine(128, 107, 128, 116, colour);
    }

    /// <summary>
    /// RAT: temporary storage used when rotating. It holds the sign to apply
    /// when swapping axes for the left and right views (see PLUT), and the
    /// amount of pitch and roll to apply when a ship turns (see TACTICS).
    /// </summary>
    private int _rotationTemp;

    /// <summary>
    /// RAT2: temporary storage used when rotating. It holds the direction of
    /// rotation for MVS5, the sign to apply when swapping axes for the left
    /// and right views (see PLUT), and the threshold beyond which a ship
    /// starts to pitch and roll when it turns (see TACTICS).
    /// </summary>
    private int _rotationTemp2;
}

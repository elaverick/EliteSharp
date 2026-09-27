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
    private void MVEIT()
    {
        // Part 1: tidy the orientation vectors every 16 iterations
        bool skipTactics = (INWK.Flags & 0b10100000) != 0;
        if (!skipTactics)
        {
            if (((MCNT ^ XSAV) & 15) == 0)
            {
                TIDY();
            }

            // Part 2 (MV3): call the tactics routine
            if (TYPE >= 128)
            {
                MV40();
                return;
            }

            if ((INWK.Ai & 0x80) != 0)
            {
                if (TYPE == ShipType.Missile || ((MCNT ^ XSAV) & 7) == 0)
                {
                    // MV26
                    TACTICS();
                }
            }
        }
        else if (TYPE >= 128)
        {
            MV40();
            return;
        }

        // MV30: remove the ship from the scanner, so we can move it
        SCAN();

        // Part 3: move the ship along its nose vector by its speed
        int q = (INWK.Speed << 2) & 0xFF;
        MoveAlongNose(ref INWK.X, INWK.Nose.X, q);
        MoveAlongNose(ref INWK.Y, INWK.Nose.Y, q);
        MoveAlongNose(ref INWK.Z, INWK.Nose.Z, q);

        // Part 4: apply acceleration
        int speed = (INWK.Speed + INWK.Acceleration) & 0xFF;
        if ((speed & 0x80) != 0)
        {
            speed = 0;
        }

        int maxSpeed = XX0?.MaxSpeed ?? 0;
        if (speed >= maxSpeed)
        {
            speed = maxSpeed;
        }

        INWK.Speed = speed;
        INWK.Acceleration = 0;

        // Part 5: rotate the ship's location in space by our pitch and roll
        RotateLocationByPitchAndRoll();

        // Part 6 onwards
        MV45();
    }

    /// <summary>
    /// MVEIT part 3: add nosev_hi * speed * 4 / 256 (using the logarithm tables)
    /// to a coordinate, with the sign of the nosev coordinate (MVT1-2).
    /// </summary>
    private static void MoveAlongNose(ref int coordinate, int nose, int q)
    {
        int hi = Ship.VectorHiByte(nose);
        int r = EliteMaths.Fmltu(hi & 0x7F, q);
        coordinate = MVT1(coordinate, hi & 0x80, r);
    }

    /// <summary>MVT1: add (A R) to a coordinate, where A is a sign-magnitude high byte.</summary>
    private static int MVT1(int coordinate, int a, int r)
    {
        int magnitude = ((a & 0x7F) << 8) | (r & 0xFF);
        return coordinate + ((a & 0x80) != 0 ? -magnitude : magnitude);
    }

    /// <summary>MVT3: add a signed 24-bit value to a coordinate.</summary>
    private static int MVT3(int coordinate, int value) => coordinate + value;

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
        int alphaSign = ALP2 & 0x80;
        int betaSign = BET2 & 0x80;

        // K2 = y - alpha * x
        int term = (ALP1 * (Math.Abs(INWK.X) & 0xFFFF)) >> 8;
        int sign = (alphaSign ^ 0x80) ^ SignOf(INWK.X);
        int k2 = EliteMaths.AddCoordinate16(INWK.Y, sign != 0 ? -term : term);

        // z = z + beta * K2
        term = (BET1 * (Math.Abs(k2) & 0xFFFF)) >> 8;
        sign = SignOf(k2) ^ betaSign;
        INWK.Z = EliteMaths.AddCoordinate16(INWK.Z, sign != 0 ? -term : term);

        // y = K2 - beta * z
        term = (BET1 * (Math.Abs(INWK.Z) & 0xFFFF)) >> 8;
        sign = (betaSign ^ 0x80) ^ SignOf(INWK.Z);
        INWK.Y = EliteMaths.AddCoordinate16(k2, sign != 0 ? -term : term);

        // x = x + alpha * y
        term = (ALP1 * (Math.Abs(INWK.Y) & 0xFFFF)) >> 8;
        sign = alphaSign ^ SignOf(INWK.Y);
        INWK.X = EliteMaths.AddCoordinate16(INWK.X, sign != 0 ? -term : term);
    }

    private static int SignOf(int value) => value < 0 ? 0x80 : 0;

    /// <summary>
    /// MVEIT parts 6 to 9 (MV45): move the ship towards us by our speed, rotate
    /// its orientation by our pitch and roll and its own pitch and roll, and
    /// redraw it on the scanner.
    /// </summary>
    private void MV45()
    {
        // Part 6: move the ship in the z-axis by our speed
        INWK.Z = MVT1(INWK.Z, 0x80, DELTA);

        // The sun doesn't need rotating
        if ((TYPE & 0b10000001) == 129)
        {
            return;
        }

        // Part 7: rotate the orientation vectors by our pitch and roll
        MVS4(ref INWK.Nose);
        MVS4(ref INWK.Roof);
        MVS4(ref INWK.Side);

        // Part 8: apply the ship's own pitch and roll
        int pitch = INWK.PitchCounter;
        RAT2 = pitch & 0x80;
        int magnitude = pitch & 0x7F;
        if (magnitude != 0)
        {
            // Dampen the pitch counter unless it is 127
            if (magnitude != 0x7F)
            {
                magnitude--;
            }

            INWK.PitchCounter = magnitude | RAT2;
            MVS5(ref INWK.Roof.X, ref INWK.Nose.X);
            MVS5(ref INWK.Roof.Y, ref INWK.Nose.Y);
            MVS5(ref INWK.Roof.Z, ref INWK.Nose.Z);
        }

        // MV8
        int roll = INWK.RollCounter;
        RAT2 = roll & 0x80;
        magnitude = roll & 0x7F;
        if (magnitude != 0)
        {
            if (magnitude != 0x7F)
            {
                magnitude--;
            }

            INWK.RollCounter = magnitude | RAT2;
            MVS5(ref INWK.Roof.X, ref INWK.Side.X);
            MVS5(ref INWK.Roof.Y, ref INWK.Side.Y);
            MVS5(ref INWK.Roof.Z, ref INWK.Side.Z);
        }

        // Part 9 (MV5): redraw on the scanner, unless the ship is exploding or killed
        if ((INWK.Flags & 0b10100000) != 0)
        {
            // MVD1
            INWK.Flags &= ~Ship.FlagScanner;
            return;
        }

        INWK.Flags |= Ship.FlagScanner;
        SCAN();
    }

    /// <summary>
    /// MVS4: rotate an orientation vector by our pitch and roll:
    ///
    ///   y = y - alpha * x_hi
    ///   x = x + alpha * y_hi
    ///   y = y - beta * z_hi
    ///   z = z + beta * y_hi
    /// </summary>
    private void MVS4(ref IntVector3 v)
    {
        int alpha = Signed(ALPHA);
        v.Y = EliteMaths.Mad(alpha, -Ship.VectorHi(v.X), v.Y);
        v.X = EliteMaths.Mad(alpha, Ship.VectorHi(v.Y), v.X);

        int beta = Signed(BETA);
        v.Y = EliteMaths.Mad(beta, -Ship.VectorHi(v.Z), v.Y);
        v.Z = EliteMaths.Mad(beta, Ship.VectorHi(v.Y), v.Z);
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
    private void MVS5(ref int x, ref int y)
    {
        int newX = EliteMaths.Add16(ScaleDown512(x), SixteenthWithSign(y, RAT2));
        int newY = EliteMaths.Add16(ScaleDown512(y), SixteenthWithSign(x, RAT2 ^ 0x80));
        x = newX;
        y = newY;
    }

    /// <summary>(S R) = (x_hi x_lo) - |x_hi| / 2, keeping the sign.</summary>
    private static int ScaleDown512(int value)
    {
        int magnitude = Math.Abs(value) & 0x7FFF;
        int t = (magnitude >> 8) >> 1;
        magnitude -= t;
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
    private void MV40()
    {
        int alpha = Signed(ALPHA);
        int beta = Signed(BETA);

        // K2 = y - alpha * x / 256
        int k = Shr8(EliteMaths.Mult3(INWK.X, -alpha));
        int k2 = MVT3(INWK.Y, k);

        // z = z + beta * K2 / 256
        k = Shr8(EliteMaths.Mult3(k2, beta));
        INWK.Z = MVT3(INWK.Z, k);

        // y = K2 - beta * z / 256
        k = Shr8(EliteMaths.Mult3(INWK.Z, -beta));
        INWK.Y = k2 + k;

        // x = x + alpha * y / 256
        k = Shr8(EliteMaths.Mult3(INWK.Y, alpha));
        INWK.X = MVT3(INWK.X, k);

        MV45();
    }

    /// <summary>
    /// K(3 2 1) from a MULT3 result K(3 2 1 0): the product divided by 256 in
    /// sign-magnitude form (so the magnitude is truncated), with a 23-bit
    /// magnitude.
    /// </summary>
    private static int Shr8(long product)
    {
        int magnitude = (int)((Math.Abs(product) >> 8) & 0x7FFFFF);
        return product < 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// TIDY: orthonormalise the orientation vectors of the ship in INWK, so
    /// that rounding errors don't accumulate as it rotates.
    /// </summary>
    private void TIDY()
    {
        // Normalise nosev
        var (nx, ny, nz) = EliteMaths.Normalise(Ship.VectorHi(INWK.Nose.X), Ship.VectorHi(INWK.Nose.Y), Ship.VectorHi(INWK.Nose.Z), out _);
        INWK.Nose = new IntVector3(nx << 8, ny << 8, nz << 8);

        int rx = Ship.VectorHi(INWK.Roof.X);
        int ry = Ship.VectorHi(INWK.Roof.Y);
        int rz = Ship.VectorHi(INWK.Roof.Z);

        // Make roofv orthogonal to nosev by solving for one of its coordinates,
        // using the largest nosev coordinate as the divisor
        if ((Math.Abs(nx) & 0b01100000) != 0)
        {
            rx = TIS3(nx, ny, nz, ry, rz);
        }
        else if ((Math.Abs(ny) & 0b01100000) != 0)
        {
            // TI1
            ry = TIS3(ny, nx, nz, rx, rz);
        }
        else
        {
            // TI2
            rz = TIS3(nz, nx, ny, rx, ry);
        }

        // TI3: normalise roofv
        (rx, ry, rz) = EliteMaths.Normalise(rx, ry, rz, out _);

        // Set sidev to the cross product of nosev and roofv
        int sx = -EliteMaths.Tis1(nz, ry, EliteMaths.Mult1(ny, rz));
        int sy = -EliteMaths.Tis1(nx, rz, EliteMaths.Mult1(nz, rx));
        int sz = -EliteMaths.Tis1(ny, rx, EliteMaths.Mult1(nx, ry));

        // Zero the low bytes of nosev, roofv and sidev, except for sidev_z_lo
        // which the original misses
        INWK.Roof = new IntVector3(rx << 8, ry << 8, rz << 8);
        int sideZLo = Math.Abs(INWK.Side.Z) & 0xFF;
        INWK.Side = new IntVector3(sx << 8, sy << 8, sz < 0 ? -((-sz << 8) | sideZLo) : (sz << 8) | sideZLo);
    }

    /// <summary>
    /// TIS3: calculate -(nosev_1 * roofv_1 + nosev_2 * roofv_2) / nosev_3,
    /// the value of roofv_3 that makes roofv orthogonal to nosev.
    /// </summary>
    private static int TIS3(int nose3, int nose1, int nose2, int roof1, int roof2)
    {
        int sum = EliteMaths.Mad(nose2, roof2, EliteMaths.Mult1(nose1, roof1));
        return EliteMaths.Dvidt(-sum, nose3);
    }

    /// <summary>
    /// PLUT: transform the ship in INWK so that it is seen from the current
    /// view (front, rear, left or right).
    /// </summary>
    private void PLUT()
    {
        switch (VIEW)
        {
            case 0:
                return;
            case 1:
                // Rear view: flip the x and z axes
                INWK.X = -INWK.X;
                INWK.Z = -INWK.Z;
                INWK.Nose.X = -INWK.Nose.X;
                INWK.Nose.Z = -INWK.Nose.Z;
                INWK.Roof.X = -INWK.Roof.X;
                INWK.Roof.Z = -INWK.Roof.Z;
                INWK.Side.X = -INWK.Side.X;
                INWK.Side.Z = -INWK.Side.Z;
                return;
        }

        // Left view (RAT2 = 0): x = z, z = -x
        // Right view (RAT2 = &80): x = -z, z = x
        RAT2 = VIEW == 3 ? 0x80 : 0;
        RAT = RAT2 ^ 0x80;
        (INWK.X, INWK.Z) = SwapForView(INWK.X, INWK.Z);
        (INWK.Nose.X, INWK.Nose.Z) = SwapForView(INWK.Nose.X, INWK.Nose.Z);
        (INWK.Roof.X, INWK.Roof.Z) = SwapForView(INWK.Roof.X, INWK.Roof.Z);
        (INWK.Side.X, INWK.Side.Z) = SwapForView(INWK.Side.X, INWK.Side.Z);
    }

    private (int X, int Z) SwapForView(int x, int z)
    {
        int newX = RAT2 != 0 ? -z : z;
        int newZ = RAT != 0 ? -x : x;
        return (newX, newZ);
    }

    /// <summary>LOOK1: switch to a new space view.</summary>
    private void LOOK1(int view)
    {
        DOVDU19(0);
        if (QQ11 != 0)
        {
            // LQ
            VIEW = view;
            TT66(0);
            SIGHT();
            if ((BOMB & 0x80) != 0)
            {
                BOMBOFF();
            }

            NWSTARS();
            return;
        }

        if (view == VIEW)
        {
            return;
        }

        VIEW = view;
        TT66(0);
        FLIP();
        if ((BOMB & 0x80) != 0)
        {
            BOMBOFF();
        }

        WPSHPS();
        SIGHT();
    }

    /// <summary>
    /// SIGHT: draw the laser crosshairs. The original draws two crosses of
    /// sizes 20 and 10 using EOR, so the inner parts cancel out, leaving four
    /// separate arms.
    /// </summary>
    private void SIGHT()
    {
        int laser = LASER[VIEW];
        if (laser == 0)
        {
            return;
        }

        int index = laser switch
        {
            POW => 0,
            POW + 128 => 1,
            Armlas => 2,
            _ => 3,
        };

        int colour = Data.GameData.SightColours[index];
        int cy = CentreY;
        _screen.DrawLine(108, cy, 117, cy, colour);
        _screen.DrawLine(138, cy, 147, cy, colour);
        _screen.DrawLine(128, 76, 128, 85, colour);
        _screen.DrawLine(128, 107, 128, 116, colour);
    }

    /// <summary>RAT and RAT2: temporary storage for rotation directions.</summary>
    private int RAT, RAT2;
}

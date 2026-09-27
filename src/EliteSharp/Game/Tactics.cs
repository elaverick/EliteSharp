using EliteSharp.Data;
using EliteSharp.Game.Ships;

namespace EliteSharp.Game;

/// <summary>
/// Ship tactics (the NPC AI), docking, missiles and E.C.M.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>
    /// K3 (which shares memory with XX2): the vector used by the tactics
    /// routines in elements 0-8 (as three signed 24-bit coordinates at 0, 3
    /// and 6), and the face visibility table used by LL9.
    /// </summary>
    private readonly int[] K3 = new int[14];

    /// <summary>XX15: a normalised vector, as signed bytes (96 = 1).</summary>
    private readonly int[] XX15 = new int[6];

    /// <summary>CNT: the dot product used by the tactics routines, as a raw sign-magnitude byte.</summary>
    private int CNT;

    /// <summary>CNT2: the maximum angle beyond which a ship will slow down to turn.</summary>
    private int CNT2;

    /// <summary>The station (or sun) in slot 1 (K%+NI%).</summary>
    private Ship? SlotOne => Slots[1];

    /// <summary>The K3 vector as a signed coordinate (axis 0 = x, 1 = y, 2 = z).</summary>
    private int K3Coord(int axis) => K3[axis * 3];

    private void SetK3Coord(int axis, int value) => K3[axis * 3] = value;

    /// <summary>TACTICS: apply tactics to the ship in INWK.</summary>
    private void TACTICS()
    {
        RAT = 3;
        RAT2 = 4;
        CNT2 = 22;

        int x = TYPE;
        if (x == ShipType.Missile)
        {
            MissileTactics();
            return;
        }

        if (x == ShipType.SpaceStation)
        {
            if ((INWK.Newb & 0b00000100) != 0)
            {
                // TN5: the station is hostile, so spawn cops
                if (DORND() < 240)
                {
                    return;
                }

                if (Many[ShipType.Viper] >= 6)
                {
                    return;
                }

                TN6(ShipType.Viper);
                return;
            }

            if (Many[ShipType.Shuttle + 1] != 0)
            {
                return;
            }

            int a = DORND();
            if (a < 253)
            {
                return;
            }

            // The C flag is set, so this is #SHU or #SHU + 1
            TN6((a & 1) + ShipType.Shuttle);
            return;
        }

        if (x == ShipType.RockHermit)
        {
            // TA13: the rock hermit spawns a ship
            int a = DORND();
            if (a < 200)
            {
                return;
            }

            INWK.Ai = 0;
            INWK.Newb = 0b00100100;

            // The C flag is set
            TN6((a & 3) + ShipType.Sidewinder + 1);
            INWK.Ai = 0;
            return;
        }

        // TA17: recharge the ship's energy
        if (INWK.Energy < XX0!.MaxEnergy)
        {
            INWK.Energy++;
        }

        // TA21
        if (x == ShipType.Thargon && Many[ShipType.Thargoid] == 0)
        {
            // A Thargon without a mothership loses its E.C.M. and slows down
            INWK.Ai &= 0xFE;
            INWK.Speed >>= 1;
            return;
        }

        // TA14
        DORND();
        int newb = INWK.Newb;
        if ((newb & 1) != 0 && _randX >= 50)
        {
            // Traders only apply tactics 20% of the time
            return;
        }

        // TN1
        int flags = newb >> 1;
        if ((flags & 1) != 0 && FIST >= 40)
        {
            // A bounty hunter becomes hostile if we are a bad offender
            INWK.Newb |= 0b00000100;
            flags = INWK.Newb >> 1;
        }

        // TN2: check bit 2 (hostile)
        if ((INWK.Newb & 0b00000100) == 0)
        {
            // Not hostile, so check bit 4 (docking)
            if ((INWK.Newb & 0b00010000) != 0)
            {
                DOCKIT();
                return;
            }

            // GOPL: head towards the planet
            SPS1();
            TA151();
            return;
        }

        // TN3: check bit 3 (pirate)
        if ((INWK.Newb & 0b00001000) != 0 && SSPR != 0)
        {
            // Pirates become passive in the station's safe zone
            INWK.Ai &= 0b10000001;
        }

        TN4();
    }

    /// <summary>TN6: spawn a child ship of the given type with E.C.M. and AI.</summary>
    private void TN6(int type) => SFS1(type, 0b11110001);

    /// <summary>TN4: set K3 to the ship's coordinates and continue with TA19.</summary>
    private void TN4()
    {
        SetK3Coord(0, INWK.X);
        SetK3Coord(1, INWK.Y);
        SetK3Coord(2, INWK.Z);
        TA19();
    }

    /// <summary>TACTICS part 1: missile tactics (TA18).</summary>
    private void MissileTactics()
    {
        if (ECMA != 0)
        {
            // TA352: an E.C.M. has destroyed the missile (only the low bytes
            // of the coordinates are checked to see if it was near us)
            if ((INWK.XLo | INWK.YLo | INWK.ZLo) == 0)
            {
                OOPS(80);
            }

            // TA872
            TA353(ShipType.AlloyPlate);
            return;
        }

        if ((INWK.Ai & 0b01000000) != 0)
        {
            // TA34: the missile is hostile, so check whether it has hit us
            if (MAS4(0) != 0)
            {
                TN4();
                return;
            }

            INWK.Flags |= Ship.FlagKilled;
            EXNO3();
            OOPS(250);
            return;
        }

        // The missile is ours, so find the target
        int targetSlot = (INWK.Ai & 0x7F) >> 1;
        var target = Slots[targetSlot];
        VCSUB(target);

        bool close = ((Ship.SignByte(K3Coord(0)) | Ship.SignByte(K3Coord(1)) | Ship.SignByte(K3Coord(2))) & 0x7F) == 0
                     && (Ship.Hi(K3Coord(0)) | Ship.Hi(K3Coord(1)) | Ship.Hi(K3Coord(2))) == 0;
        if (!close)
        {
            // TA64: the missile isn't close yet, so the target may fire its E.C.M.
            if (DORND() >= 16)
            {
                TA19();
                return;
            }

            // M32
            if (target != null && (target.Ai & 1) != 0)
            {
                ECBLB2();
                return;
            }

            TA19();
            return;
        }

        // The missile has reached its target
        if (INWK.Ai == 0b10000010)
        {
            // TA352: the target is the space station, so destroy the missile
            if ((INWK.XLo | INWK.YLo | INWK.ZLo) == 0)
            {
                OOPS(80);
            }

            TA353(ShipType.AlloyPlate);
            return;
        }

        if (target != null && (target.Flags & Ship.FlagExploding) == 0)
        {
            target.Flags |= Ship.FlagKilled;
        }

        // TA35
        if ((INWK.XLo | INWK.YLo | INWK.ZLo) == 0)
        {
            OOPS(80);
        }

        // TA87: the original passes the target's slot number to EXNO2 as if
        // it were the ship type
        TA353((INWK.Ai & 0x7F) >> 1);
    }

    /// <summary>TA353: process the kill tally and mark the missile as killed.</summary>
    private void TA353(int killType)
    {
        EXNO2(killType);

        // TA873
        INWK.Flags |= Ship.FlagKilled;
    }

    /// <summary>TACTICS part 3 onwards (TA19): work out which direction the ship should be moving.</summary>
    private void TA19()
    {
        TAS2();
        CNT = TAS3(INWK.Nose);

        if (TYPE == ShipType.Missile)
        {
            TA20();
            return;
        }

        if (TYPE == ShipType.Anaconda)
        {
            if (DORND() >= 200)
            {
                int a = DORND();
                TN6(a >= 100 ? ShipType.Worm : ShipType.Sidewinder);
                return;
            }
        }

        // TN7
        if (DORND() >= 250)
        {
            INWK.RollCounter = DORND() | 104;
        }

        // TA7: consider launching an escape pod
        int half = XX0!.MaxEnergy >> 1;
        if (half < INWK.Energy)
        {
            TA3();
            return;
        }

        if ((half >> 2) >= INWK.Energy && DORND() >= 230 && (GameData.DefaultNewbFlags[TYPE - 1] & 0x80) != 0)
        {
            INWK.Newb &= 0b11110000;
            INF!.Newb = INWK.Newb;
            INWK.Ai = 0;
            SESCP();
            return;
        }

        ta3();
    }

    /// <summary>ta3: consider firing a missile (TACTICS part 5).</summary>
    private void ta3()
    {
        int missiles = INWK.Flags & 7;
        if (missiles != 0)
        {
            int a = DORND() & 31;
            if (a < missiles && ECMA == 0)
            {
                INWK.Flags--;
                if (TYPE == ShipType.Thargoid)
                {
                    SFS1(ShipType.Thargon, INWK.Ai);
                }
                else
                {
                    SFRMIS();
                }

                return;
            }
        }

        TA3();
    }

    /// <summary>TA3: consider firing lasers at us (TACTICS part 6).</summary>
    private void TA3()
    {
        if ((MAS4(0) & 0b11100000) == 0 && CNT >= 160)
        {
            int laserByte = XX0!.LaserAndMissiles;
            if ((laserByte & 0b11111000) != 0)
            {
                INWK.Flags |= Ship.FlagFiring;
                if (CNT >= 163)
                {
                    OOPS(laserByte >> 1, (laserByte & 1) != 0);
                    INWK.Acceleration = (INWK.Acceleration - 1) & 0xFF;
                    if (ECMA != 0)
                    {
                        return;
                    }

                    ELASNO();
                }
            }
        }

        TA4();
    }

    /// <summary>TA4: decide whether to head towards or away from us (TACTICS part 7).</summary>
    private void TA4()
    {
        if (INWK.ZHi < 3 && ((INWK.XHi | INWK.YHi) & 0b11111110) == 0)
        {
            TA15();
            return;
        }

        // TA5
        int a = DORND() | 0x80;
        if (a >= INWK.Ai)
        {
            TA15();
            return;
        }

        TA20();
    }

    /// <summary>TA20: point the ship towards us (or the missile towards its target).</summary>
    private void TA20()
    {
        TAS6();
        CNT ^= 0x80;
        TA15();
    }

    /// <summary>TA15: turn the ship towards the XX15 vector, and set its acceleration.</summary>
    private void TA15()
    {
        int a = TAS3(INWK.Roof);
        INWK.PitchCounter = (a ^ 0x80) & 0x80;
        if (((a << 1) & 0xFF) >= RAT2)
        {
            INWK.PitchCounter |= RAT;
        }

        // TA11
        if (((INWK.RollCounter << 1) & 0xFF) < 32)
        {
            a = TAS3(INWK.Side);
            INWK.RollCounter = ((a ^ INWK.PitchCounter) & 0x80) ^ 0x80;
            if (((a << 1) & 0xFF) >= RAT2)
            {
                INWK.RollCounter |= RAT;
            }
        }

        // TA6
        a = CNT;
        if ((a & 0x80) == 0 && a >= CNT2)
        {
            // PH10E
            INWK.Acceleration = 3;
            return;
        }

        // TA9
        if ((a & 0x7F) < 18)
        {
            return;
        }

        INWK.Acceleration = TYPE == ShipType.Missile ? 0xFE : 0xFF;
    }

    /// <summary>TA151: make the ship head in the direction of XX15.</summary>
    private void TA151()
    {
        int a = TAS3(INWK.Nose);
        if (a >= 0x98)
        {
            RAT2 = 0;
        }

        // TA152
        CNT = a;
        TA15();
    }

    /// <summary>DOCKIT: apply docking manoeuvres to the ship in INWK.</summary>
    private void DOCKIT()
    {
        RAT2 = 6;
        RAT = 3;
        CNT2 = 29;

        if (SSPR == 0)
        {
            // GOPL
            SPS1();
            TA151();
            return;
        }

        VCSU1();
        if (((Ship.SignByte(K3Coord(0)) | Ship.SignByte(K3Coord(1)) | Ship.SignByte(K3Coord(2))) & 0x7F) != 0)
        {
            SPS1();
            TA151();
            return;
        }

        // TA2 without the scaling: K = the length of the vector in K3
        int k = TA2Length();
        TAS2();
        int a = TAS4(SlotOne!.Nose);
        if ((a & 0x80) != 0 || a < 35)
        {
            PH1();
            return;
        }

        a = TAS3(INWK.Nose);
        if (a >= 0xA2)
        {
            PH3();
            return;
        }

        if (k >= 157 && TYPE >= 128)
        {
            PH3();
            return;
        }

        // PH2: turn away from the station
        TAS6();
        TA151();
        PH22();
    }

    /// <summary>PH22: slow right down.</summary>
    private void PH22()
    {
        INWK.Acceleration = 0;
        INWK.Speed = 1;
    }

    /// <summary>PH1: fly towards the ideal docking position in front of the station.</summary>
    private void PH1()
    {
        VCSU1();
        DCS1();
        DCS1();
        TAS2();
        TAS6();
        TA151();
    }

    /// <summary>PH3: refine the approach to the station's slot.</summary>
    private void PH3()
    {
        RAT2 = 0;
        INWK.PitchCounter = 0;

        if (TYPE >= 128)
        {
            // This is our docking computer
            int sign = ((TYPE ^ ToByte(XX15[0]) ^ ToByte(XX15[1])) & 0x80) != 0 ? 0x80 : 0;
            INWK.RollCounter = 1 | sign;
            if (((ToByte(XX15[0]) << 1) & 0xFF) >= 12)
            {
                PH22();
                return;
            }

            INWK.PitchCounter = 1 | (ToByte(XX15[1]) & 0x80);
            if (((ToByte(XX15[1]) << 1) & 0xFF) >= 12)
            {
                PH22();
                return;
            }
        }

        // PH32
        INWK.RollCounter = 0;
        XX15[0] = Ship.VectorHi(INWK.Side.X);
        XX15[1] = Ship.VectorHi(INWK.Side.Y);
        XX15[2] = Ship.VectorHi(INWK.Side.Z);
        int a = TAS4(SlotOne!.Roof);
        if (((a << 1) & 0xFF) >= 66)
        {
            // TN11: accelerate and roll to match the station
            INWK.Acceleration = (INWK.Acceleration + 1) & 0xFF;
            INWK.RollCounter = 0x7F;
        }
        else
        {
            PH22();
        }

        // TN13: check whether the ship has docked
        if (K3[10] == 0)
        {
            INWK.Newb |= 0x80;
        }
    }

    /// <summary>The raw sign-magnitude byte for a signed byte value.</summary>
    private static int ToByte(int value) => EliteMaths.ToSignMagnitude(value);

    /// <summary>VCSU1: K3 = INWK - the station's coordinates.</summary>
    private void VCSU1() => VCSUB(SlotOne);

    /// <summary>VCSUB: K3 = INWK - the coordinates of the given ship.</summary>
    private void VCSUB(Ship? other)
    {
        SetK3Coord(0, INWK.X - (other?.X ?? 0));
        SetK3Coord(1, INWK.Y - (other?.Y ?? 0));
        SetK3Coord(2, INWK.Z - (other?.Z ?? 0));
    }

    /// <summary>
    /// DCS1: move the K3 vector twice along the station's nose vector, by
    /// subtracting 2 * nosev_hi each time (TAS7).
    /// </summary>
    private void DCS1()
    {
        var station = SlotOne!;
        for (int i = 0; i < 2; i++)
        {
            SetK3Coord(0, TAS7(K3Coord(0), station.Nose.X));
            SetK3Coord(1, TAS7(K3Coord(1), station.Nose.Y));
            SetK3Coord(2, TAS7(K3Coord(2), station.Nose.Z));
        }
    }

    /// <summary>TAS7: K3 coordinate = coordinate - 2 * nosev_hi (in the low byte).</summary>
    private static int TAS7(int coordinate, int nose)
    {
        int r = (Ship.VectorHiByte(nose) << 1) & 0xFF;
        int sign = Ship.VectorHiByte(nose) & 0x80;
        return coordinate + (sign != 0 ? r : -r);
    }

    /// <summary>TAS2: normalise the vector in K3 into XX15.</summary>
    private void TAS2()
    {
        var (x, y, z) = EliteMaths.NormaliseLarge(K3Coord(0), K3Coord(1), K3Coord(2), out _);
        XX15[0] = x;
        XX15[1] = y;
        XX15[2] = z;
    }

    /// <summary>
    /// TA2 and NORM: calculate XX15 from the K3 vector without the initial
    /// scaling done by TAS2, returning the length of the vector in Q.
    /// </summary>
    private int TA2Length()
    {
        static int Byte(int v) => (((Math.Abs(v) >> 8) & 0xFF) >> 1) | ((Math.Abs(v) >> 16) & 0x7F);
        int x = Byte(K3Coord(0)), y = Byte(K3Coord(1)), z = Byte(K3Coord(2));
        var (nx, ny, nz) = EliteMaths.Normalise(K3Coord(0) < 0 ? -x : x, K3Coord(1) < 0 ? -y : y, K3Coord(2) < 0 ? -z : z, out int length);
        XX15[0] = nx;
        XX15[1] = ny;
        XX15[2] = nz;
        return length;
    }

    /// <summary>
    /// TAS3 (and TAS4): the dot product of a vector's high bytes with XX15,
    /// returned as the raw sign-magnitude high byte of the 16-bit result.
    /// </summary>
    private int TAS3(IntVector3 v)
    {
        int sum = EliteMaths.Mult1(Ship.VectorHi(v.X), XX15[0]);
        sum = EliteMaths.Mad(Ship.VectorHi(v.Y), XX15[1], sum);
        sum = EliteMaths.Mad(Ship.VectorHi(v.Z), XX15[2], sum);
        return ((Math.Abs(sum) >> 8) & 0x7F) | (sum < 0 ? 0x80 : 0);
    }

    /// <summary>TAS4: the dot product of one of the station's vectors with XX15.</summary>
    private int TAS4(IntVector3 v) => TAS3(v);

    /// <summary>TAS6: negate the vector in XX15.</summary>
    private void TAS6()
    {
        XX15[0] = -XX15[0];
        XX15[1] = -XX15[1];
        XX15[2] = -XX15[2];
    }

    /// <summary>SPS1: calculate the normalised vector to the planet in XX15.</summary>
    private void SPS1()
    {
        // SPS3 copies the planet's coordinates into K3 as 24-bit values
        // (x_hi, x_sign split into magnitude and sign, dropping x_lo)
        SetK3Coord(0, SPS3(Planet.X));
        SetK3Coord(1, SPS3(Planet.Y));
        SetK3Coord(2, SPS3(Planet.Z));
        TAS2();
    }

    /// <summary>SPS3: K3 = (sign, x_sign &amp; 127, x_hi), i.e. the coordinate divided by 256.</summary>
    private static int SPS3(int coordinate)
    {
        int magnitude = (Math.Abs(coordinate) >> 8) & 0x7FFF;
        return coordinate < 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// HITCH: returns true if the ship in INWK is in our crosshairs (i.e. in
    /// front of us and within its targetable area).
    /// </summary>
    private bool HITCH()
    {
        if (INWK.ZSign != 0 || TYPE >= 128)
        {
            return false;
        }

        if (((INWK.Flags & Ship.FlagExploding) | INWK.XHi | INWK.YHi) != 0)
        {
            return false;
        }

        int sum = INWK.XLo * INWK.XLo + INWK.YLo * INWK.YLo;
        if (sum > 0xFFFF)
        {
            return false;
        }

        return XX0!.TargetableArea >= sum;
    }

    /// <summary>
    /// FRS1: launch a ship straight ahead of us, below the line of sight (used
    /// for our missiles and the escape pod). Returns true if the ship was added.
    /// </summary>
    private bool FRS1(int type, bool carry = false)
    {
        ZINF();
        INWK.Y = -28;
        INWK.Z = 14;
        INWK.Ai = ((MSTG << 1) | 0x80) & 0xFF;
        return fq1(type, carry);
    }

    /// <summary>fq1: launch a ship of the given type from INWK, pointing away from us at double our speed.</summary>
    private bool fq1(int type, bool carry = false)
    {
        INWK.Nose.Z = 0x60 << 8;
        INWK.Side.X = -(0x60 << 8);
        INWK.Speed = ((DELTA << 1) | (carry ? 1 : 0)) & 0xFF;
        return NWSHP(type);
    }

    /// <summary>FRMIS: fire a missile from our ship.</summary>
    private void FRMIS()
    {
        if (!FRS1(ShipType.Missile))
        {
            // FR1: display "Missile Jammed"
            MESS(201);
            return;
        }

        var target = Slots[MSTG];
        if (target != null)
        {
            ANGRY(target.Type, target);
        }

        ABORT(0);
        NOMSL--;
        NOISE(solaun);

        // Fall through into ANGRY with A = &80, as left by NOISE
        if (target != null)
        {
            ANGRY(0x80, target);
        }
    }

    /// <summary>ANGRY: make a ship hostile (the ship's type is in A).</summary>
    private void ANGRY(int a, Ship ship)
    {
        if (a == ShipType.SpaceStation)
        {
            AN2();
            return;
        }

        if ((ship.Newb & 0b00100000) != 0)
        {
            // This is an innocent bystander, so the station gets angry too
            AN2();
        }

        if (ship.Ai == 0)
        {
            return;
        }

        ship.Ai |= 0x80;
        ship.Acceleration = 2;
        ship.PitchCounter = 4;
        if (TYPE >= ShipType.CobraMkIII)
        {
            ship.Newb |= 0b00000100;
        }
    }

    /// <summary>AN2: make the space station hostile.</summary>
    private void AN2()
    {
        var station = SlotOne;
        if (station != null)
        {
            station.Newb |= 0b00000100;
        }
    }

    /// <summary>SESCP: spawn an escape pod from the ship in INWK.</summary>
    private void SESCP() => SFS1(ShipType.EscapePod, 0b11111110);

    /// <summary>SFRMIS: the ship in INWK fires a missile at us.</summary>
    private void SFRMIS()
    {
        if (!SFS1(ShipType.Missile, 0b11111110))
        {
            return;
        }

        MESS(120);
        NOISE(solaun);
    }

    /// <summary>
    /// SFS1: spawn a child ship from the ship in INF (the parent), with the
    /// given AI flag. Returns true if the ship was added.
    /// </summary>
    private bool SFS1(int type, int ai)
    {
        var saved = INWK;
        var savedXX0 = XX0;

        INWK = _workspace;
        INWK.CopyStateFrom(INF!);

        if (TYPE == ShipType.SpaceStation)
        {
            INWK.Speed = 32;
            INWK.X = SFS2(INWK.X, INWK.Nose.X);
            INWK.Y = SFS2(INWK.Y, INWK.Nose.Y);
            INWK.Z = SFS2(INWK.Z, INWK.Nose.Z);
        }

        // rx
        INWK.Ai = ai;
        INWK.RollCounter &= 0xFE;

        if (type >= ShipType.AlloyPlate && type <= ShipType.Splinter)
        {
            int a = DORND();
            INWK.PitchCounter = (a << 1) & 0xFF;
            INWK.Speed = _randX & 15;
            INWK.RollCounter = 0x7F | ((a & 0x80) != 0 ? 0x80 : 0);
        }

        // NOIL
        bool added = NWSHP(type);

        INWK = saved;
        XX0 = savedXX0;
        return added;
    }

    /// <summary>SFS2: add 2 * a nosev high byte to a coordinate.</summary>
    private static int SFS2(int coordinate, int nose)
    {
        int hi = Ship.VectorHiByte(nose);
        return MVT1(coordinate, hi & 0x80, (hi << 1) & 0xFF);
    }

    /// <summary>EXNO2: process the fact that we have killed a ship, updating the kill tally.</summary>
    private void EXNO2(int type)
    {
        int index = Math.Clamp(type - 1, 0, GameData.KillFraction.Length - 1);
        int low = TALLYL + GameData.KillFraction[index];
        TALLYL = low & 0xFF;
        int carry = low > 0xFF ? 1 : 0;
        int talliedLow = (TALLY & 0xFF) + GameData.KillInteger[index] + carry;
        TALLY = (TALLY & 0xFF00) | (talliedLow & 0xFF);
        if (talliedLow > 0xFF)
        {
            TALLY = (TALLY + 0x100) & 0xFFFF;
            MESS(101);
        }

        // davidscockup: fall through into EXNO3
        EXNO3();
    }

    /// <summary>EXNO3: make the sound of an explosion.</summary>
    private void EXNO3() => NOISE(soexpl);

    /// <summary>EXNO: make the sound of a laser strike on another ship.</summary>
    private void EXNO() => NOISE(sohit);

    /// <summary>ECBLB2: start the E.C.M. and light up the E.C.M. bulb.</summary>
    private void ECBLB2()
    {
        ECMA = 32;
        ECBLB();
    }

    /// <summary>ECMOF: switch off the E.C.M. and its bulb.</summary>
    private void ECMOF()
    {
        ECMA = 0;
        ECMP = 0;
        ECBLB();
    }
}

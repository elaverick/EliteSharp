using EliteSharp.Data;
using EliteSharp.Game.Ships;

namespace EliteSharp.Game;

/// <summary>
/// The main flight loop (M% in the original), plus the routines that are only
/// called from it.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>M%: the main flight loop.</summary>
    private void M()
    {
        // Part 1: seed the random number generator
        RAND[0] = Planet.XLo;

        // Part 2: calculate the alpha and beta angles from the current pitch
        // and roll of our ship
        int x = JSTX;
        x = cntr(x);
        x = cntr(x);
        int a = x ^ 0x80;
        int y = a;
        ALP2 = a & 0x80;
        JSTX = x;
        ALP2Flipped = ALP2 ^ 0x80;
        a = y;
        if ((a & 0x80) != 0)
        {
            a = (-a) & 0xFF;
        }

        a >>= 2;
        bool carry;
        if (a >= 8)
        {
            carry = true;
        }
        else
        {
            carry = (a & 1) != 0;
            a >>= 1;
        }

        ALP1 = a;
        ALPHA = a | ALP2;

        x = JSTY;
        x = cntr(x);
        a = x ^ 0x80;
        y = a;
        a &= 0x80;
        JSTY = x;
        BET2Flipped = a;
        BET2 = a ^ 0x80;
        a = y;
        if ((a & 0x80) != 0)
        {
            a ^= 0xFF;
        }

        a = (a + 4 + (carry ? 1 : 0)) & 0xFF;
        a >>= 4;
        if (a < 3)
        {
            a >>= 1;
        }

        BET1 = a;
        BETA = a | BET2;

        // Part 3: scan for flight keys and process the results
        // BS2 (the Bitstik isn't supported)
        if (KY2 && DELTA < 40)
        {
            DELTA++;
        }

        // MA17
        if (KY1)
        {
            DELTA--;
            if (DELTA == 0)
            {
                DELTA++;
            }
        }

        // MA4
        if (KY15 && NOMSL != 0)
        {
            ABORT(GREEN2);
            BOOP();
            MSAR = 0;
        }

        // MA20
        if ((MSTG & 0x80) != 0 && KY14 && NOMSL != 0)
        {
            MSAR = 0xFF;
            MSBAR(NOMSL, YELLOW2);
        }

        // MA25
        bool skipToMA64 = false;
        if (KY16)
        {
            if ((MSTG & 0x80) != 0)
            {
                skipToMA64 = true;
            }
            else
            {
                FRMIS();
            }
        }

        if (!skipToMA64)
        {
            // MA24
            if (KY12 && (BOMB & 0x80) == 0)
            {
                BOMB = (BOMB << 1) & 0xFF;
                if (BOMB != 0)
                {
                    BOMBON();
                }
            }

            // MA76
            if (KY20)
            {
                Auto = 0;
            }

            // MA78
            if (KY13 && ESCP != 0 && MJ == 0)
            {
                ESCAPE();
            }

            // noescp
            if (KY18)
            {
                WARP();
            }

            if (KY17 && ECM != 0 && ECMA == 0)
            {
                ECMP = (ECMP - 1) & 0xFF;
                ECBLB2();
            }
        }

        // MA64
        if (KY19 && DKCMP != 0)
        {
            Auto = 0xFF;
        }

        // MA68
        LAS = 0;
        DELT4 = DELTA << 6;

        if (LASCT == 0 && KY7 && GNTMP < 242)
        {
            int laser = LASER[VIEW];
            if (laser != 0)
            {
                LAS = laser & 0x7F;
                LAS2 = LAS;
                LASNO();
                LASLI();
                int count = (laser & 0x80) != 0 ? 0 : laser;
                LASCT = count & 0b11111010;
            }
        }

        if (_trace != null)
        {
            Trace($"MCNT={MCNT} QQ11={QQ11} NOSTM={NOSTM} MJ={MJ} delta={DELTA} slots=" + string.Join(" ", Slots.Where(s => s != null).Select(s => $"{s!.Type}:({s.X},{s.Y},{s.Z})")) + " dust=" + string.Join(",", Enumerable.Range(1, NOSTM).Select(i => $"{SX[i]:X2}/{SY[i]:X2}/{SZ[i]:X2}")));
        }

        // Part 4: start looping through all the ships in the local bubble
        XSAV = 0;
        while (true)
        {
            // MAL1
            var ship = Slots[XSAV];
            if (ship == null)
            {
                break;
            }

            // MAL2: copy the ship's data block into INWK
            TYPE = ship.Type;
            INF = ship;
            INWK = ship.CloneBlock();
            XX0 = ship.Blueprint;

            // Part 5: if an energy bomb has been set off, potentially kill
            // this ship
            if (TYPE < 128 && (BOMB & 0x80) != 0
                && TYPE != ShipType.SpaceStation && TYPE != ShipType.Thargoid && TYPE < ShipType.Constrictor
                && (INWK.Flags & Ship.FlagExploding) == 0)
            {
                INWK.Flags |= Ship.FlagKilled;
                EXNO2(TYPE);
            }

            // Part 6: move the ship in space and copy the updated INWK data
            // block back to K% (MAL3)
            MVEIT();
            ship.CopyStateFrom(INWK);

            // From here on, INWK is a working copy of the ship data, and only
            // bytes #31 and #35 get copied back to K%

            if (!ProcessShipInteractions(ship))
            {
                // KS1: remove the ship from the bubble
                KILLSHP(XSAV);
                continue;
            }

            // MA27
            ship.Flags = INWK.Flags;
            XSAV++;
        }

        // Part 13 (MA18): show the energy bomb effect and charge shields and energy banks
        if ((BOMB & 0x80) != 0)
        {
            BOMBEFF2();
            BOMB = (BOMB << 1) & 0xFF;
            if ((BOMB & 0x80) == 0)
            {
                BOMBOFF();
            }
        }

        // MA77
        if ((MCNT & 7) == 0)
        {
            if ((ENERGY & 0x80) != 0)
            {
                ASH = SHD(ASH);
                FSH = SHD(FSH);
            }

            // b
            int sum = ENGY + ENERGY + 1;
            if (sum <= 0xFF)
            {
                ENERGY = sum;
            }

            // Part 14: spawn a space station if we are close enough to the planet
            if (MJ == 0)
            {
                if ((MCNT & 31) == 0)
                {
                    SpawnStationIfClose();
                }
                else
                {
                    AltitudeChecks(MCNT & 31);
                }
            }
        }
        else if (MJ == 0)
        {
            // MA22
            AltitudeChecks(MCNT & 31);
        }

        // Part 16 (MA23): process laser pulsing, E.C.M. energy drain and the stardust
        if (LAS2 != 0 && LASCT < 8)
        {
            LASLI2();
            LAS2 = 0;
        }

        // MA16
        bool ecmOff = false;
        if (ECMP != 0)
        {
            if (DENGY())
            {
                ecmOff = true;
            }
        }

        if (!ecmOff && ECMA != 0)
        {
            // MA69
            NOISE(soecm);
            ECMA = (ECMA - 1) & 0xFF;
            if (ECMA == 0)
            {
                ecmOff = true;
            }
        }

        if (ecmOff)
        {
            // MA70
            ECMOF();
        }

        // MA66
        if (QQ11 == 0)
        {
            STARS();
        }
    }

    /// <summary>
    /// Main flight loop parts 7 to 12 for the ship in INWK: docking, scooping,
    /// collisions, missile lock, lasers and drawing. Returns false if the ship
    /// should be removed from the local bubble.
    /// </summary>
    private bool ProcessShipInteractions(Ship ship)
    {
        // Part 7: check whether we are docking, scooping or colliding with it
        bool skipToMA26 = false;
        if (MAS4(INWK.Flags & 0b10100000) != 0)
        {
            skipToMA26 = true;
        }
        else if (((INWK.XLo | INWK.YLo | INWK.ZLo) & 0x80) != 0 || TYPE >= 128)
        {
            skipToMA26 = true;
        }
        else if (TYPE == ShipType.SpaceStation)
        {
            // ISDK: check whether we are docking
            if (CheckDocking())
            {
                // GOIN
                DOENTRY();
            }

            // MA62: docking failed
            if (DELTA >= 5)
            {
                DEATH();
            }

            // MA67 (the C flag is clear from the CMP #5)
            DELTA = 1;
            OOPS(5, false);
            EXNO3();
        }
        else if (((INWK.XLo | INWK.YLo | INWK.ZLo) & 0b11000000) != 0 || TYPE == ShipType.Missile)
        {
            skipToMA26 = true;
        }
        else if ((BST & INWK.YSign & 0x80) == 0)
        {
            // MA58: a potentially fatal collision
            Collide();
        }
        else
        {
            // Part 8: potentially scoop this item
            int item;
            if (TYPE == ShipType.CargoCanister)
            {
                // oily
                item = DORND() & 7;
            }
            else
            {
                int byte0 = XX0!.Byte0;
                item = byte0 >> 4;
                if (item == 0)
                {
                    Collide();
                    return ProcessMissileLockAndDrawing(ship);
                }

                // ADC #1 with the C flag set to bit 3 of byte #0 from the LSRs
                item = item + 1 + ((byte0 >> 3) & 1);
            }

            // slvy2
            QQ29 = item;
            if (tnpr1(item))
            {
                // MA59: no room in the hold
                EXNO3();
                INWK.Flags |= Ship.FlagKilled;
            }
            else
            {
                QQ20[QQ29] = (QQ20[QQ29] + 1) & 0xFF;
                MESS(QQ29 + 208);
                INWK.Newb |= 0x80;
            }

            skipToMA26 = true;
        }

        _ = skipToMA26;
        return ProcessMissileLockAndDrawing(ship);
    }

    /// <summary>MA58: we have collided with the ship in INWK in a potentially fatal way.</summary>
    private void Collide()
    {
        INWK.Flags |= Ship.FlagKilled;
        int damage = 0x80 | (INWK.Energy >> 1);

        // MA63 (the C flag is bit 0 of the energy, from the ROR)
        OOPS(damage, (INWK.Energy & 1) != 0);
        EXNO3();
    }

    /// <summary>ISDK: returns true if the conditions for docking with the station in INWK are met.</summary>
    private bool CheckDocking()
    {
        SPS1();
        if (_trace != null) Trace($"ISDK newb={Slots[1]!.Newb:X2} nosez={Ship.VectorHiByte(INWK.Nose.Z)} xx15z={XX15[2]} roofx={Ship.VectorHiByte(INWK.Roof.X) & 0x7F} delta={DELTA}");
        // 1. The station must not be hostile
        if ((Slots[1]!.Newb & 0b00000100) != 0)
        {
            return false;
        }

        // 2. The angle of approach must be less than 26 degrees
        if (Ship.VectorHiByte(INWK.Nose.Z) < 214)
        {
            return false;
        }

        // 4. We must be within the 22 degree safe cone of approach (this
        // compares the raw sign-magnitude byte, as the original omits the
        // sign check)
        SPS1();
        if (ToByte(XX15[2]) < 89)
        {
            return false;
        }

        // 5. The slot must be horizontal to within 36.6 degrees
        if ((Ship.VectorHiByte(INWK.Roof.X) & 0x7F) < 80)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Main flight loop parts 11 and 12 (MA26 onwards): process missile lock,
    /// firing our laser, draw the ship, and work out whether to remove it.
    /// Returns false if the ship should be removed.
    /// </summary>
    private bool ProcessMissileLockAndDrawing(Ship ship)
    {
        // MA26
        if ((INWK.Newb & 0x80) != 0)
        {
            SCAN();
        }

        if (QQ11 == 0)
        {
            PLUT();
            if (HITCH())
            {
                if (MSAR != 0)
                {
                    BEEP();
                    ABORT2(XSAV, RED2);
                }

                // MA47
                if (LAS != 0)
                {
                    EXNO();
                    bool damage = true;
                    if (TYPE == ShipType.SpaceStation)
                    {
                        damage = false;
                    }
                    else if (TYPE >= ShipType.Constrictor)
                    {
                        if (LAS != (Armlas & 127))
                        {
                            damage = false;
                        }
                        else
                        {
                            LAS >>= 2;
                        }
                    }

                    if (damage)
                    {
                        // BURN
                        int energy = INWK.Energy - LAS;
                        if (energy >= 0)
                        {
                            INWK.Energy = energy;
                        }
                        else
                        {
                            INWK.Flags |= Ship.FlagKilled;
                            if (TYPE == ShipType.Asteroid && LAS == Mlas)
                            {
                                int count = DORND() & 3;
                                SPIN2(ShipType.Splinter, count);
                            }

                            // nosp
                            SPIN(ShipType.AlloyPlate);
                            SPIN(ShipType.CargoCanister);
                            EXNO2(TYPE);
                        }
                    }

                    ANGRY(TYPE, INF!);
                }
            }

            // MA8
            LL9();
        }

        // MA15: copy the energy back to the ship data block
        ship.Energy = INWK.Energy;

        if ((INWK.Newb & 0x80) != 0)
        {
            return false;
        }

        if ((INWK.Flags & Ship.FlagKilled) != 0 && (INWK.Flags & Ship.FlagExploding) != 0)
        {
            // The ship has finished exploding, so we get the bounty
            FIST |= INWK.Newb & 0b01000000;
            if ((DLY | MJ) == 0)
            {
                // Only the low byte of the bounty is checked for zero
                int bounty = XX0?.Bounty ?? 0;
                if ((bounty & 0xFF) != 0)
                {
                    MCASH(bounty);
                    MESS(0);
                }
            }

            return false;
        }

        // MAC1: remove the ship if it is too far away
        if (TYPE < 128 && !FAROF())
        {
            return false;
        }

        return true;
    }

    /// <summary>Main flight loop part 14: spawn a space station if we are close enough to the planet.</summary>
    private void SpawnStationIfClose()
    {
        if (SSPR != 0)
        {
            return;
        }

        if (MAS2(Planet) != 0)
        {
            return;
        }

        // Copy the planet's position and orientation into INWK
        var saved = INWK;
        INWK = _workspace;
        INWK.ResetOrientationAndPosition();
        INWK.X = Planet.X;
        INWK.Y = Planet.Y;
        INWK.Z = Planet.Z;
        INWK.Nose = Planet.Nose;
        INWK.Roof = Planet.Roof;
        INWK.Side = Planet.Side;
        INWK.Speed = Planet.Speed;
        INWK.Acceleration = Planet.Acceleration;

        INWK.X = MAS1(INWK.X, INWK.Nose.X, out int xs);
        if (xs == 0)
        {
            INWK.Y = MAS1(INWK.Y, INWK.Nose.Y, out int ys);
            if (ys == 0)
            {
                INWK.Z = MAS1(INWK.Z, INWK.Nose.Z, out int zs);
                if (zs == 0 && FAROF2(192))
                {
                    WPLS();
                    NWSPS();
                }
            }
        }

        INWK = saved;
    }

    /// <summary>
    /// MAS1: add 2 * a vector coordinate to a position coordinate, returning the
    /// new coordinate and |sign byte| in signMagnitude.
    /// </summary>
    private static int MAS1(int coordinate, int vector, out int signMagnitude)
    {
        int result = MVT3(coordinate, vector * 2);
        signMagnitude = Ship.SignByte(result) & 0x7F;
        return result;
    }

    /// <summary>MAS2: the OR of the sign bytes (without the sign bits) of a ship's coordinates.</summary>
    private static int MAS2(Ship ship, int a = 0) => (a | ship.XSign | ship.YSign | ship.ZSign) & 0x7F;

    /// <summary>MAS3: A = x_hi^2 + y_hi^2 + z_hi^2 (high bytes only), returning 255 and C set on overflow.</summary>
    private static int MAS3(Ship ship, out bool overflow)
    {
        overflow = false;
        int sum = (ship.XHi * ship.XHi) >> 8;
        sum += (ship.YHi * ship.YHi) >> 8;
        if (sum > 0xFF)
        {
            overflow = true;
            return 0xFF;
        }

        sum += (ship.ZHi * ship.ZHi) >> 8;
        if (sum > 0xFF)
        {
            overflow = true;
            return 0xFF;
        }

        return sum;
    }

    /// <summary>Main flight loop part 15: altitude checks with the planet and sun, and fuel scooping.</summary>
    private void AltitudeChecks(int a)
    {
        // MA93
        if (a == 10)
        {
            if (ENERGY <= 50)
            {
                MESS(100);
            }

            ALTIT = 0xFF;
            if (MAS2(Planet) != 0)
            {
                return;
            }

            int squared = MAS3(Planet, out bool overflow);
            if (overflow)
            {
                return;
            }

            squared -= 37;
            if (squared < 0)
            {
                DEATH();
            }

            // LL5 with R = A and Q left over from earlier (assumed to be 0)
            ALTIT = EliteMaths.Ll5(squared << 8);
            if (ALTIT == 0)
            {
                DEATH();
            }

            return;
        }

        // MA29
        if (a == 15)
        {
            if (Auto != 0)
            {
                MESS(123);
            }

            return;
        }

        // MA33
        if (a != 20)
        {
            return;
        }

        CABTMP = 30;
        if (SSPR != 0)
        {
            return;
        }

        var sun = Slots[1];
        if (sun == null || MAS2(sun) != 0)
        {
            return;
        }

        int value = MAS3(sun, out bool sunOverflow);
        int temperature = (value ^ 0xFF) + 30 + (sunOverflow ? 1 : 0);
        CABTMP = temperature & 0xFF;
        if (temperature > 0xFF)
        {
            DEATH();
        }

        if (CABTMP < 224 || BST == 0)
        {
            return;
        }

        // Fuel scooping (the C flag is clear here)
        int fuel = ((DELT4 >> 8) >> 1) + QQ14;
        if (fuel >= 70)
        {
            fuel = 70;
        }

        QQ14 = fuel;

        // MA34
        MESS(160);
    }

    /// <summary>SHD: charge a shield by one unless it is already at 255.</summary>
    private static int SHD(int x) => x == 0xFF ? 0xFF : x + 1;

    /// <summary>DENGY: drain one point of energy for the E.C.M., returning true if the energy is now zero.</summary>
    private bool DENGY()
    {
        ENERGY = (ENERGY - 1) & 0xFF;
        bool zero = ENERGY == 0;
        if (zero)
        {
            ENERGY++;
        }

        return zero;
    }

    /// <summary>SPIN: randomly spawn a cargo canister or alloy plate from the ship in INWK.</summary>
    private void SPIN(int type)
    {
        int a = DORND();
        if ((a & 0x80) == 0)
        {
            return;
        }

        // The original copies the cargo type from Y into A before the AND,
        // so the number of canisters is the cargo type AND bits 0-3 of the
        // blueprint's byte #0, rather than a random number
        int count = type & (XX0?.Byte0 ?? 0) & 15;
        SPIN2(type, count);
    }

    /// <summary>SPIN2: spawn a number of ships of the given type from the ship in INWK.</summary>
    private void SPIN2(int type, int count)
    {
        while (count != 0)
        {
            SFS1(type, 0);
            count--;
        }
    }

    /// <summary>
    /// OOPS: take damage, reducing our shields and possibly killing us. The
    /// SBC in the original uses the C flag from the caller, so that is passed
    /// in too.
    /// </summary>
    private void OOPS(int damage, bool carry = true)
    {
        // Work out which shield is hit from the z_sign of the ship in K%
        bool behind = INF != null && INF.Z < 0;
        int borrow = carry ? 0 : 1;
        int a;
        if (!behind)
        {
            a = FSH - damage - borrow;
            if (a >= 0)
            {
                FSH = a;
                return;
            }

            FSH = 0;
        }
        else
        {
            a = ASH - damage - borrow;
            if (a >= 0)
            {
                ASH = a;
                return;
            }

            ASH = 0;
        }

        // OO3: the damage has got through the shield, so reduce our energy
        // (the C flag is clear)
        int energy = (a & 0xFF) + ENERGY;
        ENERGY = energy & 0xFF;
        if (ENERGY == 0 || energy <= 0xFF)
        {
            DEATH();
        }

        EXNO3();
        OUCH();
    }

    /// <summary>WARP: perform an in-system jump (the "J" key).</summary>
    private void WARP()
    {
        int x = Junk;
        int a = SlotType(2 + x) | SSPR | MJ;
        if (a != 0)
        {
            // WA1
            BOOP();
            return;
        }

        if (Planet.Z >= 0)
        {
            if (MAS2(Planet) < 2)
            {
                BOOP();
                return;
            }
        }

        // WA3
        var sun = Slots[1]!;
        if (sun.Z >= 0)
        {
            if (MAS2(sun) < 2)
            {
                BOOP();
                return;
            }
        }

        // WA2: subtract 1 from the sign bytes of z for the planet and sun (i.e.
        // move them 65536 closer)
        Planet.Z = WarpZ(Planet.Z);
        sun.Z = WarpZ(sun.Z);

        QQ11 = 1;
        MCNT = 1;
        EV = 0;
        LOOK1(VIEW);
    }

    /// <summary>
    /// WARP moves the planet and sun by calling ADD with (A P) = (z_sign &amp;81)
    /// and (S R) = -&amp;0181, and storing the high byte of the result as the new
    /// z_sign, which effectively reduces z_sign by 1 (for objects in front).
    /// </summary>
    private static int WarpZ(int coordinate)
    {
        int sign = Ship.SignByte(coordinate);
        int ap = ((sign & 0x7F) << 8) | 0x81;
        if ((sign & 0x80) != 0)
        {
            ap = -ap;
        }

        int result = EliteMaths.Add16(ap, -0x181);
        int newSign = ((Math.Abs(result) >> 8) & 0x7F) | (result < 0 ? 0x80 : 0);
        int magnitude = (Math.Abs(coordinate) & 0xFFFF) | ((newSign & 0x7F) << 16);
        return (newSign & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>LASLI: draw the laser lines for when we fire our laser.</summary>
    private void LASLI()
    {
        LASY = ((DORND() & 7) + CentreY - 4 + (_carry ? 1 : 0)) & 0xFF;
        LASX = ((DORND() & 7) + CentreX - 4 + (_carry ? 1 : 0)) & 0xFF;

        // The C flag is clear after the addition above
        GNTMP = (GNTMP + 8) & 0xFF;
        DENGY();
        LASLI2();
    }

    /// <summary>LASLI2: draw (or erase) the laser lines.</summary>
    private void LASLI2()
    {
        if (QQ11 != 0)
        {
            return;
        }

        LASY -= 2;
        DrawLaserLines(32, 224);
        LASY += 2;
        DrawLaserLines(48, 208);
    }

    /// <summary>las: draw a pair of laser lines from the bottom corners of the space view to (LASX, LASY).</summary>
    private void DrawLaserLines(int left, int right)
    {
        _screen.DrawLine(LASX, LASY, left, 2 * CentreY - 1, RED);
        _screen.DrawLine(LASX, LASY, right, 2 * CentreY - 1, RED);
    }

    // ------------------------------------------------------------------------
    // The energy bomb
    // ------------------------------------------------------------------------

    private readonly int[] BOMBTBX = new int[10];
    private readonly int[] BOMBTBY = new int[10];

    /// <summary>BOMBOFF: draw (or erase) the zig-zag lightning bolt of the energy bomb.</summary>
    private void BOMBOFF()
    {
        if (QQ11 != 0)
        {
            return;
        }

        for (int y = 1; y < 10; y++)
        {
            _screen.DrawLine(BOMBTBX[y - 1], BOMBTBY[y - 1], BOMBTBX[y], BOMBTBY[y], CYAN);
        }
    }

    /// <summary>BOMBEFF2: erase the energy bomb's lightning bolt and draw a new one, four times.</summary>
    private void BOMBEFF2()
    {
        for (int i = 0; i < 4; i++)
        {
            BOMBEFF();
        }
    }

    /// <summary>BOMBEFF: make the energy bomb sound, erase the bolt and draw a new one.</summary>
    private void BOMBEFF()
    {
        NOISE(sobomb);
        BOMBOFF();
        BOMBON();
    }

    /// <summary>BOMBON: randomise and draw the energy bomb's lightning bolt.</summary>
    private void BOMBON()
    {
        for (int y = 0; y < 10; y++)
        {
            int a = DORND();
            BOMBTBY[y] = ((a & 127) + 3 + (_carry ? 1 : 0)) & 0xFF;
            BOMBTBX[y] = ((_randX & 31) + GameData.BombBaseX[y]) & 0xFF;
        }

        BOMBTBX[9] = 0;
        BOMBTBX[0] = 255;
        BOMBOFF();
    }
}

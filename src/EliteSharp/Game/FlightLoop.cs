using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Game;

/// <summary>
/// The main flight loop (M% in the original), plus the routines that are only
/// called from it.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>M%: the main flight loop.</summary>
    private void MainFlightLoop()
    {
        // Part 1: seed the random number generator
        _randomSeeds[0] = Planet.XLo;

        // Part 2: calculate the alpha and beta angles from the current pitch
        // and roll of our ship
        int rate = _rollRate;
        rate = DampRate(rate);
        rate = DampRate(rate);
        int angle = rate ^ 0x80;
        int signedRate = angle;
        _rollSign = angle & 0x80;
        _rollRate = rate;
        _rollSignFlipped = _rollSign ^ 0x80;
        angle = signedRate;
        if ((angle & 0x80) != 0)
        {
            angle = (-angle) & 0xFF;
        }

        angle >>= 2;
        bool carry;
        if (angle >= 8)
        {
            carry = true;
        }
        else
        {
            carry = (angle & 1) != 0;
            angle >>= 1;
        }

        _rollMagnitude = angle;
        _rollAngle = angle | _rollSign;

        rate = _pitchRate;
        rate = DampRate(rate);
        angle = rate ^ 0x80;
        signedRate = angle;
        angle &= 0x80;
        _pitchRate = rate;
        _pitchSignFlipped = angle;
        _pitchSign = angle ^ 0x80;
        angle = signedRate;
        if ((angle & 0x80) != 0)
        {
            angle ^= 0xFF;
        }

        angle = (angle + 4 + (carry ? 1 : 0)) & 0xFF;
        angle >>= 4;
        if (angle < 3)
        {
            angle >>= 1;
        }

        _pitchMagnitude = angle;
        _pitchAngle = angle | _pitchSign;

        // Part 3: scan for flight keys and process the results
        // BS2 (the Bitstik isn't supported)
        if (_keySpeedUp && _speed < 40)
        {
            _speed++;
        }

        // MA17
        if (_keySlowDown)
        {
            _speed--;
            if (_speed == 0)
            {
                _speed++;
            }
        }

        // MA4
        if (_keyUnarmMissile && _missiles != 0)
        {
            DisarmMissile(DashboardGreen);
            Boop();
            _missileArmed = 0;
        }

        // MA20
        if ((_missileTarget & 0x80) != 0 && _keyTargetMissile && _missiles != 0)
        {
            _missileArmed = 0xFF;
            SetMissileIndicator(_missiles, DashboardYellow);
        }

        // MA25
        bool skipRemainingKeys = false;
        if (_keyFireMissile)
        {
            if ((_missileTarget & 0x80) != 0)
            {
                skipRemainingKeys = true;
            }
            else
            {
                FireMissile();
            }
        }

        if (!skipRemainingKeys)
        {
            // MA24
            if (_keyEnergyBomb && (_energyBomb & 0x80) == 0)
            {
                _energyBomb = (_energyBomb << 1) & 0xFF;
                if (_energyBomb != 0)
                {
                    RandomiseBombBolt();
                }
            }

            // MA76
            if (_keyDockingComputerOff)
            {
                _autoDocking = 0;
            }

            // MA78
            if (_keyEscapePod && _escapePod != 0 && _inWitchspace == 0)
            {
                LaunchEscapePod();
            }

            // noescp
            if (_keyJump)
            {
                InSystemJump();
            }

            if (_keyEcm && _ecm != 0 && _ecmCounter == 0)
            {
                _ourEcmActive = (_ourEcmActive - 1) & 0xFF;
                StartEcm();
            }
        }

        // MA64
        if (_keyDockingComputerOn && _dockingComputer != 0)
        {
            _autoDocking = 0xFF;
        }

        // MA68
        _firingLaserPower = 0;
        _speedTimes64 = _speed << 6;

        if (_laserPulseCounter == 0 && _keyFireLaser && _laserTemperature < 242)
        {
            int laser = _lasers[_view];
            if (laser != 0)
            {
                _firingLaserPower = laser & 0x7F;
                _laserBeamPower = _firingLaserPower;
                LaserSound();
                DrawLaserBeams();
                int count = (laser & 0x80) != 0 ? 0 : laser;
                _laserPulseCounter = count & 0b11111010;
            }
        }

        if (_trace != null)
        {
            Trace($"MCNT={_mainLoopCounter} QQ11={_viewType} NOSTM={_stardustCount} MJ={_inWitchspace} delta={_speed} slots=" + string.Join(" ", Slots.Where(s => s != null).Select(s => $"{s!.Type}:({s.X},{s.Y},{s.Z})")) + " dust=" + string.Join(",", Enumerable.Range(1, _stardustCount).Select(i => $"{_dustX[i]:X2}/{_dustY[i]:X2}/{_dustZ[i]:X2}")));
        }

        // Part 4: start looping through all the ships in the local bubble
        _currentSlot = 0;
        while (true)
        {
            // MAL1
            var ship = Slots[_currentSlot];
            if (ship == null)
            {
                break;
            }

            // MAL2: copy the ship's data block into INWK
            _shipType = ship.Type;
            _slotShip = ship;
            _currentShip = ship.CloneBlock();
            _blueprint = ship.Blueprint;

            // Part 5: if an energy bomb has been set off, potentially kill
            // this ship
            if (_shipType < 128 && (_energyBomb & 0x80) != 0
                && _shipType != ShipType.SpaceStation && _shipType != ShipType.Thargoid && _shipType < ShipType.Constrictor
                && (_currentShip.Flags & Ship.FlagExploding) == 0)
            {
                _currentShip.Flags |= Ship.FlagKilled;
                RecordKill(_shipType);
            }

            // Part 6: move the ship in space and copy the updated INWK data
            // block back to K% (MAL3)
            MoveShip();
            ship.CopyStateFrom(_currentShip);

            // From here on, INWK is a working copy of the ship data, and only
            // bytes #31 and #35 get copied back to K%

            if (!ProcessShipInteractions(ship))
            {
                // KS1: remove the ship from the bubble
                RemoveShip(_currentSlot);
                continue;
            }

            // MA27
            ship.Flags = _currentShip.Flags;
            _currentSlot++;
        }

        // Part 13 (MA18): show the energy bomb effect and charge shields and energy banks
        if ((_energyBomb & 0x80) != 0)
        {
            AnimateEnergyBomb();
            _energyBomb = (_energyBomb << 1) & 0xFF;
            if ((_energyBomb & 0x80) == 0)
            {
                ToggleBombBolt();
            }
        }

        // MA77
        if ((_mainLoopCounter & 7) == 0)
        {
            if ((_energy & 0x80) != 0)
            {
                _aftShield = ChargeShield(_aftShield);
                _forwardShield = ChargeShield(_forwardShield);
            }

            // b
            int sum = _energyUnit + _energy + 1;
            if (sum <= 0xFF)
            {
                _energy = sum;
            }

            // Part 14: spawn a space station if we are close enough to the planet
            if (_inWitchspace == 0)
            {
                if ((_mainLoopCounter & 31) == 0)
                {
                    SpawnStationIfClose();
                }
                else
                {
                    AltitudeChecks(_mainLoopCounter & 31);
                }
            }
        }
        else if (_inWitchspace == 0)
        {
            // MA22
            AltitudeChecks(_mainLoopCounter & 31);
        }

        // Part 16 (MA23): process laser pulsing, E.C.M. energy drain and the stardust
        if (_laserBeamPower != 0 && _laserPulseCounter < 8)
        {
            ToggleLaserBeams();
            _laserBeamPower = 0;
        }

        // MA16
        bool ecmOff = false;
        if (_ourEcmActive != 0)
        {
            if (DrainEcmEnergy())
            {
                ecmOff = true;
            }
        }

        if (!ecmOff && _ecmCounter != 0)
        {
            // MA69
            MakeSound(SoundEcm);
            _ecmCounter = (_ecmCounter - 1) & 0xFF;
            if (_ecmCounter == 0)
            {
                ecmOff = true;
            }
        }

        if (ecmOff)
        {
            // MA70
            StopEcm();
        }

        // MA66
        if (_viewType == 0)
        {
            MoveStardust();
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
        bool skipToDrawing = false;
        if (OrCoordinateHighBytes(_currentShip.Flags & 0b10100000) != 0)
        {
            skipToDrawing = true;
        }
        else if (((_currentShip.XLo | _currentShip.YLo | _currentShip.ZLo) & 0x80) != 0 || _shipType >= 128)
        {
            skipToDrawing = true;
        }
        else if (_shipType == ShipType.SpaceStation)
        {
            // ISDK: check whether we are docking
            if (CheckDocking())
            {
                // GOIN
                DockAtStation();
            }

            // MA62: docking failed
            if (_speed >= 5)
            {
                ShowDeathScreen();
            }

            // MA67 (the C flag is clear from the CMP #5)
            _speed = 1;
            TakeDamage(5, false);
            ExplosionSound();
        }
        else if (((_currentShip.XLo | _currentShip.YLo | _currentShip.ZLo) & 0b11000000) != 0 || _shipType == ShipType.Missile)
        {
            skipToDrawing = true;
        }
        else if ((_fuelScoops & _currentShip.YSign & 0x80) == 0)
        {
            // MA58: a potentially fatal collision
            Collide();
        }
        else
        {
            // Part 8: potentially scoop this item
            int item;
            if (_shipType == ShipType.CargoCanister)
            {
                // oily
                item = NextRandom() & 7;
            }
            else
            {
                int canisterAndScoopByte = _blueprint!.CanisterAndScoopByte;
                item = canisterAndScoopByte >> 4;
                if (item == 0)
                {
                    Collide();
                    return ProcessMissileLockAndDrawing(ship);
                }

                // ADC #1 with the C flag set to bit 3 of byte #0 from the LSRs
                item = item + 1 + ((canisterAndScoopByte >> 3) & 1);
            }

            // slvy2
            _itemNumber = item;
            if (HasRoomForOne(item))
            {
                // MA59: no room in the hold
                ExplosionSound();
                _currentShip.Flags |= Ship.FlagKilled;
            }
            else
            {
                _cargo[_itemNumber] = (_cargo[_itemNumber] + 1) & 0xFF;
                ShowMessage(_itemNumber + 208);
                _currentShip.Behaviour |= 0x80;
            }

            skipToDrawing = true;
        }

        _ = skipToDrawing;
        return ProcessMissileLockAndDrawing(ship);
    }

    /// <summary>MA58: we have collided with the ship in INWK in a potentially fatal way.</summary>
    private void Collide()
    {
        _currentShip.Flags |= Ship.FlagKilled;
        int damage = 0x80 | (_currentShip.Energy >> 1);

        // MA63 (the C flag is bit 0 of the energy, from the ROR)
        TakeDamage(damage, (_currentShip.Energy & 1) != 0);
        ExplosionSound();
    }

    /// <summary>ISDK: returns true if the conditions for docking with the station in INWK are met.</summary>
    private bool CheckDocking()
    {
        CalculatePlanetVector();
        if (_trace != null) Trace($"ISDK newb={Slots[1]!.Behaviour:X2} nosez={Ship.VectorHiByte(_currentShip.Nose.Z)} xx15z={_unitVector[2]} roofx={Ship.VectorHiByte(_currentShip.Roof.X) & 0x7F} delta={_speed}");
        // 1. The station must not be hostile
        if ((Slots[1]!.Behaviour & 0b00000100) != 0)
        {
            return false;
        }

        // 2. The angle of approach must be less than 26 degrees
        if (Ship.VectorHiByte(_currentShip.Nose.Z) < 214)
        {
            return false;
        }

        // 4. We must be within the 22 degree safe cone of approach (this
        // compares the raw sign-magnitude byte, as the original omits the
        // sign check)
        CalculatePlanetVector();
        if (ToByte(_unitVector[2]) < 89)
        {
            return false;
        }

        // 5. The slot must be horizontal to within 36.6 degrees
        if ((Ship.VectorHiByte(_currentShip.Roof.X) & 0x7F) < 80)
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
        if ((_currentShip.Behaviour & 0x80) != 0)
        {
            DrawOnScanner();
        }

        if (_viewType == 0)
        {
            TransformForView();
            if (IsInCrosshairs())
            {
                if (_missileArmed != 0)
                {
                    Beep();
                    SetMissileTarget(_currentSlot, DashboardRed);
                }

                // MA47
                if (_firingLaserPower != 0)
                {
                    LaserStrikeSound();
                    bool damage = true;
                    if (_shipType == ShipType.SpaceStation)
                    {
                        damage = false;
                    }
                    else if (_shipType >= ShipType.Constrictor)
                    {
                        if (_firingLaserPower != (MilitaryLaserPower & 127))
                        {
                            damage = false;
                        }
                        else
                        {
                            _firingLaserPower >>= 2;
                        }
                    }

                    if (damage)
                    {
                        // BURN
                        int energy = _currentShip.Energy - _firingLaserPower;
                        if (energy >= 0)
                        {
                            _currentShip.Energy = energy;
                        }
                        else
                        {
                            _currentShip.Flags |= Ship.FlagKilled;
                            if (_shipType == ShipType.Asteroid && _firingLaserPower == MiningLaserPower)
                            {
                                int count = NextRandom() & 3;
                                SpawnWreckagePieces(ShipType.Splinter, count);
                            }

                            // nosp
                            SpawnWreckage(ShipType.AlloyPlate);
                            SpawnWreckage(ShipType.CargoCanister);
                            RecordKill(_shipType);
                        }
                    }

                    MakeHostile(_shipType, _slotShip!);
                }
            }

            // MA8
            DrawShip();
        }

        // MA15: copy the energy back to the ship data block
        ship.Energy = _currentShip.Energy;

        if ((_currentShip.Behaviour & 0x80) != 0)
        {
            return false;
        }

        if ((_currentShip.Flags & Ship.FlagKilled) != 0 && (_currentShip.Flags & Ship.FlagExploding) != 0)
        {
            // The ship has finished exploding, so we get the bounty
            _legalStatus |= _currentShip.Behaviour & 0b01000000;
            if ((_messageDelay | _inWitchspace) == 0)
            {
                // Only the low byte of the bounty is checked for zero
                int bounty = _blueprint?.Bounty ?? 0;
                if ((bounty & 0xFF) != 0)
                {
                    AddCash(bounty);
                    ShowMessage(0);
                }
            }

            return false;
        }

        // MAC1: remove the ship if it is too far away
        if (_shipType < 128 && !IsNearby())
        {
            return false;
        }

        return true;
    }

    /// <summary>Main flight loop part 14: spawn a space station if we are close enough to the planet.</summary>
    private void SpawnStationIfClose()
    {
        if (InSafeZone != 0)
        {
            return;
        }

        if (CombinedSignBytes(Planet) != 0)
        {
            return;
        }

        // Copy the planet's position and orientation into INWK
        var saved = _currentShip;
        _currentShip = _workspace;
        _currentShip.ResetOrientationAndPosition();
        _currentShip.X = Planet.X;
        _currentShip.Y = Planet.Y;
        _currentShip.Z = Planet.Z;
        _currentShip.Nose = Planet.Nose;
        _currentShip.Roof = Planet.Roof;
        _currentShip.Side = Planet.Side;
        _currentShip.Speed = Planet.Speed;
        _currentShip.Acceleration = Planet.Acceleration;

        _currentShip.X = AddVectorToCoordinate(_currentShip.X, _currentShip.Nose.X, out int xSign);
        if (xSign == 0)
        {
            _currentShip.Y = AddVectorToCoordinate(_currentShip.Y, _currentShip.Nose.Y, out int ySign);
            if (ySign == 0)
            {
                _currentShip.Z = AddVectorToCoordinate(_currentShip.Z, _currentShip.Nose.Z, out int zSign);
                if (zSign == 0 && IsWithinDistance(192))
                {
                    RemoveSun();
                    AddStation();
                }
            }
        }

        _currentShip = saved;
    }

    /// <summary>
    /// MAS1: add 2 * a vector coordinate to a position coordinate, returning the
    /// new coordinate and |sign byte| in signMagnitude.
    /// </summary>
    private static int AddVectorToCoordinate(int coordinate, int vector, out int signMagnitude)
    {
        int result = AddToCoordinate24(coordinate, vector * 2);
        signMagnitude = Ship.SignByte(result) & 0x7F;
        return result;
    }

    /// <summary>MAS2: the OR of the sign bytes (without the sign bits) of a ship's coordinates.</summary>
    private static int CombinedSignBytes(Ship ship, int initial = 0) => (initial | ship.XSign | ship.YSign | ship.ZSign) & 0x7F;

    /// <summary>MAS3: A = x_hi^2 + y_hi^2 + z_hi^2 (high bytes only), returning 255 and C set on overflow.</summary>
    private static int DistanceSquared(Ship ship, out bool overflow)
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
    private void AltitudeChecks(int loopCounter)
    {
        // MA93
        if (loopCounter == 10)
        {
            if (_energy <= 50)
            {
                ShowMessage(100);
            }

            _altitude = 0xFF;
            if (CombinedSignBytes(Planet) != 0)
            {
                return;
            }

            int squared = DistanceSquared(Planet, out bool overflow);
            if (overflow)
            {
                return;
            }

            squared -= 37;
            if (squared < 0)
            {
                ShowDeathScreen();
            }

            // LL5 with R = A and Q left over from earlier (assumed to be 0)
            _altitude = EliteMaths.SquareRoot(squared << 8);
            if (_altitude == 0)
            {
                ShowDeathScreen();
            }

            return;
        }

        // MA29
        if (loopCounter == 15)
        {
            if (_autoDocking != 0)
            {
                ShowMessage(123);
            }

            return;
        }

        // MA33
        if (loopCounter != 20)
        {
            return;
        }

        _cabinTemperature = 30;
        if (InSafeZone != 0)
        {
            return;
        }

        var sun = Slots[1];
        if (sun == null || CombinedSignBytes(sun) != 0)
        {
            return;
        }

        int value = DistanceSquared(sun, out bool sunOverflow);
        int temperature = (value ^ 0xFF) + 30 + (sunOverflow ? 1 : 0);
        _cabinTemperature = temperature & 0xFF;
        if (temperature > 0xFF)
        {
            ShowDeathScreen();
        }

        if (_cabinTemperature < 224 || _fuelScoops == 0)
        {
            return;
        }

        // Fuel scooping (the C flag is clear here)
        int fuel = ((_speedTimes64 >> 8) >> 1) + _fuel;
        if (fuel >= 70)
        {
            fuel = 70;
        }

        _fuel = fuel;

        // MA34
        ShowMessage(160);
    }

    /// <summary>SHD: charge a shield by one unless it is already at 255.</summary>
    private static int ChargeShield(int x) => x == 0xFF ? 0xFF : x + 1;

    /// <summary>DENGY: drain one point of energy for the E.C.M., returning true if the energy is now zero.</summary>
    private bool DrainEcmEnergy()
    {
        _energy = (_energy - 1) & 0xFF;
        bool zero = _energy == 0;
        if (zero)
        {
            _energy++;
        }

        return zero;
    }

    /// <summary>SPIN: randomly spawn a cargo canister or alloy plate from the ship in INWK.</summary>
    private void SpawnWreckage(int type)
    {
        int random = NextRandom();
        if ((random & 0x80) == 0)
        {
            return;
        }

        // The original copies the cargo type from Y into A before the AND,
        // so the number of canisters is the cargo type AND bits 0-3 of the
        // blueprint's byte #0, rather than a random number
        int count = type & (_blueprint?.CanisterAndScoopByte ?? 0) & 15;
        SpawnWreckagePieces(type, count);
    }

    /// <summary>SPIN2: spawn a number of ships of the given type from the ship in INWK.</summary>
    private void SpawnWreckagePieces(int type, int count)
    {
        while (count != 0)
        {
            SpawnChildShip(type, 0);
            count--;
        }
    }

    /// <summary>
    /// OOPS: take damage, reducing our shields and possibly killing us. The
    /// SBC in the original uses the C flag from the caller, so that is passed
    /// in too.
    /// </summary>
    private void TakeDamage(int damage, bool carry = true)
    {
        // Work out which shield is hit from the z_sign of the ship in K%
        bool behind = _slotShip != null && _slotShip.Z < 0;
        int borrow = carry ? 0 : 1;
        int shield;
        if (!behind)
        {
            shield = _forwardShield - damage - borrow;
            if (shield >= 0)
            {
                _forwardShield = shield;
                return;
            }

            _forwardShield = 0;
        }
        else
        {
            shield = _aftShield - damage - borrow;
            if (shield >= 0)
            {
                _aftShield = shield;
                return;
            }

            _aftShield = 0;
        }

        // OO3: the damage has got through the shield, so reduce our energy
        // (the C flag is clear)
        int energy = (shield & 0xFF) + _energy;
        _energy = energy & 0xFF;
        if (_energy == 0 || energy <= 0xFF)
        {
            ShowDeathScreen();
        }

        ExplosionSound();
        LoseCargoOrEquipment();
    }

    /// <summary>WARP: perform an in-system jump (the "J" key).</summary>
    private void InSystemJump()
    {
        int junk = _junkCount;
        int blocked = SlotType(2 + junk) | InSafeZone | _inWitchspace;
        if (blocked != 0)
        {
            // WA1
            Boop();
            return;
        }

        if (Planet.Z >= 0)
        {
            if (CombinedSignBytes(Planet) < 2)
            {
                Boop();
                return;
            }
        }

        // WA3
        var sun = Slots[1]!;
        if (sun.Z >= 0)
        {
            if (CombinedSignBytes(sun) < 2)
            {
                Boop();
                return;
            }
        }

        // WA2: subtract 1 from the sign bytes of z for the planet and sun (i.e.
        // move them 65536 closer)
        Planet.Z = WarpZ(Planet.Z);
        sun.Z = WarpZ(sun.Z);

        _viewType = 1;
        _mainLoopCounter = 1;
        _extraVesselsDelay = 0;
        SwitchView(_view);
    }

    /// <summary>
    /// WARP moves the planet and sun by calling ADD with (A P) = (z_sign &amp;81)
    /// and (S R) = -&amp;0181, and storing the high byte of the result as the new
    /// z_sign, which effectively reduces z_sign by 1 (for objects in front).
    /// </summary>
    private static int WarpZ(int coordinate)
    {
        int sign = Ship.SignByte(coordinate);
        int value = ((sign & 0x7F) << 8) | 0x81;
        if ((sign & 0x80) != 0)
        {
            value = -value;
        }

        int result = EliteMaths.Add16(value, -0x181);
        int newSign = ((Math.Abs(result) >> 8) & 0x7F) | (result < 0 ? 0x80 : 0);
        int magnitude = (Math.Abs(coordinate) & 0xFFFF) | ((newSign & 0x7F) << 16);
        return (newSign & 0x80) != 0 ? -magnitude : magnitude;
    }

    /// <summary>LASLI: draw the laser lines for when we fire our laser.</summary>
    private void DrawLaserBeams()
    {
        _laserEndY = ((NextRandom() & 7) + CentreY - 4 + (_carry ? 1 : 0)) & 0xFF;
        _laserEndX = ((NextRandom() & 7) + CentreX - 4 + (_carry ? 1 : 0)) & 0xFF;

        // The C flag is clear after the addition above
        _laserTemperature = (_laserTemperature + 8) & 0xFF;
        DrainEcmEnergy();
        ToggleLaserBeams();
    }

    /// <summary>
    /// LASLI2: draw (or erase) the laser lines. The original draws them with
    /// EOR logic, so drawing them a second time erases them.
    /// </summary>
    private void ToggleLaserBeams()
    {
        if (_viewType != 0)
        {
            return;
        }

        if (_screen.HasImage(_laserOwner))
        {
            RemoveFromScreen(_laserOwner);
            return;
        }

        BeginWorldDrawing(_view, inFlight: true);
        var image = new ObjectImage();
        var beams = new List<LineSegment>();
        _laserEndY -= 2;
        DrawLaserLines(image, beams, 32, 224);
        _laserEndY += 2;
        DrawLaserLines(image, beams, 48, 208);
        _screen.SetImage(_laserOwner, image, ImageLayer.World);
        _world.SetLines(_laserOwner, beams);
    }

    /// <summary>
    /// las: draw a pair of laser lines from the bottom corners of the space
    /// view to (LASX, LASY). In the 3D world, the beams start just in front of
    /// us at those corners, and reach into the distance towards (LASX, LASY).
    /// </summary>
    private void DrawLaserLines(ObjectImage image, List<LineSegment> beams, int left, int right)
    {
        var target = ScreenPointToWorld(_laserEndX, _laserEndY, LaserRange);
        foreach (int corner in (ReadOnlySpan<int>)[left, right])
        {
            image.Lines.Add(new ScreenLine(_laserEndX, _laserEndY, corner, 2 * CentreY - 1, Red));
            beams.Add(new LineSegment(ScreenPointToWorld(corner, 2 * CentreY - 1, ScreenEdgeDistance), target, Red));
        }
    }

    // ------------------------------------------------------------------------
    // The energy bomb
    // ------------------------------------------------------------------------

    /// <summary>BOMBTBX: the x-coordinates of the points in the energy bomb's lightning bolt.</summary>
    private readonly int[] _bombBoltX = new int[10];
    /// <summary>BOMBTBY: the y-coordinates of the points in the energy bomb's lightning bolt.</summary>
    private readonly int[] _bombBoltY = new int[10];

    /// <summary>BOMBOFF: draw (or erase) the zig-zag lightning bolt of the energy bomb.</summary>
    private void ToggleBombBolt()
    {
        if (_viewType != 0)
        {
            return;
        }

        for (int y = 1; y < 10; y++)
        {
            _screen.DrawLine(_bombBoltX[y - 1], _bombBoltY[y - 1], _bombBoltX[y], _bombBoltY[y], Cyan);
        }
    }

    /// <summary>BOMBEFF2: erase the energy bomb's lightning bolt and draw a new one, four times.</summary>
    private void AnimateEnergyBomb()
    {
        for (int i = 0; i < 4; i++)
        {
            EnergyBombEffect();
        }
    }

    /// <summary>BOMBEFF: make the energy bomb sound, erase the bolt and draw a new one.</summary>
    private void EnergyBombEffect()
    {
        MakeSound(SoundBomb);
        ToggleBombBolt();
        RandomiseBombBolt();
    }

    /// <summary>BOMBON: randomise and draw the energy bomb's lightning bolt.</summary>
    private void RandomiseBombBolt()
    {
        for (int point = 0; point < 10; point++)
        {
            int random = NextRandom();
            _bombBoltY[point] = ((random & 127) + 3 + (_carry ? 1 : 0)) & 0xFF;
            _bombBoltX[point] = ((_randomX & 31) + GameData.BombBaseX[point]) & 0xFF;
        }

        _bombBoltX[9] = 0;
        _bombBoltX[0] = 255;
        ToggleBombBolt();
    }
}

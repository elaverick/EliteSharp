using System.Numerics;
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
        // Part 1: seed the random number generator from x_lo of the planet
        _randomSeeds[0] = LowByte(Planet.Position.X);

        // Part 2: calculate the alpha and beta angles from the current roll
        // and pitch rates of our ship. The rates (JSTX and JSTY) are 128 when
        // centred, and the angles are in steps of 1/256 radian, with the
        // original's response to the controls (including its use of the C flag
        // from the roll calculation in the pitch calculation)
        int rate = _rollRate;
        rate = DampRate(rate);
        rate = DampRate(rate);
        _rollRate = rate;
        int angle = rate ^ 0x80;
        bool rollNegative = (angle & 0x80) != 0;
        if (rollNegative)
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

        _roll = rollNegative ? -angle : angle;

        rate = _pitchRate;
        rate = DampRate(rate);
        _pitchRate = rate;
        angle = rate ^ 0x80;
        bool pitchNegative = (angle & 0x80) == 0;
        if (!pitchNegative)
        {
            angle ^= 0xFF;
        }

        angle = (angle + 4 + (carry ? 1 : 0)) & 0xFF;
        angle >>= 4;
        if (angle < 3)
        {
            angle >>= 1;
        }

        _pitch = pitchNegative ? -angle : angle;

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
            _missileArmed = false;
        }

        // MA20
        if ((_missileTarget & 0x80) != 0 && _keyTargetMissile && _missiles != 0)
        {
            _missileArmed = true;
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
            Trace($"MCNT={_mainLoopCounter} QQ11={_viewType} NOSTM={_stardustCount} MJ={_inWitchspace} delta={_speed} slots=" + string.Join(" ", Slots.Where(s => s != null).Select(s => $"{s!.Type}:{s.Position}")) + " dust=" + string.Join(",", Enumerable.Range(1, _stardustCount).Select(i => $"{_dustX[i]:F1}/{_dustY[i]:F1}/{_dustZ[i]:F1}")));
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
                HideBombBolt();
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
        // Part 7: check whether we are docking, scooping or colliding with it,
        // which is only possible if it is within 256 of us in each axis (and
        // isn't exploding or killed): we can dock with the station within 128,
        // and scoop or collide with anything else within 64
        bool skipToDrawing = false;
        var position = _currentShip.Position;
        if ((_currentShip.Flags & 0b10100000) != 0 || !IsWithin(position, 256))
        {
            skipToDrawing = true;
        }
        else if (!IsWithin(position, 128) || _shipType >= 128)
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
        else if (!IsWithin(position, 64) || _shipType == ShipType.Missile)
        {
            skipToDrawing = true;
        }
        else if ((_fuelScoops & 0x80) == 0 || position.Y >= 0)
        {
            // MA58: a potentially fatal collision (we can only scoop things
            // below us)
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
                ShowMessage(CommodityKeys[_itemNumber]);
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
        if (_trace != null) Trace($"ISDK newb={Slots[1]!.Behaviour:X2} nose={_currentShip.Nose} xx15={_unitVector} roof={_currentShip.Roof} delta={_speed}");
        // 1. The station must not be hostile
        if ((Slots[1]!.Behaviour & 0b00000100) != 0)
        {
            return false;
        }

        // 2. The angle of approach must be less than 26 degrees (the
        // station's nose must point at us, with nosev_z of -86/96 or less)
        if (_currentShip.Nose.Z > -86 / 96f)
        {
            return false;
        }

        // 4. We must be within the 22 degree safe cone of approach, with
        // XX15's z-coordinate at least 89/96 (the original compares the
        // sign-magnitude byte, so any negative z-coordinate also passes)
        CalculatePlanetVector();
        if (_unitVector.Z >= 0 && _unitVector.Z < 89 / 96f)
        {
            return false;
        }

        // 5. The slot must be horizontal to within 36.6 degrees
        if (MathF.Abs(_currentShip.Roof.X) < 80 / 96f)
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
                if (_missileArmed)
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
                    ShowMessage("messages.bounty");
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

    /// <summary>
    /// The distance from the planet's centre to the station's, along the
    /// planet's nose: twice the planet's radius (MAS1 adds 2 * nosev, and the
    /// original's nosev is as long as the planet's radius).
    /// </summary>
    private const float StationOrbitRadius = 2 * PlanetRadius;

    /// <summary>
    /// The distance from us (in each axis) within which the station is
    /// spawned: x_hi, y_hi and z_hi must all be 192 or less.
    /// </summary>
    private const float StationSpawnDistance = 193 * 256;

    /// <summary>
    /// The altimeter, the cabin temperature and fuel scooping measure the
    /// distance to the planet and sun in units of 256 (the high bytes of the
    /// coordinates in the original).
    /// </summary>
    private const float AltimeterUnit = 256;

    /// <summary>
    /// The square of the planet's radius for the altimeter, in altimeter units
    /// (the original's MAS3 result, x_hi^2 + y_hi^2 + z_hi^2 in 256ths, must be
    /// at least 37), so we crash at about 97.3 (24,915 in space).
    /// </summary>
    private const float PlanetSurfaceSquared = 37 * 256;

    /// <summary>
    /// The square of the altimeter's range, in altimeter units: MAS3 returns
    /// 255 (in 256ths) if the distance is any greater.
    /// </summary>
    private const float AltimeterRangeSquared = 255 * 256;

    /// <summary>Main flight loop part 14: spawn a space station if we are close enough to the planet.</summary>
    private void SpawnStationIfClose()
    {
        if (InSafeZone != 0)
        {
            return;
        }

        // The planet must be within 65,536 of us in each axis (MAS2)
        if (!IsWithin(Planet.Position, 65536))
        {
            return;
        }

        // Copy the planet's position and orientation into INWK, and move it
        // along the planet's nose to where the station is
        var saved = _currentShip;
        _currentShip = _workspace;
        _currentShip.ResetOrientationAndPosition();
        _currentShip.Position = Planet.Position + Planet.Nose * StationOrbitRadius;
        _currentShip.Nose = Planet.Nose;
        _currentShip.Roof = Planet.Roof;
        _currentShip.Side = Planet.Side;
        _currentShip.Speed = Planet.Speed;
        _currentShip.Acceleration = Planet.Acceleration;

        if (IsWithin(_currentShip.Position, StationSpawnDistance))
        {
            RemoveSun();
            AddStation();
        }

        _currentShip = saved;
    }

    /// <summary>
    /// MAS3: the square of a ship's distance from us, in altimeter units
    /// (x_hi^2 + y_hi^2 + z_hi^2).
    /// </summary>
    private static float AltimeterDistanceSquared(Ship ship) => ship.Position.LengthSquared() / (AltimeterUnit * AltimeterUnit);

    /// <summary>Main flight loop part 15: altitude checks with the planet and sun, and fuel scooping.</summary>
    private void AltitudeChecks(int loopCounter)
    {
        // MA93
        if (loopCounter == 10)
        {
            if (_energy <= 50)
            {
                ShowMessage("messages.energy_low");
            }

            // The altimeter shows our height above the planet's surface, and
            // is full if the planet is out of its range
            _altitude = 0xFF;
            if (!IsWithin(Planet.Position, 65536))
            {
                return;
            }

            float distanceSquared = AltimeterDistanceSquared(Planet);
            if (distanceSquared > AltimeterRangeSquared)
            {
                return;
            }

            float altitudeSquared = distanceSquared - PlanetSurfaceSquared;
            if (altitudeSquared < 0)
            {
                ShowDeathScreen();
            }

            _altitude = (int)MathF.Sqrt(altitudeSquared);
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
                ShowMessage("messages.docking_computers_on");
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

        // The cabin temperature rises as we get closer to the sun, from 30
        // at the altimeter's range, and we burn up if it goes over 255
        var sun = Slots[1];
        if (sun == null || !IsWithin(sun.Position, 65536))
        {
            return;
        }

        int distance = (int)MathF.Min(AltimeterDistanceSquared(sun) / AltimeterUnit, 255);
        int temperature = 255 - distance + 30;
        _cabinTemperature = temperature & 0xFF;
        if (temperature > 0xFF)
        {
            ShowDeathScreen();
        }

        if (_cabinTemperature < 224 || _fuelScoops == 0)
        {
            return;
        }

        // Fuel scooping: we scoop speed / 8 tenths of a light year
        int fuel = _speed / 8 + _fuel;
        if (fuel >= 70)
        {
            fuel = 70;
        }

        _fuel = fuel;

        // MA34
        ShowMessage("messages.fuel_scoops_on");
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
        // Work out which shield is hit from whether the ship in K% is behind us
        bool behind = _slotShip != null && _slotShip.Position.Z < 0;
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

        // We can't jump if the planet or sun is in front of us and within
        // 131,072 in each axis (so their sign bytes are all less than 2)
        if (Planet.Position.Z >= 0 && IsWithin(Planet.Position, 2 * 65536))
        {
            Boop();
            return;
        }

        // WA3
        var sun = Slots[1]!;
        if (sun.Position.Z >= 0 && IsWithin(sun.Position, 2 * 65536))
        {
            Boop();
            return;
        }

        // WA2: jump forwards by 65,536, moving the planet and sun towards us
        Planet.Position.Z -= 65536;
        sun.Position.Z -= 65536;

        _viewType = 1;
        _mainLoopCounter = 1;
        _extraVesselsDelay = 0;
        SwitchView(_view);
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

        if (_world.Contains(_laserOwner))
        {
            _world.Remove(_laserOwner);
            return;
        }

        BeginWorldDrawing(_view);
        Span<LineSegment> beams = stackalloc LineSegment[4];
        _laserEndY -= 2;
        DrawLaserLines(beams[..2], 32, 224);
        _laserEndY += 2;
        DrawLaserLines(beams[2..], 48, 208);
        _world.SetLines(_laserOwner, beams);
    }

    /// <summary>
    /// las: draw a pair of laser lines from the bottom corners of the space
    /// view to (LASX, LASY). In the 3D world, the beams start just in front of
    /// us at those corners, and reach into the distance towards (LASX, LASY).
    /// </summary>
    private void DrawLaserLines(Span<LineSegment> beams, int left, int right)
    {
        var target = ScreenPointToWorld(_laserEndX, _laserEndY, LaserRange);
        beams[0] = new LineSegment(ScreenPointToWorld(left, 2 * CentreY - 1, ScreenEdgeDistance), target, Red);
        beams[1] = new LineSegment(ScreenPointToWorld(right, 2 * CentreY - 1, ScreenEdgeDistance), target, Red);
    }

    // ------------------------------------------------------------------------
    // The energy bomb
    // ------------------------------------------------------------------------

    /// <summary>BOMBTBX: the x-coordinates of the points in the energy bomb's lightning bolt.</summary>
    private readonly int[] _bombBoltX = new int[10];
    /// <summary>BOMBTBY: the y-coordinates of the points in the energy bomb's lightning bolt.</summary>
    private readonly int[] _bombBoltY = new int[10];

    /// <summary>
    /// BOMBOFF: draw the zig-zag lightning bolt of the energy bomb. The
    /// original draws and erases the bolt with EOR logic; here it is a group in
    /// the HUD, which is replaced each time it's drawn.
    /// </summary>
    private void DrawBombBolt()
    {
        if (_viewType != 0)
        {
            return;
        }

        using var group = _hud.Group(HudGroup.BombBolt);
        for (int y = 1; y < 10; y++)
        {
            _hud.DrawLine(_bombBoltX[y - 1], _bombBoltY[y - 1], _bombBoltX[y], _bombBoltY[y], Cyan);
        }
    }

    /// <summary>BOMBOFF (when the bolt is on-screen): erase the energy bomb's lightning bolt.</summary>
    private void HideBombBolt() => _hud.ClearGroup(HudGroup.BombBolt);

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
        HideBombBolt();
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
        DrawBombBolt();
    }
}

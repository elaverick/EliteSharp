using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// Ship tactics (the NPC AI), docking, missiles and E.C.M.
/// </summary>
public sealed partial class EliteGame
{
    // The original's unit vectors have a length of 96, so its tests of their
    // coordinates are in 96ths. Its dot products of two unit vectors keep the
    // high byte of the result, so its tests of those are in units of
    // 96 * 96 / 256 = 36, and 36 is the dot product of parallel vectors.

    /// <summary>K3: the vector used by the tactics routines (such as the vector from a target to the ship in INWK).</summary>
    private Vector3 _tacticsVector;

    /// <summary>XX15: a unit vector, usually the direction of K3.</summary>
    private Vector3 _unitVector;

    /// <summary>CNT: the dot product of the ship's nose and XX15, used by the tactics routines.</summary>
    private float _dotProduct;

    /// <summary>
    /// CNT2: the dot product of the ship's nose and the direction it wants to
    /// go, above which it speeds up (and below which it slows down to turn).
    /// </summary>
    private float _turnAngleLimit;

    /// <summary>RAT: how much a ship pitches and rolls to turn (as its pitch and roll counters).</summary>
    private int _turnRate;

    /// <summary>
    /// RAT2: how far a ship must be turned away from the direction it wants to
    /// go (as the dot product of its roof or side with that direction) before
    /// it pitches or rolls to turn towards it. The original compares RAT2 with
    /// twice the dot product, so its values are in 72nds.
    /// </summary>
    private float _turnThreshold;

    /// <summary>The station (or sun) in slot 1 (K%+NI%).</summary>
    private Ship? StationOrSun => Slots[1];

    /// <summary>Whether a vector is within a distance of the origin in all three axes.</summary>
    private static bool IsWithin(Vector3 v, float distance) =>
        MathF.Abs(v.X) < distance && MathF.Abs(v.Y) < distance && MathF.Abs(v.Z) < distance;

    /// <summary>
    /// Whether the low bytes of the ship's coordinates (x_lo, y_lo and z_lo)
    /// are all zero, which the original checks when deciding whether a
    /// missile that blows up damages us.
    /// </summary>
    private bool LowBytesAreZero() =>
        (LowByte(_currentShip.Position.X) | LowByte(_currentShip.Position.Y) | LowByte(_currentShip.Position.Z)) == 0;

    /// <summary>
    /// The low byte of a coordinate's magnitude (such as x_lo), which the
    /// original uses as a source of randomness.
    /// </summary>
    private static int LowByte(float coordinate) => (int)MathF.Abs(coordinate) & 0xFF;

    /// <summary>TACTICS: apply tactics to the ship in INWK.</summary>
    private void ApplyTactics()
    {
        _turnRate = 3;
        _turnThreshold = 4 / 72f;
        _turnAngleLimit = 22 / 36f;

        int type = _shipType;
        if (type == ShipType.Missile)
        {
            MissileTactics();
            return;
        }

        if (type == ShipType.SpaceStation)
        {
            if ((_currentShip.Behaviour & 0b00000100) != 0)
            {
                // TN5: the station is hostile, so spawn cops
                if (NextRandom() < 240)
                {
                    return;
                }

                if (_shipCounts[ShipType.Viper] >= 6)
                {
                    return;
                }

                SpawnChildWithEcm(ShipType.Viper);
                return;
            }

            if (_shipCounts[ShipType.Shuttle + 1] != 0)
            {
                return;
            }

            int random = NextRandom();
            if (random < 253)
            {
                return;
            }

            // The C flag is set, so this is #SHU or #SHU + 1
            SpawnChildWithEcm((random & 1) + ShipType.Shuttle);
            return;
        }

        if (type == ShipType.RockHermit)
        {
            // TA13: the rock hermit spawns a ship
            int random = NextRandom();
            if (random < 200)
            {
                return;
            }

            _currentShip.Ai = 0;
            _currentShip.Behaviour = 0b00100100;

            // The C flag is set
            SpawnChildWithEcm((random & 3) + ShipType.Sidewinder + 1);
            _currentShip.Ai = 0;
            return;
        }

        // TA17: recharge the ship's energy
        if (_currentShip.Energy < _blueprint!.MaxEnergy)
        {
            _currentShip.Energy++;
        }

        // TA21
        if (type == ShipType.Thargon && _shipCounts[ShipType.Thargoid] == 0)
        {
            // A Thargon without a mothership loses its E.C.M. and slows down
            _currentShip.Ai &= 0xFE;
            _currentShip.Speed >>= 1;
            return;
        }

        // TA14
        NextRandom();
        int behaviour = _currentShip.Behaviour;
        if ((behaviour & 1) != 0 && _randomX >= 50)
        {
            // Traders only apply tactics 20% of the time
            return;
        }

        // TN1
        int flags = behaviour >> 1;
        if ((flags & 1) != 0 && _legalStatus >= 40)
        {
            // A bounty hunter becomes hostile if we are a bad offender
            _currentShip.Behaviour |= 0b00000100;
            flags = _currentShip.Behaviour >> 1;
        }

        // TN2: check bit 2 (hostile)
        if ((_currentShip.Behaviour & 0b00000100) == 0)
        {
            // Not hostile, so check bit 4 (docking)
            if ((_currentShip.Behaviour & 0b00010000) != 0)
            {
                ApplyDockingManoeuvres();
                return;
            }

            // GOPL: head towards the planet
            CalculatePlanetVector();
            HeadTowardsVector();
            return;
        }

        // TN3: check bit 3 (pirate)
        if ((_currentShip.Behaviour & 0b00001000) != 0 && InSafeZone != 0)
        {
            // Pirates become passive in the station's safe zone
            _currentShip.Ai &= 0b10000001;
        }

        SetVectorToShip();
    }

    /// <summary>TN6: spawn a child ship of the given type with E.C.M. and AI.</summary>
    private void SpawnChildWithEcm(int type) => SpawnChildShip(type, 0b11110001);

    /// <summary>TN4: set K3 to the ship's coordinates and continue with TA19.</summary>
    private void SetVectorToShip()
    {
        _tacticsVector = _currentShip.Position;
        DecideDirection();
    }

    /// <summary>TACTICS part 1: missile tactics (TA18).</summary>
    private void MissileTactics()
    {
        if (_ecmCounter != 0)
        {
            // TA352: an E.C.M. has destroyed the missile (only the low bytes
            // of the coordinates are checked to see if it was near us)
            if (LowBytesAreZero())
            {
                TakeDamage(80);
            }

            // TA872
            MissileHitsTarget(ShipType.AlloyPlate);
            return;
        }

        if ((_currentShip.Ai & 0b01000000) != 0)
        {
            // TA34: the missile is hostile, so check whether it has hit us
            // (x_hi, y_hi and z_hi are all zero)
            if (!IsWithin(_currentShip.Position, 256))
            {
                SetVectorToShip();
                return;
            }

            _currentShip.Flags |= Ship.FlagKilled;
            ExplosionSound();
            TakeDamage(250);
            return;
        }

        // The missile is ours, so find the target
        int targetSlot = (_currentShip.Ai & 0x7F) >> 1;
        var target = Slots[targetSlot];
        VectorFromShip(target);

        if (!IsWithin(_tacticsVector, 256))
        {
            // TA64: the missile isn't close yet, so the target may fire its E.C.M.
            if (NextRandom() >= 16)
            {
                DecideDirection();
                return;
            }

            // M32
            if (target != null && (target.Ai & 1) != 0)
            {
                StartEcm();
                return;
            }

            DecideDirection();
            return;
        }

        // The missile has reached its target
        if (_currentShip.Ai == 0b10000010)
        {
            // TA352: the target is the space station, so destroy the missile
            if (LowBytesAreZero())
            {
                TakeDamage(80);
            }

            MissileHitsTarget(ShipType.AlloyPlate);
            return;
        }

        if (target != null && (target.Flags & Ship.FlagExploding) == 0)
        {
            target.Flags |= Ship.FlagKilled;
        }

        // TA35
        if (LowBytesAreZero())
        {
            TakeDamage(80);
        }

        // TA87: the original passes the target's slot number to EXNO2 as if
        // it were the ship type
        MissileHitsTarget((_currentShip.Ai & 0x7F) >> 1);
    }

    /// <summary>TA353: process the kill tally and mark the missile as killed.</summary>
    private void MissileHitsTarget(int killType)
    {
        RecordKill(killType);

        // TA873
        _currentShip.Flags |= Ship.FlagKilled;
    }

    /// <summary>TACTICS part 3 onwards (TA19): work out which direction the ship should be moving.</summary>
    private void DecideDirection()
    {
        NormaliseVector();
        _dotProduct = DotProduct(_currentShip.Nose);

        if (_shipType == ShipType.Missile)
        {
            TurnTowardsTarget();
            return;
        }

        if (_shipType == ShipType.Anaconda)
        {
            if (NextRandom() >= 200)
            {
                int random = NextRandom();
                SpawnChildWithEcm(random >= 100 ? ShipType.Worm : ShipType.Sidewinder);
                return;
            }
        }

        // TN7
        if (NextRandom() >= 250)
        {
            _currentShip.RollCounter = NextRandom() | 104;
        }

        // TA7: consider launching an escape pod
        int half = _blueprint!.MaxEnergy >> 1;
        if (half < _currentShip.Energy)
        {
            ConsiderFiringLasers();
            return;
        }

        if ((half >> 2) >= _currentShip.Energy && NextRandom() >= 230 && (ShipCatalogue.Get(_shipType).DefaultBehaviour & 0x80) != 0)
        {
            _currentShip.Behaviour &= 0b11110000;
            _slotShip!.Behaviour = _currentShip.Behaviour;
            _currentShip.Ai = 0;
            LaunchEscapePodFromShip();
            return;
        }

        ConsiderFiringMissile();
    }

    /// <summary>ta3: consider firing a missile (TACTICS part 5).</summary>
    private void ConsiderFiringMissile()
    {
        int missiles = _currentShip.Flags & 7;
        if (missiles != 0)
        {
            int random = NextRandom() & 31;
            if (random < missiles && _ecmCounter == 0)
            {
                _currentShip.Flags--;
                if (_shipType == ShipType.Thargoid)
                {
                    SpawnChildShip(ShipType.Thargon, _currentShip.Ai);
                }
                else
                {
                    ShipFiresMissile();
                }

                return;
            }
        }

        ConsiderFiringLasers();
    }

    /// <summary>TA3: consider firing lasers at us (TACTICS part 6).</summary>
    private void ConsiderFiringLasers()
    {
        // The ship fires if it is within 8,192 of us in each axis (x_hi, y_hi
        // and z_hi are less than 32) and pointing at us, and hits us if it is
        // pointing straight at us
        if (IsWithin(_currentShip.Position, 8192) && _dotProduct <= -32 / 36f)
        {
            int laserByte = _blueprint!.LaserAndMissiles;
            if ((laserByte & 0b11111000) != 0)
            {
                _currentShip.Flags |= Ship.FlagFiring;
                if (_dotProduct <= -35 / 36f)
                {
                    TakeDamage(laserByte >> 1, (laserByte & 1) != 0);
                    _currentShip.Acceleration = (_currentShip.Acceleration - 1) & 0xFF;
                    if (_ecmCounter != 0)
                    {
                        return;
                    }

                    HitByLaserSound();
                }
            }
        }

        DecideApproach();
    }

    /// <summary>TA4: decide whether to head towards or away from us (TACTICS part 7).</summary>
    private void DecideApproach()
    {
        // Head away if we are very close (z_hi is less than 3, and x_hi and
        // y_hi are less than 2)
        var position = _currentShip.Position;
        if (MathF.Abs(position.Z) < 768 && MathF.Abs(position.X) < 512 && MathF.Abs(position.Y) < 512)
        {
            TurnTowardsVector();
            return;
        }

        // TA5
        int random = NextRandom() | 0x80;
        if (random >= _currentShip.Ai)
        {
            TurnTowardsVector();
            return;
        }

        TurnTowardsTarget();
    }

    /// <summary>TA20: point the ship towards us (or the missile towards its target).</summary>
    private void TurnTowardsTarget()
    {
        NegateVector();
        _dotProduct = -_dotProduct;
        TurnTowardsVector();
    }

    /// <summary>TA15: turn the ship towards the XX15 vector, and set its acceleration.</summary>
    private void TurnTowardsVector()
    {
        // Pitch towards XX15 (the pitch counter's sign is the opposite of the
        // dot product's)
        float dot = DotProduct(_currentShip.Roof);
        _currentShip.PitchCounter = dot >= 0 ? 0x80 : 0;
        if (MathF.Abs(dot) >= _turnThreshold)
        {
            _currentShip.PitchCounter |= _turnRate;
        }

        // TA11: roll towards XX15, unless the ship is already rolling fast
        if ((_currentShip.RollCounter & 0x7F) < 16)
        {
            dot = DotProduct(_currentShip.Side);
            int sign = dot < 0 ? 0x80 : 0;
            _currentShip.RollCounter = ((sign ^ _currentShip.PitchCounter) & 0x80) ^ 0x80;
            if (MathF.Abs(dot) >= _turnThreshold)
            {
                _currentShip.RollCounter |= _turnRate;
            }
        }

        // TA6
        if (_dotProduct >= _turnAngleLimit)
        {
            // PH10E
            _currentShip.Acceleration = 3;
            return;
        }

        // TA9
        if (MathF.Abs(_dotProduct) < 0.5f)
        {
            return;
        }

        _currentShip.Acceleration = _shipType == ShipType.Missile ? 0xFE : 0xFF;
    }

    /// <summary>TA151: make the ship head in the direction of XX15.</summary>
    private void HeadTowardsVector()
    {
        // If the ship is heading away from XX15, turn as hard as possible
        float dot = DotProduct(_currentShip.Nose);
        if (dot <= -24 / 36f)
        {
            _turnThreshold = 0;
        }

        // TA152
        _dotProduct = dot;
        TurnTowardsVector();
    }

    /// <summary>DOCKIT: apply docking manoeuvres to the ship in INWK.</summary>
    private void ApplyDockingManoeuvres()
    {
        _turnThreshold = 6 / 72f;
        _turnRate = 3;
        _turnAngleLimit = 29 / 36f;

        if (InSafeZone == 0)
        {
            // GOPL
            CalculatePlanetVector();
            HeadTowardsVector();
            return;
        }

        // If the station is 65,536 or more away in any axis, head for the
        // planet
        VectorFromStation();
        if (!IsWithin(_tacticsVector, 65536))
        {
            CalculatePlanetVector();
            HeadTowardsVector();
            return;
        }

        // If the ship isn't in front of the slot, fly to a point in front of it
        float distance = _tacticsVector.Length();
        NormaliseVector();
        if (DotProduct(StationOrSun!.Nose) < 35 / 36f)
        {
            FlyToDockingPosition();
            return;
        }

        // If the ship is facing the slot, or it is our docking computer and
        // we are far enough from the slot (80,384), approach the slot
        if (DotProduct(_currentShip.Nose) <= -34 / 36f)
        {
            ApproachSlot();
            return;
        }

        if (distance >= 157 * 512 && _shipType >= 128)
        {
            ApproachSlot();
            return;
        }

        // PH2: turn away from the station
        NegateVector();
        HeadTowardsVector();
        SlowRightDown();
    }

    /// <summary>PH22: slow right down.</summary>
    private void SlowRightDown()
    {
        _currentShip.Acceleration = 0;
        _currentShip.Speed = 1;
    }

    /// <summary>
    /// PH1: fly towards the ideal docking position in front of the station,
    /// which is 768 in front of its slot (DCS1 twice).
    /// </summary>
    private void FlyToDockingPosition()
    {
        VectorFromStation();
        _tacticsVector -= StationOrSun!.Nose * 768;
        NormaliseVector();
        NegateVector();
        HeadTowardsVector();
    }

    /// <summary>PH3: refine the approach to the station's slot.</summary>
    private void ApproachSlot()
    {
        _turnThreshold = 0;
        _currentShip.PitchCounter = 0;

        if (_shipType >= 128)
        {
            // This is our docking computer, so line up with the slot, slowing
            // right down unless XX15 is within 1/16 of straight ahead in x
            // and y
            int xSign = _unitVector.X < 0 ? 0x80 : 0;
            int ySign = _unitVector.Y < 0 ? 0x80 : 0;
            _currentShip.RollCounter = 1 | ((_shipType ^ xSign ^ ySign) & 0x80);
            if (MathF.Abs(_unitVector.X) >= 6 / 96f)
            {
                SlowRightDown();
                return;
            }

            _currentShip.PitchCounter = 1 | ySign;
            if (MathF.Abs(_unitVector.Y) >= 6 / 96f)
            {
                SlowRightDown();
                return;
            }
        }

        // PH32: accelerate and roll to match the station if the slot isn't
        // horizontal enough
        _currentShip.RollCounter = 0;
        _unitVector = _currentShip.Side;
        if (MathF.Abs(DotProduct(StationOrSun!.Roof)) >= 33 / 36f)
        {
            // TN11: accelerate and roll to match the station
            _currentShip.Acceleration = (_currentShip.Acceleration + 1) & 0xFF;
            _currentShip.RollCounter = 0x7F;
        }
        else
        {
            SlowRightDown();
        }

        // TN13: the ship has docked (the original checks K3+10 here, which
        // this port never sets, so this is always the case)
        _currentShip.Behaviour |= 0x80;
    }

    /// <summary>VCSU1: K3 = INWK - the station's coordinates.</summary>
    private void VectorFromStation() => VectorFromShip(StationOrSun);

    /// <summary>VCSUB: K3 = INWK - the coordinates of the given ship.</summary>
    private void VectorFromShip(Ship? other) => _tacticsVector = _currentShip.Position - (other?.Position ?? Vector3.Zero);

    /// <summary>TAS2: normalise the vector in K3 into XX15 (which is zero if K3 is).</summary>
    private void NormaliseVector() =>
        _unitVector = _tacticsVector == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(_tacticsVector);

    /// <summary>TAS3 (and TAS4): the dot product of one of a ship's orientation vectors with XX15.</summary>
    private float DotProduct(Vector3 v) => Vector3.Dot(v, _unitVector);

    /// <summary>TAS6: negate the vector in XX15.</summary>
    private void NegateVector() => _unitVector = -_unitVector;

    /// <summary>SPS1: calculate the unit vector to the planet in XX15.</summary>
    private void CalculatePlanetVector()
    {
        _tacticsVector = Planet.Position;
        NormaliseVector();
    }

    /// <summary>
    /// HITCH: returns true if the ship in INWK is in our crosshairs: in front
    /// of us and less than 65,536 away, not exploding, and with its centre
    /// within its targetable area of our line of sight.
    /// </summary>
    private bool IsInCrosshairs()
    {
        var position = _currentShip.Position;
        if (position.Z < 0 || position.Z >= 65536 || _shipType >= 128)
        {
            return false;
        }

        if ((_currentShip.Flags & Ship.FlagExploding) != 0 || MathF.Abs(position.X) >= 256 || MathF.Abs(position.Y) >= 256)
        {
            return false;
        }

        return position.X * position.X + position.Y * position.Y <= _blueprint!.TargetableArea;
    }

    /// <summary>
    /// FRS1: launch a ship straight ahead of us, below the line of sight (used
    /// for our missiles and the escape pod). Returns true if the ship was added.
    /// </summary>
    private bool LaunchFromUs(int type, bool carry = false)
    {
        ResetWorkspace();
        _currentShip.Position = new Vector3(0, -28, 14);
        _currentShip.Ai = ((_missileTarget << 1) | 0x80) & 0xFF;
        return LaunchFromShip(type, carry);
    }

    /// <summary>fq1: launch a ship of the given type from INWK, pointing away from us at double our speed.</summary>
    private bool LaunchFromShip(int type, bool carry = false)
    {
        _currentShip.Nose.Z = 1;
        _currentShip.Side.X = -1;
        _currentShip.Speed = ((_speed << 1) | (carry ? 1 : 0)) & 0xFF;
        return AddShip(type);
    }

    /// <summary>FRMIS: fire a missile from our ship.</summary>
    private void FireMissile()
    {
        if (!LaunchFromUs(ShipType.Missile))
        {
            // FR1: display "Missile Jammed"
            ShowMessage("messages.missile_jammed");
            return;
        }

        var target = Slots[_missileTarget];
        if (target != null)
        {
            MakeHostile(target.Type, target);
        }

        DisarmMissile(Ink.None);
        _missiles--;
        MakeSound(SoundLaunch);

        // Fall through into ANGRY with A = &80, as left by NOISE
        if (target != null)
        {
            MakeHostile(0x80, target);
        }
    }

    /// <summary>ANGRY: make a ship hostile (the ship's type is in A).</summary>
    private void MakeHostile(int type, Ship ship)
    {
        if (type == ShipType.SpaceStation)
        {
            MakeStationHostile();
            return;
        }

        if ((ship.Behaviour & 0b00100000) != 0)
        {
            // This is an innocent bystander, so the station gets angry too
            MakeStationHostile();
        }

        if (ship.Ai == 0)
        {
            return;
        }

        ship.Ai |= 0x80;
        ship.Acceleration = 2;
        ship.PitchCounter = 4;
        if (_shipType >= ShipType.CobraMkIII)
        {
            ship.Behaviour |= 0b00000100;
        }
    }

    /// <summary>AN2: make the space station hostile.</summary>
    private void MakeStationHostile()
    {
        var station = StationOrSun;
        if (station != null)
        {
            station.Behaviour |= 0b00000100;
        }
    }

    /// <summary>SESCP: spawn an escape pod from the ship in INWK.</summary>
    private void LaunchEscapePodFromShip() => SpawnChildShip(ShipType.EscapePod, 0b11111110);

    /// <summary>SFRMIS: the ship in INWK fires a missile at us.</summary>
    private void ShipFiresMissile()
    {
        if (!SpawnChildShip(ShipType.Missile, 0b11111110))
        {
            return;
        }

        ShowMessage("messages.incoming_missile");
        MakeSound(SoundLaunch);
    }

    /// <summary>
    /// SFS1: spawn a child ship from the ship in INF (the parent), with the
    /// given AI flag. Returns true if the ship was added.
    /// </summary>
    private bool SpawnChildShip(int type, int ai)
    {
        var saved = _currentShip;
        var savedBlueprint = _blueprint;

        _currentShip = _workspace;
        _currentShip.CopyStateFrom(_slotShip!);

        if (_shipType == ShipType.SpaceStation)
        {
            // Launch the ship from the slot, 192 along the station's nose
            // (2 * nosev_hi)
            _currentShip.Speed = 32;
            _currentShip.Position += _currentShip.Nose * 192;
        }

        // rx
        _currentShip.Ai = ai;
        _currentShip.RollCounter &= 0xFE;

        if (type >= ShipType.AlloyPlate && type <= ShipType.Splinter)
        {
            int random = NextRandom();
            _currentShip.PitchCounter = (random << 1) & 0xFF;
            _currentShip.Speed = _randomX & 15;
            _currentShip.RollCounter = 0x7F | ((random & 0x80) != 0 ? 0x80 : 0);
        }

        // NOIL
        bool added = AddShip(type);

        _currentShip = saved;
        _blueprint = savedBlueprint;
        return added;
    }

    /// <summary>EXNO2: process the fact that we have killed a ship, updating the kill tally.</summary>
    private void RecordKill(int type)
    {
        var blueprint = ShipCatalogue.Get(Math.Clamp(type, 1, ShipCatalogue.Count));
        int low = _killTallyFraction + blueprint.KillFraction;
        _killTallyFraction = low & 0xFF;
        int carry = low > 0xFF ? 1 : 0;
        int talliedLow = (_killTally & 0xFF) + blueprint.KillInteger + carry;
        _killTally = (_killTally & 0xFF00) | (talliedLow & 0xFF);
        if (talliedLow > 0xFF)
        {
            _killTally = (_killTally + 0x100) & 0xFFFF;
            ShowMessage("messages.right_on_commander");
        }

        // davidscockup: fall through into EXNO3
        ExplosionSound();
    }

    /// <summary>EXNO3: make the sound of an explosion.</summary>
    private void ExplosionSound() => MakeSound(SoundExplosion);

    /// <summary>EXNO: make the sound of a laser strike on another ship.</summary>
    private void LaserStrikeSound() => MakeSound(SoundHit);

    /// <summary>ECBLB2: start the E.C.M. and light up the E.C.M. bulb.</summary>
    private void StartEcm()
    {
        _ecmCounter = 32;
        ToggleEcmBulb();
    }

    /// <summary>ECMOF: switch off the E.C.M. and its bulb.</summary>
    private void StopEcm()
    {
        _ecmCounter = 0;
        _ourEcmActive = 0;
        ToggleEcmBulb();
    }
}

using EliteSharp.Data;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;

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
    private readonly int[] _tacticsVector = new int[14];

    /// <summary>XX15: a normalised vector, as signed bytes (96 = 1).</summary>
    private readonly int[] _unitVector = new int[6];

    /// <summary>CNT: the dot product used by the tactics routines, as a raw sign-magnitude byte.</summary>
    private int _dotProduct;

    /// <summary>CNT2: the maximum angle beyond which a ship will slow down to turn.</summary>
    private int _turnAngleLimit;

    /// <summary>The station (or sun) in slot 1 (K%+NI%).</summary>
    private Ship? StationOrSun => Slots[1];

    /// <summary>The K3 vector as a signed coordinate (axis 0 = x, 1 = y, 2 = z).</summary>
    private int VectorCoordinate(int axis) => _tacticsVector[axis * 3];

    /// <summary>Set one coordinate of the K3 vector (axis 0 = x, 1 = y, 2 = z).</summary>
    private void SetVectorCoordinate(int axis, int value) => _tacticsVector[axis * 3] = value;

    /// <summary>TACTICS: apply tactics to the ship in INWK.</summary>
    private void ApplyTactics()
    {
        _rotationTemp = 3;
        _rotationTemp2 = 4;
        _turnAngleLimit = 22;

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
        SetVectorCoordinate(0, _currentShip.X);
        SetVectorCoordinate(1, _currentShip.Y);
        SetVectorCoordinate(2, _currentShip.Z);
        DecideDirection();
    }

    /// <summary>TACTICS part 1: missile tactics (TA18).</summary>
    private void MissileTactics()
    {
        if (_ecmCounter != 0)
        {
            // TA352: an E.C.M. has destroyed the missile (only the low bytes
            // of the coordinates are checked to see if it was near us)
            if ((_currentShip.XLo | _currentShip.YLo | _currentShip.ZLo) == 0)
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
            if (OrCoordinateHighBytes(0) != 0)
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

        bool close = ((Ship.SignByte(VectorCoordinate(0)) | Ship.SignByte(VectorCoordinate(1)) | Ship.SignByte(VectorCoordinate(2))) & 0x7F) == 0
                     && (Ship.Hi(VectorCoordinate(0)) | Ship.Hi(VectorCoordinate(1)) | Ship.Hi(VectorCoordinate(2))) == 0;
        if (!close)
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
            if ((_currentShip.XLo | _currentShip.YLo | _currentShip.ZLo) == 0)
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
        if ((_currentShip.XLo | _currentShip.YLo | _currentShip.ZLo) == 0)
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
        if ((OrCoordinateHighBytes(0) & 0b11100000) == 0 && _dotProduct >= 160)
        {
            int laserByte = _blueprint!.LaserAndMissiles;
            if ((laserByte & 0b11111000) != 0)
            {
                _currentShip.Flags |= Ship.FlagFiring;
                if (_dotProduct >= 163)
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
        if (_currentShip.ZHi < 3 && ((_currentShip.XHi | _currentShip.YHi) & 0b11111110) == 0)
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
        _dotProduct ^= 0x80;
        TurnTowardsVector();
    }

    /// <summary>TA15: turn the ship towards the XX15 vector, and set its acceleration.</summary>
    private void TurnTowardsVector()
    {
        int dot = DotProduct(_currentShip.Roof);
        _currentShip.PitchCounter = (dot ^ 0x80) & 0x80;
        if (((dot << 1) & 0xFF) >= _rotationTemp2)
        {
            _currentShip.PitchCounter |= _rotationTemp;
        }

        // TA11
        if (((_currentShip.RollCounter << 1) & 0xFF) < 32)
        {
            dot = DotProduct(_currentShip.Side);
            _currentShip.RollCounter = ((dot ^ _currentShip.PitchCounter) & 0x80) ^ 0x80;
            if (((dot << 1) & 0xFF) >= _rotationTemp2)
            {
                _currentShip.RollCounter |= _rotationTemp;
            }
        }

        // TA6
        dot = _dotProduct;
        if ((dot & 0x80) == 0 && dot >= _turnAngleLimit)
        {
            // PH10E
            _currentShip.Acceleration = 3;
            return;
        }

        // TA9
        if ((dot & 0x7F) < 18)
        {
            return;
        }

        _currentShip.Acceleration = _shipType == ShipType.Missile ? 0xFE : 0xFF;
    }

    /// <summary>TA151: make the ship head in the direction of XX15.</summary>
    private void HeadTowardsVector()
    {
        int dot = DotProduct(_currentShip.Nose);
        if (dot >= 0x98)
        {
            _rotationTemp2 = 0;
        }

        // TA152
        _dotProduct = dot;
        TurnTowardsVector();
    }

    /// <summary>DOCKIT: apply docking manoeuvres to the ship in INWK.</summary>
    private void ApplyDockingManoeuvres()
    {
        _rotationTemp2 = 6;
        _rotationTemp = 3;
        _turnAngleLimit = 29;

        if (InSafeZone == 0)
        {
            // GOPL
            CalculatePlanetVector();
            HeadTowardsVector();
            return;
        }

        VectorFromStation();
        if (((Ship.SignByte(VectorCoordinate(0)) | Ship.SignByte(VectorCoordinate(1)) | Ship.SignByte(VectorCoordinate(2))) & 0x7F) != 0)
        {
            CalculatePlanetVector();
            HeadTowardsVector();
            return;
        }

        // TA2 without the scaling: K = the length of the vector in K3
        int length = NormaliseVectorUnscaled();
        NormaliseVector();
        int dot = StationDotProduct(StationOrSun!.Nose);
        if ((dot & 0x80) != 0 || dot < 35)
        {
            FlyToDockingPosition();
            return;
        }

        dot = DotProduct(_currentShip.Nose);
        if (dot >= 0xA2)
        {
            ApproachSlot();
            return;
        }

        if (length >= 157 && _shipType >= 128)
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

    /// <summary>PH1: fly towards the ideal docking position in front of the station.</summary>
    private void FlyToDockingPosition()
    {
        VectorFromStation();
        MoveAlongStationNose();
        MoveAlongStationNose();
        NormaliseVector();
        NegateVector();
        HeadTowardsVector();
    }

    /// <summary>PH3: refine the approach to the station's slot.</summary>
    private void ApproachSlot()
    {
        _rotationTemp2 = 0;
        _currentShip.PitchCounter = 0;

        if (_shipType >= 128)
        {
            // This is our docking computer
            int sign = ((_shipType ^ ToByte(_unitVector[0]) ^ ToByte(_unitVector[1])) & 0x80) != 0 ? 0x80 : 0;
            _currentShip.RollCounter = 1 | sign;
            if (((ToByte(_unitVector[0]) << 1) & 0xFF) >= 12)
            {
                SlowRightDown();
                return;
            }

            _currentShip.PitchCounter = 1 | (ToByte(_unitVector[1]) & 0x80);
            if (((ToByte(_unitVector[1]) << 1) & 0xFF) >= 12)
            {
                SlowRightDown();
                return;
            }
        }

        // PH32
        _currentShip.RollCounter = 0;
        _unitVector[0] = Ship.VectorHi(_currentShip.Side.X);
        _unitVector[1] = Ship.VectorHi(_currentShip.Side.Y);
        _unitVector[2] = Ship.VectorHi(_currentShip.Side.Z);
        int dot = StationDotProduct(StationOrSun!.Roof);
        if (((dot << 1) & 0xFF) >= 66)
        {
            // TN11: accelerate and roll to match the station
            _currentShip.Acceleration = (_currentShip.Acceleration + 1) & 0xFF;
            _currentShip.RollCounter = 0x7F;
        }
        else
        {
            SlowRightDown();
        }

        // TN13: check whether the ship has docked
        if (_tacticsVector[10] == 0)
        {
            _currentShip.Behaviour |= 0x80;
        }
    }

    /// <summary>The raw sign-magnitude byte for a signed byte value.</summary>
    private static int ToByte(int value) => EliteMaths.ToSignMagnitude(value);

    /// <summary>VCSU1: K3 = INWK - the station's coordinates.</summary>
    private void VectorFromStation() => VectorFromShip(StationOrSun);

    /// <summary>VCSUB: K3 = INWK - the coordinates of the given ship.</summary>
    private void VectorFromShip(Ship? other)
    {
        SetVectorCoordinate(0, _currentShip.X - (other?.X ?? 0));
        SetVectorCoordinate(1, _currentShip.Y - (other?.Y ?? 0));
        SetVectorCoordinate(2, _currentShip.Z - (other?.Z ?? 0));
    }

    /// <summary>
    /// DCS1: move the K3 vector twice along the station's nose vector, by
    /// subtracting 2 * nosev_hi each time (TAS7).
    /// </summary>
    private void MoveAlongStationNose()
    {
        var station = StationOrSun!;
        for (int i = 0; i < 2; i++)
        {
            SetVectorCoordinate(0, SubtractNose(VectorCoordinate(0), station.Nose.X));
            SetVectorCoordinate(1, SubtractNose(VectorCoordinate(1), station.Nose.Y));
            SetVectorCoordinate(2, SubtractNose(VectorCoordinate(2), station.Nose.Z));
        }
    }

    /// <summary>TAS7: K3 coordinate = coordinate - 2 * nosev_hi (in the low byte).</summary>
    private static int SubtractNose(int coordinate, int nose)
    {
        int offset = (Ship.VectorHiByte(nose) << 1) & 0xFF;
        int sign = Ship.VectorHiByte(nose) & 0x80;
        return coordinate + (sign != 0 ? offset : -offset);
    }

    /// <summary>TAS2: normalise the vector in K3 into XX15.</summary>
    private void NormaliseVector()
    {
        var (x, y, z) = EliteMaths.NormaliseLarge(VectorCoordinate(0), VectorCoordinate(1), VectorCoordinate(2), out _);
        _unitVector[0] = x;
        _unitVector[1] = y;
        _unitVector[2] = z;
    }

    /// <summary>
    /// TA2 and NORM: calculate XX15 from the K3 vector without the initial
    /// scaling done by TAS2, returning the length of the vector in Q.
    /// </summary>
    private int NormaliseVectorUnscaled()
    {
        static int ScaledHighByte(int v) => (((Math.Abs(v) >> 8) & 0xFF) >> 1) | ((Math.Abs(v) >> 16) & 0x7F);
        int x = ScaledHighByte(VectorCoordinate(0)), y = ScaledHighByte(VectorCoordinate(1)), z = ScaledHighByte(VectorCoordinate(2));
        var (unitX, unitY, unitZ) = EliteMaths.Normalise(VectorCoordinate(0) < 0 ? -x : x, VectorCoordinate(1) < 0 ? -y : y, VectorCoordinate(2) < 0 ? -z : z, out int length);
        _unitVector[0] = unitX;
        _unitVector[1] = unitY;
        _unitVector[2] = unitZ;
        return length;
    }

    /// <summary>
    /// TAS3 (and TAS4): the dot product of a vector's high bytes with XX15,
    /// returned as the raw sign-magnitude high byte of the 16-bit result.
    /// </summary>
    private int DotProduct(IntVector3 v)
    {
        int sum = EliteMaths.MultiplySigned(Ship.VectorHi(v.X), _unitVector[0]);
        sum = EliteMaths.MultiplyAdd(Ship.VectorHi(v.Y), _unitVector[1], sum);
        sum = EliteMaths.MultiplyAdd(Ship.VectorHi(v.Z), _unitVector[2], sum);
        return ((Math.Abs(sum) >> 8) & 0x7F) | (sum < 0 ? 0x80 : 0);
    }

    /// <summary>TAS4: the dot product of one of the station's vectors with XX15.</summary>
    private int StationDotProduct(IntVector3 v) => DotProduct(v);

    /// <summary>TAS6: negate the vector in XX15.</summary>
    private void NegateVector()
    {
        _unitVector[0] = -_unitVector[0];
        _unitVector[1] = -_unitVector[1];
        _unitVector[2] = -_unitVector[2];
    }

    /// <summary>SPS1: calculate the normalised vector to the planet in XX15.</summary>
    private void CalculatePlanetVector()
    {
        // SPS3 copies the planet's coordinates into K3 as 24-bit values
        // (x_hi, x_sign split into magnitude and sign, dropping x_lo)
        SetVectorCoordinate(0, ScaleDownCoordinate(Planet.X));
        SetVectorCoordinate(1, ScaleDownCoordinate(Planet.Y));
        SetVectorCoordinate(2, ScaleDownCoordinate(Planet.Z));
        NormaliseVector();
    }

    /// <summary>SPS3: K3 = (sign, x_sign &amp; 127, x_hi), i.e. the coordinate divided by 256.</summary>
    private static int ScaleDownCoordinate(int coordinate)
    {
        int magnitude = (Math.Abs(coordinate) >> 8) & 0x7FFF;
        return coordinate < 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// HITCH: returns true if the ship in INWK is in our crosshairs (i.e. in
    /// front of us and within its targetable area).
    /// </summary>
    private bool IsInCrosshairs()
    {
        if (_currentShip.ZSign != 0 || _shipType >= 128)
        {
            return false;
        }

        if (((_currentShip.Flags & Ship.FlagExploding) | _currentShip.XHi | _currentShip.YHi) != 0)
        {
            return false;
        }

        int sum = _currentShip.XLo * _currentShip.XLo + _currentShip.YLo * _currentShip.YLo;
        if (sum > 0xFFFF)
        {
            return false;
        }

        return _blueprint!.TargetableArea >= sum;
    }

    /// <summary>
    /// FRS1: launch a ship straight ahead of us, below the line of sight (used
    /// for our missiles and the escape pod). Returns true if the ship was added.
    /// </summary>
    private bool LaunchFromUs(int type, bool carry = false)
    {
        ResetWorkspace();
        _currentShip.Y = -28;
        _currentShip.Z = 14;
        _currentShip.Ai = ((_missileTarget << 1) | 0x80) & 0xFF;
        return LaunchFromShip(type, carry);
    }

    /// <summary>fq1: launch a ship of the given type from INWK, pointing away from us at double our speed.</summary>
    private bool LaunchFromShip(int type, bool carry = false)
    {
        _currentShip.Nose.Z = 0x60 << 8;
        _currentShip.Side.X = -(0x60 << 8);
        _currentShip.Speed = ((_speed << 1) | (carry ? 1 : 0)) & 0xFF;
        return AddShip(type);
    }

    /// <summary>FRMIS: fire a missile from our ship.</summary>
    private void FireMissile()
    {
        if (!LaunchFromUs(ShipType.Missile))
        {
            // FR1: display "Missile Jammed"
            ShowMessage(201);
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

        ShowMessage(120);
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
            _currentShip.Speed = 32;
            _currentShip.X = AddNoseToCoordinate(_currentShip.X, _currentShip.Nose.X);
            _currentShip.Y = AddNoseToCoordinate(_currentShip.Y, _currentShip.Nose.Y);
            _currentShip.Z = AddNoseToCoordinate(_currentShip.Z, _currentShip.Nose.Z);
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

    /// <summary>SFS2: add 2 * a nosev high byte to a coordinate.</summary>
    private static int AddNoseToCoordinate(int coordinate, int nose)
    {
        int high = Ship.VectorHiByte(nose);
        return AddToCoordinate(coordinate, high & 0x80, (high << 1) & 0xFF);
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
            ShowMessage(101);
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

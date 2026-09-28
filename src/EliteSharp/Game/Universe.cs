using EliteSharp.Data;
using EliteSharp.Game.Ships;

namespace EliteSharp.Game;

/// <summary>
/// Managing the local bubble of universe: adding and removing ships, setting
/// up the solar system, and resetting things.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The INWK workspace used when setting up new ships.</summary>
    private readonly Ship _workspace = Ship.Workspace();

    /// <summary>True if the station in this system is a Dodo rather than a Coriolis.</summary>
    private bool _dodoStation;

    /// <summary>The ship blueprint for a ship type, as looked up in XX21.</summary>
    private ShipBlueprint? BlueprintFor(int type) => Ship.BlueprintFor(type, _dodoStation);

    /// <summary>
    /// NWSHP: add a new ship of the given type to the local bubble, using the
    /// data in INWK. Returns true if the ship was added (C set).
    /// </summary>
    private bool AddShip(int type)
    {
        int slot = 0;
        while (Slots[slot] != null)
        {
            slot++;
            if (slot >= MaxShips)
            {
                // NW3: no room
                return false;
            }
        }

        // NW1
        Ship ship;
        if (type >= 128)
        {
            ship = Ship.Create(type);
        }
        else
        {
            var blueprint = BlueprintFor(type);
            if (blueprint == null)
            {
                return false;
            }

            ship = Ship.Create(type, _dodoStation);
            _blueprint = blueprint;

            // NW6
            _currentShip.Energy = blueprint.MaxEnergy;
            _currentShip.Flags = blueprint.Missiles;
        }

        // NW2
        if (type < 128)
        {
            if (type == ShipType.RockHermit || (type >= ShipType.JunkLow && type < ShipType.JunkHigh))
            {
                // gangbang
                _junkCount++;
            }

            // NW7
            _shipCounts[type]++;

            // NW8: add the default NEWB flags from E%
            _currentShip.Behaviour |= ShipCatalogue.Get(type).DefaultBehaviour & 0b01101111;
        }

        ship.CopyStateFrom(_currentShip);
        Slots[slot] = ship;
        _slotShip = ship;
        return true;
    }

    /// <summary>KILLSHP: remove the ship in the given slot from the local bubble.</summary>
    private void RemoveShip(int slot)
    {
        if (_missileTarget == slot)
        {
            DisarmMissile(DashboardGreen);
            ShowMessage(200);
        }

        // KS5
        var ship = Slots[slot]!;
        int type = ship.Type;
        RemoveFromScreen(ship.DisplayOwner);

        if (type == ShipType.SpaceStation)
        {
            // KS4: remove the space station and replace it with the sun
            Slots[1] = null;
            RemoveFromScreen(ship.DisplayOwner);
            ResetWorkspace();
            ResetSunLines();
            InSafeZone = 0;
            ToggleStationBulb();
            _currentShip.Y = 6 << 16;
            AddShip(ShipType.Sun);
            return;
        }

        if (type == ShipType.Constrictor)
        {
            _missionStatus |= 0b00000010;
            _killTally = (_killTally + 0x100) & 0xFFFF;
        }

        // lll
        if (type < 128)
        {
            if (type == ShipType.RockHermit || (type >= ShipType.JunkLow && type < ShipType.JunkHigh))
            {
                _junkCount--;
            }

            // KS7
            _shipCounts[type]--;
        }

        // KSL1: move the ships above this slot down by one
        for (int i = slot; i < MaxShips; i++)
        {
            Slots[i] = Slots[i + 1];
        }

        Slots[MaxShips] = null;

        // KS2: update the targets of any missiles
        for (int i = 0; i < MaxShips; i++)
        {
            var missile = Slots[i];
            if (missile == null)
            {
                break;
            }

            if (missile.Type != ShipType.Missile || (missile.Ai & 0x80) == 0)
            {
                continue;
            }

            int target = (missile.Ai & 0x7F) >> 1;
            if (target < slot)
            {
                continue;
            }

            if (target == slot)
            {
                // KS6: the missile's target has gone
                missile.Ai = 0;
            }
            else
            {
                missile.Ai = (((target - 1) << 1) | 0x80) & 0xFF;
            }
        }
    }

    /// <summary>ZINF: reset the INWK workspace, with the orientation vectors pointing along the axes.</summary>
    private void ResetWorkspace()
    {
        _currentShip = _workspace;
        _currentShip.ResetOrientationAndPosition();
    }

    /// <summary>ZERO: reset the local bubble of universe and the flight variables from FRIN to de.</summary>
    private void ResetBubble()
    {
        foreach (var ship in Slots)
        {
            if (ship != null)
            {
                RemoveFromScreen(ship.DisplayOwner);
            }
        }

        Array.Clear(Slots);
        Array.Clear(_shipCounts);
        _junkCount = 0;
        _autoDocking = 0;
        _ourEcmActive = 0;
        _inWitchspace = 0;
        _cabinTemperature = 0;
        _laserBeamPower = 0;
        _missileArmed = 0;
        _view = 0;
        _laserPulseCounter = 0;
        _laserTemperature = 0;
        HyperspaceColoursOn = 0;
        _extraVesselsDelay = 0;
        _messageDelay = 0;
        _messageDestroyed = 0;
    }

    /// <summary>RESET: reset our ship and the universe, ready to start a new game (or after dying).</summary>
    private void ResetShipAndUniverse()
    {
        ResetBubble();

        // Zero BETA through BETA+6
        _pitchAngle = 0;
        _pitchMagnitude = 0;
        _hyperspaceTicks = 0;
        _hyperspaceCountdown = 0;
        _ecmCounter = 0;
        _rollMagnitude = 0;
        _rollSign = 0;

        // JSTGY = &FF
        ToggleOptions[4] = 0xFF;
        _docked = 0xFF;
        _forwardShield = 0xFF;
        _aftShield = 0xFF;
        _energy = 0xFF;
        ResetFlight();
    }

    /// <summary>RES2: reset a number of flight variables and workspaces.</summary>
    private void ResetFlight()
    {
        _stardustCount = NormalStardustCount;
        _missileTarget = 0xFF;
        _pitchRate = 128;
        _rollSign = 128;
        _pitchSign = 128;
        _pitchAngle = 0;
        _pitchMagnitude = 0;
        _rollSignFlipped = 0;
        _pitchSignFlipped = 0;
        _mainLoopCounter = 0;
        _speed = 3;
        _rollAngle = 3;
        _rollMagnitude = 3;

        if (InSafeZone != 0)
        {
            ToggleStationBulb();
        }

        if (_ecmCounter != 0)
        {
            StopEcm();
        }

        WipeScanner();
        ResetBubble();
        ResetWorkspace();
    }

    /// <summary>GTHG: spawn a Thargoid ship and a Thargon companion.</summary>
    private void SpawnThargoid()
    {
        SetUpDistantShip();
        _currentShip.Ai = 0xFF;
        AddShip(ShipType.Thargoid);
        AddShip(ShipType.Thargon);
    }

    /// <summary>
    /// Ze: set up INWK as a fairly aggressive ship a fair distance away, and
    /// return a random number (with X and the C flag also random).
    /// </summary>
    private int SetUpDistantShip()
    {
        ResetWorkspace();
        int random = NextRandom();
        int x = _randomX;
        _currentShip.X = (random & 0x80) != 0 ? -(25 << 8) : 25 << 8;
        _currentShip.Y = (x & 0x80) != 0 ? -(25 << 8) : 25 << 8;
        _currentShip.Z = 25 << 8;
        _currentShip.Ai = ((((x << 1) | (x >= 245 ? 1 : 0)) & 0xFF) | 0b11000000);

        // Fall through into DORND2
        return NextRandomRepeatable();
    }

    /// <summary>THERE: returns true (C set) if we are in the Constrictor's system in mission 1.</summary>
    private bool InConstrictorSystem() => _galaxyNumber == 1 && _currentSystemX == 144 && _currentSystemY == 33;

    /// <summary>SOLAR: set up various aspects of arriving in a new system (the planet and sun).</summary>
    private void SetUpSystem()
    {
        // There are no Trumbles in this version, so skip to nobirths
        bool carry = (_legalStatus & 1) != 0;
        _legalStatus >>= 1;

        ResetWorkspace();
        int zSign = ((_selectedSeeds[1] & 3) + 3 + (carry ? 1 : 0)) & 0xFF;
        int xySign = zSign >> 1;
        _currentShip.Z = zSign << 16;
        _currentShip.X = xySign << 16;
        _currentShip.Y = xySign << 16;
        AddPlanet();

        // Set up the sun
        int sunZSign = (_selectedSeeds[3] & 7) | 0b10000001;
        _currentShip.Z = -((sunZSign & 0x7F) << 16);
        // Only x_sign and x_hi are set, so the sun keeps the planet's y
        int sunXHigh = _selectedSeeds[5] & 3;
        _currentShip.X = (sunXHigh << 16) | (sunXHigh << 8);
        _currentShip.RollCounter = 0;
        _currentShip.PitchCounter = 0;
        AddShip(ShipType.Sun);

        InitialiseStardust();
    }

    /// <summary>SOS1: update the missile indicators and add the planet in INWK.</summary>
    private void AddPlanet()
    {
        ResetMissileIndicators();
        _currentShip.RollCounter = 127;
        _currentShip.PitchCounter = 127;
        AddShip((_techLevel & 0b00000010) | 0b10000000);
    }

    /// <summary>NWSTARS: initialise the stardust field (if this is a space view) and wipe the scanner.</summary>
    private void InitialiseStardust()
    {
        if (_viewType == 0)
        {
            CreateStardust();
        }

        WipeScanner();
    }

    /// <summary>nWq: create a random cloud of stardust.</summary>
    private void CreateStardust()
    {
        for (int particle = _stardustCount; particle > 0; particle--)
        {
            _dustZ[particle] = NextRandom() | 8;
            _dustX[particle] = NextRandom();
            _dustY[particle] = NextRandom();
        }

        UpdateStardustImage();
    }

    /// <summary>WPSHPS: wipe all the ships from the scanner and mark them as not being on-screen.</summary>
    private void WipeScanner()
    {
        var savedInwk = _currentShip;
        for (int x = 0; x < MaxShips; x++)
        {
            var ship = Slots[x];
            if (ship == null)
            {
                break;
            }

            if (ship.Type >= 128)
            {
                continue;
            }

            _shipType = ship.Type;
            _currentShip = ship.CloneBlock();
            _currentSlot = x;
            DrawOnScanner();
            ship.Flags &= 0b10100111;
        }

        _currentShip = savedInwk;

        // WS2: reset the ball line heap (the planet is no longer on-screen)
        if (Slots[0] != null)
        {
            RemoveFromScreen(Slots[0]!.DisplayOwner);
        }

        ResetSunLines();
    }

    /// <summary>FLFLLS: reset the sun line heap (the sun is no longer on-screen).</summary>
    private void ResetSunLines()
    {
        _world.Remove(_sunOwner);
        if (_sunImage != null)
        {
            _screen.RemoveImage(_sunOwner);
            _sunImage = null;
        }

        Array.Clear(_sunHalfWidths);
        _sunHidden = 0xFF;
    }

    /// <summary>
    /// NWSPS: add a new space station to the local bubble, using the data in
    /// INWK (the planet's orientation and the station's position).
    /// </summary>
    private void AddStation()
    {
        ToggleStationBulb();
        _currentShip.Ai = 0b10000001;
        _currentShip.PitchCounter = 0;
        _currentShip.Behaviour = 0;
        if (Slots[1] != null)
        {
            RemoveFromScreen(Slots[1]!.DisplayOwner);
        }

        Slots[1] = null;
        _currentShip.RollCounter = 0xFF;

        // Flip the signs of nosev
        _currentShip.Nose = new IntVector3(-_currentShip.Nose.X, -_currentShip.Nose.Y, -_currentShip.Nose.Z);

        _dodoStation = _techLevel >= 10;
        AddShip(ShipType.SpaceStation);
    }

    /// <summary>MJP: process a mis-jump into witchspace.</summary>
    private void MisJump()
    {
        ClearScreen(3);
        HyperspaceTunnel();
        ResetFlight();
        _inWitchspace = 0xFF;

        do
        {
            SpawnThargoid();
        }
        while (_shipCounts[ShipType.Thargoid] <= 2);

        _stardustCount = 2;
        SwitchView(0);

        // Move us to a random point in witchspace
        _currentSystemY ^= 0b00011111;
    }

    /// <summary>TT18: try to initiate a jump into hyperspace.</summary>
    private void Hyperspace()
    {
        int fuel = _fuel - _selectedDistance;
        _fuel = fuel < 0 ? 0 : fuel;

        if (_viewType == 0)
        {
            ClearScreen(0);
            HyperspaceTunnel();
        }

        // ee5: holding CTRL during the jump forces a mis-jump if PATG is set
        if ((CtrlPressed() & AuthorNamesShown & 0x80) != 0)
        {
            // ptg
            _competitionFlags |= 1;
            MisJump();
            return;
        }

        if (NextRandom() >= 253)
        {
            MisJump();
            return;
        }

        // Arrive in the new system (hyp1+3 skips the call to TT111)
        SetCurrentSystem();
        ArriveInSystem();
        ResetFlight();
        SetUpSystem();

        if ((_viewType & 0b00111111) != 0)
        {
            return;
        }

        ClearSpaceView();
        if (_viewType != 0)
        {
            ShowChartAfterJump();
            return;
        }

        _viewType++;
        Launch();
    }

    /// <summary>TT114: show the relevant chart after arriving in hyperspace in a chart view.</summary>
    private void ShowChartAfterJump()
    {
        if ((_viewType & 0x80) != 0)
        {
            ShowShortRangeChart();
        }
        else
        {
            ShowLongRangeChart();
        }
    }

    /// <summary>TT110: launch from the station, or show the front space view.</summary>
    private void Launch()
    {
        if (_docked != 0)
        {
            LaunchTunnel();
            ResetFlight();
            SelectNearestSystem();

            // INC INWK+8 puts the planet at z = 65536, in front of us
            _currentShip.Z = 1 << 16;
            AddPlanet();

            // Setting z_sign to &80 and incrementing z_hi puts the station at
            // z = -256, just behind us
            _currentShip.Z = -(1 << 8);
            AddStation();
            _speed = 12;
            _legalStatus |= ContrabandBadness();
            _viewType = 0xFF;
            DrawTunnelCircles();
        }

        // NLUNCH
        _docked = 0;
        SwitchView(0);
    }

    /// <summary>hyp1+3: set up the new system after a hyperspace jump.</summary>
    private void ArriveInSystem()
    {
        for (int x = 5; x >= 0; x--)
        {
            _currentSystemSeeds[x] = _destinationSeeds[x];
        }

        _extraVesselsDelay = 0;
        _currentEconomy = _selectedEconomy;
        _techLevel = _selectedTechLevel;
        _government = _selectedGovernment;
        CalculateMarketAvailability();
    }

    /// <summary>hyp1: do a hyperspace jump to the system at the crosshairs (from the galactic hyperdrive).</summary>
    private void JumpToSelectedSystem()
    {
        SelectNearestSystem();
        SetCurrentSystem();
        ArriveInSystem();
    }

    /// <summary>GVL: calculate the availability of market items.</summary>
    private void CalculateMarketAvailability()
    {
        _marketRandom = NextRandom();
        // The loop stops when 4 * the item number reaches 63, so the last
        // item (alien items) is not included
        for (int item = 0; item < 16; item++)
        {
            int priceIndex = item * 4;
            _scratch[1] = GameData.MarketPrices[priceIndex + 1];
            CalculateEconomicFactor();
            int availability = (GameData.MarketPrices[priceIndex + 3] & _marketRandom) + GameData.MarketPrices[priceIndex + 2];
            if ((_scratch[1] & 0x80) != 0)
            {
                availability += _scratch[3];
            }
            else
            {
                availability -= _scratch[3];
            }

            availability &= 0xFF;
            if ((availability & 0x80) != 0)
            {
                availability = 0;
            }

            _marketAvailability[item] = availability & 0b00111111;
        }
    }

    /// <summary>var: calculate QQ19+3 = economy * |economic factor|.</summary>
    private void CalculateEconomicFactor()
    {
        _scratch[2] = _scratch[1] & 31;
        int product = 0;
        _marketAvailability[16] = 0;
        for (int count = _currentEconomy; count > 0; count--)
        {
            product += _scratch[2];
        }

        _scratch[3] = product & 0xFF;
    }

    /// <summary>LAUN: make the launch sound and draw the launch tunnel.</summary>
    private void LaunchTunnel()
    {
        MakeSound(SoundLaunch);
        DrawTunnel(8);
    }

    /// <summary>LL164: make the hyperspace sound and draw the hyperspace tunnel.</summary>
    private void HyperspaceTunnel()
    {
        MakeSound(SoundHyperspace);
        MakeSound(SoundHyperspace2);
        HyperspaceColoursOn = 4;
        DrawTunnel(4);
        HyperspaceColoursOn = 0;
    }

    /// <summary>jmp: set the current system to the selected system.</summary>
    private void SetCurrentSystem()
    {
        _currentSystemX = _crosshairX;
        _currentSystemY = _crosshairY;
    }

    /// <summary>ping: move the crosshairs to the current system.</summary>
    private void MoveCrosshairsHome()
    {
        _crosshairX = _currentSystemX;
        _crosshairY = _currentSystemY;
    }

    /// <summary>MCASH: add an amount of cash (in Cr * 10) to our cash pot.</summary>
    private void AddCash(int amount) => _cash = unchecked(_cash + (uint)amount);

    /// <summary>LCASH: subtract an amount of cash, returning false (C clear) if we can't afford it.</summary>
    private bool SpendCash(int amount)
    {
        if (_cash < (uint)amount)
        {
            return false;
        }

        _cash -= (uint)amount;
        return true;
    }
}

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
    private readonly WorkspaceShip _workspace = new();

    /// <summary>True if the station in this system is a Dodo rather than a Coriolis.</summary>
    private bool _dodoStation;

    /// <summary>The ship blueprint for a ship type, as looked up in XX21.</summary>
    private ShipBlueprint? BlueprintFor(int type) => Ship.BlueprintFor(type, _dodoStation);

    /// <summary>
    /// NWSHP: add a new ship of the given type to the local bubble, using the
    /// data in INWK. Returns true if the ship was added (C set).
    /// </summary>
    private bool NWSHP(int type)
    {
        int slot = 0;
        while (Slots[slot] != null)
        {
            slot++;
            if (slot >= NOSH)
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
            XX0 = blueprint;

            // NW6
            INWK.Energy = blueprint.MaxEnergy;
            INWK.Flags = blueprint.Missiles;
        }

        // NW2
        if (type < 128)
        {
            if (type == ShipType.RockHermit || (type >= ShipType.JunkLow && type < ShipType.JunkHigh))
            {
                // gangbang
                Junk++;
            }

            // NW7
            Many[type]++;

            // NW8: add the default NEWB flags from E%
            INWK.Newb |= GameData.DefaultNewbFlags[type - 1] & 0b01101111;
        }

        ship.CopyStateFrom(INWK);
        Slots[slot] = ship;
        INF = ship;
        return true;
    }

    /// <summary>KILLSHP: remove the ship in the given slot from the local bubble.</summary>
    private void KILLSHP(int slot)
    {
        if (MSTG == slot)
        {
            ABORT(GREEN2);
            MESS(200);
        }

        // KS5
        var ship = Slots[slot]!;
        int type = ship.Type;
        _screen.RemoveImage(ship.DisplayOwner);

        if (type == ShipType.SpaceStation)
        {
            // KS4: remove the space station and replace it with the sun
            Slots[1] = null;
            _screen.RemoveImage(ship.DisplayOwner);
            ZINF();
            FLFLLS();
            SSPR = 0;
            SPBLB();
            INWK.Y = 6 << 16;
            NWSHP(ShipType.Sun);
            return;
        }

        if (type == ShipType.Constrictor)
        {
            TP |= 0b00000010;
            TALLY = (TALLY + 0x100) & 0xFFFF;
        }

        // lll
        if (type < 128)
        {
            if (type == ShipType.RockHermit || (type >= ShipType.JunkLow && type < ShipType.JunkHigh))
            {
                Junk--;
            }

            // KS7
            Many[type]--;
        }

        // KSL1: move the ships above this slot down by one
        for (int i = slot; i < NOSH; i++)
        {
            Slots[i] = Slots[i + 1];
        }

        Slots[NOSH] = null;

        // KS2: update the targets of any missiles
        for (int i = 0; i < NOSH; i++)
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
    private void ZINF()
    {
        INWK = _workspace;
        INWK.ResetOrientationAndPosition();
    }

    /// <summary>ZERO: reset the local bubble of universe and the flight variables from FRIN to de.</summary>
    private void ZERO()
    {
        foreach (var ship in Slots)
        {
            if (ship != null)
            {
                _screen.RemoveImage(ship.DisplayOwner);
            }
        }

        Array.Clear(Slots);
        Array.Clear(Many);
        Junk = 0;
        Auto = 0;
        ECMP = 0;
        MJ = 0;
        CABTMP = 0;
        LAS2 = 0;
        MSAR = 0;
        VIEW = 0;
        LASCT = 0;
        GNTMP = 0;
        HFX = 0;
        EV = 0;
        DLY = 0;
        de = 0;
    }

    /// <summary>RESET: reset our ship and the universe, ready to start a new game (or after dying).</summary>
    private void RESET()
    {
        ZERO();

        // Zero BETA through BETA+6
        BETA = 0;
        BET1 = 0;
        QQ22 = 0;
        QQ22Hi = 0;
        ECMA = 0;
        ALP1 = 0;
        ALP2 = 0;

        // JSTGY = &FF
        ToggleOptions[4] = 0xFF;
        QQ12 = 0xFF;
        FSH = 0xFF;
        ASH = 0xFF;
        ENERGY = 0xFF;
        RES2();
    }

    /// <summary>RES2: reset a number of flight variables and workspaces.</summary>
    private void RES2()
    {
        NOSTM = NOST;
        MSTG = 0xFF;
        JSTY = 128;
        ALP2 = 128;
        BET2 = 128;
        BETA = 0;
        BET1 = 0;
        ALP2Flipped = 0;
        BET2Flipped = 0;
        MCNT = 0;
        DELTA = 3;
        ALPHA = 3;
        ALP1 = 3;

        if (SSPR != 0)
        {
            SPBLB();
        }

        if (ECMA != 0)
        {
            ECMOF();
        }

        WPSHPS();
        ZERO();
        ZINF();
    }

    /// <summary>GTHG: spawn a Thargoid ship and a Thargon companion.</summary>
    private void GTHG()
    {
        Ze();
        INWK.Ai = 0xFF;
        NWSHP(ShipType.Thargoid);
        NWSHP(ShipType.Thargon);
    }

    /// <summary>
    /// Ze: set up INWK as a fairly aggressive ship a fair distance away, and
    /// return a random number (with X and the C flag also random).
    /// </summary>
    private int Ze()
    {
        ZINF();
        int a = DORND();
        int x = _randX;
        INWK.X = (a & 0x80) != 0 ? -(25 << 8) : 25 << 8;
        INWK.Y = (x & 0x80) != 0 ? -(25 << 8) : 25 << 8;
        INWK.Z = 25 << 8;
        INWK.Ai = ((((x << 1) | (x >= 245 ? 1 : 0)) & 0xFF) | 0b11000000);

        // Fall through into DORND2
        return DORND2();
    }

    /// <summary>THERE: returns true (C set) if we are in the Constrictor's system in mission 1.</summary>
    private bool THERE() => GCNT == 1 && QQ0 == 144 && QQ1 == 33;

    /// <summary>SOLAR: set up various aspects of arriving in a new system (the planet and sun).</summary>
    private void SOLAR()
    {
        // There are no Trumbles in this version, so skip to nobirths
        bool carry = (FIST & 1) != 0;
        FIST >>= 1;

        ZINF();
        int zSign = ((QQ15[1] & 3) + 3 + (carry ? 1 : 0)) & 0xFF;
        int xySign = zSign >> 1;
        INWK.Z = zSign << 16;
        INWK.X = xySign << 16;
        INWK.Y = xySign << 16;
        SOS1();

        // Set up the sun
        int sunZSign = (QQ15[3] & 7) | 0b10000001;
        INWK.Z = -((sunZSign & 0x7F) << 16);
        // Only x_sign and x_hi are set, so the sun keeps the planet's y
        int xs = QQ15[5] & 3;
        INWK.X = (xs << 16) | (xs << 8);
        INWK.RollCounter = 0;
        INWK.PitchCounter = 0;
        NWSHP(ShipType.Sun);

        NWSTARS();
    }

    /// <summary>SOS1: update the missile indicators and add the planet in INWK.</summary>
    private void SOS1()
    {
        msblob();
        INWK.RollCounter = 127;
        INWK.PitchCounter = 127;
        NWSHP((tek & 0b00000010) | 0b10000000);
    }

    /// <summary>NWSTARS: initialise the stardust field (if this is a space view) and wipe the scanner.</summary>
    private void NWSTARS()
    {
        if (QQ11 == 0)
        {
            nWq();
        }

        WPSHPS();
    }

    /// <summary>nWq: create a random cloud of stardust.</summary>
    private void nWq()
    {
        for (int y = NOSTM; y > 0; y--)
        {
            SZ[y] = DORND() | 8;
            SX[y] = DORND();
            SY[y] = DORND();
        }

        UpdateStardustImage();
    }

    /// <summary>WPSHPS: wipe all the ships from the scanner and mark them as not being on-screen.</summary>
    private void WPSHPS()
    {
        var savedInwk = INWK;
        for (int x = 0; x < NOSH; x++)
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

            TYPE = ship.Type;
            INWK = ship.CloneBlock();
            XSAV = x;
            SCAN();
            ship.Flags &= 0b10100111;
        }

        INWK = savedInwk;

        // WS2: reset the ball line heap (the planet is no longer on-screen)
        if (Slots[0] != null)
        {
            _screen.RemoveImage(Slots[0]!.DisplayOwner);
        }

        LSP = 0;
        LSX2Empty = true;
        FLFLLS();
    }

    /// <summary>FLFLLS: reset the sun line heap (the sun is no longer on-screen).</summary>
    private void FLFLLS()
    {
        if (_sunImage != null)
        {
            _screen.RemoveImage(_sunOwner);
            _sunImage = null;
        }

        Array.Clear(LSO);
        LSX = 0xFF;
    }

    /// <summary>
    /// NWSPS: add a new space station to the local bubble, using the data in
    /// INWK (the planet's orientation and the station's position).
    /// </summary>
    private void NWSPS()
    {
        SPBLB();
        INWK.Ai = 0b10000001;
        INWK.PitchCounter = 0;
        INWK.Newb = 0;
        if (Slots[1] != null)
        {
            _screen.RemoveImage(Slots[1]!.DisplayOwner);
        }

        Slots[1] = null;
        INWK.RollCounter = 0xFF;

        // Flip the signs of nosev
        INWK.Nose = new IntVector3(-INWK.Nose.X, -INWK.Nose.Y, -INWK.Nose.Z);

        _dodoStation = tek >= 10;
        NWSHP(ShipType.SpaceStation);
    }

    /// <summary>MJP: process a mis-jump into witchspace.</summary>
    private void MJP()
    {
        TT66(3);
        LL164();
        RES2();
        MJ = 0xFF;

        do
        {
            GTHG();
        }
        while (Many[ShipType.Thargoid] <= 2);

        NOSTM = 2;
        LOOK1(0);

        // Move us to a random point in witchspace
        QQ1 ^= 0b00011111;
    }

    /// <summary>TT18: try to initiate a jump into hyperspace.</summary>
    private void TT18()
    {
        int fuel = QQ14 - QQ8;
        QQ14 = fuel < 0 ? 0 : fuel;

        if (QQ11 == 0)
        {
            TT66(0);
            LL164();
        }

        // ee5: holding CTRL during the jump forces a mis-jump if PATG is set
        if ((CtrlPressed() & PATG & 0x80) != 0)
        {
            // ptg
            COK |= 1;
            MJP();
            return;
        }

        if (DORND() >= 253)
        {
            MJP();
            return;
        }

        // Arrive in the new system (hyp1+3 skips the call to TT111)
        jmp();
        hyp1Tail();
        RES2();
        SOLAR();

        if ((QQ11 & 0b00111111) != 0)
        {
            return;
        }

        TTX66();
        if (QQ11 != 0)
        {
            TT114();
            return;
        }

        QQ11++;
        TT110();
    }

    /// <summary>TT114: show the relevant chart after arriving in hyperspace in a chart view.</summary>
    private void TT114()
    {
        if ((QQ11 & 0x80) != 0)
        {
            TT23();
        }
        else
        {
            TT22();
        }
    }

    /// <summary>TT110: launch from the station, or show the front space view.</summary>
    private void TT110()
    {
        if (QQ12 != 0)
        {
            LAUN();
            RES2();
            TT111();

            // INC INWK+8 puts the planet at z = 65536, in front of us
            INWK.Z = 1 << 16;
            SOS1();

            // Setting z_sign to &80 and incrementing z_hi puts the station at
            // z = -256, just behind us
            INWK.Z = -(1 << 8);
            NWSPS();
            DELTA = 12;
            FIST |= BAD();
            QQ11 = 0xFF;
            HFS1();
        }

        // NLUNCH
        QQ12 = 0;
        LOOK1(0);
    }

    /// <summary>hyp1+3: set up the new system after a hyperspace jump.</summary>
    private void hyp1Tail()
    {
        for (int x = 5; x >= 0; x--)
        {
            QQ2[x] = safehouse[x];
        }

        EV = 0;
        QQ28 = QQ3;
        tek = QQ5;
        gov = QQ4;
        GVL();
    }

    /// <summary>hyp1: do a hyperspace jump to the system at the crosshairs (from the galactic hyperdrive).</summary>
    private void hyp1()
    {
        TT111();
        jmp();
        hyp1Tail();
    }

    /// <summary>GVL: calculate the availability of market items.</summary>
    private void GVL()
    {
        QQ26 = DORND();
        // The loop stops when 4 * the item number reaches 63, so the last
        // item (alien items) is not included
        for (int item = 0; item < 16; item++)
        {
            int x = item * 4;
            QQ19[1] = GameData.MarketPrices[x + 1];
            var_();
            int a = (GameData.MarketPrices[x + 3] & QQ26) + GameData.MarketPrices[x + 2];
            if ((QQ19[1] & 0x80) != 0)
            {
                a += QQ19[3];
            }
            else
            {
                a -= QQ19[3];
            }

            a &= 0xFF;
            if ((a & 0x80) != 0)
            {
                a = 0;
            }

            AVL[item] = a & 0b00111111;
        }
    }

    /// <summary>var: calculate QQ19+3 = economy * |economic factor|.</summary>
    private void var_()
    {
        QQ19[2] = QQ19[1] & 31;
        int a = 0;
        AVL[16] = 0;
        for (int y = QQ28; y > 0; y--)
        {
            a += QQ19[2];
        }

        QQ19[3] = a & 0xFF;
    }

    /// <summary>LAUN: make the launch sound and draw the launch tunnel.</summary>
    private void LAUN()
    {
        NOISE(solaun);
        HFS2(8);
    }

    /// <summary>LL164: make the hyperspace sound and draw the hyperspace tunnel.</summary>
    private void LL164()
    {
        NOISE(sohyp);
        NOISE(sohyp2);
        HFX = 4;
        HFS2(4);
        HFX = 0;
    }

    /// <summary>jmp: set the current system to the selected system.</summary>
    private void jmp()
    {
        QQ0 = QQ9;
        QQ1 = QQ10;
    }

    /// <summary>ping: move the crosshairs to the current system.</summary>
    private void ping()
    {
        QQ9 = QQ0;
        QQ10 = QQ1;
    }

    /// <summary>MCASH: add an amount of cash (in Cr * 10) to our cash pot.</summary>
    private void MCASH(int amount) => CASH = unchecked(CASH + (uint)amount);

    /// <summary>LCASH: subtract an amount of cash, returning false (C clear) if we can't afford it.</summary>
    private bool LCASH(int amount)
    {
        if (CASH < (uint)amount)
        {
            return false;
        }

        CASH -= (uint)amount;
        return true;
    }
}

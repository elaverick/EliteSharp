namespace EliteSharp.Game.Ships;

/// <summary>
/// A 3D vector of integers, used for the orientation vectors (nosev, roofv and
/// sidev) where 96 * 256 represents 1.0.
/// </summary>
public struct IntVector3(int x, int y, int z)
{
    public int X = x;
    public int Y = y;
    public int Z = z;

    public int this[int axis]
    {
        readonly get => axis switch { 0 => X, 1 => Y, _ => Z };
        set
        {
            switch (axis)
            {
                case 0: X = value; break;
                case 1: Y = value; break;
                default: Z = value; break;
            }
        }
    }

    public override readonly string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>
/// The explosion cloud data that the original stores at the start of the ship
/// line heap (bytes #0 to #6 plus the exploding vertex coordinates).
/// </summary>
public sealed class ExplosionCloud
{
    /// <summary>Heap byte #0: the projected cloud size.</summary>
    public int Size;

    /// <summary>Heap byte #1: the cloud counter, which increases by 4 each time the cloud is drawn.</summary>
    public int Counter;

    /// <summary>Heap byte #2: the explosion count from the blueprint (4 * n + 6).</summary>
    public int CountByte;

    /// <summary>Heap bytes #3-6: random seeds so the cloud is repeatable.</summary>
    public readonly byte[] Seeds = new byte[4];

    /// <summary>The screen coordinates of the vertices that act as cloud origins.</summary>
    public readonly List<(int X, int Y)> Origins = [];
}

/// <summary>
/// A ship (or planet, or sun) in the local bubble of universe. This mirrors the
/// 37-byte ship data block (INWK / K%) from the original, with coordinates and
/// orientation vectors held as signed integers rather than sign-magnitude bytes.
/// Each ship type is a subclass (see ShipBlueprints.g.cs) that supplies its
/// blueprint.
/// </summary>
public class Ship
{
    protected Ship(int type, ShipBlueprint? blueprint)
    {
        Type = type;
        Blueprint = blueprint;
        DisplayOwner = this;
    }

    /// <summary>
    /// The ship type as stored in the FRIN slots: 1-33 for ships, 128/130 for the
    /// planet (with meridians/crater) and 129 for the sun.
    /// </summary>
    public int Type { get; set; }

    /// <summary>The ship's blueprint, or null for the planet and sun.</summary>
    public ShipBlueprint? Blueprint { get; set; }

    /// <summary>True if this is the planet or the sun (bit 7 of the type is set).</summary>
    public bool IsPlanetOrSun => Type >= 128;

    /// <summary>Bytes #0-2, #3-5, #6-8: the ship's position relative to us.</summary>
    public int X, Y, Z;

    /// <summary>Bytes #9-14: nosev, the direction the ship is pointing.</summary>
    public IntVector3 Nose;

    /// <summary>Bytes #15-20: roofv, the direction of the ship's roof.</summary>
    public IntVector3 Roof;

    /// <summary>Bytes #21-26: sidev, the direction out of the ship's right side.</summary>
    public IntVector3 Side;

    /// <summary>Byte #27: speed.</summary>
    public int Speed;

    /// <summary>Byte #28: acceleration (a signed byte, applied once then zeroed).</summary>
    public int Acceleration;

    /// <summary>Byte #29: roll counter (sign-magnitude, 127 = no damping).</summary>
    public int RollCounter;

    /// <summary>Byte #30: pitch counter (sign-magnitude, 127 = no damping).</summary>
    public int PitchCounter;

    /// <summary>
    /// Byte #31: bits 0-2 = missiles, bit 3 = drawn on-screen, bit 4 = shown on
    /// the scanner, bit 5 = exploding, bit 6 = firing lasers, bit 7 = killed.
    /// </summary>
    public int Flags;

    /// <summary>Byte #32: bit 7 = AI enabled, bits 1-6 = aggression, bit 0 = E.C.M.</summary>
    public int Ai;

    /// <summary>Byte #35: energy.</summary>
    public int Energy;

    /// <summary>Byte #36: the NEWB flags (trader, bounty hunter, hostile, pirate, docking, innocent, cop, scooped).</summary>
    public int Newb;

    /// <summary>The explosion cloud data (the start of the ship line heap in the original).</summary>
    public ExplosionCloud Explosion { get; private set; } = new();

    /// <summary>
    /// The object that owns this ship's on-screen image. When the main loop makes
    /// a view-rotated copy of a ship (as PLUT does with INWK), the copy draws into
    /// the original ship's display slot.
    /// </summary>
    public Ship DisplayOwner { get; private set; }

    public const int FlagDrawn = 0x08;
    public const int FlagScanner = 0x10;
    public const int FlagExploding = 0x20;
    public const int FlagFiring = 0x40;

    /// <summary>Bit 6 of byte #31 means the explosion cloud is on-screen when the ship is exploding.</summary>
    public const int FlagOnScreenCloud = 0x40;
    public const int FlagKilled = 0x80;

    public int MissileCount
    {
        get => Flags & 7;
        set => Flags = (Flags & ~7) | (value & 7);
    }

    /// <summary>Byte #0 (x_lo) etc: the low byte of |x|.</summary>
    public static int Lo(int value) => Math.Abs(value) & 0xFF;

    /// <summary>The high byte of a 24-bit sign-magnitude coordinate, e.g. x_hi.</summary>
    public static int Hi(int value) => (Math.Abs(value) >> 8) & 0xFF;

    /// <summary>The sign byte of a 24-bit sign-magnitude coordinate, e.g. x_sign (bit 7 = sign).</summary>
    public static int SignByte(int value) => ((Math.Abs(value) >> 16) & 0x7F) | (value < 0 ? 0x80 : 0);

    public int XLo => Lo(X);
    public int XHi => Hi(X);
    public int YLo => Lo(Y);
    public int YHi => Hi(Y);
    public int ZLo => Lo(Z);
    public int ZHi => Hi(Z);
    public int XSign => SignByte(X);
    public int YSign => SignByte(Y);
    public int ZSign => SignByte(Z);

    /// <summary>
    /// The high byte of a 16-bit sign-magnitude vector coordinate as a signed
    /// value, e.g. nosev_x_hi with its sign applied.
    /// </summary>
    public static int VectorHi(int value) => value < 0 ? -((-value) >> 8) : value >> 8;

    /// <summary>The raw sign-magnitude byte for the high byte of a vector coordinate, e.g. nosev_x_hi.</summary>
    public static int VectorHiByte(int value) => ((Math.Abs(value) >> 8) & 0x7F) | (value < 0 ? 0x80 : 0);

    /// <summary>
    /// Create a shallow copy of this ship's data block, as the original does when
    /// it copies K% into INWK. The copy shares this ship's explosion data and
    /// on-screen display.
    /// </summary>
    public Ship CloneBlock()
    {
        var copy = (Ship)MemberwiseClone();
        copy.DisplayOwner = DisplayOwner;
        return copy;
    }

    /// <summary>
    /// Copy the data block from another ship (as the original does when it
    /// copies a ship's data block between INWK and K%), keeping this ship's
    /// own explosion data and on-screen display.
    /// </summary>
    public void CopyStateFrom(Ship other)
    {
        X = other.X; Y = other.Y; Z = other.Z;
        Nose = other.Nose; Roof = other.Roof; Side = other.Side;
        Speed = other.Speed;
        Acceleration = other.Acceleration;
        RollCounter = other.RollCounter;
        PitchCounter = other.PitchCounter;
        Flags = other.Flags;
        Ai = other.Ai;
        Energy = other.Energy;
        Newb = other.Newb;
    }

    /// <summary>Give this ship its own explosion heap and display, rather than sharing another ship's.</summary>
    public void DetachHeap()
    {
        Explosion = new ExplosionCloud();
        DisplayOwner = this;
    }

    /// <summary>
    /// ZINF: reset the data block to all zeroes, with the orientation vectors set
    /// so that sidev = (1, 0, 0), roofv = (0, 1, 0) and nosev = (0, 0, -1).
    /// </summary>
    public void ResetOrientationAndPosition()
    {
        X = Y = Z = 0;
        Nose = new IntVector3(0, 0, -96 * 256);
        Roof = new IntVector3(0, 96 * 256, 0);
        Side = new IntVector3(96 * 256, 0, 0);
        Speed = 0;
        Acceleration = 0;
        RollCounter = 0;
        PitchCounter = 0;
        Flags = 0;
        Ai = 0;
        Energy = 0;
        Newb = 0;
    }

    public override string ToString() => $"{Blueprint?.Name ?? GetType().Name} (type {Type}) at ({X}, {Y}, {Z})";

    /// <summary>Create a new ship of the given type (as stored in FRIN).</summary>
    public static Ship Create(int type, bool dodoStation = false) => type switch
    {
        ShipType.Missile => new Missile(),
        ShipType.SpaceStation => dodoStation ? new DodoStation { Type = ShipType.SpaceStation } : new CoriolisStation(),
        ShipType.EscapePod => new EscapePod(),
        ShipType.AlloyPlate => new AlloyPlate(),
        ShipType.CargoCanister => new CargoCanister(),
        6 => new Boulder(),
        ShipType.Asteroid => new Asteroid(),
        ShipType.Splinter => new Splinter(),
        ShipType.Shuttle => new Shuttle(),
        10 => new Transporter(),
        ShipType.CobraMkIII => new CobraMkIII(),
        12 => new Python(),
        13 => new Boa(),
        ShipType.Anaconda => new Anaconda(),
        ShipType.RockHermit => new RockHermit(),
        ShipType.Viper => new Viper(),
        ShipType.Sidewinder => new Sidewinder(),
        18 => new Mamba(),
        ShipType.Krait => new Krait(),
        ShipType.Adder => new Adder(),
        21 => new Gecko(),
        22 => new CobraMkI(),
        ShipType.Worm => new Worm(),
        ShipType.CobraMkIIIPirate => new CobraMkIIIPirate(),
        ShipType.AspMkII => new AspMkII(),
        26 => new PythonPirate(),
        27 => new FerDeLance(),
        28 => new Moray(),
        ShipType.Thargoid => new Thargoid(),
        ShipType.Thargon => new Thargon(),
        ShipType.Constrictor => new Constrictor(),
        ShipType.Cougar => new Cougar(),
        ShipType.Dodo => new DodoStation(),
        ShipType.Planet or ShipType.PlanetWithCrater => new Planet(type),
        ShipType.Sun => new Sun(),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ship type"),
    };

    /// <summary>The blueprint for a ship type, as looked up in XX21.</summary>
    public static ShipBlueprint? BlueprintFor(int type, bool dodoStation = false) => type switch
    {
        ShipType.SpaceStation => dodoStation ? DodoStation.Data : CoriolisStation.Data,
        >= 1 and <= 33 => Create(type).Blueprint,
        _ => null,
    };
}

/// <summary>
/// A ship data block that isn't in the local bubble, used as the INWK
/// workspace when setting up new ships.
/// </summary>
public sealed class WorkspaceShip() : Ship(0, null);

/// <summary>The planet, which is type 128 (equator and meridian) or 130 (crater).</summary>
public sealed class Planet(int type) : Ship(type, null)
{
    public bool HasCrater => Type == ShipType.PlanetWithCrater;
}

/// <summary>The sun, which is type 129.</summary>
public sealed class Sun() : Ship(ShipType.Sun, null);

/// <summary>Ship type numbers, as defined by the configuration variables in the original.</summary>
public static class ShipType
{
    public const int Missile = 1;           // MSL
    public const int SpaceStation = 2;      // SST
    public const int EscapePod = 3;         // ESC
    public const int AlloyPlate = 4;        // PLT
    public const int CargoCanister = 5;     // OIL
    public const int Asteroid = 7;          // AST
    public const int Splinter = 8;          // SPL
    public const int Shuttle = 9;           // SHU
    public const int CobraMkIII = 11;       // CYL
    public const int Anaconda = 14;         // ANA
    public const int RockHermit = 15;       // HER
    public const int Viper = 16;            // COPS
    public const int Sidewinder = 17;       // SH3
    public const int Krait = 19;            // KRA
    public const int Adder = 20;            // ADA
    public const int Worm = 23;             // WRM
    public const int CobraMkIIIPirate = 24; // CYL2
    public const int AspMkII = 25;          // ASP
    public const int Thargoid = 29;         // THG
    public const int Thargon = 30;          // TGL
    public const int Constrictor = 31;      // CON
    public const int Cougar = 32;           // COU
    public const int Dodo = 33;             // DOD

    public const int Planet = 128;
    public const int Sun = 129;
    public const int PlanetWithCrater = 130;

    public const int JunkLow = EscapePod;   // JL
    public const int JunkHigh = Shuttle + 2; // JH
    public const int PackHunters = Sidewinder; // PACK

    /// <summary>The number of different ship types (NTY).</summary>
    public const int Count = 33;
}

using System.Diagnostics;
using EliteSharp.Data;
using EliteSharp.Game.Missions;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering;
using EliteSharp.Sound;

namespace EliteSharp.Game;

/// <summary>
/// Thrown to jump to a label that resets the 6502 stack in the original (such as
/// TT170, DEATH2 or QU5), or to one that is reached with a JMP from deep inside
/// the call stack and eventually ends up there (such as BAY, which forces a key
/// press into the main loop). Unwinding the C# call stack with an exception is
/// the equivalent of the original's stack reset.
/// </summary>
internal sealed class GameJumpException(GameJump target, int key = 0) : Exception
{
    public GameJump Target { get; } = target;

    /// <summary>For <see cref="GameJump.ForceKey"/>, the key to process (FRCE).</summary>
    public int Key { get; } = key;
}

internal enum GameJump
{
    /// <summary>TT170: start (or restart) the game.</summary>
    StartGame,

    /// <summary>DEATH2: reset and show the title screens after dying.</summary>
    RestartAfterDeath,

    /// <summary>QU5: load the default commander and show the second title screen.</summary>
    LoadDefaultCommander,

    /// <summary>FRCE: jump into the main loop to process the key in A.</summary>
    ForceKey,

    /// <summary>MLOOP: jump to the part of the main loop that reads the keyboard.</summary>
    MainLoop,
}

/// <summary>
/// The game itself: a port of the BBC Master version of Elite. The game runs on
/// its own thread as a straightforward imperative program, just like the 6502
/// original, and it hands frames to the renderer at the points where the
/// original would have finished drawing something (such as when waiting for the
/// vertical sync, or at the end of each iteration of the main loop).
///
/// The class is split across several files, roughly following the categories
/// used in the original source.
///
/// Each member's documentation starts with the label of the routine or
/// variable it was ported from (such as "TT26:" or "INWK:"), and comments
/// refer to the original's labels in capitals, so the code can be checked
/// against the annotated source line by line.
/// </summary>
public sealed partial class EliteGame
{
    private readonly Hud _hud;
    private readonly BbcKeyboard _keyboard;
    private readonly SoundEngine? _sound;
    private readonly GameOptions _options;
    private readonly IGameClock _clock;
    private readonly FixedRateTimer _vsyncTimer;
    private readonly FixedRateTimer _mainLoopTimer;
    private volatile bool _quit;

    /// <summary>
    /// Create the game. The missions are loaded from Assets/Missions, and the
    /// game's text and system descriptions from Assets/Strings (in the language
    /// in the options), and the markets and equipment prices from
    /// Assets/trading.yml (or the mod's, see <see cref="GameAssets"/>), unless
    /// they are given. The game is paced by the real clock unless it is given
    /// another one.
    /// </summary>
    public EliteGame(
        Hud hud,
        BbcKeyboard keyboard,
        SoundEngine? sound,
        GameOptions options,
        Gamepad? gamepad = null,
        MissionCatalogue? missions = null,
        GameStrings? strings = null,
        DescriptionGrammar? descriptions = null,
        TradingData? trading = null,
        IGameClock? clock = null)
    {
        _clock = clock ?? RealTimeClock.Instance;
        _vsyncTimer = new FixedRateTimer(_clock);
        _mainLoopTimer = new FixedRateTimer(_clock);
        _missions = new MissionRuntime(missions ?? MissionLoader.LoadGame());
        _strings = strings ?? GameStrings.Load(options.Language, HudAtlas.InFont);
        _descriptions = descriptions ?? DescriptionGrammar.Load(options.Language);
        _trading = trading ?? TradingData.Load();
        CheckTradingNames(_trading);
        _gamepad = gamepad;
        _hud = hud;
        _keyboard = keyboard;
        _sound = sound;
        _options = options;
        _hud.DashboardRenderer = DrawDashboard;
        ConnectWorld();

        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i] = null;
        }
    }

    /// <summary>Ask the game thread to stop at the next frame boundary.</summary>
    public void Quit() => _quit = true;

    private sealed class QuitException : Exception;

    // ------------------------------------------------------------------------
    // Configuration constants
    // ------------------------------------------------------------------------

    /// <summary>NOST: the number of stardust particles in normal space.</summary>
    private const int NormalStardustCount = 20;

    /// <summary>NOSH: the maximum number of ships in the local bubble.</summary>
    private const int MaxShips = 12;

    /// <summary>POW: pulse laser power.</summary>
    private const int PulseLaserPower = 15;

    /// <summary>Mlas: mining laser power.</summary>
    private const int MiningLaserPower = 50;

    /// <summary>Armlas: military laser power (with bit 7 set to indicate a beam laser).</summary>
    private const int MilitaryLaserPower = 151; // INT(128.5 + 1.5 * POW)

    /// <summary>X: the x-coordinate of the centre of the space view.</summary>
    private const int CentreX = 128;

    /// <summary>Y: the y-coordinate of the centre of the space view.</summary>
    private const int CentreY = 96;

    /// <summary>GCYT: the y-coordinate of the top of the galactic chart.</summary>
    private const int GalacticChartTop = 24;

    /// <summary>GCYB: the y-coordinate of the bottom of the galactic chart.</summary>
    private const int GalacticChartBottom = GalacticChartTop + 128;

    // The codes that ReadKey returns (via TRTB%) for the red function keys f0-f9

    /// <summary>f0: launch, or the front space view.</summary>
    private const int FunctionKey0 = 0x80;

    /// <summary>f1: buy cargo, or the rear space view.</summary>
    private const int FunctionKey1 = 0x81;

    /// <summary>f2: sell cargo, or the left space view.</summary>
    private const int FunctionKey2 = 0x82;

    /// <summary>f3: equip ship, or the right space view.</summary>
    private const int FunctionKey3 = 0x83;

    /// <summary>f4: the long-range (galactic) chart.</summary>
    private const int FunctionKey4 = 0x84;

    /// <summary>f5: the short-range chart.</summary>
    private const int FunctionKey5 = 0x85;

    /// <summary>f6: data on the selected system.</summary>
    private const int FunctionKey6 = 0x86;

    /// <summary>f7: market prices.</summary>
    private const int FunctionKey7 = 0x87;

    /// <summary>f8: the Status Mode screen.</summary>
    private const int FunctionKey8 = 0x88;

    /// <summary>f9: the inventory.</summary>
    private const int FunctionKey9 = 0x89;

    // The colours the original draws with in the space view (see Ink)

    /// <summary>YELLOW: yellow in the space view.</summary>
    private const Ink Yellow = Ink.Yellow;

    /// <summary>RED: red in the space view.</summary>
    private const Ink Red = Ink.Red;

    /// <summary>CYAN: cyan in the space view.</summary>
    private const Ink Cyan = Ink.Cyan;

    /// <summary>GREEN: green in the space view (cyan and yellow stripes).</summary>
    private const Ink Green = Ink.Green;

    /// <summary>WHITE: white in the space view (cyan and red stripes).</summary>
    private const Ink White = Ink.White;

    /// <summary>MAGENTA: magenta in the space view (the space view's red, which the trading screens' palette shows as magenta).</summary>
    private const Ink Magenta = Ink.Red;

    /// <summary>DUST: the colour of the stardust.</summary>
    private const Ink DustColour = Ink.White;

    // The colours the original draws with on the dashboard

    /// <summary>RED2: red on the dashboard.</summary>
    private const Ink DashboardRed = Ink.DashboardRed;

    /// <summary>GREEN2: green on the dashboard.</summary>
    private const Ink DashboardGreen = Ink.DashboardGreen;

    /// <summary>YELLOW2: yellow on the dashboard.</summary>
    private const Ink DashboardYellow = Ink.DashboardYellow;

    /// <summary>BLUE2: blue on the dashboard.</summary>
    private const Ink DashboardBlue = Ink.DashboardBlue;

    /// <summary>MAG2: magenta on the dashboard.</summary>
    private const Ink DashboardMagenta = Ink.DashboardMagenta;

    /// <summary>CYAN2: cyan on the dashboard.</summary>
    private const Ink DashboardCyan = Ink.DashboardCyan;

    /// <summary>WHITE2: white on the dashboard.</summary>
    private const Ink DashboardWhite = Ink.DashboardWhite;

    /// <summary>STRIPE: magenta and red stripes on the dashboard.</summary>
    private const Ink DashboardStripe = Ink.DashboardStripes;

    /// <summary>coltabl: the colours of explosion particles.</summary>
    private static readonly Ink[] ExplosionColours = [Yellow, Red, Yellow, Cyan];

    /// <summary>sightcol: the colours of the laser crosshairs for pulse, beam, military and mining lasers.</summary>
    private static readonly Ink[] SightColours = [Yellow, Cyan, Cyan, Yellow];

    // Sound effect numbers, as passed to MakeSound

    /// <summary>soboop: a long, low beep.</summary>
    private const int SoundBoop = 0;

    /// <summary>sobeep: a short, high beep.</summary>
    private const int SoundBeep = 1;

    /// <summary>soclick: a click.</summary>
    private const int SoundClick = 2;

    /// <summary>solaser: our laser firing (first part).</summary>
    private const int SoundLaser = 3;

    /// <summary>soexpl: an explosion.</summary>
    private const int SoundExplosion = 4;

    /// <summary>solas2: the second part of the sound of a laser firing (ours or an enemy's).</summary>
    private const int SoundLaser2 = 5;

    /// <summary>sohit: a laser strike on another ship.</summary>
    private const int SoundHit = 6;

    /// <summary>sobomb: the energy bomb (the same sound as a laser strike).</summary>
    private const int SoundBomb = 6;

    /// <summary>soecm: the E.C.M.</summary>
    private const int SoundEcm = 7;

    /// <summary>solaun: launching from or docking with the space station.</summary>
    private const int SoundLaunch = 8;

    /// <summary>The first part of the sound of us being hit by lasers, which ELASNO makes (the original has no name for it).</summary>
    private const int SoundHitUs = 9;

    /// <summary>sohyp: hyperspace (first part).</summary>
    private const int SoundHyperspace = 10;

    /// <summary>sohyp2: hyperspace (second part).</summary>
    private const int SoundHyperspace2 = 11;

    // ------------------------------------------------------------------------
    // The local bubble of universe
    // ------------------------------------------------------------------------

    /// <summary>
    /// FRIN and K%: the ship slots. Slot 0 is the planet, slot 1 is the sun or
    /// the space station, and the rest are ships. There is always an empty slot
    /// at the end (FRIN+NOSH) to terminate the list.
    /// </summary>
    private readonly Ship?[] Slots = new Ship?[MaxShips + 1];

    /// <summary>FRIN: the type of the ship in a slot, or 0 if the slot is empty.</summary>
    private int SlotType(int slot) => Slots[slot]?.Type ?? 0;

    /// <summary>MANY: the number of ships of each type in the bubble.</summary>
    private readonly int[] _shipCounts = new int[ShipType.Count + 1];

    /// <summary>SSPR: non-zero if we are inside the space station's safe zone.</summary>
    private int InSafeZone
    {
        get => _shipCounts[ShipType.SpaceStation];
        set => _shipCounts[ShipType.SpaceStation] = value;
    }

    /// <summary>JUNK: the amount of junk (asteroids, canisters and so on) in the bubble.</summary>
    private int _junkCount;

    /// <summary>
    /// The planet, which lives in slot 0 (K%). If the slot is empty, the
    /// original would read whatever data was left in K%, so we return a
    /// block of zeroes instead.
    /// </summary>
    private Ship Planet => Slots[0] ?? _emptySlot;

    /// <summary>An empty data block returned by <see cref="Planet"/> when slot 0 is empty.</summary>
    private readonly Ship _emptySlot = Ship.Workspace();

    /// <summary>
    /// INWK: the ship we are currently working with. The original copies each
    /// ship's data block into INWK; here INWK refers to the ship object itself,
    /// or to a copy where the original's copy diverges from the stored data.
    /// </summary>
    private Ship _currentShip = null!;

    /// <summary>INF: the slot number of the ship in INWK (the original stores its address).</summary>
    private Ship? _slotShip;

    /// <summary>TYPE: the type of the ship in INWK.</summary>
    private int _shipType;

    /// <summary>XX0: the blueprint of the ship in INWK.</summary>
    private ShipBlueprint? _blueprint;

    /// <summary>XSAV: the slot number being processed in the main flight loop.</summary>
    private int _currentSlot;

    // ------------------------------------------------------------------------
    // Flight state
    // ------------------------------------------------------------------------

    /// <summary>DELTA: our current speed (1-40).</summary>
    private int _speed;

    /// <summary>ALPHA: our roll angle this iteration, in steps of 1/256 radian (-31 to +31).</summary>
    private int _roll;

    /// <summary>BETA: our pitch angle this iteration, in steps of 1/256 radian (-8 to +8).</summary>
    private int _pitch;

    /// <summary>
    /// JSTX and JSTY: the current roll and pitch rates (128 = centre). These
    /// aren't initialised by the game code (the loader leaves them centred).
    /// </summary>
    private int _rollRate = 128, _pitchRate = 128;

    /// <summary>ENERGY: our energy banks (0-255).</summary>
    private int _energy;

    /// <summary>FSH and ASH: forward and aft shields.</summary>
    private int _forwardShield, _aftShield;

    /// <summary>CABTMP: cabin temperature.</summary>
    private int _cabinTemperature;

    /// <summary>GNTMP: laser temperature.</summary>
    private int _laserTemperature;

    /// <summary>ALTIT: our altitude above the planet.</summary>
    private int _altitude;

    /// <summary>LAS: the laser power of the current view's laser if it is firing this iteration.</summary>
    private int _firingLaserPower;

    /// <summary>LAS2: the laser power of the laser beam currently on-screen.</summary>
    private int _laserBeamPower;

    /// <summary>
    /// Whether the laser lines were due to be erased in the same iteration
    /// that drew them, so they are erased at the start of the next one
    /// instead (see the main flight loop's part 16).
    /// </summary>
    private bool _laserBeamsErasePending;

    /// <summary>Whether the laser lines were drawn in this iteration of the main flight loop.</summary>
    private bool _laserBeamsDrawnThisIteration;

    /// <summary>LASCT: the laser pulse counter.</summary>
    private int _laserPulseCounter;

    /// <summary>LASX and LASY: the screen coordinates of the laser beam's end point.</summary>
    private int _laserEndX, _laserEndY;

    /// <summary>MSAR: non-zero if the missile is armed and looking for a target.</summary>
    private bool _missileArmed;

    /// <summary>MSTG: the slot number of the current missile target, or &amp;FF for none.</summary>
    private int _missileTarget;

    /// <summary>ECMA: the E.C.M. counter (non-zero while an E.C.M. is active).</summary>
    private int _ecmCounter;

    /// <summary>ECMP: non-zero if our E.C.M. is the active one.</summary>
    private int _ourEcmActive;

    /// <summary>MJ: non-zero if we are in witchspace.</summary>
    private int _inWitchspace;

    /// <summary>auto: non-zero if the docking computer is engaged.</summary>
    private int _autoDocking;

    /// <summary>VIEW: the current space view (0 = front, 1 = rear, 2 = left, 3 = right).</summary>
    private int _view;

    /// <summary>QQ11: the type of the current view (0 = space view).</summary>
    private int _viewType;

    /// <summary>QQ12: non-zero if we are docked.</summary>
    private int _docked;

    /// <summary>MCNT: the main loop counter.</summary>
    private int _mainLoopCounter;

    /// <summary>DLY: the in-flight message delay counter.</summary>
    private int _messageDelay;

    /// <summary>de: bit 1 set means append " DESTROYED" to the in-flight message.</summary>
    private int _messageDestroyed;

    /// <summary>
    /// MCH: the current in-flight message (the original stores its token
    /// number, which starts at 0, the token that prints our cash).
    /// </summary>
    private string _messageKey = "messages.bounty";

    /// <summary>messXC: the x-coordinate of the current in-flight message.</summary>
    private int _messageX;

    /// <summary>EV: the extra vessels spawning counter.</summary>
    private int _extraVesselsDelay;

    /// <summary>NOSTM: the number of stardust particles.</summary>
    private int _stardustCount;

    /// <summary>HFX: non-zero while the hyperspace colour effect is on.</summary>
    private int HyperspaceColoursOn
    {
        get => _hud.HyperspaceColours ? 1 : 0;
        set => _hud.HyperspaceColours = value != 0;
    }

    /// <summary>
    /// QQ22: the inner hyperspace countdown, which counts down from 5 (or 15
    /// at the start) between each tick of <see cref="_hyperspaceCountdown"/>.
    /// </summary>
    private int _hyperspaceTicks;

    /// <summary>QQ22+1: the hyperspace countdown shown on-screen, or 0 if no countdown is in progress.</summary>
    private int _hyperspaceCountdown;

    // The key logger, which records which keys are being pressed

    /// <summary>KL: the ASCII code (via TRTB%) of the last key pressed, or 0 for none.</summary>
    private int _keyPressed;

    /// <summary>KY1: "?" is being pressed (slow down).</summary>
    private bool _keySlowDown;

    /// <summary>KY2: Space is being pressed (speed up).</summary>
    private bool _keySpeedUp;

    /// <summary>KY3: "&lt;" is being pressed (roll left).</summary>
    private bool _keyRollLeft;

    /// <summary>KY4: "&gt;" is being pressed (roll right).</summary>
    private bool _keyRollRight;

    /// <summary>KY5: "X" is being pressed (pull up, or climb).</summary>
    private bool _keyClimb;

    /// <summary>KY6: "S" is being pressed (pitch down, or dive).</summary>
    private bool _keyDive;

    /// <summary>KY7: "A" is being pressed (fire lasers).</summary>
    private bool _keyFireLaser;

    /// <summary>KY12: Tab is being pressed (energy bomb).</summary>
    private bool _keyEnergyBomb;

    /// <summary>KY13: Escape is being pressed (launch the escape pod).</summary>
    private bool _keyEscapePod;

    /// <summary>KY14: "T" is being pressed (target a missile).</summary>
    private bool _keyTargetMissile;

    /// <summary>KY15: "U" is being pressed (unarm the missile).</summary>
    private bool _keyUnarmMissile;

    /// <summary>KY16: "M" is being pressed (fire the missile).</summary>
    private bool _keyFireMissile;

    /// <summary>KY17: "E" is being pressed (fire the E.C.M.).</summary>
    private bool _keyEcm;

    /// <summary>KY18: "J" is being pressed (in-system jump).</summary>
    private bool _keyJump;

    /// <summary>KY19: "C" is being pressed (turn on the docking computer).</summary>
    private bool _keyDockingComputerOn;

    /// <summary>KY20: "P" is being pressed (turn off the docking computer).</summary>
    private bool _keyDockingComputerOff;

    // The stardust particles (index 1 to _stardustCount). Each particle is at
    // a point on the screen (in pixels from the centre, with y up) and at a
    // distance from us

    /// <summary>SX: the x-coordinate of each stardust particle.</summary>
    private readonly float[] _dustX = new float[NormalStardustCount + 1];

    /// <summary>SY: the y-coordinate of each stardust particle.</summary>
    private readonly float[] _dustY = new float[NormalStardustCount + 1];

    /// <summary>SZ: the distance of each stardust particle.</summary>
    private readonly float[] _dustZ = new float[NormalStardustCount + 1];

    /// <summary>
    /// The number of times each stardust particle has been recycled (which
    /// isn't in the original). It goes into the particle's id in the 3D world,
    /// so the renderer doesn't move a recycled particle smoothly from where it
    /// was to where it starts again.
    /// </summary>
    private readonly int[] _dustRecycles = new int[NormalStardustCount + 1];

    /// <summary>
    /// For each stardust particle that was recycled in the latest move, where
    /// it would have been a move earlier had it been there all along (which
    /// isn't in the original), or null. The renderer moves it in from there,
    /// so it carries on like the rest of the stardust rather than sitting
    /// still for a move.
    /// </summary>
    private readonly (float X, float Y, float Z)?[] _dustEntries = new (float, float, float)?[NormalStardustCount + 1];

    // ------------------------------------------------------------------------
    // Universe and commander state
    // ------------------------------------------------------------------------

    /// <summary>NAME: the commander's name.</summary>
    private string CommanderName = DefaultCommander.Name;

    /// <summary>TP: the mission status.</summary>
    private int _missionStatus;

    /// <summary>QQ0 and QQ1: our current galactic coordinates.</summary>
    private int _currentSystemX, _currentSystemY;

    /// <summary>QQ21: the three 16-bit seeds for the current galaxy.</summary>
    private readonly int[] _galaxySeeds = new int[6];

    /// <summary>CASH: our cash in Cr * 10.</summary>
    private uint _cash;

    /// <summary>QQ14: our fuel level in light years * 10.</summary>
    private int _fuel;

    /// <summary>COK: the competition flags.</summary>
    private int _competitionFlags;

    /// <summary>GCNT: the current galaxy number (0-7).</summary>
    private int _galaxyNumber;

    /// <summary>LASER: the laser power for each of the four views (0 = none).</summary>
    private readonly int[] _lasers = new int[4];

    /// <summary>CRGO: our cargo capacity.</summary>
    private int _cargoCapacity;

    /// <summary>QQ20: the contents of the cargo hold.</summary>
    private readonly int[] _cargo = new int[17];

    /// <summary>Equipment flags.</summary>
    private int _ecm, _fuelScoops, _energyBomb, _energyUnit, _dockingComputer, _galacticHyperdrive, _escapePod;

    /// <summary>TALLYL: the fractional part of the kill tally.</summary>
    private int _killTallyFraction;

    /// <summary>NOMSL: the number of missiles we have.</summary>
    private int _missiles;

    /// <summary>FIST: our legal status.</summary>
    private int _legalStatus;

    /// <summary>AVL: the market availability for each item.</summary>
    private readonly int[] _marketAvailability = new int[17];

    /// <summary>QQ26: the random byte that changes the market on each visit.</summary>
    private int _marketRandom;

    /// <summary>TALLY: the number of kills.</summary>
    private int _killTally;

    /// <summary>SVC: the save count.</summary>
    private int _saveCount;

    /// <summary>QQ2: the seeds of the current system.</summary>
    private readonly int[] _currentSystemSeeds = new int[6];

    /// <summary>safehouse: the seeds of the system we are jumping to.</summary>
    private readonly int[] _destinationSeeds = new int[6];

    /// <summary>QQ3, QQ4, QQ5, QQ6, QQ7: the economy, government, tech level, population and productivity of the selected system.</summary>
    private int _selectedEconomy, _selectedGovernment, _selectedTechLevel, _selectedPopulation, _selectedProductivity;

    /// <summary>QQ8: the distance to the selected system in light years * 10.</summary>
    private int _selectedDistance;

    /// <summary>QQ9 and QQ10: the galactic coordinates of the crosshairs on the charts.</summary>
    private int _crosshairX, _crosshairY;

    /// <summary>QQ15: the three 16-bit seeds of the selected system.</summary>
    private readonly int[] _selectedSeeds = new int[6];

    /// <summary>QQ19: temporary storage (used for seeds and crosshair coordinates).</summary>
    private readonly int[] _scratch = new int[6];

    /// <summary>QQ24, QQ25, QQ28, QQ29: market price, availability, current economy and item number.</summary>
    private int _itemPrice, _itemAvailability, _currentEconomy, _itemNumber;

    /// <summary>gov: the current system's government type (0-7).</summary>
    private int _government;

    /// <summary>tek: the current system's tech level (0-14).</summary>
    private int _techLevel;

    // ------------------------------------------------------------------------
    // Configuration options (toggled while paused)
    // ------------------------------------------------------------------------

    /// <summary>DAMP, DJD, PATG, FLH, JSTGY, JSTE, JSTK, UPTOG, DISK: the toggle options, indexed as in TGINT.</summary>
    private readonly int[] ToggleOptions = new int[9];

    /// <summary>DAMP: non-zero if keyboard damping is disabled (toggled with Caps Lock while paused).</summary>
    private int DampingDisabled => ToggleOptions[0];

    /// <summary>DJD: non-zero if keyboard auto-recentre is disabled (toggled with "A" while paused).</summary>
    private int AutoRecentreDisabled => ToggleOptions[1];

    /// <summary>
    /// PATG: non-zero to show the authors' names on the title screen and allow
    /// manual mis-jumps into witchspace (toggled with "X" while paused).
    /// </summary>
    private int AuthorNamesShown => ToggleOptions[2];

    /// <summary>FLH: non-zero if the dashboard bars flash when in danger (toggled with "F" while paused).</summary>
    private int FlashingBars => ToggleOptions[3];

    /// <summary>DNOIZ: non-zero if sound is disabled.</summary>
    private int _soundDisabled;

    /// <summary>VOL: the sound volume (0-7).</summary>
    private int _volume = 7;

    /// <summary>DISK: toggled with "T" while paused (selects which file system name is shown).</summary>
    private int FilingSystemToggle
    {
        get => ToggleOptions[8] != 0 ? 1 : 0;
        set => ToggleOptions[8] = value != 0 ? 0xFF : 0;
    }

    /// <summary>
    /// JSTK: non-zero if the joystick is configured (toggled with "K" while
    /// paused), in which case the controller's left stick is always in control.
    /// </summary>
    private int JoystickEnabled
    {
        get => ToggleOptions[6];
        set => ToggleOptions[6] = value & 0xFF;
    }

    // ------------------------------------------------------------------------
    // The main entry point for the game thread
    // ------------------------------------------------------------------------

    /// <summary>Run the game (S% and BEGIN in the original). This never returns until the game is quit.</summary>
    public void Run()
    {
        try
        {
            _sound?.SetVolumeSource(() => _soundDisabled != 0 ? -1 : _volume);
            ApplyEffectsVolume();
            ApplyMusicVolume();
            Begin();
            GameJump next = GameJump.StartGame;
            int key = 0;
            while (true)
            {
                try
                {
                    switch (next)
                    {
                        case GameJump.StartGame:
                            StartGame();
                            break;
                        case GameJump.RestartAfterDeath:
                            RestartAfterDeath();
                            break;
                        case GameJump.LoadDefaultCommander:
                            LoadDefaultCommander();
                            break;
                        case GameJump.ForceKey:
                            MainLoop(key);
                            break;
                        case GameJump.MainLoop:
                            MainLoop(null);
                            break;
                    }
                }
                catch (GameJumpException jump)
                {
                    next = jump.Target;
                    key = jump.Key;
                }
            }
        }
        catch (QuitException)
        {
        }
    }

    // ------------------------------------------------------------------------
    // Timing
    // ------------------------------------------------------------------------

    /// <summary>The time between vertical syncs (the BBC's screen refreshes at 50 Hz).</summary>
    private static readonly long VsyncTicks = Stopwatch.Frequency / 50;

    /// <summary>
    /// Send the current screen contents to the renderer, saying when the game
    /// expects to send the next frame (so the renderer can move smoothly from
    /// this frame to the next in the meantime).
    /// </summary>
    private void Present(long nextFrameTime)
    {
        if (_quit)
        {
            throw new QuitException();
        }

        _hud.EscapePodFitted = _escapePod != 0;
        _hud.Present(_clock.Now, nextFrameTime);
    }

    /// <summary>WSCAN: wait for the vertical sync (the original runs at 50 Hz).</summary>
    private void WaitForVsync()
    {
        long next = _vsyncTimer.Schedule(VsyncTicks);
        Present(next);
        _clock.SleepUntil(next);
    }

    /// <summary>DELAY: wait for Y vertical syncs.</summary>
    private void Delay(int y)
    {
        do
        {
            WaitForVsync();
            y = (y - 1) & 0xFF;
        }
        while (y != 0);
    }

    /// <summary>
    /// The original main loop runs as fast as the 6502 can manage, so here we
    /// limit the rate of the main loop to the configured simulation rate, which
    /// gives a similar game speed to the original. Each iteration moves the
    /// game on by one fixed step, however often the renderer draws.
    /// </summary>
    private void ThrottleMainLoop()
    {
        long next = _mainLoopTimer.Schedule(Stopwatch.Frequency / Math.Max(1, _options.MainLoopRate));
        Present(next);
        _clock.SleepUntil(next);
    }

    /// <summary>Send the screen to the renderer and pause for a number of milliseconds.</summary>
    private void PresentAndPause(int milliseconds)
    {
        long until = _clock.Now + milliseconds * Stopwatch.Frequency / 1000;
        Present(until);
        _clock.SleepUntil(until);
    }

    // ------------------------------------------------------------------------
    // Random numbers
    // ------------------------------------------------------------------------

    /// <summary>
    /// RAND: the four-byte random number seed. On the BBC this lives in zero
    /// page and starts off containing whatever the loader left behind, and the
    /// generator is also stirred by the state of the C flag on entry to DORND,
    /// so we seed it randomly (an all-zero seed would never change).
    /// </summary>
    private readonly int[] _randomSeeds = [.. Enumerable.Range(0, 4).Select(_ => Random.Shared.Next(1, 256))];

    /// <summary>The C flag as left by DORND, which feeds into the next call.</summary>
    private bool _carry;

    /// <summary>The V flag as left by the last call to DORND.</summary>
    private bool _overflow;

    /// <summary>The value returned in X by the last call to DORND.</summary>
    private int _randomX;

    /// <summary>
    /// DORND: generate a random number in A (returned) and X (in <see cref="_randomX"/>),
    /// setting the C and V flags. The C flag on entry feeds into the result, as in
    /// the original.
    /// </summary>
    private int NextRandom()
    {
        int seed = _randomSeeds[0];
        int carryIn = _carry ? 1 : 0;
        int rolled = ((seed << 1) | carryIn) & 0xFF;
        int carry = (seed >> 7) & 1;
        int previousSeed = rolled;
        int sum = rolled + _randomSeeds[2] + carry;
        _randomSeeds[0] = sum & 0xFF;
        _randomSeeds[2] = previousSeed;
        carry = sum > 0xFF ? 1 : 0;

        seed = _randomSeeds[1];
        previousSeed = seed;
        sum = seed + _randomSeeds[3] + carry;
        int result = sum & 0xFF;
        _overflow = ((~(seed ^ _randomSeeds[3]) & (seed ^ result)) & 0x80) != 0;
        _carry = sum > 0xFF;
        _randomSeeds[1] = result;
        _randomSeeds[3] = previousSeed;
        _randomX = previousSeed;
        return result;
    }

    /// <summary>DORND2: DORND with the C flag cleared first, so the sequence is repeatable.</summary>
    private int NextRandomRepeatable()
    {
        _carry = false;
        return NextRandom();
    }
}

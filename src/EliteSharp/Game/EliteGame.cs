using System.Diagnostics;
using EliteSharp.Data;
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
    TT170,

    /// <summary>DEATH2: reset and show the title screens after dying.</summary>
    Death2,

    /// <summary>QU5: load the default commander and show the second title screen.</summary>
    QU5,

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
/// </summary>
public sealed partial class EliteGame
{
    private readonly Screen _screen;
    private readonly BbcKeyboard _keyboard;
    private readonly SoundEngine? _sound;
    private readonly GameOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextVsyncTicks;
    private long _nextMainLoopTicks;
    private volatile bool _quit;

    public EliteGame(Screen screen, BbcKeyboard keyboard, SoundEngine? sound, GameOptions options, Gamepad? gamepad = null)
    {
        _gamepad = gamepad;
        _screen = screen;
        _keyboard = keyboard;
        _sound = sound;
        _options = options;
        _screen.DashboardRenderer = DrawDashboard;

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
    private const int NOST = 20;

    /// <summary>NOSH: the maximum number of ships in the local bubble.</summary>
    private const int NOSH = 12;

    /// <summary>POW: pulse laser power.</summary>
    private const int POW = 15;

    /// <summary>Mlas: mining laser power.</summary>
    private const int Mlas = 50;

    /// <summary>Armlas: military laser power (with bit 7 set to indicate a beam laser).</summary>
    private const int Armlas = 151; // INT(128.5 + 1.5 * POW)

    /// <summary>X and Y: the centre of the space view.</summary>
    private const int CentreX = 128;
    private const int CentreY = 96;

    /// <summary>GCYT and GCYB: the top and bottom of the galactic chart.</summary>
    private const int GCYT = 24;
    private const int GCYB = GCYT + 128;

    // Function key codes, as returned by RDKEY via TRTB%
    private const int f0 = 0x80, f1 = 0x81, f2 = 0x82, f3 = 0x83, f4 = 0x84;
    private const int f5 = 0x85, f6 = 0x86, f7 = 0x87, f8 = 0x88, f9 = 0x89;

    // Mode 1 colour bytes (space view)
    private const int YELLOW = 0b00001111;
    private const int RED = 0b11110000;
    private const int CYAN = 0b11111111;
    private const int GREEN = 0b10101111;
    private const int WHITE = 0b11111010;
    private const int MAGENTA = RED;
    private const int DUST = WHITE;

    // Mode 2 colour bytes (dashboard)
    private const int RED2 = 0b00000011;
    private const int GREEN2 = 0b00001100;
    private const int YELLOW2 = 0b00001111;
    private const int BLUE2 = 0b00110000;
    private const int MAG2 = 0b00110011;
    private const int CYAN2 = 0b00111100;
    private const int WHITE2 = 0b00111111;
    private const int STRIPE = 0b00100011;

    // Sound effect numbers
    private const int soboop = 0, sobeep = 1, soclick = 2, solaser = 3, soexpl = 4;
    private const int solas2 = 5, sohit = 6, sobomb = 6, soecm = 7, solaun = 8;
    private const int sohyp = 10, sohyp2 = 11;

    // ------------------------------------------------------------------------
    // The local bubble of universe
    // ------------------------------------------------------------------------

    /// <summary>
    /// FRIN and K%: the ship slots. Slot 0 is the planet, slot 1 is the sun or
    /// the space station, and the rest are ships. There is always an empty slot
    /// at the end (FRIN+NOSH) to terminate the list.
    /// </summary>
    private readonly Ship?[] Slots = new Ship?[NOSH + 1];

    /// <summary>FRIN: the type of the ship in a slot, or 0 if the slot is empty.</summary>
    private int SlotType(int slot) => Slots[slot]?.Type ?? 0;

    /// <summary>MANY: the number of ships of each type in the bubble.</summary>
    private readonly int[] Many = new int[ShipType.Count + 1];

    /// <summary>SSPR: non-zero if we are inside the space station's safe zone.</summary>
    private int SSPR
    {
        get => Many[ShipType.SpaceStation];
        set => Many[ShipType.SpaceStation] = value;
    }

    /// <summary>JUNK: the amount of junk (asteroids, canisters and so on) in the bubble.</summary>
    private int Junk;

    /// <summary>
    /// The planet, which lives in slot 0 (K%). If the slot is empty, the
    /// original would read whatever data was left in K%, so we return a
    /// block of zeroes instead.
    /// </summary>
    private Ship Planet => Slots[0] ?? _emptySlot;

    private readonly WorkspaceShip _emptySlot = new();

    /// <summary>
    /// INWK: the ship we are currently working with. The original copies each
    /// ship's data block into INWK; here INWK refers to the ship object itself,
    /// or to a copy where the original's copy diverges from the stored data.
    /// </summary>
    private Ship INWK = null!;

    /// <summary>INF: the slot number of the ship in INWK (the original stores its address).</summary>
    private Ship? INF;

    /// <summary>TYPE: the type of the ship in INWK.</summary>
    private int TYPE;

    /// <summary>XX0: the blueprint of the ship in INWK.</summary>
    private ShipBlueprint? XX0;

    /// <summary>XSAV: the slot number being processed in the main flight loop.</summary>
    private int XSAV;

    // ------------------------------------------------------------------------
    // Flight state
    // ------------------------------------------------------------------------

    /// <summary>DELTA: our current speed (1-40).</summary>
    private int DELTA;

    /// <summary>DELT4: our speed * 64 as a 16-bit value (DELT4+1 is the high byte).</summary>
    private int DELT4;

    /// <summary>ALPHA: the roll angle as a sign-magnitude byte.</summary>
    private int ALPHA;

    /// <summary>ALP1: the magnitude of the roll angle (0-31).</summary>
    private int ALP1;

    /// <summary>ALP2 and ALP2+1: the sign of the roll angle, and its flipped sign.</summary>
    private int ALP2, ALP2Flipped;

    /// <summary>BETA: the pitch angle as a sign-magnitude byte.</summary>
    private int BETA;

    /// <summary>BET1: the magnitude of the pitch angle (0-8).</summary>
    private int BET1;

    /// <summary>BET2 and BET2+1: the sign of the pitch angle, and its flipped sign.</summary>
    private int BET2, BET2Flipped;

    /// <summary>
    /// JSTX and JSTY: the current roll and pitch rates (128 = centre). These
    /// aren't initialised by the game code (the loader leaves them centred).
    /// </summary>
    private int JSTX = 128, JSTY = 128;

    /// <summary>ENERGY: our energy banks (0-255).</summary>
    private int ENERGY;

    /// <summary>FSH and ASH: forward and aft shields.</summary>
    private int FSH, ASH;

    /// <summary>CABTMP: cabin temperature.</summary>
    private int CABTMP;

    /// <summary>GNTMP: laser temperature.</summary>
    private int GNTMP;

    /// <summary>ALTIT: our altitude above the planet.</summary>
    private int ALTIT;

    /// <summary>LAS: the laser power of the current view's laser if it is firing this iteration.</summary>
    private int LAS;

    /// <summary>LAS2: the laser power of the laser beam currently on-screen.</summary>
    private int LAS2;

    /// <summary>LASCT: the laser pulse counter.</summary>
    private int LASCT;

    /// <summary>LASX and LASY: the screen coordinates of the laser beam's end point.</summary>
    private int LASX, LASY;

    /// <summary>MSAR: non-zero if the missile is armed and looking for a target.</summary>
    private int MSAR;

    /// <summary>MSTG: the slot number of the current missile target, or &amp;FF for none.</summary>
    private int MSTG;

    /// <summary>ECMA: the E.C.M. counter (non-zero while an E.C.M. is active).</summary>
    private int ECMA;

    /// <summary>ECMP: non-zero if our E.C.M. is the active one.</summary>
    private int ECMP;

    /// <summary>MJ: non-zero if we are in witchspace.</summary>
    private int MJ;

    /// <summary>auto: non-zero if the docking computer is engaged.</summary>
    private int Auto;

    /// <summary>VIEW: the current space view (0 = front, 1 = rear, 2 = left, 3 = right).</summary>
    private int VIEW;

    /// <summary>QQ11: the type of the current view (0 = space view).</summary>
    private int QQ11;

    /// <summary>QQ12: non-zero if we are docked.</summary>
    private int QQ12;

    /// <summary>MCNT: the main loop counter.</summary>
    private int MCNT;

    /// <summary>DLY: the in-flight message delay counter.</summary>
    private int DLY;

    /// <summary>de: bit 1 set means append " DESTROYED" to the in-flight message.</summary>
    private int de;

    /// <summary>MCH: the token number of the current in-flight message.</summary>
    private int MCH;

    /// <summary>messXC: the x-coordinate of the current in-flight message.</summary>
    private int messXC;

    /// <summary>EV: the extra vessels spawning counter.</summary>
    private int EV;

    /// <summary>NOSTM: the number of stardust particles.</summary>
    private int NOSTM;

    /// <summary>HFX: non-zero while the hyperspace colour effect is on.</summary>
    private int HFX
    {
        get => _screen.HyperspaceColours ? 1 : 0;
        set => _screen.HyperspaceColours = value != 0;
    }

    /// <summary>QQ22 and QQ22+1: the hyperspace countdown timers.</summary>
    private int QQ22, QQ22Hi;

    /// <summary>KL and the KY flags: the keyboard logger.</summary>
    private int KL;
    private bool KY1, KY2, KY3, KY4, KY5, KY6, KY7, KY12, KY13, KY14, KY15, KY16, KY17, KY18, KY19, KY20;

    // Stardust (SX, SY, SZ and their low bytes)
    private readonly int[] SX = new int[NOST + 1];
    private readonly int[] SY = new int[NOST + 1];
    private readonly int[] SZ = new int[NOST + 1];
    private readonly int[] SXL = new int[NOST + 1];
    private readonly int[] SYL = new int[NOST + 1];
    private readonly int[] SZL = new int[NOST + 1];

    // ------------------------------------------------------------------------
    // Universe and commander state
    // ------------------------------------------------------------------------

    /// <summary>NAME: the commander's name.</summary>
    private string CommanderName = "JAMESON";

    /// <summary>TP: the mission status.</summary>
    private int TP;

    /// <summary>QQ0 and QQ1: our current galactic coordinates.</summary>
    private int QQ0, QQ1;

    /// <summary>QQ21: the three 16-bit seeds for the current galaxy.</summary>
    private readonly int[] QQ21 = new int[6];

    /// <summary>CASH: our cash in Cr * 10.</summary>
    private uint CASH;

    /// <summary>QQ14: our fuel level in light years * 10.</summary>
    private int QQ14;

    /// <summary>COK: the competition flags.</summary>
    private int COK;

    /// <summary>GCNT: the current galaxy number (0-7).</summary>
    private int GCNT;

    /// <summary>LASER: the laser power for each of the four views (0 = none).</summary>
    private readonly int[] LASER = new int[4];

    /// <summary>CRGO: our cargo capacity.</summary>
    private int CRGO;

    /// <summary>QQ20: the contents of the cargo hold.</summary>
    private readonly int[] QQ20 = new int[17];

    /// <summary>Equipment flags.</summary>
    private int ECM, BST, BOMB, ENGY, DKCMP, GHYP, ESCP;

    /// <summary>TALLYL: the fractional part of the kill tally.</summary>
    private int TALLYL;

    /// <summary>NOMSL: the number of missiles we have.</summary>
    private int NOMSL;

    /// <summary>FIST: our legal status.</summary>
    private int FIST;

    /// <summary>AVL: the market availability for each item.</summary>
    private readonly int[] AVL = new int[17];

    /// <summary>QQ26: the random byte that changes the market on each visit.</summary>
    private int QQ26;

    /// <summary>TALLY: the number of kills.</summary>
    private int TALLY;

    /// <summary>SVC: the save count.</summary>
    private int SVC;

    /// <summary>QQ2: the seeds of the current system.</summary>
    private readonly int[] QQ2 = new int[6];

    /// <summary>safehouse: the seeds of the system we are jumping to.</summary>
    private readonly int[] safehouse = new int[6];

    /// <summary>QQ3, QQ4, QQ5, QQ6, QQ7: the economy, government, tech level, population and productivity of the selected system.</summary>
    private int QQ3, QQ4, QQ5, QQ6, QQ7;

    /// <summary>QQ8: the distance to the selected system in light years * 10.</summary>
    private int QQ8;

    /// <summary>QQ9 and QQ10: the galactic coordinates of the crosshairs on the charts.</summary>
    private int QQ9, QQ10;

    /// <summary>QQ15: the three 16-bit seeds of the selected system.</summary>
    private readonly int[] QQ15 = new int[6];

    /// <summary>QQ19: temporary storage (used for seeds and crosshair coordinates).</summary>
    private readonly int[] QQ19 = new int[6];

    /// <summary>QQ24, QQ25, QQ28, QQ29: market price, availability, current economy and item number.</summary>
    private int QQ24, QQ25, QQ28, QQ29;

    /// <summary>gov and tek: the current system's government and tech level.</summary>
    private int gov, tek;

    // ------------------------------------------------------------------------
    // Configuration options (toggled while paused)
    // ------------------------------------------------------------------------

    /// <summary>DAMP, DJD, PATG, FLH, JSTGY, JSTE, JSTK, UPTOG, DISK: the toggle options, indexed as in TGINT.</summary>
    private readonly int[] ToggleOptions = new int[9];

    private int DAMP => ToggleOptions[0];
    private int DJD => ToggleOptions[1];
    private int PATG => ToggleOptions[2];
    private int FLH => ToggleOptions[3];

    /// <summary>DNOIZ: non-zero if sound is disabled.</summary>
    private int DNOIZ;

    /// <summary>VOL: the sound volume (0-7).</summary>
    private int VOL = 7;

    /// <summary>DISK: toggled with "T" while paused (selects which file system name is shown).</summary>
    private int DISK
    {
        get => ToggleOptions[8] != 0 ? 1 : 0;
        set => ToggleOptions[8] = value != 0 ? 0xFF : 0;
    }

    /// <summary>
    /// JSTK: non-zero if the joystick is configured (toggled with "K" while
    /// paused), in which case the controller's left stick is always in control.
    /// </summary>
    private int JSTK
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
            _sound?.SetVolumeSource(() => DNOIZ != 0 ? -1 : VOL);
            BEGIN();
            GameJump next = GameJump.TT170;
            int key = 0;
            while (true)
            {
                try
                {
                    switch (next)
                    {
                        case GameJump.TT170:
                            TT170();
                            break;
                        case GameJump.Death2:
                            DEATH2();
                            break;
                        case GameJump.QU5:
                            QU5();
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

    private static readonly long VsyncTicks = Stopwatch.Frequency / 50;

    /// <summary>Send the current screen contents to the renderer.</summary>
    private void Present()
    {
        if (_quit)
        {
            throw new QuitException();
        }

        _screen.EscapePodFitted = ESCP != 0;
        _screen.Present();
    }

    /// <summary>WSCAN: wait for the vertical sync (the original runs at 50 Hz).</summary>
    private void WSCAN()
    {
        Present();
        long now = _clock.ElapsedTicks;
        if (_nextVsyncTicks < now - VsyncTicks)
        {
            _nextVsyncTicks = now;
        }

        _nextVsyncTicks += VsyncTicks;
        SleepUntil(_nextVsyncTicks);
    }

    /// <summary>DELAY: wait for Y vertical syncs.</summary>
    private void DELAY(int y)
    {
        do
        {
            WSCAN();
            y = (y - 1) & 0xFF;
        }
        while (y != 0);
    }

    /// <summary>
    /// The original main loop runs as fast as the 6502 can manage, so here we
    /// limit the rate of the main loop to the configured frame rate, which
    /// gives a similar game speed to the original.
    /// </summary>
    private void ThrottleMainLoop()
    {
        Present();
        long period = Stopwatch.Frequency / Math.Max(1, _options.MainLoopRate);
        long now = _clock.ElapsedTicks;
        if (_nextMainLoopTicks < now - period)
        {
            _nextMainLoopTicks = now;
        }

        _nextMainLoopTicks += period;
        SleepUntil(_nextMainLoopTicks);
    }

    private void SleepUntil(long ticks)
    {
        while (true)
        {
            long remaining = ticks - _clock.ElapsedTicks;
            if (remaining <= 0)
            {
                return;
            }

            double ms = remaining * 1000.0 / Stopwatch.Frequency;
            if (ms > 2)
            {
                Thread.Sleep((int)(ms - 1));
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    /// <summary>Send the screen to the renderer and pause for a number of milliseconds.</summary>
    private void PresentAndPause(int milliseconds)
    {
        Present();
        SleepUntil(_clock.ElapsedTicks + milliseconds * Stopwatch.Frequency / 1000);
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
    private readonly int[] RAND = [.. Enumerable.Range(0, 4).Select(_ => Random.Shared.Next(1, 256))];

    /// <summary>The C flag as left by DORND, which feeds into the next call.</summary>
    private bool _carry;

    /// <summary>The V flag as left by the last call to DORND.</summary>
    private bool _overflow;

    /// <summary>The value returned in X by the last call to DORND.</summary>
    private int _randX;

    /// <summary>
    /// DORND: generate a random number in A (returned) and X (in <see cref="_randX"/>),
    /// setting the C and V flags. The C flag on entry feeds into the result, as in
    /// the original.
    /// </summary>
    private int DORND()
    {
        int a = RAND[0];
        int carryIn = _carry ? 1 : 0;
        int rolled = ((a << 1) | carryIn) & 0xFF;
        int carry = (a >> 7) & 1;
        int x = rolled;
        int sum = rolled + RAND[2] + carry;
        RAND[0] = sum & 0xFF;
        RAND[2] = x;
        carry = sum > 0xFF ? 1 : 0;

        a = RAND[1];
        x = a;
        sum = a + RAND[3] + carry;
        int result = sum & 0xFF;
        _overflow = ((~(a ^ RAND[3]) & (a ^ result)) & 0x80) != 0;
        _carry = sum > 0xFF;
        RAND[1] = result;
        RAND[3] = x;
        _randX = x;
        return result;
    }

    /// <summary>DORND2: DORND with the C flag cleared first, so the sequence is repeatable.</summary>
    private int DORND2()
    {
        _carry = false;
        return DORND();
    }
}

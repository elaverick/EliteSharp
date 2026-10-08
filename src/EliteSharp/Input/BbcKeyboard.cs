using Silk.NET.Input;

namespace EliteSharp.Input;

/// <summary>
/// Emulates the BBC Micro keyboard. PC keys are mapped onto the BBC's internal
/// key numbers (as used by the original's keyboard scanning routines, DKS4).
///
/// Key presses are latched until the game has seen them, so short taps are not
/// missed between the game's keyboard scans.
/// </summary>
public sealed class BbcKeyboard
{
    // Internal key numbers (see TRTB% and KYTB in the original)
    public const int Shift = 0x00;
    public const int Ctrl = 0x01;
    public const int Q = 0x10;
    public const int Three = 0x11;
    public const int Four = 0x12;
    public const int Five = 0x13;
    public const int F4 = 0x14;
    public const int Eight = 0x15;
    public const int F7 = 0x16;
    public const int Minus = 0x17;
    public const int Caret = 0x18;
    public const int Left = 0x19;
    public const int F0 = 0x20;
    public const int W = 0x21;
    public const int E = 0x22;
    public const int T = 0x23;
    public const int Seven = 0x24;
    public const int I = 0x25;
    public const int Nine = 0x26;
    public const int Zero = 0x27;
    public const int Underscore = 0x28;
    public const int Down = 0x29;
    public const int One = 0x30;
    public const int Two = 0x31;
    public const int D = 0x32;
    public const int R = 0x33;
    public const int Six = 0x34;
    public const int U = 0x35;
    public const int O = 0x36;
    public const int P = 0x37;
    public const int LeftBracket = 0x38;
    public const int Up = 0x39;
    public const int CapsLock = 0x40;
    public const int A = 0x41;
    public const int X = 0x42;
    public const int F = 0x43;
    public const int Y = 0x44;
    public const int J = 0x45;
    public const int K = 0x46;
    public const int At = 0x47;
    public const int Colon = 0x48;
    public const int Return = 0x49;
    public const int ShiftLock = 0x50;
    public const int S = 0x51;
    public const int C = 0x52;
    public const int G = 0x53;
    public const int H = 0x54;
    public const int N = 0x55;
    public const int L = 0x56;
    public const int Semicolon = 0x57;
    public const int RightBracket = 0x58;
    public const int Delete = 0x59;
    public const int Tab = 0x60;
    public const int Z = 0x61;
    public const int Space = 0x62;
    public const int V = 0x63;
    public const int B = 0x64;
    public const int M = 0x65;
    public const int Comma = 0x66;
    public const int Period = 0x67;
    public const int Slash = 0x68;
    public const int Copy = 0x69;
    public const int Escape = 0x70;
    public const int F1 = 0x71;
    public const int F2 = 0x72;
    public const int F3 = 0x73;
    public const int F5 = 0x74;
    public const int F6 = 0x75;
    public const int F8 = 0x76;
    public const int F9 = 0x77;
    public const int Backslash = 0x78;
    public const int Right = 0x79;

    private readonly bool[] _held = new bool[128];
    private readonly bool[] _padHeld = new bool[128];
    private readonly bool[] _latched = new bool[128];
    private readonly object _lock = new();
    private bool _settingsRequested;
    private volatile bool _focused = true;

    /// <summary>The mapping from PC keys to BBC internal key numbers.</summary>
    private static readonly Dictionary<Key, int> KeyMap = new()
    {
        [Key.A] = A, [Key.B] = B, [Key.C] = C, [Key.D] = D, [Key.E] = E, [Key.F] = F,
        [Key.G] = G, [Key.H] = H, [Key.I] = I, [Key.J] = J, [Key.K] = K, [Key.L] = L,
        [Key.M] = M, [Key.N] = N, [Key.O] = O, [Key.P] = P, [Key.Q] = Q, [Key.R] = R,
        [Key.S] = S, [Key.T] = T, [Key.U] = U, [Key.V] = V, [Key.W] = W, [Key.X] = X,
        [Key.Y] = Y, [Key.Z] = Z,
        [Key.Number0] = Zero, [Key.Number1] = One, [Key.Number2] = Two, [Key.Number3] = Three,
        [Key.Number4] = Four, [Key.Number5] = Five, [Key.Number6] = Six, [Key.Number7] = Seven,
        [Key.Number8] = Eight, [Key.Number9] = Nine,
        [Key.Space] = Space, [Key.Comma] = Comma, [Key.Period] = Period, [Key.Slash] = Slash,
        [Key.Enter] = Return, [Key.KeypadEnter] = Return, [Key.Escape] = Escape, [Key.Tab] = Tab,
        [Key.Backspace] = Delete, [Key.Delete] = Delete,
        [Key.F11] = Copy, [Key.Pause] = Copy, [Key.End] = Copy,
        [Key.CapsLock] = CapsLock,
        [Key.ShiftLeft] = Shift, [Key.ShiftRight] = Shift,
        [Key.ControlLeft] = Ctrl, [Key.ControlRight] = Ctrl,
        [Key.Left] = Left, [Key.Right] = Right, [Key.Up] = Up, [Key.Down] = Down,
        [Key.GraveAccent] = At,
        [Key.Minus] = Minus, [Key.Semicolon] = Semicolon, [Key.Apostrophe] = Colon,
        [Key.LeftBracket] = LeftBracket, [Key.RightBracket] = RightBracket, [Key.BackSlash] = Backslash,
        [Key.Equal] = Caret,

        // The BBC's red function keys f0-f9 are on F1-F10
        [Key.F1] = F0, [Key.F2] = F1, [Key.F3] = F2, [Key.F4] = F3, [Key.F5] = F4,
        [Key.F6] = F5, [Key.F7] = F6, [Key.F8] = F7, [Key.F9] = F8, [Key.F10] = F9,
    };

    public void OnKeyDown(Key key)
    {
        // F12 opens the Settings screen, which isn't a BBC key
        if (key == Key.F12)
        {
            RequestSettings();
            return;
        }

        if (KeyMap.TryGetValue(key, out int bbcKey))
        {
            lock (_lock)
            {
                _held[bbcKey] = true;
                _latched[bbcKey] = true;
            }
        }
    }

    public void OnKeyUp(Key key)
    {
        if (KeyMap.TryGetValue(key, out int bbcKey))
        {
            lock (_lock)
            {
                // Shift and Ctrl have two PC keys each, so only release if
                // neither is still down is too fiddly; just release
                _held[bbcKey] = false;
            }
        }
    }

    /// <summary>
    /// True while the game window has the focus. The game pauses itself while
    /// the window is in the background, and carries on when it comes back.
    /// </summary>
    public bool HasFocus => _focused;

    /// <summary>
    /// Tell the keyboard whether the game window has the focus. Losing the
    /// focus releases any keys being held, as their key-up events will go to
    /// another application.
    /// </summary>
    public void SetFocus(bool focused)
    {
        _focused = focused;
        if (!focused)
        {
            lock (_lock)
            {
                Array.Clear(_held);
            }
        }
    }

    /// <summary>Ask the game to open the Settings screen (F12, or RB on the controller while paused).</summary>
    public void RequestSettings()
    {
        lock (_lock)
        {
            _settingsRequested = true;
        }
    }

    /// <summary>Returns true if the Settings screen has been asked for since this was last called.</summary>
    public bool TakeSettingsRequest()
    {
        lock (_lock)
        {
            bool requested = _settingsRequested;
            _settingsRequested = false;
            return requested;
        }
    }

    /// <summary>
    /// Set the keys being held down by the game controller, and latch the keys
    /// that it has just pressed (see <see cref="GamepadMapper"/>).
    /// </summary>
    public void SetPadKeys(IEnumerable<int> held, IEnumerable<int> pressed)
    {
        lock (_lock)
        {
            Array.Clear(_padHeld);
            foreach (int key in held)
            {
                _padHeld[key] = true;
            }

            foreach (int key in pressed)
            {
                _latched[key] = true;
            }
        }
    }

    /// <summary>
    /// Returns true if the key with the given internal number is being pressed,
    /// or was pressed since it was last checked. Checking a key clears its latch.
    /// </summary>
    public bool IsPressed(int bbcKey)
    {
        lock (_lock)
        {
            bool pressed = _held[bbcKey] || _padHeld[bbcKey] || _latched[bbcKey];
            _latched[bbcKey] = false;
            return pressed;
        }
    }

    /// <summary>Returns true if the key is currently held down (ignoring latches).</summary>
    public bool IsHeld(int bbcKey)
    {
        lock (_lock)
        {
            return _held[bbcKey] || _padHeld[bbcKey];
        }
    }

    /// <summary>
    /// Scan the keyboard for any key being pressed (as OSBYTE 121 with X = 16
    /// does), returning its internal key number, or -1 if none. Shift and Ctrl
    /// are not included in the scan.
    /// </summary>
    public int ScanAny()
    {
        lock (_lock)
        {
            for (int key = 0x10; key < 128; key++)
            {
                if (_held[key] || _padHeld[key] || _latched[key])
                {
                    _latched[key] = false;
                    return key;
                }
            }

            return -1;
        }
    }

    /// <summary>Clear any latched key presses and the keyboard buffer (*FX 15).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            Array.Clear(_latched);
        }

    }

    /// <summary>Clear the latches only.</summary>
    public void ClearLatches()
    {
        lock (_lock)
        {
            Array.Clear(_latched);
        }
    }
}

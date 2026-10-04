using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// What the screens with a highlighted list (buying and selling cargo, and
/// buying equipment) share: reading the cursor keys, D-pad and left stick
/// with key repeat, and drawing the highlight. These screens aren't in the
/// original, which asks for numbers to be typed in instead.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>How many vertical syncs a direction is held for before it repeats.</summary>
    private const int ListRepeatDelay = 15;

    /// <summary>How many vertical syncs there are between repeats once a direction is repeating.</summary>
    private const int ListRepeatInterval = 3;

    /// <summary>The cursor key codes (from TRTB%).</summary>
    private const int CursorLeft = 0x8C, CursorRight = 0x8D, CursorDown = 0x8E, CursorUp = 0x8F;

    /// <summary>
    /// The colour of the highlight: the BBC's blue, which the dashboard uses
    /// (the original's space view can't show it, having only four colours).
    /// </summary>
    private const Ink HighlightInk = Ink.DashboardBlue;

    /// <summary>
    /// Read the keyboard and controller each vertical sync until a press is
    /// handled as the end of the list (or a function key moves to another
    /// screen). Directions from the cursor keys, the D-pad or the left stick go
    /// to <paramref name="move"/>, repeating while held, and every other key
    /// press goes to <paramref name="press"/>, which returns true to finish.
    /// </summary>
    private void RunListScreen(Action<int, int> move, Func<int, bool> press)
    {
        // Ignore whatever is held down when the screen appears (such as the
        // key that chose it) until it is released
        int lastKey = ReadKey();
        var lastDirection = CursorDirection(lastKey) ?? ReadPadStickDirection();
        int repeat = 0;

        while (true)
        {
            WaitForVsync();
            int key = ReadKey();

            var direction = CursorDirection(key) ?? ReadPadStickDirection();
            if (direction != (0, 0))
            {
                if (direction != lastDirection)
                {
                    repeat = ListRepeatDelay;
                    move(direction.X, direction.Y);
                }
                else if (--repeat == 0)
                {
                    repeat = ListRepeatInterval;
                    move(direction.X, direction.Y);
                }
            }

            lastDirection = direction;

            // Everything else happens once per press
            bool pressed = key != lastKey;
            lastKey = key;
            if (!pressed || key == 0)
            {
                continue;
            }

            if (key is >= FunctionKey0 and <= FunctionKey9)
            {
                throw new GameJumpException(GameJump.ForceKey, key);
            }

            if (press(key))
            {
                return;
            }

            // The press may have run another list (such as choosing a laser's
            // view), so don't act on anything that it left held down
            lastKey = ReadKey();
            lastDirection = CursorDirection(lastKey) ?? ReadPadStickDirection();
        }
    }

    /// <summary>The direction of a cursor key, or null if it isn't one.</summary>
    private static (int X, int Y)? CursorDirection(int key) => key switch
    {
        CursorLeft => (-1, 0),
        CursorRight => (1, 0),
        CursorUp => (0, -1),
        CursorDown => (0, 1),
        _ => null,
    };

    /// <summary>
    /// Clear a text row ready for drawing a list item, and highlight it if it's
    /// the selected one (across the whole width inside the border).
    /// </summary>
    private void StartListRow(int row, bool selected)
    {
        _hud.ClearRows(row * 8, row * 8 + 7);
        if (selected)
        {
            _hud.DrawRect(2, row * 8, Hud.Width - 4, 8, HighlightInk);
        }

        _cursorY = row;
        _cursorX = 1;
        _textCase = 0x80;
    }

    /// <summary>Print a label and an amount of money (in Cr * 10) with the amount at column 7.</summary>
    private void PrintMoneyLine(string key, long amount)
    {
        PrintTextColon(key);
        _cursorX = 7;
        PrintNumber(amount, 9, true);
        PrintText("market.credits");
    }
}

using System.Text;
using EliteSharp.Input;

namespace EliteSharp.Game;

/// <summary>
/// The Load Commander and Save Commander screens, which replace the
/// original's disc access menu. Both are lists with a highlight, moved with
/// the cursor keys, the D-pad or the left stick, and the details of the
/// highlighted save are shown below the list.
///
/// Load Commander lists the commanders with saves (the most recently saved
/// first), and choosing one lists their saves, newest first; choosing a save
/// loads it. Save Commander lists the current commander's saves, below "New
/// Save" and "Save As New Commander"; choosing a save saves over it. Either
/// screen can delete the highlighted save (or a whole commander, with all of
/// their saves) with Delete (or X), after asking. Saves are labelled, with a
/// label that can be changed before saving (which needs the keyboard, as do
/// new commanders' names, though Return or A takes the label as it is).
///
/// Load Commander opens from the title screen's "Load New Commander (Y/N)?",
/// and while docked, with L on the Status screen (or from the Settings
/// screen); Save Commander opens with S on the Status screen while docked (or
/// from the Settings screen).
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The text row of the first row of a commander list.</summary>
    private const int CommanderListFirstRow = 3;

    /// <summary>How many rows of a commander list fit on the screen (it scrolls if there are more).</summary>
    private const int CommanderListRows = 13;

    /// <summary>The text row of the first line of the highlighted save's details.</summary>
    private const int CommanderDetailsRow = 17;

    /// <summary>The longest commander's name (as in the original).</summary>
    private const int CommanderNameLength = 7;

    /// <summary>The longest label for a save (so it fits in the list).</summary>
    private const int SaveLabelLength = 18;

    /// <summary>A row in a commander list: its text, the date on its right, and the save whose details it shows.</summary>
    private sealed record CommanderListRow(string Text, string Date, CommanderSave? Save);

    /// <summary>What was done in a commander list.</summary>
    private enum ListChoice
    {
        Back,
        Select,
        Delete,
    }

    /// <summary>The rows in the commander list on the screen.</summary>
    private List<CommanderListRow> _commanderRows = [];

    /// <summary>The highlighted row in the commander list, and the first row shown (as it scrolls).</summary>
    private int _commanderSelected, _commanderTop;

    /// <summary>Whether a commander screen is open (so the Settings screen doesn't open another).</summary>
    private bool _commanderScreenOpen;

    /// <summary>
    /// Show the Load Commander screen. Returns true if a commander was chosen,
    /// in which case they are now the last saved commander (in NA%), ready to
    /// be loaded by DFAULT, and false if we went back without choosing.
    /// </summary>
    private bool ShowLoadCommander() => InCommanderScreen(() =>
    {
        _commanderSelected = 0;
        while (true)
        {
            var commanders = CommanderStore.GroupByCommander(LoadSaves());
            var rows = commanders
                .Select(c => new CommanderListRow(CommanderRowText(c.Commander, c.Saves.Count), c.Saves[0].SavedDateText, c.Saves[0]))
                .ToList();

            // JAMESON: start again as the default commander
            rows.Add(new CommanderListRow(_strings.Get("commanders.start_again"), "", null));

            var (choice, index) = RunCommanderList(_strings.Get("commanders.load_title"), rows, "commanders.load_help");
            if (choice == ListChoice.Back)
            {
                return false;
            }

            if (index == commanders.Count)
            {
                if (choice == ListChoice.Select && Confirm(_strings.Get("commanders.confirm_start_again")))
                {
                    RestoreDefaultCommander();
                    return true;
                }

                continue;
            }

            var (commander, saves) = commanders[index];
            if (choice == ListChoice.Delete)
            {
                if (Confirm(_strings.Get("commanders.delete_commander") + commander, _strings.Get("commanders.delete_commander_saves")))
                {
                    DeleteSaves(saves);
                }

                continue;
            }

            int selected = _commanderSelected;
            if (ChooseSaveToLoad(commander))
            {
                return true;
            }

            _commanderSelected = selected;
        }
    });

    /// <summary>
    /// List a commander's saves to choose one to load, returning true if one
    /// was loaded into NA%, and false if we went back (or there are no saves left).
    /// </summary>
    private bool ChooseSaveToLoad(string commander)
    {
        _commanderSelected = 0;
        while (true)
        {
            var saves = SavesOf(commander);
            if (saves.Count == 0)
            {
                return false;
            }

            var rows = saves.Select(s => new CommanderListRow(s.Label, s.SavedDateText, s)).ToList();
            string title = _strings.Get("commanders.load_title") + " " + commander;
            var (choice, index) = RunCommanderList(title, rows, "commanders.load_help");
            switch (choice)
            {
                case ListChoice.Back:
                    return false;
                case ListChoice.Select:
                    LoadCommander(saves[index]);
                    return true;
                case ListChoice.Delete:
                    if (Confirm(_strings.Get("commanders.delete_save")))
                    {
                        DeleteSaves([saves[index]]);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Show the Save Commander screen, and save the current commander if a
    /// save is chosen (or go back without saving).
    /// </summary>
    private void ShowSaveCommander() => InCommanderScreen(() =>
    {
        _commanderSelected = 0;
        while (true)
        {
            var saves = SavesOf(CommanderName);
            var rows = new List<CommanderListRow>
            {
                new(_strings.Get("commanders.new_save"), "", null),
                new(_strings.Get("commanders.new_commander"), "", null),
            };
            rows.AddRange(saves.Select(s => new CommanderListRow(s.Label, s.SavedDateText, s)));

            string title = _strings.Get("commanders.save_title") + " " + CommanderName;
            var (choice, index) = RunCommanderList(title, rows, "commanders.save_help");
            if (choice == ListChoice.Back)
            {
                return false;
            }

            if (choice == ListChoice.Delete)
            {
                if (index >= 2 && Confirm(_strings.Get("commanders.delete_save")))
                {
                    DeleteSaves([saves[index - 2]]);
                }

                continue;
            }

            string name = CommanderName;
            CommanderSave? replacing = null;
            string label = DefaultSaveLabel(CurrentSystemName());
            if (index == 1)
            {
                // A new commander, which needs a name
                if (EditLine("commanders.name_prompt", "", CommanderNameLength, isLabel: false) is not { Length: > 0 } newName)
                {
                    continue;
                }

                name = newName;
            }
            else if (index >= 2)
            {
                replacing = saves[index - 2];
                if (!Confirm(_strings.Get("commanders.confirm_overwrite")))
                {
                    continue;
                }

                // Keep a label that was typed in, but not one that says where the old save was
                if (replacing.Label != DefaultSaveLabel(replacing.SystemName))
                {
                    label = replacing.Label;
                }
            }

            if (EditLine("commanders.label_prompt", label, SaveLabelLength, isLabel: true) is not { } newLabel)
            {
                continue;
            }

            if (SaveCommander(name, newLabel.Length > 0 ? newLabel : label, replacing))
            {
                ShowCommanderMessage("commanders.saved_message");
                Delay(50);
            }
            else
            {
                ShowCommanderMessage("commanders.save_failed");
                Beep();
                WaitForKey();
            }

            return true;
        }
    });

    /// <summary>
    /// Run a commander screen, with the controller's buttons working as they
    /// do on the other screens, and put back the view type afterwards (as the
    /// Settings screen can open these over any screen while docked).
    /// </summary>
    private bool InCommanderScreen(Func<bool> screen)
    {
        var padContext = _padContextOverride;
        int viewType = _viewType;
        _padContextOverride = PadContext.Screen;
        _commanderScreenOpen = true;
        try
        {
            bool result = screen();

            // Wait for the key that left the screen to be let go of, so the
            // next screen doesn't act on it as well
            do
            {
                WaitForVsync();
            }
            while (AnyKeyHeld());

            _keyboard.ClearLatches();
            return result;
        }
        finally
        {
            _padContextOverride = padContext;
            _viewType = viewType;
            _commanderScreenOpen = false;
        }
    }

    /// <summary>Read the saves, or none if the folder can't be read.</summary>
    private List<CommanderSave> LoadSaves()
    {
        try
        {
            return Store.LoadAll();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A commander's saves, newest first.</summary>
    private List<CommanderSave> SavesOf(string commander) =>
        [.. LoadSaves().Where(s => s.Commander == commander)];

    /// <summary>Delete saves, carrying on past any that can't be deleted.</summary>
    private static void DeleteSaves(IEnumerable<CommanderSave> saves)
    {
        foreach (var save in saves)
        {
            try
            {
                CommanderStore.Delete(save);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A commander's row in the Load Commander list: their name, and how many saves they have.</summary>
    private string CommanderRowText(string commander, int saves) =>
        $"{commander,-8}{saves,3} " + _strings.Get(saves == 1 ? "commanders.save" : "commanders.saves");

    /// <summary>The name of the system we're in, in capitals.</summary>
    private string CurrentSystemName()
    {
        Span<byte> seeds = stackalloc byte[6];
        for (int i = 0; i < 6; i++)
        {
            seeds[i] = (byte)_galaxySeeds[i];
        }

        return CommanderStore.SystemNameAt(seeds, _currentSystemX, _currentSystemY);
    }

    /// <summary>The label that a save made at a system starts with, such as "Docked at Lave".</summary>
    private string DefaultSaveLabel(string system) => _strings.Get("commanders.default_label") + SystemNameInWords(system);

    /// <summary>A system's name with only its first letter in capitals, as the screens print it.</summary>
    private static string SystemNameInWords(string system) =>
        system.Length == 0 ? system : system[0] + system[1..].ToLowerInvariant();

    /// <summary>
    /// Show a commander list with the given title until a row is chosen (with
    /// Return or A), or deleted (with Delete or X), or we go back (with
    /// Escape or B), returning what was done and the highlighted row.
    /// </summary>
    private (ListChoice Choice, int Index) RunCommanderList(string title, List<CommanderListRow> rows, string chooseHelp)
    {
        _commanderRows = rows;
        _commanderSelected = Math.Clamp(_commanderSelected, 0, rows.Count - 1);
        _commanderTop = 0;
        ScrollCommanderList();

        ShowTradingScreen(1);
        _cursorY = 1;
        _cursorX = Math.Max(1, (32 - title.Length) / 2);
        PrintRaw(title);
        DrawTitleLine();
        DrawCommanderRows();
        DrawHorizontalLine(CommanderDetailsRow * 8 - 5);
        DrawCommanderDetails();
        DrawCommanderHelp(chooseHelp);

        var result = (ListChoice.Back, 0);
        RunListScreen(MoveInCommanderList, key =>
        {
            switch (key)
            {
                case 0x0D:
                    result = (ListChoice.Select, _commanderSelected);
                    return true;
                case 0x7F:
                    result = (ListChoice.Delete, _commanderSelected);
                    return true;
                case 0x1B or 'N':
                    return true;
                default:
                    return false;
            }
        }, functionKeysLeave: false);

        return result;
    }

    /// <summary>Move the highlight up or down the list, scrolling it if need be.</summary>
    private void MoveInCommanderList(int x, int y)
    {
        int selected = Math.Clamp(_commanderSelected + y, 0, _commanderRows.Count - 1);
        if (y == 0 || selected == _commanderSelected)
        {
            return;
        }

        int previous = _commanderSelected;
        int top = _commanderTop;
        _commanderSelected = selected;
        ScrollCommanderList();
        if (_commanderTop != top)
        {
            DrawCommanderRows();
        }
        else
        {
            DrawCommanderRow(previous);
            DrawCommanderRow(selected);
        }

        DrawCommanderDetails();
    }

    /// <summary>Scroll the list so the highlighted row is on the screen.</summary>
    private void ScrollCommanderList()
    {
        if (_commanderSelected < _commanderTop)
        {
            _commanderTop = _commanderSelected;
        }
        else if (_commanderSelected >= _commanderTop + CommanderListRows)
        {
            _commanderTop = _commanderSelected - CommanderListRows + 1;
        }
    }

    /// <summary>Draw the rows of the list that are on the screen.</summary>
    private void DrawCommanderRows()
    {
        for (int i = _commanderTop; i < _commanderTop + CommanderListRows; i++)
        {
            DrawCommanderRow(i);
        }
    }

    /// <summary>Draw a row of the list, highlighted if it's the selected row (or clear it if there's no such row).</summary>
    private void DrawCommanderRow(int index)
    {
        int row = CommanderListFirstRow + index - _commanderTop;
        if (row < CommanderListFirstRow || row >= CommanderListFirstRow + CommanderListRows)
        {
            return;
        }

        if (index >= _commanderRows.Count)
        {
            _hud.ClearRows(row * 8, row * 8 + 7);
            return;
        }

        StartListRow(row, index == _commanderSelected);
        _colour = Cyan;
        _cursorX = 2;
        // The text stops short of the date (if there is one)
        var (text, date, _) = _commanderRows[index];
        int width = date.Length > 0 ? SaveLabelLength : 30;
        PrintRaw(text.Length > width ? text[..width] : text);
        _cursorX = 21;
        PrintRaw(date);
    }

    /// <summary>Draw the details of the highlighted row's save: when it was saved, where, the cash and the rating.</summary>
    private void DrawCommanderDetails()
    {
        _hud.ClearRows(CommanderDetailsRow * 8, (CommanderDetailsRow + 4) * 8 - 1);
        if (_commanderRows[_commanderSelected].Save is not { } save)
        {
            return;
        }

        _colour = Cyan;
        _textCase = 0x80;
        DrawDetail(0, "commanders.saved", save.SavedText);
        DrawDetail(1, "commanders.system", $"{SystemNameInWords(save.SystemName)}, {_strings.Get("commanders.galaxy")}{save.Galaxy}");
        DrawDetail(2, "commanders.cash", $"{save.Cash / 10}.{save.Cash % 10}{_strings.Get("commanders.credits")}");
        DrawDetail(3, "commanders.rating", _strings.Get(RatingKeys[RatingFor(save.KillTally) - 1]).Trim());

        void DrawDetail(int line, string key, string value)
        {
            _cursorY = CommanderDetailsRow + line;
            _cursorX = 2;
            PrintRaw(_strings.Get(key) + ":");
            _cursorX = 10;
            PrintRaw(value);
        }
    }

    /// <summary>Show the keys for choosing, deleting and going back in the bottom three rows.</summary>
    private void DrawCommanderHelp(string chooseHelp)
    {
        ClearBottomRows();
        string[] keys = [chooseHelp, "commanders.delete_help", "commanders.back_help"];
        for (int i = 0; i < keys.Length; i++)
        {
            _cursorX = 1;
            _cursorY = 21 + i;
            PrintRaw(_strings.Get(keys[i]));
        }
    }

    /// <summary>Show a message in the bottom rows.</summary>
    private void ShowCommanderMessage(string key)
    {
        ClearBottomRows();
        _cursorX = 1;
        _cursorY = 22;
        PrintRaw(_strings.Get(key));
    }

    /// <summary>
    /// Ask a question in the bottom rows (over one or two lines), followed by
    /// "(Y/N)?", and wait for "Y" (returning true) or "N" or Escape (false).
    /// </summary>
    private bool Confirm(string question, string? secondLine = null)
    {
        ClearBottomRows();
        _cursorX = 1;
        _cursorY = secondLine == null ? 22 : 21;
        PrintRaw(question);
        if (secondLine != null)
        {
            _cursorX = 1;
            _cursorY = 22;
            PrintRaw(secondLine);
        }

        PrintRaw(" " + _strings.Get("prompts.yes_no"));
        while (true)
        {
            switch (WaitForKey())
            {
                case 'Y':
                    return true;
                case 'N' or 0x1B:
                    return false;
            }
        }
    }

    /// <summary>
    /// Ask for a line of text in the bottom rows, starting with the given
    /// text, returning it when Return (or A) is pressed, or null for Escape
    /// (or B). A label can have spaces, and its letters are in lower case
    /// unless Shift is held; a commander's name is in capitals, as in the
    /// original. Delete (or X) deletes the last character.
    /// </summary>
    private string? EditLine(string promptKey, string initial, int maxLength, bool isLabel)
    {
        var padContext = _padContextOverride;
        _padContextOverride = PadContext.TextEntry;
        try
        {
            var text = new StringBuilder(initial.Length > maxLength ? initial[..maxLength] : initial);
            while (true)
            {
                ClearBottomRows();
                _cursorX = 1;
                _cursorY = 22;
                PrintRaw(_strings.Get(promptKey));
                _colour = Red;
                PrintRaw(text.ToString());
                if (text.Length < maxLength)
                {
                    PutCharacter('_');
                }

                _colour = Cyan;
                _cursorX = 1;
                _cursorY = 23;
                PrintRaw(_strings.Get("commanders.edit_help"));

                int key = WaitForKey();
                switch (key)
                {
                    case 0x0D:
                        return text.ToString().Trim();
                    case 0x1B:
                        return null;
                    case 0x7F:
                        if (text.Length == 0)
                        {
                            Beep();
                        }
                        else
                        {
                            text.Length--;
                        }

                        continue;
                }

                if (isLabel && key is >= 'A' and <= 'Z' && !ShiftPressed())
                {
                    key += 'a' - 'A';
                }

                bool allowed = isLabel ? key is >= ' ' and <= '~' : key is >= '!' and < '{';
                if (!allowed || text.Length >= maxLength)
                {
                    Beep();
                    continue;
                }

                text.Append((char)key);
            }
        }
        finally
        {
            _padContextOverride = padContext;
        }
    }

    /// <summary>Print some text as it is, at the text cursor (a commander's name or a label, say).</summary>
    private void PrintRaw(string text)
    {
        foreach (char c in text)
        {
            PutCharacter(c);
        }
    }
}

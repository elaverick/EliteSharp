using EliteSharp.Input;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// The Controls screen, which isn't in the original: it opens from the
/// Settings screen, and shows what each connected controller's controls do
/// (see <see cref="ControllerBindings"/>), with a picture of the controller
/// (from its profile, see <see cref="ControllerProfile"/>) above the list.
///
/// The top two rows of the list choose the controller (if more than one is
/// connected) and the page: the axes, or the buttons in flight, on the other
/// screens, with the second functions' button held, while paused, or while
/// typing. Left and right change the page from anywhere below the controller
/// row. Return (or A) on a row waits for the control to bind to it: a button
/// or hat direction, or on the axes page, an axis moved in the direction the
/// prompt asks for (which decides which way round it works). Delete clears
/// the highlighted row, and the last row puts everything back to the
/// controller's defaults. The changes are saved straight away (see
/// <see cref="ControllerBindingStore"/>).
///
/// Whatever is pressed on the controller is shown by name under the list,
/// with what it does on the page, and is marked in green on the picture; the
/// highlighted row's controls are marked in magenta.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The area of the screen for the controller's picture, in the original's pixels.</summary>
    private const int ControlsPictureLeft = 4, ControlsPictureTop = 22, ControlsPictureWidth = 248, ControlsPictureHeight = 72;

    /// <summary>The text rows of the controller and page rows, and the first and number of rows of the list.</summary>
    private const int ControlsDeviceRow = 12, ControlsPageRow = 13, ControlsListRow = 14, ControlsListRows = 7;

    /// <summary>The text row that shows the control being pressed, or the prompt while waiting for one.</summary>
    private const int ControlsStatusRow = 21;

    /// <summary>The column that the bound controls start in (each name has the columns before it).</summary>
    private const int ControlsBindingColumn = 19;

    /// <summary>How far an axis must move to be shown as being used, or to be bound.</summary>
    private const float ControlsAxisMovement = 0.5f;

    /// <summary>The pages, in order: the axes (null), then the buttons' pages.</summary>
    private static readonly ControlPage?[] ControlsPages =
        [null, ControlPage.Flight, ControlPage.Screens, ControlPage.SecondFunctions, ControlPage.Paused, ControlPage.Typing];

    /// <summary>The selected controller and page, the highlighted row (0 is the controller, 1 the page, and 2 onwards the list), and the list's first row on the screen.</summary>
    private int _controlsDevice, _controlsPage = 1, _controlsSelected = 2, _controlsTop;

    /// <summary>The controllers that the screen was drawn for (by their instance IDs), to redraw it when one is plugged in or removed.</summary>
    private int[] _controlsDeviceIds = [];

    /// <summary>The selected controller's axes when it was selected, to see which have moved.</summary>
    private float[] _controlsRestingAxes = [];

    /// <summary>What the status row shows.</summary>
    private string _controlsStatus = "";

    /// <summary>
    /// Show the Controls screen until it is closed (with Escape or B), going
    /// back to the Settings screen.
    /// </summary>
    private void ShowControls()
    {
        var padContext = _padContextOverride;
        _padContextOverride = PadContext.Screen;
        try
        {
            _hud.ClearSpaceView();
            _hud.WorldHidden = true;
            _hud.Palette = SpacePalette.Trade;
            DrawBorderBox();
            _controlsSelected = Math.Max(_controlsSelected, 2);
            SelectControlsDevice(_controlsDevice);
            DrawControlsScreen();

            RunListScreen(MoveInControls, PressInControls, functionKeysLeave: false, everyFrame: UpdateControlsScreen, stickMoves: false);

            do
            {
                WaitForVsync();
            }
            while (AnyKeyHeld());

            _keyboard.ClearLatches();
        }
        finally
        {
            if (_controllers != null)
            {
                _controllers.Capturing = false;
            }

            _hud.ClearGroup(HudGroup.ControlMarkers);
            _padContextOverride = padContext;
        }
    }

    /// <summary>The connected controllers.</summary>
    private IReadOnlyList<ControllerDevice> ControlsDevices => _controllers?.Devices ?? [];

    /// <summary>The selected controller, or null if none is connected.</summary>
    private ControllerDevice? ControlsDevice
    {
        get
        {
            var devices = ControlsDevices;
            return devices.Count == 0 ? null : devices[Math.Clamp(_controlsDevice, 0, devices.Count - 1)];
        }
    }

    /// <summary>The selected page, or null for the axes.</summary>
    private ControlPage? ControlsPage => ControlsPages[_controlsPage];

    /// <summary>The axis functions, in the order the axes page lists them.</summary>
    private static readonly AxisFunction[] ControlsAxisFunctions = Enum.GetValues<AxisFunction>();

    /// <summary>The number of rows in the list: the page's functions or actions, then "Reset To Defaults".</summary>
    private int ControlsListCount => (ControlsPage is { } page ? ControlsActions(page).Count : ControlsAxisFunctions.Length) + 1;

    private static IReadOnlyList<PadAction> ControlsActions(ControlPage page) => ControllerBindings.ActionsOn(page);

    /// <summary>The bindings of the selected controller (its profile's defaults if the player hasn't changed them).</summary>
    private ControllerBindings? ControlsBindings => ControlsDevice is { } device ? _controllers!.Bindings.For(device.Profile) : null;

    /// <summary>Select a controller, noting where its axes are resting.</summary>
    private void SelectControlsDevice(int index)
    {
        var devices = ControlsDevices;
        _controlsDevice = devices.Count == 0 ? 0 : Math.Clamp(index, 0, devices.Count - 1);
        _controlsDeviceIds = [.. devices.Select(d => d.InstanceId)];
        _controlsRestingAxes = ControlsDevice is { } device ? (float[])device.State.Axes.Clone() : [];
    }

    /// <summary>Draw the whole screen.</summary>
    private void DrawControlsScreen()
    {
        _hud.ClearRows(8, 23 * 8 + 7);
        _colour = Cyan;
        _textCase = 0x80;
        string title = _strings.Get("controls.title");
        _cursorY = 1;
        _cursorX = Math.Max(1, (32 - title.Length) / 2);
        PrintRaw(title);
        DrawTitleLine();
        DrawControlsPicture();
        DrawControlsRow(0);
        DrawControlsRow(1);
        DrawControlsList();
        DrawControlsHelp();
    }

    /// <summary>
    /// Show the selected controller's picture, or for a controller without
    /// one, how many buttons, axes and hats it has.
    /// </summary>
    private void DrawControlsPicture()
    {
        _hud.Picture = null;
        _hud.ClearRows(ControlsPictureTop, ControlsPictureTop + ControlsPictureHeight - 1);
        if (ControlsDevice is not { } device)
        {
            PrintControlsCentred(6, _strings.Get("controls.no_controller"));
            return;
        }

        if (ControlsPictureArea(device) is { } area)
        {
            _hud.Picture = new HudPicture(device.Profile.ImagePath!, area.X, area.Y, area.Width, area.Height);
            return;
        }

        PrintControlsCentred(5, $"{device.ButtonsPresent.Count(b => b)} {_strings.Get("controls.buttons")}, "
            + $"{device.AxisCount} {_strings.Get("controls.axes")}, {device.HatCount} {_strings.Get("controls.hats")}");
        PrintControlsCentred(7, $"{_strings.Get("controls.usb_id")} {device.Vendor:X4}:{device.Product:X4}");
        PrintControlsCentred(9, _strings.Get("controls.press_to_see"));
    }

    /// <summary>
    /// Where a controller's picture goes: as big as it fits in the picture
    /// area, in the middle, with its scale (the original's pixels per pixel
    /// of the picture), or null if it has no picture.
    /// </summary>
    private static (float X, float Y, float Width, float Height, float Scale)? ControlsPictureArea(ControllerDevice device)
    {
        if (device.Profile.ImagePath is not { } path)
        {
            return null;
        }

        (int width, int height) size;
        try
        {
            size = PictureImage.ReadSize(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }

        float scale = Math.Min((float)ControlsPictureWidth / size.width, (float)ControlsPictureHeight / size.height);
        float width = size.width * scale, height = size.height * scale;
        return (ControlsPictureLeft + (ControlsPictureWidth - width) / 2, ControlsPictureTop + (ControlsPictureHeight - height) / 2, width, height, scale);
    }

    /// <summary>Print text in the middle of a row (cut short if it doesn't fit).</summary>
    private void PrintControlsCentred(int row, string text)
    {
        text = FitControlsText(text, 30);
        _cursorY = row;
        _cursorX = 1 + (30 - text.Length) / 2;
        PrintRaw(text);
    }

    /// <summary>
    /// Cut text short to fit in a number of columns, at the end of a word if
    /// it can be (so "Saitek X52 Flight Control System" becomes "Saitek X52
    /// Flight Control").
    /// </summary>
    private static string FitControlsWords(string text, int columns)
    {
        if (text.Length <= columns)
        {
            return text;
        }

        int space = text.LastIndexOf(' ', columns);
        return space > 0 ? text[..space] : text[..columns];
    }

    /// <summary>Cut text short to fit in a number of columns.</summary>
    private static string FitControlsText(string text, int columns) => text.Length > columns ? text[..columns] : text;

    /// <summary>Draw the rows of the list that are on the screen.</summary>
    private void DrawControlsList()
    {
        for (int i = 0; i < ControlsListRows; i++)
        {
            DrawControlsRow(_controlsTop + i + 2);
        }
    }

    /// <summary>
    /// Draw a row: the controller (0), the page (1), or a row of the list
    /// (from 2), highlighted if it's the selected one (or cleared, for a row
    /// of the list that is off the end).
    /// </summary>
    private void DrawControlsRow(int index)
    {
        int row = index switch
        {
            0 => ControlsDeviceRow,
            1 => ControlsPageRow,
            _ => ControlsListRow + index - 2 - _controlsTop,
        };

        if (index >= 2 && (row < ControlsListRow || row >= ControlsListRow + ControlsListRows))
        {
            return;
        }

        if (index >= 2 + ControlsListCount)
        {
            _hud.ClearRows(row * 8, row * 8 + 7);
            return;
        }

        StartListRow(row, index == _controlsSelected);
        _colour = Cyan;
        if (index < 2)
        {
            string text = index == 0
                ? ControlsDevice?.Name ?? _strings.Get("controls.no_controller")
                : _strings.Get(ControlsPage is { } page ? $"controls.page_{ControllerBindings.FileName(page)}" : "controls.page_axes");
            bool arrows = index == 1 || ControlsDevices.Count > 1;
            text = FitControlsWords(text, arrows ? 24 : 30);
            PrintControlsCentred(row, arrows ? $"<  {text}  >" : text);
            return;
        }

        int item = index - 2;
        _cursorX = 2;
        if (item == ControlsListCount - 1)
        {
            PrintRaw(_strings.Get("controls.reset"));
            return;
        }

        PrintRaw(FitControlsText(ControlsRowName(item), ControlsBindingColumn - 3));
        _cursorX = ControlsBindingColumn;
        _colour = Yellow;
        PrintRaw(FitControlsText(ControlsRowBinding(item), 31 - ControlsBindingColumn));
    }

    /// <summary>The name of a row of the list (an axis function or an action).</summary>
    private string ControlsRowName(int item) => ControlsPage is { } page
        ? _strings.Get($"controls.{ControllerBindings.FileName(ControlsActions(page)[item])}")
        : _strings.Get($"controls.axis_{ControllerBindings.FileName(ControlsAxisFunctions[item])}");

    /// <summary>The controls bound to a row of the list, by name (a reversed axis has a minus sign).</summary>
    private string ControlsRowBinding(int item)
    {
        if (ControlsDevice is not { } device || ControlsBindings is not { } bindings)
        {
            return "";
        }

        if (ControlsPage is { } page)
        {
            return string.Join("/", bindings.InputsFor(page, ControlsActions(page)[item]).Select(device.Profile.InputName));
        }

        return bindings.AxisFor(ControlsAxisFunctions[item]) is { } axis
            ? (axis.Reversed ? "-" : "") + device.Profile.InputName(ControlInput.Axis(axis.Axis))
            : "";
    }

    /// <summary>The controls bound to the highlighted row, to mark on the picture.</summary>
    private IEnumerable<ControlInput> ControlsSelectedInputs()
    {
        int item = _controlsSelected - 2;
        if (item < 0 || item >= ControlsListCount - 1 || ControlsBindings is not { } bindings)
        {
            return [];
        }

        if (ControlsPage is { } page)
        {
            return bindings.InputsFor(page, ControlsActions(page)[item]);
        }

        return bindings.AxisFor(ControlsAxisFunctions[item]) is { } axis ? [ControlInput.Axis(axis.Axis)] : [];
    }

    /// <summary>Show the keys for changing and going back in the bottom two rows.</summary>
    private void DrawControlsHelp()
    {
        _hud.ClearRows((ControlsStatusRow + 1) * 8, 23 * 8 + 7);
        _colour = Cyan;
        _cursorX = 1;
        _cursorY = 22;
        PrintRaw(_strings.Get("controls.change_help"));
        _cursorX = 1;
        _cursorY = 23;
        PrintRaw(_strings.Get("controls.back_help"));
    }

    /// <summary>
    /// Each vertical sync: redraw everything if a controller has been plugged
    /// in or removed, and show what is being pressed.
    /// </summary>
    private void UpdateControlsScreen()
    {
        if (!ControlsDevices.Select(d => d.InstanceId).SequenceEqual(_controlsDeviceIds))
        {
            var selected = ControlsDevice;
            var devices = ControlsDevices;
            int index = selected == null ? 0 : devices.ToList().FindIndex(d => d.InstanceId == selected.InstanceId);
            SelectControlsDevice(Math.Max(0, index));
            DrawControlsScreen();
        }

        var pressed = ControlsPressed();
        SetControlsStatus(ControlsStatusText(pressed));
        DrawControlsMarkers(pressed);
    }

    /// <summary>
    /// The selected controller's buttons and hat directions that are pressed,
    /// and on the axes page, the axes that have moved from where they were
    /// resting when it was selected.
    /// </summary>
    private List<ControlInput> ControlsPressed()
    {
        if (ControlsDevice is not { } device)
        {
            return [];
        }

        var state = device.State;
        var pressed = state.Pressed();
        if (ControlsPage == null)
        {
            for (int i = 0; i < state.Axes.Length && i < _controlsRestingAxes.Length; i++)
            {
                if (MathF.Abs(state.Axes[i] - _controlsRestingAxes[i]) >= ControlsAxisMovement)
                {
                    pressed.Add(ControlInput.Axis(i));
                }
            }
        }

        return pressed;
    }

    /// <summary>The status row's text for what is being pressed: the first control's name, and what it does on the page.</summary>
    private string ControlsStatusText(List<ControlInput> pressed)
    {
        if (ControlsDevice is not { } device || ControlsBindings is not { } bindings || pressed.Count == 0)
        {
            return "";
        }

        // On the axes page, show an axis in preference to a button, and on the
        // other pages, a button that does something on the page (rather than,
        // say, the second functions' button that is being held with it)
        string? action = null;
        ControlInput? shown = null;
        if (ControlsPage is { } page)
        {
            foreach (var input in pressed)
            {
                if (bindings.ActionFor(page, input) is { } bound && bound != PadAction.SecondFunctions)
                {
                    shown = input;
                    action = _strings.Get($"controls.{ControllerBindings.FileName(bound)}");
                    break;
                }
            }
        }
        else
        {
            foreach (var input in pressed.Where(i => i.Kind == InputKind.Axis))
            {
                shown = input;
                foreach (var function in ControlsAxisFunctions)
                {
                    if (bindings.AxisFor(function)?.Axis == input.Index)
                    {
                        action = _strings.Get($"controls.axis_{ControllerBindings.FileName(function)}");
                        break;
                    }
                }

                break;
            }
        }

        string name = device.Profile.InputName(shown ?? pressed[0]);
        return action == null ? name : $"{name}: {action}";
    }

    /// <summary>Show text on the status row, if it has changed.</summary>
    private void SetControlsStatus(string text)
    {
        if (text == _controlsStatus)
        {
            return;
        }

        _controlsStatus = text;
        _hud.ClearRows(ControlsStatusRow * 8, ControlsStatusRow * 8 + 7);
        _colour = DashboardGreen;
        PrintControlsCentred(ControlsStatusRow, text);
    }

    /// <summary>
    /// Mark the controls on the picture: the highlighted row's in magenta,
    /// and those being pressed in green (around the outside of the first).
    /// </summary>
    private void DrawControlsMarkers(List<ControlInput> pressed)
    {
        using var group = _hud.Group(HudGroup.ControlMarkers);
        if (ControlsDevice is not { } device || ControlsPictureArea(device) is not { } area)
        {
            return;
        }

        void Mark(ControlInput input, int size, Ink ink)
        {
            if (device.Profile.Position(input) is not var (x, y))
            {
                return;
            }

            int left = (int)MathF.Round(area.X + x * area.Scale) - size, right = left + 2 * size;
            int top = (int)MathF.Round(area.Y + y * area.Scale) - size, bottom = top + 2 * size;
            _hud.DrawLine(left, top, right, top, ink);
            _hud.DrawLine(right, top, right, bottom, ink);
            _hud.DrawLine(right, bottom, left, bottom, ink);
            _hud.DrawLine(left, bottom, left, top, ink);
        }

        foreach (var input in ControlsSelectedInputs())
        {
            Mark(input, 4, Magenta);
        }

        foreach (var input in pressed)
        {
            Mark(input, 6, Ink.DashboardGreen);
        }
    }

    /// <summary>Move the highlight up or down (scrolling the list), or change the controller or page.</summary>
    private void MoveInControls(int x, int y)
    {
        if (y != 0)
        {
            int selected = Math.Clamp(_controlsSelected + y, 0, ControlsListCount + 1);
            if (selected == _controlsSelected)
            {
                return;
            }

            int previous = _controlsSelected;
            _controlsSelected = selected;
            if (ScrollControlsList())
            {
                DrawControlsList();
            }

            DrawControlsRow(previous);
            DrawControlsRow(selected);
            return;
        }

        if (_controlsSelected == 0)
        {
            int count = ControlsDevices.Count;
            if (count > 1)
            {
                SelectControlsDevice((_controlsDevice + x + count) % count);
                DrawControlsScreen();
            }

            return;
        }

        // Anywhere else, left and right change the page, keeping the
        // highlight on the list (at the top of the new page's list)
        _controlsPage = (_controlsPage + x + ControlsPages.Length) % ControlsPages.Length;
        if (_controlsSelected >= 2)
        {
            _controlsSelected = 2;
        }

        _controlsTop = 0;
        SelectControlsDevice(_controlsDevice);
        DrawControlsRow(1);
        DrawControlsList();
    }

    /// <summary>Scroll the list so the highlighted row is on the screen, returning true if it moved.</summary>
    private bool ScrollControlsList()
    {
        int item = _controlsSelected - 2;
        int top = _controlsTop;
        if (item < 0)
        {
            return false;
        }

        if (item < _controlsTop)
        {
            _controlsTop = item;
        }
        else if (item >= _controlsTop + ControlsListRows)
        {
            _controlsTop = item - ControlsListRows + 1;
        }

        return _controlsTop != top;
    }

    /// <summary>Act on a key press (other than a direction) on the Controls screen, returning true to close it.</summary>
    private bool PressInControls(int key)
    {
        int item = _controlsSelected - 2;
        switch (key)
        {
            case 0x1B or 'N':
                return true;
            case 0x0D when item == ControlsListCount - 1:
                ResetControls();
                break;
            case 0x0D when item >= 0:
                CaptureControl(item);
                break;
            case 0x7F when item >= 0 && item < ControlsListCount - 1:
                ChangeControlsBindings(bindings => ClearControlsRow(bindings, item));
                break;
        }

        return false;
    }

    /// <summary>Unbind a row of the list.</summary>
    private void ClearControlsRow(ControllerBindings bindings, int item)
    {
        if (ControlsPage is { } page)
        {
            bindings.Clear(page, ControlsActions(page)[item]);
        }
        else
        {
            bindings.SetAxis(ControlsAxisFunctions[item], null);
        }
    }

    /// <summary>Change the selected controller's bindings, save them, and redraw the list.</summary>
    private void ChangeControlsBindings(Action<ControllerBindings> change)
    {
        if (ControlsDevice is not { } device)
        {
            Beep();
            return;
        }

        var bindings = _controllers!.Bindings.For(device.Profile).Clone();
        change(bindings);
        _controllers.Bindings.Set(device.Profile, bindings);
        DrawControlsList();
    }

    /// <summary>Ask, and then put the selected controller's bindings back to its defaults.</summary>
    private void ResetControls()
    {
        if (ControlsDevice is not { } device)
        {
            Beep();
            return;
        }

        _controlsStatus = "";
        bool reset = Confirm(_strings.Get("controls.confirm_reset"));
        if (reset)
        {
            _controllers!.Bindings.Reset(device.Profile);
            DrawControlsList();
        }

        _hud.ClearRows(ControlsStatusRow * 8, 23 * 8 + 7);
        DrawControlsHelp();
    }

    /// <summary>
    /// Wait for the control to bind to a row of the list: on a page of
    /// buttons, a button or hat direction that is pressed (one that was
    /// already held must be let go of first), or on the axes page, an axis
    /// that moves well away from where it was (the way it moves deciding
    /// whether it's reversed). Escape cancels, and Delete unbinds the row.
    /// While waiting, the controllers do nothing else.
    /// </summary>
    private void CaptureControl(int item)
    {
        if (ControlsDevice is not { } device)
        {
            Beep();
            return;
        }

        var page = ControlsPage;
        _controlsStatus = "";
        _hud.ClearRows(ControlsStatusRow * 8, 23 * 8 + 7);
        _colour = Cyan;
        _cursorX = 1;
        _cursorY = ControlsStatusRow;
        PrintRaw(_strings.Get(page == null ? "controls.move_axis" : "controls.press_control"));
        _cursorX = 1;
        _cursorY = ControlsStatusRow + 1;
        _colour = Yellow;
        string name = ControlsRowName(item);
        if (page == null)
        {
            name += ": " + _strings.Get($"controls.hint_{ControllerBindings.FileName(ControlsAxisFunctions[item])}");
        }

        PrintRaw(FitControlsText(name, 30));
        _colour = Cyan;
        _cursorX = 1;
        _cursorY = 23;
        PrintRaw(_strings.Get("controls.cancel_help"));

        _controllers!.Capturing = true;
        try
        {
            var start = device.State;
            var ready = new HashSet<ControlInput>(device.Inputs().Where(i => !start.IsPressed(i)));
            while (true)
            {
                WaitForVsync();
                DrawControlsMarkers(ControlsPressed());
                int key = ReadKey();
                if (key == 0x1B || !ControlsDevices.Contains(device))
                {
                    return;
                }

                if (key == 0x7F)
                {
                    ChangeControlsBindings(bindings => ClearControlsRow(bindings, item));
                    return;
                }

                var state = device.State;
                if (page is { } buttonPage)
                {
                    foreach (var input in device.Inputs())
                    {
                        if (!state.IsPressed(input))
                        {
                            ready.Add(input);
                        }
                        else if (ready.Contains(input))
                        {
                            ChangeControlsBindings(bindings => bindings.Bind(buttonPage, ControlsActions(buttonPage)[item], input));
                            return;
                        }
                    }
                }
                else
                {
                    for (int axis = 0; axis < state.Axes.Length && axis < start.Axes.Length; axis++)
                    {
                        float moved = state.Axes[axis] - start.Axes[axis];
                        if (MathF.Abs(moved) >= ControlsAxisMovement)
                        {
                            ChangeControlsBindings(bindings => bindings.SetAxis(ControlsAxisFunctions[item], new AxisBinding(axis, moved < 0)));
                            return;
                        }
                    }
                }
            }
        }
        finally
        {
            _controllers.Capturing = false;
            _hud.ClearRows(ControlsStatusRow * 8, 23 * 8 + 7);
            DrawControlsHelp();
            SelectControlsDevice(_controlsDevice);
        }
    }
}

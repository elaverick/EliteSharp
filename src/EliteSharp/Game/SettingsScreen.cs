using EliteSharp.Input;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// The Settings screen, which isn't in the original: F12 (or RB on the
/// controller while paused) opens it over whatever is on the screen, and the
/// game waits until it is closed with Escape (or B, or F12 again). It has the
/// command line's options that make sense to change while playing, and the
/// volume of the sound effects and music. The cursor keys, D-pad or left stick
/// move up and down and change the highlighted setting (as does Return, or
/// A, for the settings that are on or off), and every change takes effect
/// straight away and is saved to the settings file (see <see cref="SettingsFile"/>).
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The settings on the Settings screen, in order.</summary>
    private enum Setting
    {
        FullScreen,
        WindowSize,
        Frame,
        VSync,
        SmoothMotion,
        Controller,
        SoundEffects,
        Music,
    }

    /// <summary>The text keys of the settings' names, in the order of <see cref="Setting"/>.</summary>
    private static readonly string[] SettingKeys =
    [
        "settings.full_screen", "settings.window_size", "settings.frame", "settings.vsync",
        "settings.smooth_motion", "settings.controller", "settings.sound_effects", "settings.music",
    ];

    /// <summary>The text row of the first setting (each is followed by a blank row).</summary>
    private const int SettingsFirstRow = 3;

    /// <summary>The column that the settings' values start in.</summary>
    private const int SettingsValueColumn = 18;

    /// <summary>Whether the Settings screen is open.</summary>
    private bool _settingsOpen;

    /// <summary>The highlighted setting.</summary>
    private Setting _settingSelected;

    /// <summary>
    /// Show the Settings screen over whatever is on the screen until it is
    /// closed, and then put back what was there.
    /// </summary>
    private void ShowSettings()
    {
        _settingsOpen = true;
        var screen = _hud.SaveScreen();
        var (cursorX, cursorY, textCase, colour) = (_cursorX, _cursorY, _textCase, _colour);
        var (notPrintingWord, capitaliseMask) = (_notPrintingWord, _capitaliseMask);
        var padContext = _padContextOverride;
        _padContextOverride = PadContext.Screen;
        try
        {
            _hud.ClearSpaceView();
            _hud.WorldHidden = true;
            _hud.Palette = SpacePalette.Trade;
            DrawBorderBox();
            DrawSettingsScreen();

            // Alt+Enter can switch full screen while the screen is open, which
            // shows or hides the window size
            bool fullScreen = _options.FullScreen;
            RunListScreen(MoveInSettings, PressInSettings, functionKeysLeave: false, everyFrame: () =>
            {
                if (_options.FullScreen != fullScreen)
                {
                    fullScreen = _options.FullScreen;
                    DrawSettingRows();
                }
            });

            // Wait for the key that closed the screen to be let go of, so the
            // game doesn't act on it as well (Escape in flight launches the
            // escape pod)
            do
            {
                WaitForVsync();
            }
            while (AnyKeyHeld());

            _keyboard.ClearLatches();
        }
        finally
        {
            _hud.RestoreScreen(screen);
            (_cursorX, _cursorY, _textCase, _colour) = (cursorX, cursorY, textCase, colour);
            (_notPrintingWord, _capitaliseMask) = (notPrintingWord, capitaliseMask);
            _padContextOverride = padContext;
            _settingsOpen = false;
        }
    }

    /// <summary>Draw the title, every setting and the instructions.</summary>
    private void DrawSettingsScreen()
    {
        _colour = Cyan;
        _textCase = 0;
        _cursorX = 12;
        _cursorY = 1;
        PrintTitle("settings.title");
        DrawSettingRows();

        ClearBottomRows();
        _cursorY = 22;
        PrintText("settings.change");
        _cursorX = 1;
        _cursorY = 23;
        _textCase = 0x80;
        PrintText("settings.close");
    }

    /// <summary>
    /// The settings that are shown: all of them, except the window size in
    /// full screen (where it does nothing).
    /// </summary>
    private List<Setting> VisibleSettings() =>
        [.. Enum.GetValues<Setting>().Where(s => s != Setting.WindowSize || !_options.FullScreen)];

    /// <summary>Clear the settings' rows and draw the settings that are shown.</summary>
    private void DrawSettingRows()
    {
        var visible = VisibleSettings();
        if (!visible.Contains(_settingSelected))
        {
            _settingSelected = Setting.FullScreen;
        }

        _hud.ClearRows(SettingsFirstRow * 8, (SettingsFirstRow + Enum.GetValues<Setting>().Length * 2) * 8 - 1);
        foreach (var setting in visible)
        {
            DrawSettingRow(setting);
        }
    }

    /// <summary>Draw a setting's row: its name and value, highlighted if it's the selected setting.</summary>
    private void DrawSettingRow(Setting setting)
    {
        int row = SettingsFirstRow + VisibleSettings().IndexOf(setting) * 2;
        StartListRow(row, setting == _settingSelected);
        _cursorX = 2;
        PrintText(SettingKeys[(int)setting]);
        _cursorX = SettingsValueColumn;
        _textCase = 0x80;

        switch (setting)
        {
            case Setting.FullScreen:
                PrintOnOff(_options.FullScreen);
                break;
            case Setting.WindowSize:
                if (_options.WindowSize is var (width, height))
                {
                    PrintPlainNumber(width);
                    PrintCharacter(' ');
                    PrintCharacter('x');
                    PrintCharacter(' ');
                    PrintPlainNumber(height);
                }

                break;
            case Setting.Frame:
                PrintText(_options.FourByThreeFrame ? "settings.four_by_three" : "settings.wide");
                break;
            case Setting.VSync:
                PrintOnOff(_options.VSync);
                break;
            case Setting.SmoothMotion:
                PrintOnOff(_options.Interpolate);
                break;
            case Setting.Controller:
                PrintOnOff(_options.Gamepad);
                break;
            case Setting.SoundEffects:
                DrawSlider(row, _options.EffectsVolume);
                break;
            case Setting.Music:
                DrawSlider(row, _options.MusicVolume);
                break;
        }
    }

    /// <summary>Print "On" or "Off".</summary>
    private void PrintOnOff(bool on) => PrintText(on ? "settings.on" : "settings.off");

    /// <summary>Print a number without any padding.</summary>
    private void PrintPlainNumber(int number)
    {
        foreach (char digit in number.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            PrintCharacter(digit);
        }
    }

    /// <summary>
    /// Draw a volume slider in the value column: a yellow block for each step
    /// of the volume and a dot for each step above it, then the volume.
    /// </summary>
    private void DrawSlider(int row, int volume)
    {
        int x = SettingsValueColumn * 8;
        int y = row * 8;
        for (int step = 0; step < GameOptions.MaxVolume; step++)
        {
            if (step < volume)
            {
                _hud.DrawRect(x + step * 8 + 1, y + 1, 6, 6, Yellow);
            }
            else
            {
                _hud.DrawRect(x + step * 8 + 3, y + 3, 2, 2, Cyan);
            }
        }

        _cursorX = SettingsValueColumn + GameOptions.MaxVolume + 1;
        PrintPlainNumber(volume);
    }

    /// <summary>Move the highlight up or down, or change the highlighted setting.</summary>
    private void MoveInSettings(int x, int y)
    {
        if (y != 0)
        {
            var visible = VisibleSettings();
            var selected = visible[Math.Clamp(visible.IndexOf(_settingSelected) + y, 0, visible.Count - 1)];
            if (selected != _settingSelected)
            {
                var previous = _settingSelected;
                _settingSelected = selected;
                DrawSettingRow(previous);
                DrawSettingRow(selected);
            }

            return;
        }

        ChangeSetting(_settingSelected, x);
    }

    /// <summary>Act on a key press (other than a direction) on the Settings screen, returning true to close it.</summary>
    private bool PressInSettings(int key)
    {
        switch (key)
        {
            case 0x1B or 'N':
                return true;
            case 0x0D:
                ChangeSetting(_settingSelected, 1);
                break;
        }

        return false;
    }

    /// <summary>
    /// Change a setting one step in the given direction (a setting that is on
    /// or off just switches), and redraw it. The window picks the change up
    /// on its next frame (see Program.cs).
    /// </summary>
    private void ChangeSetting(Setting setting, int step)
    {
        switch (setting)
        {
            case Setting.FullScreen:
                // The window size comes and goes, so the rows below it move
                _options.FullScreen = !_options.FullScreen;
                DrawSettingRows();
                return;
            case Setting.WindowSize:
                _options.WindowSize = NextWindowSize(step);
                break;
            case Setting.Frame:
                _options.FourByThreeFrame = !_options.FourByThreeFrame;
                break;
            case Setting.VSync:
                _options.VSync = !_options.VSync;
                break;
            case Setting.SmoothMotion:
                _options.Interpolate = !_options.Interpolate;
                break;
            case Setting.Controller:
                _options.Gamepad = !_options.Gamepad;
                break;
            case Setting.SoundEffects:
                _options.EffectsVolume += step;
                ApplyEffectsVolume();
                Beep();
                break;
            case Setting.Music:
                _options.MusicVolume += step;
                ApplyMusicVolume();
                break;
        }

        DrawSettingRow(setting);
    }

    /// <summary>
    /// The window size one step up or down from the current one: the
    /// original's screen (256 x 248) at the next whole scale, from 1 to 8.
    /// </summary>
    private (int Width, int Height) NextWindowSize(int step)
    {
        int width = _options.WindowSize?.Width ?? 256 * _options.Scale;
        int scale = step > 0 ? width / 256 + 1 : (width + 255) / 256 - 1;
        scale = Math.Clamp(scale, 1, 8);
        return (256 * scale, 248 * scale);
    }

    /// <summary>Set the music's volume from the settings (as the sound effects' volume is set).</summary>
    private void ApplyMusicVolume()
    {
        if (_sound != null)
        {
            float level = (float)_options.MusicVolume / GameOptions.MaxVolume;
            _sound.MusicGain = level * level;
        }
    }

    /// <summary>
    /// Set the sound effects' volume from the settings, as a gain that rises
    /// with the square of the setting (which sounds more even than a straight line).
    /// </summary>
    private void ApplyEffectsVolume()
    {
        if (_sound != null)
        {
            float level = (float)_options.EffectsVolume / GameOptions.MaxVolume;
            _sound.EffectsGain = level * level;
        }
    }
}

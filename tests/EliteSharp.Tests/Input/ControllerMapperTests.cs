using EliteSharp.Input;

namespace EliteSharp.Tests.Input;

/// <summary>
/// The controllers' buttons press BBC keys, through each controller's
/// bindings, and its bound axes give the roll, pitch, speed triggers and
/// throttle. These use the test scripts' gamepad, which has the gamepad
/// profile (an Xbox controller's), whose defaults are the controls in the
/// README's picture.
/// </summary>
public sealed class ControllerMapperTests
{
    private static readonly ControllerProfiles Profiles = ControllerProfiles.Load([Path.Combine(GameAssets.BaseFolder, "Controllers")]);

    private readonly BbcKeyboard _keyboard = new();
    private readonly ControllerBindingStore _bindings = ControllerBindingStore.InMemory();
    private readonly Controllers _controllers;

    public ControllerMapperTests()
    {
        _controllers = Controllers.CreateForTests(_keyboard, Profiles, _bindings);
    }

    /// <summary>Press a button (and poll), returning the keys it pressed.</summary>
    private List<int> Press(PadButton button, PadContext context = PadContext.SpaceView)
    {
        _controllers.Mapper.Context = context;
        _controllers.InjectButton(button, true);
        _controllers.Poll();
        var keys = Enumerable.Range(0, 128).Where(_keyboard.IsHeld).ToList();
        _controllers.InjectButton(button, false);
        _controllers.Poll();
        _keyboard.ClearLatches();
        return keys;
    }

    [Theory]
    [InlineData(PadButton.A, PadContext.SpaceView, BbcKeyboard.A)]
    [InlineData(PadButton.B, PadContext.SpaceView, BbcKeyboard.M)]
    [InlineData(PadButton.DpadDown, PadContext.SpaceView, BbcKeyboard.F1)]
    [InlineData(PadButton.Start, PadContext.SpaceView, BbcKeyboard.Copy)]
    [InlineData(PadButton.A, PadContext.Screen, BbcKeyboard.Return)]
    [InlineData(PadButton.X, PadContext.Screen, BbcKeyboard.Delete)]
    [InlineData(PadButton.Start, PadContext.Screen, BbcKeyboard.F0)]
    [InlineData(PadButton.Start, PadContext.Paused, BbcKeyboard.Delete)]
    [InlineData(PadButton.Back, PadContext.Paused, BbcKeyboard.Escape)]
    [InlineData(PadButton.B, PadContext.TextEntry, BbcKeyboard.Escape)]
    public void AGamepadsButtonsPressTheKeysInTheReadme(PadButton button, PadContext context, int key)
    {
        Assert.Equal([key], Press(button, context));
    }

    [Fact]
    public void HoldingLbGivesTheSecondFunctions()
    {
        _controllers.InjectButton(PadButton.LeftShoulder, true);

        Assert.Equal([BbcKeyboard.H], Press(PadButton.A));
        Assert.Equal([BbcKeyboard.Ctrl, BbcKeyboard.H], Press(PadButton.B));
        Assert.Equal([BbcKeyboard.Escape], Press(PadButton.Back));
    }

    [Fact]
    public void AChangedBindingTakesEffect()
    {
        var bindings = Profiles.Gamepad.Defaults.Clone();
        bindings.Bind(ControlPage.Flight, PadAction.Ecm, ControlInput.Button((int)PadButton.A));
        _bindings.Set(Profiles.Gamepad, bindings);

        Assert.Equal([BbcKeyboard.E], Press(PadButton.A));
    }

    [Fact]
    public void NothingIsPressedWhileTheControlsScreenWaitsForAControl()
    {
        _controllers.Capturing = true;

        Assert.Empty(Press(PadButton.A));
    }

    [Fact]
    public void TheSticksAndTriggersGiveRollPitchAndSpeed()
    {
        _controllers.InjectAxis("lx", 0.5f);
        _controllers.InjectAxis("ly", -0.25f);
        _controllers.InjectAxis("rt", 1);
        _controllers.Poll();

        var axes = _controllers.Axes;
        Assert.Equal(0.5f, axes.Roll);
        Assert.Equal(-0.25f, axes.Pitch);
        Assert.Equal(1, axes.SpeedUp);
        Assert.Null(axes.Throttle);
    }

    [Fact]
    public void AnAxisBoundToTheThrottleGivesItsPosition()
    {
        var bindings = Profiles.Gamepad.Defaults.Clone();
        bindings.SetAxis(AxisFunction.Throttle, new AxisBinding(1, Reversed: true));
        _bindings.Set(Profiles.Gamepad, bindings);

        // Pushed fully forward (which is -1 on the y-axis, so reversed)
        _controllers.InjectAxis("ly", -1);
        _controllers.Poll();
        Assert.Equal(1, _controllers.Axes.Throttle);

        _controllers.InjectAxis("ly", 0);
        _controllers.Poll();
        Assert.Equal(0.5f, _controllers.Axes.Throttle);
    }
}

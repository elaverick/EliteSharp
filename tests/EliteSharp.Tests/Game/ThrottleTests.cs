using EliteSharp.Input;

namespace EliteSharp.Tests.Game;

/// <summary>
/// A controller's throttle (such as an X52's) is read as the BBC Master's
/// Bitstik rotation, whose high byte the main loop divides by 4 to set the
/// speed directly (BS2), so the throttle's travel covers the speeds from 0 to
/// 40.
/// </summary>
public sealed class ThrottleTests
{
    private static readonly ControllerProfiles Profiles = ControllerProfiles.Load([Path.Combine(GameAssets.BaseFolder, "Controllers")]);

    private readonly Controllers _controllers;
    private readonly PrivateGame _game;

    public ThrottleTests()
    {
        var keyboard = new BbcKeyboard();
        var bindings = ControllerBindingStore.InMemory();
        var throttle = Profiles.Gamepad.Defaults.Clone();
        throttle.SetAxis(AxisFunction.Throttle, new AxisBinding(1, Reversed: true));
        bindings.Set(Profiles.Gamepad, throttle);
        _controllers = Controllers.CreateForTests(keyboard, Profiles, bindings);
        _game = new PrivateGame(keyboard: keyboard, controllers: _controllers);
    }

    private int? Throttle(float stickY)
    {
        _controllers.InjectAxis("ly", stickY);
        _controllers.Poll();
        return (int?)_game.Call("ReadPadThrottle");
    }

    [Theory]
    [InlineData(-1f, 40)]
    [InlineData(0f, 20)]
    [InlineData(1f, 0)]
    public void TheThrottlesTravelCoversEverySpeed(float stickY, int speed)
    {
        Assert.Equal(speed, Throttle(stickY) >> 2);
    }

    [Fact]
    public void TheThrottleIsLeftOutWhileTheDockingComputerIsOn()
    {
        _game.Set("_autoDocking", 0xFF);

        Assert.Null(Throttle(-1));
    }

    [Fact]
    public void ThereIsNoThrottleUnlessOneIsBound()
    {
        var game = new PrivateGame(controllers: Controllers.CreateForTests(new BbcKeyboard(), Profiles, ControllerBindingStore.InMemory()));

        Assert.Null(game.Call("ReadPadThrottle"));
    }
}

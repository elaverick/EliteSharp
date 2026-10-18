using EliteSharp.Input;
using EliteSharp.Rendering;

namespace EliteSharp.Tests.Input;

/// <summary>
/// The controller profiles in Assets/Controllers say what each model of
/// controller is called, how to recognise it, what its controls are called
/// and where they are on its picture, and what they do by default.
/// </summary>
public sealed class ControllerProfileTests
{
    private static readonly ControllerProfiles Profiles = ControllerProfiles.Load([Path.Combine(GameAssets.BaseFolder, "Controllers")]);

    [Fact]
    public void TheGamesProfilesLoad()
    {
        Assert.Equal(["generic", "grs-yoke", "ps4", "ps5", "saitek-yoke", "x52", "x52-pro", "xbox"], Profiles.All.Select(p => p.Id).Order());
        Assert.True(Profiles.Gamepad.IsGamepad);
        Assert.Equal(ControllerKind.Joystick, Profiles.Generic.Kind);
    }

    [Theory]
    [InlineData(0x06A3, 0x075C, "x52")]
    [InlineData(0x06A3, 0x0255, "x52")]
    [InlineData(0x06A3, 0x0762, "x52-pro")]
    [InlineData(0x06A3, 0x0BAC, "saitek-yoke")]
    public void AControllerIsRecognisedByItsUsbIds(int vendor, int product, string id)
    {
        Assert.Equal(id, Profiles.Find((ushort)vendor, (ushort)product)?.Id);
    }

    [Theory]
    [InlineData("ps5", "ps5", "Cross")]
    [InlineData("ps4", "ps4", "Cross")]
    [InlineData("ps3", "ps4", "Cross")]
    [InlineData("xboxone", "xbox", "A")]
    [InlineData("switch_pro", "xbox", "A")]
    public void AGamepadHasTheProfileForItsKind(string type, string id, string firstButton)
    {
        var profile = Profiles.GamepadFor(type);

        Assert.Equal(id, profile.Id);
        Assert.Equal(firstButton, profile.InputName(ControlInput.Button(0)));
    }

    [Fact]
    public void ThePlayStationControllersNameTheirButtonsTheirOwnWay()
    {
        Assert.Equal("Create", Profiles.Find("ps5")!.InputName(ControlInput.Button(4)));
        Assert.Equal("Share", Profiles.Find("ps4")!.InputName(ControlInput.Button(4)));
        Assert.Equal("R2", Profiles.Find("ps5")!.InputName(ControlInput.Axis(5)));
    }

    [Theory]
    [InlineData("GRS Flight Yoke")]
    [InlineData("Thunderstick Studios grs star wars yoke")]
    public void AControllerWhoseIdsArentKnownIsRecognisedByItsName(string name)
    {
        var profile = Profiles.Find(0x1234, 0x5678, name);

        Assert.Equal("grs-yoke", profile?.Id);
        Assert.Equal(ControllerKind.Yoke, profile!.Kind);
        Assert.Equal("R Trigger", profile.InputName(ControlInput.Button(1)));
    }

    [Fact]
    public void AnUnknownControllerHasNoProfileOfItsOwn()
    {
        Assert.Null(Profiles.Find(0x1234, 0x5678));
        Assert.Null(Profiles.Find(0x1234, 0x5678, "USB Gamepad"));
    }

    [Fact]
    public void EveryControlOnAPictureIsInsideIt()
    {
        foreach (var profile in Profiles.All.Where(p => p.ImagePath != null))
        {
            var (width, height) = PictureImage.ReadSize(profile.ImagePath!);
            foreach (var control in profile.Buttons.Concat(profile.Axes).Concat(profile.Hats).Where(c => c.X != null))
            {
                Assert.InRange(control.X!.Value, 0, width - 1);
                Assert.InRange(control.Y!.Value, 0, height - 1);
            }
        }
    }

    [Fact]
    public void TheX52sThrottleSetsTheSpeedAndItsStickRolls()
    {
        var x52 = Profiles.Find("x52")!;

        Assert.Equal(new AxisBinding(2, Reversed: true), x52.Defaults.AxisFor(AxisFunction.Throttle));
        Assert.Equal(new AxisBinding(0, Reversed: false), x52.Defaults.AxisFor(AxisFunction.Roll));
        Assert.Equal(new AxisBinding(1, Reversed: false), x52.Defaults.AxisFor(AxisFunction.Pitch));
        Assert.Equal("Throttle", x52.InputName(ControlInput.Axis(2)));
    }

    [Fact]
    public void TheYokesLeverSetsTheSpeedAndTurningItRolls()
    {
        var yoke = Profiles.Find("saitek-yoke")!;

        Assert.Equal(ControllerKind.Yoke, yoke.Kind);
        Assert.Equal(new AxisBinding(2, Reversed: true), yoke.Defaults.AxisFor(AxisFunction.Throttle));
        Assert.Equal(new AxisBinding(0, Reversed: false), yoke.Defaults.AxisFor(AxisFunction.Roll));
    }

    [Fact]
    public void ControlsAreNamedFromTheProfileOrElseByNumber()
    {
        var x52 = Profiles.Find("x52")!;

        Assert.Equal("Trigger", x52.InputName(ControlInput.Button(0)));
        Assert.Equal("Clutch", x52.InputName(ControlInput.Button(29)));
        Assert.Equal("Button 31", x52.InputName(ControlInput.Button(30)));
        Assert.Equal("Hat 1 Left", x52.InputName(ControlInput.Hat(0, HatDirection.Left)));
        Assert.Equal("Axis 8", x52.InputName(ControlInput.Axis(7)));
    }

    [Fact]
    public void AButtonWhoseNameIsntKnownIsShownByNumber()
    {
        var pro = Profiles.Find("x52-pro")!;

        Assert.Equal("Button 16", pro.InputName(ControlInput.Button(15)));
        Assert.Null(pro.Position(ControlInput.Button(15)));
        Assert.Equal("Hat 2 Up", pro.InputName(ControlInput.Button(19)));
    }

    [Fact]
    public void AProfileThatIsntValidIsReported()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            ControllerProfile.Parse("name: Test\ndevices: [nonsense]\n", "test", "test.yml", "."));

        Assert.Contains("test.yml", e.Message);
        Assert.Contains("vendor and product IDs", e.Message);
    }
}

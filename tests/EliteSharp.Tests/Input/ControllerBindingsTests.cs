using EliteSharp.Input;
using YamlDotNet.RepresentationModel;

namespace EliteSharp.Tests.Input;

/// <summary>
/// A controller's bindings say which axis does each function and, on each
/// page, which buttons do each action. They are changed on the Controls screen
/// and saved in controllers.yml.
/// </summary>
public sealed class ControllerBindingsTests
{
    private static ControllerBindings Read(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return ControllerBindings.Read((YamlMappingNode)stream.Documents[0].RootNode, "test.yml");
    }

    [Fact]
    public void BindingAnInputTakesItFromTheOtherActionOnThePage()
    {
        var bindings = new ControllerBindings();
        bindings.Bind(ControlPage.Flight, PadAction.FireLaser, ControlInput.Button(0));
        bindings.Bind(ControlPage.Screens, PadAction.Select, ControlInput.Button(0));

        bindings.Bind(ControlPage.Flight, PadAction.Ecm, ControlInput.Button(0));

        Assert.Empty(bindings.InputsFor(ControlPage.Flight, PadAction.FireLaser));
        Assert.Equal(PadAction.Ecm, bindings.ActionFor(ControlPage.Flight, ControlInput.Button(0)));
        Assert.Equal(PadAction.Select, bindings.ActionFor(ControlPage.Screens, ControlInput.Button(0)));
    }

    [Fact]
    public void BindingAnActionReplacesItsInputs()
    {
        var bindings = Read("paused:\n  resume: [button 6, button 1]\n");

        bindings.Bind(ControlPage.Paused, PadAction.Resume, ControlInput.Hat(0, HatDirection.Up));

        Assert.Equal([ControlInput.Hat(0, HatDirection.Up)], bindings.InputsFor(ControlPage.Paused, PadAction.Resume));
    }

    [Fact]
    public void BindingsAreWrittenAndReadBack()
    {
        var bindings = new ControllerBindings();
        bindings.SetAxis(AxisFunction.Throttle, new AxisBinding(2, Reversed: true));
        bindings.Add(ControlPage.Paused, PadAction.Resume, ControlInput.Button(6));
        bindings.Add(ControlPage.Paused, PadAction.Resume, ControlInput.Button(1));
        bindings.Bind(ControlPage.Flight, PadAction.FrontView, ControlInput.Hat(0, HatDirection.Up));

        var lines = new List<string>();
        bindings.Write(lines, 0);
        var read = Read(string.Join('\n', lines));

        Assert.Equal(new AxisBinding(2, Reversed: true), read.AxisFor(AxisFunction.Throttle));
        Assert.Equal([ControlInput.Button(6), ControlInput.Button(1)], read.InputsFor(ControlPage.Paused, PadAction.Resume));
        Assert.Equal(PadAction.FrontView, read.ActionFor(ControlPage.Flight, ControlInput.Hat(0, HatDirection.Up)));
    }

    [Fact]
    public void AnActionThatIsntOnThePageIsReported()
    {
        var e = Assert.Throws<InvalidDataException>(() => Read("typing:\n  fire_laser: button 0\n"));
        Assert.Equal("test.yml: 'fire_laser' isn't an action on the typing page", e.Message);
    }

    [Fact]
    public void AnInputThatIsntValidIsReported()
    {
        var e = Assert.Throws<InvalidDataException>(() => Read("flight:\n  fire_laser: trigger\n"));
        Assert.Contains("'button 3' or 'hat 0 up', not 'trigger'", e.Message);
    }

    [Fact]
    public void ThePlayersBindingsAreSavedAndReloaded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "elitesharp-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, ControllerBindingStore.FileName);
        try
        {
            var profile = new ControllerProfile { Id = "test", Name = "Test", Defaults = Read("flight:\n  fire_laser: button 0\n") };
            var store = ControllerBindingStore.Load(path);
            Assert.Same(profile.Defaults, store.For(profile));

            var changed = store.For(profile).Clone();
            changed.Bind(ControlPage.Flight, PadAction.FireLaser, ControlInput.Button(5));
            store.Set(profile, changed);

            var reloaded = ControllerBindingStore.Load(path);
            Assert.True(reloaded.IsChanged(profile));
            Assert.Equal([ControlInput.Button(5)], reloaded.For(profile).InputsFor(ControlPage.Flight, PadAction.FireLaser));

            reloaded.Reset(profile);
            Assert.Same(profile.Defaults, ControllerBindingStore.Load(path).For(profile));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}

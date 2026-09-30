using System.Text.RegularExpressions;
using EliteSharp.Game.Missions;
using EliteSharp.Game.Ships;

namespace EliteSharp.Tests.Missions;

public sealed class MissionLoaderTests
{
    /// <summary>A small, valid mission, which the tests break in different ways.</summary>
    private const string TestMission = """
        id: test-mission
        name: Test
        progress:
          bits: [4, 5]
          stages: { not-started: 0, done: 1 }
        values:
          greeting: Hello
        docking:
          - name: offer
            when: { stage: not-started, galaxy: 1, system: Lave }
            steps:
              - setStage: done
              - text:
                  row: 10
                  lines:
                    - "{{greeting}} {{commanderName}}"
        """;

    /// <summary>The shipped mission files, by name.</summary>
    private static Dictionary<string, string> ShippedFiles() =>
        Directory.EnumerateFiles(MissionLoader.DefaultFolder, "*.yml")
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllText, StringComparer.Ordinal);

    private static MissionCatalogue LoadWith(string name, string text)
    {
        var files = ShippedFiles();
        files[name] = text;
        return MissionLoader.Load(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (f.Key, f.Value)));
    }

    private static MissionLoadException LoadFails(string text) =>
        Assert.Throws<MissionLoadException>(() => LoadWith("test-mission.yml", text));

    [Fact]
    public void TheShippedMissionsLoad()
    {
        var catalogue = MissionLoader.Load(MissionLoader.DefaultFolder);
        Assert.Equal(["constrictor", "thargoid-plans"], catalogue.Missions.Select(m => m.Id));
        Assert.Equal(["incomingMessage", "messageEnds"], catalogue.Sequences.Keys.Order());

        var constrictor = catalogue.Get("constrictor");
        Assert.Equal(ShipType.Constrictor, constrictor.Target!.ShipType);
        Assert.Equal("ORARRA", constrictor.Target.System.Name);
        Assert.Equal(22, constrictor.SystemDescriptions!.Systems.Count);

        var plans = catalogue.Get("thargoid-plans");
        Assert.Equal([new StageRequirement("constrictor", 2)], plans.Requires);
        Assert.Equal(["offer", "briefing", "debriefing"], plans.DockingEvents.Select(e => e.Name));
    }

    [Fact]
    public void TheValidTestMissionLoads()
    {
        var catalogue = LoadWith("test-mission.yml", TestMission);
        Assert.Equal(3, catalogue.Missions.Count);
    }

    [Fact]
    public void TheExampleInTheReadmeLoads()
    {
        string readme = File.ReadAllText(Path.Combine(MissionLoader.DefaultFolder, "README.md")).ReplaceLineEndings("\n");
        string section = readme[readme.IndexOf("## Adding a mission", StringComparison.Ordinal)..];
        string example = Regex.Match(section, "```yaml\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
        var catalogue = LoadWith("my-mission.yml", example);
        Assert.Contains(catalogue.Missions, m => m.Id == "my-mission");
    }

    [Theory]
    [InlineData("setStage: done", "setStage: finished", "docking[0].steps[0].setStage", "mission 'test-mission' has no stage 'finished'")]
    [InlineData("system: Lave", "system: Atlantis", "docking[0].when.system", "there is no system called 'Atlantis' in galaxy 1")]
    [InlineData("galaxy: 1,", "galaxy: 9,", "docking[0].when.galaxy", "expected a whole number from 1 to 8, not '9'")]
    [InlineData("- setStage: done", "- introduceShip: tie-fighter", "docking[0].steps[0].introduceShip", "there is no ship 'tie-fighter'")]
    [InlineData("- setStage: done", "- teleport: done", "docking[0].steps[0].teleport", "unknown command 'teleport'")]
    [InlineData("- setStage: done", "- showShipUntilKey", "docking[0].steps[0]", "'showShipUntilKey' needs an 'introduceShip' earlier")]
    [InlineData("- setStage: done", "- run: fanfare", "docking[0].steps[0].run", "there is no shared sequence 'fanfare'")]
    [InlineData("- setStage: done", "- pause: 7", "docking[0].steps[0].pause", "expected a number of seconds")]
    [InlineData("- setStage: done", "- addCash: lots", "docking[0].steps[0].addCash", "expected an amount of credits")]
    [InlineData("{{greeting}}", "{{salutation}}", "docking[0].steps[1].text.lines[0]", "'{{salutation}}' has no value")]
    [InlineData("{{commanderName}}", "{{commanderName", "docking[0].steps[1].text.lines[0]", "has no matching '}}'")]
    [InlineData("row: 10", "row: 30", "docking[0].steps[1].text.row", "expected a whole number from 1 to 23")]
    [InlineData("name: Test", "name: Test\ncolour: red", "colour", "unknown field 'colour'")]
    [InlineData("name: offer", "name: offer\n    after: 3", "docking[0].after", "unknown field 'after'")]
    [InlineData("stage: not-started,", "stage: started,", "docking[0].when.stage", "mission 'test-mission' has no stage 'started'")]
    public void InvalidMissionsGiveUsefulErrors(string find, string replace, string path, string message)
    {
        Assert.Contains(find, TestMission);
        var error = LoadFails(TestMission.Replace(find, replace, StringComparison.Ordinal));
        string text = Assert.Single(error.Errors);
        Assert.StartsWith("test-mission.yml, mission 'test-mission'", text, StringComparison.Ordinal);
        Assert.Contains($", {path} (line ", text, StringComparison.Ordinal);
        Assert.Contains(message, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdMustMatchTheFileName()
    {
        var error = LoadFails(TestMission.Replace("id: test-mission", "id: other-mission", StringComparison.Ordinal));
        Assert.Contains("the mission's id is 'other-mission', but it must be the same as the file name ('test-mission')", Assert.Single(error.Errors));
    }

    [Fact]
    public void MissingFieldsAreReported()
    {
        var error = LoadFails(TestMission.ReplaceLineEndings("\n").Replace("name: Test\n", "", StringComparison.Ordinal));
        Assert.Contains("the field 'name' is missing", Assert.Single(error.Errors));
    }

    [Fact]
    public void MissionsCantShareBitsOfTheMissionByte()
    {
        var error = LoadFails(TestMission.Replace("bits: [4, 5]", "bits: [1, 2]", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("uses the same bits of the mission byte as mission 'constrictor'", StringComparison.Ordinal));
    }

    [Fact]
    public void GalaxyValuesMustCoverTheGalaxiesWhereTheyAreShown()
    {
        var error = LoadFails(TestMission.Replace("greeting: Hello", "greeting:\n    byGalaxy: { 2: Hello }", StringComparison.Ordinal));
        Assert.Contains("'{{greeting}}' has no value for galaxy 1, where this text can be shown", Assert.Single(error.Errors));
    }

    [Fact]
    public void ValuesCantReferToThemselves()
    {
        var error = LoadFails(TestMission.Replace("greeting: Hello", "greeting: \"Hello {{greeting}}\"", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("'{{greeting}}' refers to itself", StringComparison.Ordinal));
    }

    [Fact]
    public void YamlSyntaxErrorsGiveTheLine()
    {
        var error = LoadFails(TestMission.Replace("name: Test", "name: \"Test", StringComparison.Ordinal));
        Assert.Matches(@"^test-mission\.yml \(line \d+\): ", Assert.Single(error.Errors));
    }

    [Fact]
    public void AllTheProblemsAreReportedTogether()
    {
        string broken = TestMission
            .Replace("setStage: done", "setStage: finished", StringComparison.Ordinal)
            .Replace("system: Lave", "system: Atlantis", StringComparison.Ordinal);
        var error = LoadFails(broken);
        Assert.Equal(2, error.Errors.Count);
        Assert.Contains("mission 'test-mission' has no stage 'finished'", error.Message, StringComparison.Ordinal);
        Assert.Contains("there is no system called 'Atlantis'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedSequencesCantChangeAMission()
    {
        var files = ShippedFiles();
        files["extra.yml"] = "sequences:\n  cheat:\n    - addCash: 1000\n";
        var error = Assert.Throws<MissionLoadException>(() => MissionLoader.Load(files.Select(f => (f.Key, f.Value))));
        Assert.Contains("extra.yml, sequences.cheat[0].addCash (line 3): 'addCash' can only be used in a mission", Assert.Single(error.Errors));
    }
}

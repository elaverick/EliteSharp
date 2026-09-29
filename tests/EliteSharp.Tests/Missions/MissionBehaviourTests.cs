using EliteSharp.Game.Missions;
using EliteSharp.Game.Ships;

namespace EliteSharp.Tests.Missions;

/// <summary>
/// Checks that the missions in Assets/Missions behave exactly as the original
/// game's mission code did. Each test compares the runtime with a model of
/// the original's logic (from DOENTRY, KILLSHP, the main loop's spawning and
/// PDESC), across every value of the mission byte.
/// </summary>
public sealed class MissionBehaviourTests
{
    private static readonly MissionRuntime Runtime = new(MissionLoader.Load(MissionLoader.DefaultFolder));

    private static readonly (int X, int Y)[] Systems = [(215, 84), (63, 72), (144, 33), (20, 173), (0, 0)];

    private static readonly int[] KillTallies = [0, 17, 255, 256, 1000, 1279, 1280, 5000, 0xFFFF];

    // ------------------------------------------------------------------------
    // The original's logic
    // ------------------------------------------------------------------------

    /// <summary>DOENTRY: the mission event the original runs when we dock, if any.</summary>
    private static string? OriginalDockingEvent(int status, int galaxy, int tally, (int X, int Y) system)
    {
        int mission1 = status & 0b11;
        if (mission1 == 0)
        {
            return (tally >> 8) != 0 && (galaxy >> 1) == 0 ? "constrictor/briefing" : null;
        }

        if (mission1 == 0b11)
        {
            return "constrictor/debriefing";
        }

        if (galaxy == 2)
        {
            switch (status & 0b1111)
            {
                case 0b0010:
                    return (tally >> 8) >= 5 ? "thargoid-plans/offer" : null;
                case 0b0110:
                    return system == (215, 84) ? "thargoid-plans/briefing" : null;
                case 0b1010:
                    return system == (63, 72) ? "thargoid-plans/debriefing" : null;
            }
        }

        return null;
    }

    /// <summary>BRIEF, DEBRIEF, BRIEF2, BRIEF3 and DEBRIEF2: the state after each event.</summary>
    private static (int Status, int Cash, int Tally, bool NavalEnergyUnit) OriginalStateAfter(string? dockingEvent, int status, int tally) =>
        dockingEvent switch
        {
            "constrictor/briefing" => (status | 1, 0, tally, false),
            "constrictor/debriefing" => (status & 0xFE, 50000, tally, false),
            "thargoid-plans/offer" => (status | 0b100, 0, tally, false),
            "thargoid-plans/briefing" => ((status & 0xF0) | 0b1010, 0, tally, false),
            "thargoid-plans/debriefing" => (status | 0b100, 0, (tally + 0x100) & 0xFFFF, true),
            _ => (status, 0, tally, false),
        };

    /// <summary>The systems (galaxy from 0, system number) where the original shows a mission 1 clue.</summary>
    private static readonly (int Galaxy, int System)[] OriginalClueSystems =
    [
        (0, 150), (0, 36), (0, 28), (1, 253), (1, 79), (1, 53), (1, 118), (1, 32), (1, 68), (1, 164), (1, 220),
        (1, 106), (1, 16), (1, 162), (1, 3), (1, 107), (1, 26), (1, 192), (1, 184), (1, 5), (2, 101), (1, 193),
    ];

    // ------------------------------------------------------------------------
    // Availability and progress
    // ------------------------------------------------------------------------

    [Fact]
    public void DockingEventsHappenUnderTheSameConditionsAsTheOriginal()
    {
        int checkedCount = 0;
        for (int status = 0; status < 256; status++)
        {
            for (int galaxy = 0; galaxy < 8; galaxy++)
            {
                foreach (int tally in KillTallies)
                {
                    foreach (var system in Systems)
                    {
                        var host = new FakeMissionHost { MissionFlags = status, Galaxy = galaxy, KillTally = tally, CurrentSystem = system };
                        var found = Runtime.FindDockingEvent(host);
                        string? actual = found is var (mission, dockingEvent) ? $"{mission.Id}/{dockingEvent.Name}" : null;
                        string? expected = OriginalDockingEvent(status, galaxy, tally, system);
                        Assert.True(expected == actual, $"status={status} galaxy={galaxy} tally={tally} system={system}: expected {expected}, got {actual}");

                        Runtime.OnDocked(host);
                        var state = OriginalStateAfter(expected, status, tally);
                        Assert.Equal(state.Status, host.MissionFlags);
                        Assert.Equal(state.Cash, host.Cash);
                        Assert.Equal(state.Tally, host.KillTally);
                        Assert.Equal(state.NavalEnergyUnit, host.Equipment.Contains(MissionEquipment.NavalEnergyUnit));
                        checkedCount++;
                    }
                }
            }
        }

        Assert.Equal(256 * 8 * KillTallies.Length * Systems.Length, checkedCount);
    }

    [Fact]
    public void TheMissionsProgressThroughTheirStages()
    {
        var host = new FakeMissionHost { KillTally = 256 }.At(1, "Lave");
        Assert.Equal("not-started", Runtime.StageNameOf("constrictor", host));

        Assert.True(Runtime.OnDocked(host));
        Assert.Equal("hunting", Runtime.StageNameOf("constrictor", host));

        // The Constrictor appears in Orarra, in place of pirates
        host.At(2, "Orarra");
        Assert.True(Runtime.TrySpawnTarget(host));
        host.ShipCounts[ShipType.Constrictor] = 1;
        Assert.False(Runtime.TrySpawnTarget(host));

        Runtime.OnShipRemoved(host, ShipType.Constrictor);
        Assert.Equal("constrictor-destroyed", Runtime.StageNameOf("constrictor", host));
        Assert.Equal(512, host.KillTally);

        Assert.True(Runtime.OnDocked(host));
        Assert.Equal("complete", Runtime.StageNameOf("constrictor", host));
        Assert.Equal(50000, host.Cash);

        // Mission 2 needs galaxy 3 and a higher kill tally
        host.At(3, "Ceerdi");
        Assert.False(Runtime.OnDocked(host));
        host.KillTally = 1280;
        Assert.True(Runtime.OnDocked(host));
        Assert.Equal("going-to-ceerdi", Runtime.StageNameOf("thargoid-plans", host));

        Assert.True(Runtime.OnDocked(host));
        Assert.Equal("carrying-plans", Runtime.StageNameOf("thargoid-plans", host));

        host.At(3, "Birera");
        Assert.True(Runtime.OnDocked(host));
        Assert.Equal("complete", Runtime.StageNameOf("thargoid-plans", host));
        Assert.Equal([MissionEquipment.NavalEnergyUnit], host.Equipment);
        Assert.Equal(1280 + 256, host.KillTally);

        Assert.False(Runtime.OnDocked(host));
        Assert.Equal(0b1110, host.MissionFlags);
    }

    [Fact]
    public void RemovingAConstrictorChangesTheStageAsTheOriginalDoes()
    {
        for (int status = 0; status < 256; status++)
        {
            foreach (int type in new[] { ShipType.Constrictor, ShipType.Viper, ShipType.Thargoid })
            {
                var host = new FakeMissionHost { MissionFlags = status, KillTally = 0xFF80 };
                Runtime.OnShipRemoved(host, type);
                bool constrictor = type == ShipType.Constrictor;
                Assert.Equal(constrictor ? status | 0b10 : status, host.MissionFlags);
                Assert.Equal(constrictor ? 0x0080 : 0xFF80, host.KillTally);
            }
        }
    }

    [Fact]
    public void TheConstrictorAppearsWhereAndWhenTheOriginalSpawnsIt()
    {
        for (int status = 0; status < 256; status++)
        {
            for (int galaxy = 0; galaxy < 8; galaxy++)
            {
                foreach (var system in Systems)
                {
                    foreach (int present in new[] { 0, 1 })
                    {
                        var host = new FakeMissionHost { MissionFlags = status, Galaxy = galaxy, CurrentSystem = system };
                        host.ShipCounts[ShipType.Constrictor] = present;
                        bool expected = galaxy == 1 && system == (144, 33) && (status & 0b11) == 0b01 && present == 0;
                        Assert.Equal(expected, Runtime.TrySpawnTarget(host));
                        Assert.Equal(expected ? [$"spawnTarget {ShipType.Constrictor} ai={0b11111001}"] : Array.Empty<string>(), host.Calls);
                        Assert.Empty(host.RandomNumbersDrawn);
                    }
                }
            }
        }
    }

    [Fact]
    public void ThargoidsChaseThePlansAsInTheOriginal()
    {
        for (int status = 0; status < 256; status++)
        {
            foreach (int random in new[] { 0, 219, 220, 255 })
            {
                var host = new FakeMissionHost { MissionFlags = status }.WithRandomNumbers(random);
                Runtime.RunEncounters(host);

                // The original only draws a random number while carrying the plans
                bool carrying = (status & 0b1100) == 0b1000;
                Assert.Equal(carrying ? [random] : Array.Empty<int>(), host.RandomNumbersDrawn);
                Assert.Equal(carrying && random >= 220 ? [$"spawnHostile {ShipType.Thargoid}"] : Array.Empty<string>(), host.Calls);
            }
        }
    }

    [Fact]
    public void TheClueDescriptionsAppearWhereAndWhenTheOriginalShowsThem()
    {
        for (int status = 0; status < 16; status++)
        {
            for (int galaxy = 0; galaxy < 8; galaxy++)
            {
                for (int system = 0; system < 256; system++)
                {
                    var host = new FakeMissionHost { MissionFlags = status };
                    bool expected = (status & 1) != 0 && OriginalClueSystems.Contains((galaxy, system));
                    Assert.Equal(expected, Runtime.TryShowSystemDescription(host, galaxy, system));
                }
            }
        }
    }

    // ------------------------------------------------------------------------
    // Presentation
    // ------------------------------------------------------------------------

    [Fact]
    public void TheConstrictorBriefingRunsItsStepsInOrder()
    {
        var host = new FakeMissionHost { KillTally = 256, CommanderName = "BLAKE" }.At(1, "Lave");
        Runtime.OnDocked(host);

        string[] expected =
        [
            "clearScreen",
            "text row=10 column=6 justify=False newlineAtEnd=False",
            "  | INCOMING MESSAGE|",
            "pause 100",
            $"introduceShip {ShipType.Constrictor}",
            "text row=10 column= justify=True newlineAtEnd=True",
            "  |Greetings Commander BLAKE, I am Captain Curruthers of Her Majesty's Space Navy and I beg a moment of your valuable time.|",
            "  | We would like you to do a little job for us.|",
            "  | The ship you see here is a new model, the Constrictor, equiped with a top secret new shield generator.|",
            "  | Unfortunately it's been stolen.|",
            "showShipUntilKey",
            "text row= column= justify=True newlineAtEnd=True",
            "  | It went missing from our ship yard on Xeer five months ago and was last seen at Reesdice.|",
            "  | Your mission, should you decide to accept it, is to seek and destroy this ship.|",
            "  | You are cautioned that only Military  Lasers will penetrate the new shields and that the Constrictor is fitted with an E.C.M.System.|",
            "text row= column=6 justify=False newlineAtEnd=True",
            "  |Good Luck, Commander.|",
            "text row= column=6 justify=False newlineAtEnd=False",
            "  |  MESSAGE ENDS|",
            "showShipUntilKey",
        ];
        Assert.Equal(expected, host.Calls);
        Assert.Equal("hunting", Runtime.StageNameOf("constrictor", host));
    }

    [Theory]
    [InlineData(1, "Curruthers", "was last seen at Reesdice")]
    [InlineData(2, "Fosdyke Smythe", "is believed to have jumped to this galaxy")]
    public void TheBriefingDependsOnTheGalaxy(int galaxy, string captain, string lastSeen)
    {
        var host = new FakeMissionHost { KillTally = 256, Galaxy = galaxy - 1 };
        Runtime.OnDocked(host);
        Assert.Contains(host.TextLines, l => l.Contains($"Captain {captain} of", StringComparison.Ordinal));
        Assert.Contains(host.TextLines, l => l.EndsWith($" and {lastSeen}.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheThargoidMissionUsesTheCommanderNameAndCaptain()
    {
        var host = new FakeMissionHost { MissionFlags = 0b0010, KillTally = 1280, Galaxy = 2, CommanderName = "ELITE" };
        Runtime.OnDocked(host);
        Assert.Equal(
            "  Attention Commander ELITE, I am Captain Fortesque of Her Majesty's Space Navy. We have need of your services again.",
            host.TextLines.ElementAt(1));
        Assert.Equal("waitForKey", host.Calls[^1]);
    }

    [Fact]
    public void RandomWordsAreChosenAsTheOriginalChoosesThem()
    {
        // DT6: the number of the thresholds 51, 102, 153 and 204 that the random number reaches
        for (int random = 0; random < 256; random++)
        {
            int expected = (random >= 51 ? 1 : 0) + (random >= 102 ? 1 : 0) + (random >= 153 ? 1 : 0) + (random >= 204 ? 1 : 0);
            Assert.Equal(expected, MissionRuntime.ChooseRandomly(5, random));
        }
    }

    [Theory]
    [InlineData(1, 36, new[] { 60 }, "A WEIRD LOOKING SHIP LEFT HERE A WHILE BACK. LOOKED BOUND FOR AREXE")]
    [InlineData(2, 79, new[] { 0, 255 }, "SON OF A BITCH SHIP WENT FOR ME AT AUSAR. MY LASERS DIDN'T EVEN SCRATCH THE WHORESON BEETLE HEADED FLAP EAR'D KNAVE")]
    [InlineData(2, 118, new[] { 110, 160 }, "YOU CAN TACKLE THE EVIL ROGUE IF YOU LIKE. HE'S AT ORARRA")]
    [InlineData(2, 32, new[] { 51, 204 }, "YEAH, I HEAR A PECULIAR SHIP LEFT ERRIUS A  WHILE BACK")]
    [InlineData(2, 32, new[] { 204, 170 }, "TRY ERRIUS")]
    [InlineData(2, 253, new[] { 102 }, "THIS  UNUSUAL SHIP DEHYPED HERE FROM NOWHERE, SUN SKIMMED AND JUMPED. I HEAR IT WENT TO INBIBE")]
    public void TheCluesUseRandomWordsInOrder(int galaxy, int system, int[] randomNumbers, string expected)
    {
        var host = new FakeMissionHost { MissionFlags = 1 }.WithRandomNumbers([.. randomNumbers, 99, 99]);
        Assert.True(Runtime.TryShowSystemDescription(host, galaxy - 1, system));
        Assert.Equal([$"description |{expected}|"], host.Calls);

        // Only the random numbers for the words that are printed are drawn
        int used = expected == "TRY ERRIUS" ? 1 : randomNumbers.Length;
        Assert.Equal(randomNumbers.Take(used), host.RandomNumbersDrawn);
    }
}

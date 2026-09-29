namespace EliteSharp.Game.Missions;

/// <summary>
/// Runs the missions: the game calls this at the points where missions can
/// change what happens (docking, spawning ships, ships leaving the local
/// bubble and showing system descriptions), and this checks the mission
/// definitions and carries out their steps through the <see cref="IMissionHost"/>.
///
/// The runtime holds no state of its own: each mission's progress is its
/// stage, which is kept in the game's mission byte (see <see cref="MissionProgress"/>).
/// </summary>
public sealed class MissionRuntime(MissionCatalogue catalogue)
{
    public MissionCatalogue Catalogue { get; } = catalogue;

    /// <summary>A mission's current stage.</summary>
    public int StageOf(MissionDefinition mission, IMissionHost host) => mission.Progress.Read(host.MissionFlags);

    /// <summary>The name of a mission's current stage.</summary>
    public string StageNameOf(string missionId, IMissionHost host)
    {
        var mission = Catalogue.Get(missionId);
        return mission.Progress.NameOf(StageOf(mission, host));
    }

    /// <summary>
    /// The docking event that would happen if we docked now (the first one,
    /// in mission order, whose conditions are met), if any.
    /// </summary>
    public (MissionDefinition Mission, DockingEvent Event)? FindDockingEvent(IMissionHost host)
    {
        foreach (var mission in Catalogue.Missions)
        {
            if (!RequirementsMet(mission, host))
            {
                continue;
            }

            foreach (var dockingEvent in mission.DockingEvents)
            {
                if (Matches(mission, dockingEvent.When, host))
                {
                    return (mission, dockingEvent);
                }
            }
        }

        return null;
    }

    /// <summary>We have docked: run the first docking event whose conditions are met, returning whether there was one.</summary>
    public bool OnDocked(IMissionHost host)
    {
        if (FindDockingEvent(host) is not var (mission, dockingEvent))
        {
            return false;
        }

        Run(mission, dockingEvent.Steps, host);
        return true;
    }

    /// <summary>
    /// The game is about to spawn a group of pirates: if a mission's target
    /// should appear here instead, spawn it and return true.
    /// </summary>
    public bool TrySpawnTarget(IMissionHost host)
    {
        foreach (var mission in Catalogue.Missions)
        {
            if (mission.Target is not { } target)
            {
                continue;
            }

            if (InSystem(target.System, host) && StageOf(mission, host) == target.Stage && host.ShipCount(target.ShipType) == 0)
            {
                host.SpawnTarget(target.ShipType, target.Ai);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The game is considering spawning extra ships: give each encounter for
    /// the missions' current stages its chance of appearing. This only draws a
    /// random number for encounters whose mission is at the right stage.
    /// </summary>
    public void RunEncounters(IMissionHost host)
    {
        foreach (var mission in Catalogue.Missions)
        {
            foreach (var encounter in mission.Encounters)
            {
                if (StageOf(mission, host) == encounter.Stage && host.NextRandom() >= 256 - encounter.ChanceIn256)
                {
                    host.SpawnHostileShip(encounter.ShipType);
                }
            }
        }
    }

    /// <summary>A ship has left the local bubble (usually because it has been destroyed).</summary>
    public void OnShipRemoved(IMissionHost host, int shipType)
    {
        foreach (var mission in Catalogue.Missions)
        {
            foreach (var removed in mission.ShipRemovedEvents)
            {
                if (removed.ShipType == shipType)
                {
                    Run(mission, removed.Steps, host);
                }
            }
        }
    }

    /// <summary>
    /// If a mission replaces the description of the given system (while
    /// docked there), show it and return true.
    /// </summary>
    public bool TryShowSystemDescription(IMissionHost host, int galaxy, int systemIndex)
    {
        foreach (var mission in Catalogue.Missions)
        {
            if (mission.SystemDescriptions is not { } descriptions || !descriptions.Stages.Contains(StageOf(mission, host)))
            {
                continue;
            }

            foreach (var description in descriptions.Systems)
            {
                if (description.System.Galaxy == galaxy && description.System.Index == systemIndex)
                {
                    host.ShowSystemDescription(Resolve(mission, description.Text, host));
                    return true;
                }
            }
        }

        return false;
    }

    private bool RequirementsMet(MissionDefinition mission, IMissionHost host) =>
        mission.Requires.All(r => StageOf(Catalogue.Get(r.MissionId), host) == r.Stage);

    private bool Matches(MissionDefinition mission, EventCondition condition, IMissionHost host) =>
        StageOf(mission, host) == condition.Stage
        && (condition.Galaxies == null || condition.Galaxies.Contains(host.Galaxy))
        && (condition.System == null || InSystem(condition.System, host))
        && host.KillTally >= condition.MinimumKillTally;

    private static bool InSystem(StarSystem system, IMissionHost host) =>
        host.Galaxy == system.Galaxy && host.CurrentSystem == (system.X, system.Y);

    /// <summary>Carry out a list of steps, in order.</summary>
    private void Run(MissionDefinition mission, IReadOnlyList<MissionStep> steps, IMissionHost host)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case SetStageStep setStage:
                    host.MissionFlags = mission.Progress.Write(host.MissionFlags, setStage.Stage);
                    break;

                case ChangeStageStep changeStage:
                    if (changeStage.Changes.TryGetValue(StageOf(mission, host), out int newStage))
                    {
                        host.MissionFlags = mission.Progress.Write(host.MissionFlags, newStage);
                    }

                    break;

                case AddCashStep addCash:
                    host.AddCash(addCash.Tenths);
                    break;

                case AddKillTallyStep addKillTally:
                    host.KillTally = (host.KillTally + addKillTally.Amount) & 0xFFFF;
                    break;

                case FitEquipmentStep fitEquipment:
                    host.FitEquipment(fitEquipment.Equipment);
                    break;

                case RunSequenceStep runSequence:
                    Run(mission, Catalogue.Sequences[runSequence.Name].Steps, host);
                    break;

                case ClearScreenStep:
                    host.ClearScreen();
                    break;

                case TextStep text:
                    // The lines are filled in first (any random choices are
                    // made in order), and then printed
                    var lines = text.Lines.Select(line => Resolve(mission, line, host)).ToList();
                    host.ShowText(text.Row, text.Column, text.Justify, lines, text.NewlineAtEnd);
                    break;

                case PauseStep pause:
                    host.Pause(pause.Frames);
                    break;

                case WaitForKeyStep:
                    host.WaitForKey();
                    break;

                case IntroduceShipStep introduceShip:
                    host.IntroduceShip(introduceShip.ShipType);
                    break;

                case ShowShipUntilKeyStep:
                    host.ShowShipUntilKey();
                    break;

                default:
                    throw new InvalidOperationException($"{step.Location}: unknown step {step.GetType().Name}");
            }
        }
    }

    /// <summary>Fill in the references in some of a mission's text.</summary>
    public static string Resolve(MissionDefinition mission, MissionText text, IMissionHost host) =>
        text.Resolve(name => ResolveReference(mission, name, host));

    private static string ResolveReference(MissionDefinition mission, string name, IMissionHost host)
    {
        if (name == MissionText.CommanderName)
        {
            return host.CommanderName;
        }

        var chosen = mission.Values[name] switch
        {
            FixedValue fixedValue => fixedValue.Text,
            GalaxyValue galaxyValue => galaxyValue.ByGalaxy.TryGetValue(host.Galaxy, out var text)
                ? text
                : throw new InvalidOperationException($"Mission '{mission.Id}': '{name}' has no value for galaxy {host.Galaxy + 1}"),
            RandomValue randomValue => randomValue.Choices[ChooseRandomly(randomValue.Choices.Count, host.NextRandom())],
            _ => throw new InvalidOperationException($"Mission '{mission.Id}': '{name}' has an unknown kind of value"),
        };

        return Resolve(mission, chosen, host);
    }

    /// <summary>
    /// Which of a number of choices a random number (0-255) picks: the range
    /// is split into equal parts of 256 / count (rounded down), with anything
    /// left over going to the last choice. With five choices, as all of the
    /// original's random words have, this is the original's choice (DT6).
    /// </summary>
    public static int ChooseRandomly(int count, int random) => Math.Min(random / (256 / count), count - 1);
}

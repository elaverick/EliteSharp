namespace EliteSharp.Game.Missions;

// The mission definitions, as loaded from Assets/Missions (see the README
// there for the file format). These are read-only once loaded; a mission's
// progress is kept separately, in the game state (see MissionProgress).

/// <summary>All the missions and shared sequences, in the order they are checked.</summary>
public sealed class MissionCatalogue(IReadOnlyList<MissionDefinition> missions, IReadOnlyDictionary<string, SharedSequence> sequences)
{
    /// <summary>The missions, in the order their events are checked (by file name).</summary>
    public IReadOnlyList<MissionDefinition> Missions { get; } = missions;

    /// <summary>The sequences that any mission can run, by name.</summary>
    public IReadOnlyDictionary<string, SharedSequence> Sequences { get; } = sequences;

    /// <summary>The mission with the given id.</summary>
    public MissionDefinition Get(string id) =>
        Missions.FirstOrDefault(m => m.Id == id) ?? throw new KeyNotFoundException($"There is no mission '{id}'");
}

/// <summary>A named list of steps in a shared file, which any mission can run with <c>run</c>.</summary>
public sealed record SharedSequence(string Name, string SourceFile, IReadOnlyList<MissionStep> Steps);

/// <summary>A mission.</summary>
/// <param name="Id">The mission's id (its file name).</param>
/// <param name="Name">The mission's name, for people.</param>
/// <param name="SourceFile">The file the mission came from.</param>
/// <param name="Progress">Where the mission's stage is saved, and the stages.</param>
/// <param name="Requires">Other missions' stages that the docking events need.</param>
/// <param name="Values">The named values that the mission's text can use.</param>
/// <param name="DockingEvents">What happens when we dock, in the order they are checked.</param>
/// <param name="Target">The ship that the mission sends us after, if there is one.</param>
/// <param name="ShipRemovedEvents">What happens when particular ships leave the local bubble.</param>
/// <param name="Encounters">Ships that the mission sends after us.</param>
/// <param name="SystemDescriptions">Replacement system descriptions for the Data on System screen.</param>
public sealed record MissionDefinition(
    string Id,
    string Name,
    string SourceFile,
    MissionProgress Progress,
    IReadOnlyList<StageRequirement> Requires,
    IReadOnlyDictionary<string, MissionValue> Values,
    IReadOnlyList<DockingEvent> DockingEvents,
    MissionTarget? Target,
    IReadOnlyList<ShipRemovedEvent> ShipRemovedEvents,
    IReadOnlyList<Encounter> Encounters,
    SystemDescriptions? SystemDescriptions);

/// <summary>
/// Where a mission's stage is kept: a group of bits in the mission byte (TP)
/// that is saved in the commander file, which keeps saved games compatible.
/// </summary>
/// <param name="FirstBit">The lowest bit of the group.</param>
/// <param name="BitCount">The number of bits in the group.</param>
/// <param name="Stages">The stages, and the value of the bits for each.</param>
public sealed record MissionProgress(int FirstBit, int BitCount, IReadOnlyDictionary<string, int> Stages)
{
    private int Mask => ((1 << BitCount) - 1) << FirstBit;

    /// <summary>The stage value in the given mission flags.</summary>
    public int Read(int flags) => (flags & Mask) >> FirstBit;

    /// <summary>The mission flags with this mission's stage changed.</summary>
    public int Write(int flags, int stage) => (flags & ~Mask) | (stage << FirstBit);

    /// <summary>The name of a stage value (or the value, if it has no name).</summary>
    public string NameOf(int stage) => Stages.FirstOrDefault(s => s.Value == stage).Key ?? stage.ToString();
}

/// <summary>A condition on another mission's stage.</summary>
public sealed record StageRequirement(string MissionId, int Stage);

/// <summary>The conditions for a mission event.</summary>
/// <param name="Stage">The stage the mission must be at.</param>
/// <param name="Galaxies">The galaxies (numbered from 0) the event can happen in, or null for any.</param>
/// <param name="System">The system we must be in, or null for any.</param>
/// <param name="MinimumKillTally">The lowest kill tally (TALLY) at which the event can happen.</param>
public sealed record EventCondition(int Stage, IReadOnlySet<int>? Galaxies, StarSystem? System, int MinimumKillTally);

/// <summary>Something that happens when we dock, if its conditions are met; the game then goes to the docking bay as usual.</summary>
public sealed record DockingEvent(string Name, EventCondition When, IReadOnlyList<MissionStep> Steps);

/// <summary>
/// The ship a mission sends us after: while the mission is at the given
/// stage, it appears in the given system (in place of a group of pirates)
/// whenever there isn't one already in the local bubble.
/// </summary>
/// <param name="ShipType">The ship's type.</param>
/// <param name="System">The system it appears in.</param>
/// <param name="Stage">The stage in which it appears.</param>
/// <param name="Ai">The ship's AI byte (INWK+32): tactics, aggression and E.C.M.</param>
public sealed record MissionTarget(int ShipType, StarSystem System, int Stage, int Ai);

/// <summary>What happens when a ship of a particular type leaves the local bubble (usually because it has been destroyed).</summary>
public sealed record ShipRemovedEvent(int ShipType, IReadOnlyList<MissionStep> Steps);

/// <summary>
/// A ship that the mission sends after us while it is at a particular stage:
/// each time the game considers spawning extra ships, there is a chance of
/// this ship appearing.
/// </summary>
/// <param name="Stage">The stage in which the ship can appear.</param>
/// <param name="ShipType">The ship's type.</param>
/// <param name="ChanceIn256">The chance, out of 256, of the ship appearing each time.</param>
public sealed record Encounter(int Stage, int ShipType, int ChanceIn256);

/// <summary>Replacement system descriptions, shown while the mission is at one of the given stages.</summary>
public sealed record SystemDescriptions(IReadOnlySet<int> Stages, IReadOnlyList<SystemDescription> Systems);

/// <summary>A replacement description for a system on the Data on System screen, shown while docked there.</summary>
public sealed record SystemDescription(StarSystem System, MissionText Text);

/// <summary>A named value that mission text can use with {{name}}.</summary>
public abstract record MissionValue;

/// <summary>A value that is always the same text.</summary>
public sealed record FixedValue(MissionText Text) : MissionValue;

/// <summary>A value that depends on the galaxy we are in (keyed by galaxy number, from 0).</summary>
public sealed record GalaxyValue(IReadOnlyDictionary<int, MissionText> ByGalaxy) : MissionValue;

/// <summary>A value that is chosen at random each time it's used, using the game's random numbers.</summary>
public sealed record RandomValue(IReadOnlyList<MissionText> Choices) : MissionValue;

/// <summary>Equipment that a mission can fit to our ship.</summary>
public enum MissionEquipment
{
    /// <summary>The naval energy unit, which recharges the energy banks faster than the standard one.</summary>
    NavalEnergyUnit,
}

/// <summary>A step in a mission's script.</summary>
public abstract record MissionStep
{
    /// <summary>Where the step is in its file, for error messages.</summary>
    public required string Location { get; init; }
}

/// <summary>Move the mission to a stage.</summary>
public sealed record SetStageStep(int Stage) : MissionStep;

/// <summary>Move the mission from each of the given stages to another (stages not listed are left as they are).</summary>
public sealed record ChangeStageStep(IReadOnlyDictionary<int, int> Changes) : MissionStep;

/// <summary>Add cash to our account, in tenths of a credit.</summary>
public sealed record AddCashStep(int Tenths) : MissionStep;

/// <summary>Add to our kill tally (TALLY), which sets our combat rank.</summary>
public sealed record AddKillTallyStep(int Amount) : MissionStep;

/// <summary>Fit a piece of equipment to our ship.</summary>
public sealed record FitEquipmentStep(MissionEquipment Equipment) : MissionStep;

/// <summary>Run a shared sequence.</summary>
public sealed record RunSequenceStep(string Name) : MissionStep;

/// <summary>Clear the screen.</summary>
public sealed record ClearScreenStep : MissionStep;

/// <summary>
/// Print lines of text in cyan. Each line is followed by a new line, except
/// the last if <paramref name="NewlineAtEnd"/> is false.
/// </summary>
/// <param name="Row">The text row to move to first (1-23), if any.</param>
/// <param name="Column">The text column to move to first (1-32), if any.</param>
/// <param name="Justify">Whether to justify the text (in lines of 30 characters), rather than printing it as it is.</param>
/// <param name="Lines">The lines of text.</param>
/// <param name="NewlineAtEnd">Whether to start a new line after the last line.</param>
public sealed record TextStep(int? Row, int? Column, bool Justify, IReadOnlyList<MissionText> Lines, bool NewlineAtEnd) : MissionStep;

/// <summary>Pause for a number of fiftieths of a second (1-255).</summary>
public sealed record PauseStep(int Frames) : MissionStep;

/// <summary>Wait for a key to be released and then pressed.</summary>
public sealed record WaitForKeyStep : MissionStep;

/// <summary>
/// Clear the screen and show a ship flying in close, spinning and moving off,
/// and leave it spinning at the top of the screen.
/// </summary>
public sealed record IntroduceShipStep(int ShipType) : MissionStep;

/// <summary>
/// Keep the introduced ship spinning at the top of the screen until a key is
/// pressed, then clear the screen and move the text cursor to row 10.
/// </summary>
public sealed record ShowShipUntilKeyStep : MissionStep;

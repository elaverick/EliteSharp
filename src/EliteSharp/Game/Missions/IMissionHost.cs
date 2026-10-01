namespace EliteSharp.Game.Missions;

/// <summary>
/// The operations that the mission runtime uses to drive the game. The game
/// implements these with its own systems (text printing, ships, the
/// commander's state and so on), so the missions describe what happens, and
/// the game decides how.
/// </summary>
public interface IMissionHost
{
    /// <summary>The mission byte (TP), which holds every mission's stage and is saved with the commander.</summary>
    int MissionFlags { get; set; }

    /// <summary>The galaxy we are in, numbered from 0.</summary>
    int Galaxy { get; }

    /// <summary>The galactic coordinates of the system we are in.</summary>
    (int X, int Y) CurrentSystem { get; }

    /// <summary>The kill tally (TALLY), which sets our combat rank (it wraps at 65536, as in the original).</summary>
    int KillTally { get; set; }

    /// <summary>The commander's name.</summary>
    string CommanderName { get; }

    /// <summary>The number of ships of the given type in the local bubble.</summary>
    int ShipCount(int shipType);

    /// <summary>The next of the game's random numbers (0-255), from the same generator that the rest of the game uses.</summary>
    int NextRandom();

    /// <summary>
    /// The next of the game's random numbers (0-255) for a random word in some
    /// text, which the original only draws with the C flag clear (DT6), so it
    /// doesn't depend on what used the random number generator before.
    /// </summary>
    int NextRandomForText();

    /// <summary>Add cash to our account, in tenths of a credit.</summary>
    void AddCash(int tenths);

    /// <summary>Fit a piece of equipment to our ship.</summary>
    void FitEquipment(MissionEquipment equipment);

    /// <summary>Add a mission's target ship to the local bubble, in the place the game has set up for a new pirate.</summary>
    void SpawnTarget(int shipType, int ai);

    /// <summary>Add a hostile ship to the local bubble, some distance away, as for a Thargoid invasion.</summary>
    void SpawnHostileShip(int shipType);

    /// <summary>Clear the screen.</summary>
    void ClearScreen();

    /// <summary>Print lines of text in cyan (see <see cref="TextStep"/>).</summary>
    void ShowText(int? row, int? column, bool justify, IReadOnlyList<string> lines, bool newlineAtEnd);

    /// <summary>Pause for the given number of fiftieths of a second.</summary>
    void Pause(int frames);

    /// <summary>Wait for a key to be released and then pressed.</summary>
    void WaitForKey();

    /// <summary>Show a ship flying in close, spinning and moving off, and leave it spinning at the top of the screen.</summary>
    void IntroduceShip(int shipType);

    /// <summary>Keep the introduced ship spinning until a key is pressed, then clear the text and move to row 10.</summary>
    void ShowShipUntilKey();

    /// <summary>Print a system description in place of the usual one on the Data on System screen.</summary>
    void ShowSystemDescription(string text);
}

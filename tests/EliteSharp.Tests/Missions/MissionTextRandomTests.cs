using EliteSharp.Game.Missions;
using EliteSharp.Tests.Game;

namespace EliteSharp.Tests.Missions;

/// <summary>
/// The random words in the missions' text are chosen as the original chooses
/// the random tokens in its text (DT6), which it only does with the C flag
/// clear, so what used the random number generator before can't change them.
/// </summary>
public sealed class MissionTextRandomTests
{
    /// <summary>The Constrictor's clues that have random words (galaxy from 1, system number).</summary>
    public static TheoryData<int, int> CluesWithRandomWords => new()
    {
        { 1, 36 },   // Reesdice: "A {{strange}} LOOKING SHIP..."
        { 2, 79 },   // "{{scoundrel}} SHIP WENT FOR ME AT AUSAR..."
        { 2, 118 },  // "YOU CAN TACKLE THE {{killer}} {{scoundrel}}..."
        { 2, 32 },   // "{{seenNearErrius}}", which can include {{strange}}
        { 2, 253 },  // "THIS  {{strange}} SHIP DEHYPED HERE..."
    };

    [Theory]
    [MemberData(nameof(CluesWithRandomWords))]
    public void TheCluesDontDependOnTheCFlag(int galaxy, int system)
    {
        for (int seed = 0; seed < 256; seed++)
        {
            Assert.Equal(Clue(galaxy, system, seed, carry: false), Clue(galaxy, system, seed, carry: true));
        }
    }

    [Fact]
    public void ARandomWordUsesTheRandomNumberThatTheOriginalWouldDraw()
    {
        // With the C flag clear, as in DT6
        for (int seed = 0; seed < 256; seed++)
        {
            var game = Game(seed, carry: true);
            game.Set("_carry", false);
            int random = (int)game.Call("NextRandom")!;
            string word = new[] { "FUNNY", "WEIRD", "UNUSUAL", "STRANGE", "PECULIAR" }[MissionRuntime.ChooseRandomly(5, random)];

            // The clue is justified, so compare the words
            string shown = string.Join(" ", Clue(1, 36, seed, carry: true).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal($"A {word} LOOKING SHIP LEFT HERE A WHILE BACK. LOOKED BOUND FOR AREXE.", shown);
        }
    }

    /// <summary>The clue the game shows for a system during mission 1, with the random number generator in a given state.</summary>
    private static string Clue(int galaxy, int system, int seed, bool carry)
    {
        var game = Game(seed, carry);
        var missions = game.Get<MissionRuntime>("_missions");
        Assert.True(missions.TryShowSystemDescription(game.Game, galaxy - 1, system));
        return string.Join("\n", game.ScreenText()).Trim();
    }

    /// <summary>A game in mission 1, with the random number generator's state set from a seed and the C flag.</summary>
    private static PrivateGame Game(int seed, bool carry)
    {
        var game = new PrivateGame();
        game.Call("Begin");
        game.Call("ApplySavedCommander");
        game.Call("ClearScreen", 1);
        game.Set("_missionStatus", 1);
        int[] seeds = [seed, (seed * 7 + 3) & 0xFF, (seed * 13 + 5) & 0xFF, (seed * 31 + 11) & 0xFF];
        seeds.CopyTo(game.Get<int[]>("_randomSeeds"), 0);
        game.Set("_carry", carry);
        return game;
    }
}

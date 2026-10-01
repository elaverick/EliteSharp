using System.Security.Cryptography;
using System.Text;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The Data on System screen must show exactly what the original shows for
/// every system: the fixed text (from en-strings.yml), and the generated text
/// (from en-descriptions.yml), which is the species and the "goat soup"
/// description.
/// </summary>
public sealed class SystemDescriptionTests
{
    /// <summary>
    /// The SHA-256 of the Data on System screen for every system in all eight
    /// galaxies (see <see cref="DataOnSystemScreens"/>). The descriptions were
    /// checked against a model of the original's 6502 routines (DETOK, DT6,
    /// MT17, MT18 and DORND, with the C flag as the 6502 leaves it), which
    /// gives the well-known descriptions of Lave and Tibedied below. The one
    /// difference is that the random words don't have the question mark that
    /// the original prints in a few of them (see
    /// <see cref="ARandomWordDoesntHaveAQuestionMark"/>).
    /// </summary>
    private const string OriginalChecksum = "0d35e1992c8384c8be99146499bfe401ba5adceea7e0a5cd07082e2a21d8480b";

    [Fact]
    public void EverySystemIsDescribedAsInTheOriginal()
    {
        string text = string.Join("", DataOnSystemScreens().Select(screen => screen + "\n"));
        Assert.Equal(OriginalChecksum, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    /// <summary>
    /// The SHA-256 of the state that printing each system's description leaves
    /// the game in (see <see cref="StatesAfterDescriptions"/>), as the
    /// original's token tables leave it (this was checked against them before
    /// they were replaced by en-descriptions.yml), so what the game does next
    /// is the same.
    /// </summary>
    private const string OriginalStateChecksum = "4ac8219b75c2bf26de5a7d34c42e7e0c8c04bead239f8f4df9e62ccb32ce6f15";

    [Fact]
    public void EverySystemsDescriptionLeavesTheGameAsInTheOriginal()
    {
        string text = string.Join("", StatesAfterDescriptions().Select(state => state + "\n"));
        Assert.Equal(OriginalStateChecksum, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    [Theory]
    [InlineData(1, 211, "The colonists here have violated Intergalactic Cloning Protocol and should be avoided.")]
    [InlineData(3, 100, "COMING SOON: ELITE II.")]
    [InlineData(3, 41, "The inhabitants of Anreer are so amazingly primitive that they still think ***** ****** is 3d.")]
    public void TheSpecialDescriptionsAreAsInTheOriginal(int galaxy, int system, string expected)
    {
        // These are shown when we are docked at the system (the inhabitants of
        // Anreer use a random choice, which depends on the random numbers)
        var game = new PrivateGame();
        SelectSystem(game, galaxy - 1, system);
        game.Set("SystemNumber", system);
        game.Set("_selectedDistance", 0);
        game.Set("_docked", 0xFF);
        new[] { 1, 2, 3, 4 }.CopyTo(game.Get<int[]>("_randomSeeds"), 0);
        game.Call("ShowSystemData");

        var rows = game.ScreenText();
        int radius = Array.FindIndex(rows, row => row.StartsWith("Average Radius"));
        Assert.Equal(expected, string.Join(" ", string.Join(" ", rows[(radius + 1)..]).Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void TheDescriptionsDontDependOnTheCFlag()
    {
        // The original only makes the random choices in a description with
        // the C flag clear, so whatever used the random number generator
        // before can't change a description
        Assert.Equal(DataOnSystemScreens(carry: false), DataOnSystemScreens(carry: true));
    }

    [Fact]
    public void LaveIsDescribedAsInTheOriginal()
    {
        Assert.Equal(
            """
                    DATA ON LAVE

            Distance:  3.7 Light Years

            Economy:Rich Agricultural

            Government:Dictatorship

            Tech.Level:  5

            Population:2.5 Billion

            (Human Colonials)

            Gross Productivity: 7000 M CR

            Average Radius: 4116 km

            Lave is most  famous  for  its
            vast  rain  forests  and   the
            Lavian tree grub.
            """.ReplaceLineEndings("\n"),
            DataOnSystemScreens().ElementAt(7 * 2 + 1).TrimEnd());
    }

    [Fact]
    public void TibediedIsDescribedAsInTheOriginal()
    {
        Assert.EndsWith(
            """
            This planet  is  most  notable
            for  Tibediedian  Arnu  Brandy
            but ravaged  by  unpredictable
            solar activity.
            """.ReplaceLineEndings("\n"),
            DataOnSystemScreens().ElementAt(0 * 2 + 1).TrimEnd());
    }

    [Fact]
    public void ARandomWordDoesntHaveAQuestionMark()
    {
        // The random words (MT18) are made from the two-letter tokens, one of
        // which is the single A, which the original stores as "A?" and prints
        // with its "?" in the random words (as "Esseina?oid" here, in galaxy
        // 1, system 223). The words are the same without it, as the random
        // numbers don't depend on the letters.
        Assert.EndsWith(
            """
            This planet  is  mildly  noted
            for  the   Beenrian   mountain
            Esseinaoid  but  scourged   by
            frequent civil war.
            """.ReplaceLineEndings("\n"),
            DataOnSystemScreens().ElementAt(223 * 2 + 1).TrimEnd());
    }

    /// <summary>
    /// For each system in each galaxy, the state of the random number
    /// generator and the text printer after printing its description.
    /// </summary>
    private static IEnumerable<string> StatesAfterDescriptions()
    {
        var game = new PrivateGame();
        for (int galaxy = 0; galaxy < 8; galaxy++)
        {
            SelectSystem(game, galaxy, 0);
            var selected = game.Get<int[]>("_selectedSeeds");
            for (int system = 0; system < 256; system++)
            {
                var seeds = (int[])selected.Clone();
                game.Call("ClearScreen", 1);
                game.Set("_selectedDistance", 37);
                game.Call("PrintSystemDescription");
                var buffer = game.Get<int[]>("_lineBuffer").Take(game.Get<int>("_lineBufferSize"));
                yield return string.Join(" ", [
                    string.Join(",", game.Get<int[]>("_randomSeeds")),
                    game.Get<bool>("_carry"),
                    game.Get<bool>("_overflow"),
                    game.Get<int>("_randomX"),
                    game.Get<int>("_cursorX"),
                    game.Get<int>("_cursorY"),
                    game.Get<int>("_textCase"),
                    string.Join(",", buffer),
                    game.Get<int>("_justifyFlags"),
                    game.Get<int>("_lowerCaseMask"),
                    game.Get<int>("_notPrintingWord"),
                    game.Get<int>("_lowerCaseEnabled"),
                    game.Get<int>("_capitaliseMask"),
                ]);
                Array.Copy(seeds, selected, 6);
                game.Call("NextSystem");
            }
        }
    }

    /// <summary>Start a new game and select a system (galaxy numbered from 0).</summary>
    private static void SelectSystem(PrivateGame game, int galaxy, int system)
    {
        game.Call("Begin");
        game.Call("ApplySavedCommander");
        game.Set("_galaxyNumber", galaxy);
        var galaxySeeds = game.Get<int[]>("_galaxySeeds");
        for (int jump = 0; jump < galaxy; jump++)
        {
            // The galactic hyperdrive rotates each seed byte left
            for (int x = 0; x < 6; x++)
            {
                galaxySeeds[x] = ((galaxySeeds[x] << 1) | (galaxySeeds[x] >> 7)) & 0xFF;
            }
        }

        game.Call("SelectFirstSystem");
        for (int i = 0; i < system; i++)
        {
            game.Call("NextSystem");
        }

        game.Call("CalculateSystemData");
    }

    /// <summary>
    /// The Data on System screen for each system in each galaxy, first for the
    /// system we are docked at, and then as seen from another system, with
    /// the C flag clear or set beforehand.
    /// </summary>
    private static IEnumerable<string> DataOnSystemScreens(bool carry = false)
    {
        var game = new PrivateGame();
        for (int galaxy = 0; galaxy < 8; galaxy++)
        {
            game.Call("Begin");
            game.Call("ApplySavedCommander");
            game.Set("_galaxyNumber", galaxy);
            var galaxySeeds = game.Get<int[]>("_galaxySeeds");
            for (int jump = 0; jump < galaxy; jump++)
            {
                // The galactic hyperdrive rotates each seed byte left
                for (int x = 0; x < 6; x++)
                {
                    galaxySeeds[x] = ((galaxySeeds[x] << 1) | (galaxySeeds[x] >> 7)) & 0xFF;
                }
            }

            game.Call("SelectFirstSystem");
            var selected = game.Get<int[]>("_selectedSeeds");
            for (int system = 0; system < 256; system++)
            {
                var seeds = (int[])selected.Clone();
                game.Call("CalculateSystemData");
                foreach (int distance in new[] { 0, 37 })
                {
                    Array.Copy(seeds, selected, 6);
                    game.Set("_selectedDistance", distance);
                    game.Set("_docked", 0xFF);
                    game.Set("_carry", carry);
                    game.Call("ShowSystemData");
                    yield return string.Join("\n", game.ScreenText());
                }

                Array.Copy(seeds, selected, 6);
                game.Call("NextSystem");
            }
        }
    }
}

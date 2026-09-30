using System.Security.Cryptography;
using System.Text;

namespace EliteSharp.Tests.Game;

/// <summary>
/// The universe must be generated exactly as the original generates it: the
/// galaxies' and systems' seeds, the systems' names, positions and data, and
/// the positions of each system's planet and sun.
/// </summary>
public sealed class UniverseTests
{
    /// <summary>
    /// The SHA-256 of every system's generated data in all eight galaxies (see
    /// <see cref="GenerateUniverse"/>), as the original fixed-point
    /// implementation generates it.
    /// </summary>
    private const string OriginalChecksum = "f986fcb9c7888949c9fe6ad1aaee8adfeb054617c47d2572131afd21c261f65f";

    [Fact]
    public void EveryGalaxyIsGeneratedAsInTheOriginal()
    {
        string text = string.Join("", GenerateUniverse().Select(line => line + "\n"));
        Assert.Equal(OriginalChecksum, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public void LaveIsAsInTheOriginal()
    {
        // Galaxy 1, system 7: seeds, name, economy 5 (mainly agricultural),
        // government 3 (dictatorship), tech level 4, population 25 (2.5
        // billion), productivity 7000, radius 4116, a planet with meridians at
        // (131072, 131072, 262144), and the sun at (65792, 131072, -327680)
        Assert.Equal("0,7,56,173,156,20,29,21,LAVE,5,3,4,25,7000,4116,128,128,131072,131072,262144,129,65792,131072,-327680", GenerateUniverse().ElementAt(7));
    }

    /// <summary>
    /// One line for each system in each galaxy: the galaxy and system numbers,
    /// the seeds, the name, the economy, government, tech level, population
    /// and productivity, the radius, the planet's type, and the planet's and
    /// sun's types and positions in space when we arrive (SOLAR).
    /// </summary>
    private static IEnumerable<string> GenerateUniverse()
    {
        var game = new PrivateGame();
        for (int galaxy = 0; galaxy < 8; galaxy++)
        {
            game.Call("Begin");
            game.Call("ApplySavedCommander");
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
                string name = (string)game.Call("SystemName")!;
                game.Call("CalculateSystemData");
                int techLevel = game.Get<int>("_selectedTechLevel");
                int radius = (((seeds[5] & 15) + 11) << 8) | seeds[3];
                int planetType = (techLevel & 0b00000010) | 0b10000000;

                game.Set("_techLevel", techLevel);
                game.Set("_legalStatus", 0);
                game.Call("ResetBubble");
                game.Call("SetUpSystem");
                var planet = game.Slots[0]!;
                var sun = game.Slots[1]!;

                yield return string.Join(",", [
                    galaxy, system, .. seeds, name,
                    game.Get<int>("_selectedEconomy"), game.Get<int>("_selectedGovernment"), techLevel,
                    game.Get<int>("_selectedPopulation"), game.Get<int>("_selectedProductivity"), radius, planetType,
                    planet.Type, (int)planet.Position.X, (int)planet.Position.Y, (int)planet.Position.Z,
                    sun.Type, (int)sun.Position.X, (int)sun.Position.Y, (int)sun.Position.Z]);

                seeds.CopyTo(selected, 0);
                game.Call("NextSystem");
            }
        }
    }
}

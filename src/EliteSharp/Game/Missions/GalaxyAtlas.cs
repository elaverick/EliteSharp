using System.Text;
using EliteSharp.Data;

namespace EliteSharp.Game.Missions;

/// <summary>A star system, as the mission definitions refer to it.</summary>
/// <param name="Galaxy">The galaxy number, from 0 (galaxy 1 on screen).</param>
/// <param name="Index">The system's number within its galaxy (0-255).</param>
/// <param name="X">The system's galactic x-coordinate.</param>
/// <param name="Y">The system's galactic y-coordinate.</param>
/// <param name="Name">The system's name, in capitals.</param>
public sealed record StarSystem(int Galaxy, int Index, int X, int Y, string Name);

/// <summary>
/// A read-only list of the systems in each galaxy, so the mission definitions
/// can name systems rather than give their coordinates. This works the systems
/// out from the galaxy seeds in the same way the game does (TT54 and cpl), but
/// on its own copy of the seeds, so it has no effect on the game.
/// </summary>
public static class GalaxyAtlas
{
    /// <summary>The number of galaxies.</summary>
    public const int GalaxyCount = 8;

    private static readonly StarSystem[][] Galaxies = [.. Enumerable.Range(0, GalaxyCount).Select(BuildGalaxy)];

    /// <summary>The systems in a galaxy (numbered from 0), in system number order.</summary>
    public static IReadOnlyList<StarSystem> Systems(int galaxy) => Galaxies[galaxy];

    /// <summary>The systems in a galaxy (numbered from 0) with the given name (in any case).</summary>
    public static IEnumerable<StarSystem> Find(int galaxy, string name) =>
        Galaxies[galaxy].Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static StarSystem[] BuildGalaxy(int galaxy)
    {
        // The seeds for galaxy 1 are in the default commander (after the name
        // and the mission byte and coordinates), and each galaxy's seeds are
        // the previous galaxy's seeds with each byte rotated left
        var seeds = new int[6];
        for (int i = 0; i < 6; i++)
        {
            int value = GameData.DefaultCommander[8 + 3 + i];
            for (int g = 0; g < galaxy; g++)
            {
                value = ((value << 1) | (value >> 7)) & 0xFF;
            }

            seeds[i] = value;
        }

        var systems = new StarSystem[256];
        for (int index = 0; index < 256; index++)
        {
            systems[index] = new StarSystem(galaxy, index, seeds[3], seeds[1], Name(seeds));
            for (int i = 0; i < 4; i++)
            {
                Twist(seeds);
            }
        }

        return systems;
    }

    /// <summary>The name of the system with the given seeds (cpl).</summary>
    private static string Name(int[] systemSeeds)
    {
        var seeds = (int[])systemSeeds.Clone();
        var name = new StringBuilder();
        int pairs = (seeds[0] & 0x40) != 0 ? 3 : 2;
        for (int pair = pairs; pair >= 0; pair--)
        {
            int token = seeds[5] & 31;
            if (token != 0)
            {
                int index = token << 1;
                name.Append((char)GameData.TwoLetterTokens[index]);
                if (GameData.TwoLetterTokens[index + 1] != '?')
                {
                    name.Append((char)GameData.TwoLetterTokens[index + 1]);
                }
            }

            Twist(seeds);
        }

        return name.ToString();
    }

    /// <summary>Twist the seeds once (TT54).</summary>
    private static void Twist(int[] seeds)
    {
        int lo = seeds[0] + seeds[2];
        int hi = seeds[1] + seeds[3] + (lo > 0xFF ? 1 : 0);
        lo &= 0xFF;
        hi &= 0xFF;

        seeds[0] = seeds[2];
        seeds[1] = seeds[3];
        seeds[3] = seeds[5];
        seeds[2] = seeds[4];

        int newLo = lo + seeds[2];
        seeds[4] = newLo & 0xFF;
        seeds[5] = (hi + seeds[3] + (newLo > 0xFF ? 1 : 0)) & 0xFF;
    }
}

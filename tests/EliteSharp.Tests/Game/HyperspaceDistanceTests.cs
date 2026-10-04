namespace EliteSharp.Tests.Game;

/// <summary>
/// Hyperspace distances (readdistnce), which decide which systems are within
/// range, must be the original's.
/// </summary>
public sealed class HyperspaceDistanceTests
{
    /// <summary>The most fuel we can hold, in tenths of a light year.</summary>
    private const int FullTank = 70;

    /// <summary>The distance in tenths of a light year between two systems at the given galactic coordinates.</summary>
    private static int Distance(PrivateGame game, int fromX, int fromY, int toX, int toY)
    {
        game.Set("_currentSystemX", fromX);
        game.Set("_currentSystemY", fromY);
        var seeds = game.Get<int[]>("_selectedSeeds");
        seeds[1] = toY;
        seeds[3] = toX;
        game.Call("CalculateDistance", toX);
        return game.Get<int>("_selectedDistance");
    }

    private static int Distance(int fromX, int fromY, int toX, int toY) => Distance(new PrivateGame(), fromX, fromY, toX, toY);

    /// <summary>
    /// readdistnce as the original calculates it: the 16-bit sum of the
    /// squares (capping the high byte at 255), its square root with LL5, and
    /// then 4 times the root.
    /// </summary>
    private static int OriginalDistance(int fromX, int fromY, int toX, int toY)
    {
        int dx = Math.Abs(toX - fromX) & 0xFF;
        int dy = (Math.Abs(toY - fromY) & 0xFF) >> 1;
        int sum = dx * dx + dy * dy;
        int sumHigh = Math.Min(sum >> 8, 255);
        return (OriginalSquareRoot((sumHigh << 8) | (sum & 0xFF)) << 2) & 0x3FF;
    }

    /// <summary>LL5: the 8-bit square root of a 16-bit value, as the original's 6502 code calculates it.</summary>
    private static int OriginalSquareRoot(int value)
    {
        int y = (value >> 8) & 0xFF;
        int s = value & 0xFF;
        int x = 0;
        int q = 0;
        for (int t = 0; t < 8; t++)
        {
            int carry = 0;
            if (x > q || (x == q && y >= 64))
            {
                // SBC #64 then SBC Q, both with the C flag set on entry
                int ySub = y - 64;
                y = ySub & 0xFF;
                int xSub = x - q - (ySub >= 0 ? 0 : 1);
                carry = xSub >= 0 ? 1 : 0;
                x = xSub & 0xFF;
            }

            // ROL Q, then shift (X Y S) left twice
            q = ((q << 1) | carry) & 0xFF;
            for (int pair = 0; pair < 2; pair++)
            {
                int carryLow = (s >> 7) & 1;
                s = (s << 1) & 0xFF;
                int carryHigh = (y >> 7) & 1;
                y = ((y << 1) | carryLow) & 0xFF;
                x = ((x << 1) | carryHigh) & 0xFF;
            }
        }

        return q;
    }

    /// <summary>Whether a full tank takes us this far (the checks in hyp, and the short-range chart's labels).</summary>
    private static bool WithinRange(int distance) => (distance >> 8) == 0 && distance <= FullTank;

    /// <summary>The galactic coordinates of every system in the given galaxy.</summary>
    private static List<(int X, int Y)> Systems(PrivateGame game, int galaxy)
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
        var systems = new List<(int X, int Y)>();
        for (int system = 0; system < 256; system++)
        {
            systems.Add((selected[3], selected[1]));
            game.Call("NextSystem");
        }

        return systems;
    }

    [Fact]
    public void TheSquareRootIsLL5()
    {
        // LL5 rounds the square root down, for every 16-bit value
        for (int value = 0; value <= 0xFFFF; value++)
        {
            Assert.Equal((int)Math.Sqrt(value), OriginalSquareRoot(value));
        }
    }

    [Theory]
    [InlineData(20, 173, 11, 174, 36)]  // Lave to Diso
    [InlineData(20, 173, 13, 186, 36)]  // Lave to Leesti
    [InlineData(20, 173, 20, 173, 0)]
    [InlineData(0, 0, 0, 1, 0)]         // Half a y unit is rounded down
    [InlineData(0, 0, 17, 0, 68)]       // The last distance below a full tank...
    [InlineData(0, 0, 18, 0, 72)]       // ...and the first beyond it
    [InlineData(0, 0, 255, 255, 1136)]
    public void HyperspaceDistancesAreInTenthsOfALightYear(int fromX, int fromY, int toX, int toY, int expected)
    {
        // Each unit of x is 0.4 light years, and each unit of y is 0.2
        Assert.Equal(expected, Distance(fromX, fromY, toX, toY));
        Assert.Equal(expected, Distance(toX, toY, fromX, fromY));
    }

    [Fact]
    public void EveryDistanceIsTheOriginals()
    {
        // The distance depends only on the size of the differences in x and
        // y, so this covers every pair of galactic coordinates. Only where
        // the original's 16-bit sum of the squares overflows (more than 102
        // light years) is the distance larger, and that is out of range
        // either way.
        var game = new PrivateGame();
        for (int dx = 0; dx < 256; dx++)
        {
            for (int dy = 0; dy < 256; dy++)
            {
                int distance = Distance(game, 0, 0, dx, dy);
                int original = OriginalDistance(0, 0, dx, dy);
                if (dx * dx + (dy >> 1) * (dy >> 1) <= 0xFFFF)
                {
                    Assert.Equal(original, distance);
                }
                else
                {
                    Assert.True(distance > original, $"({dx}, {dy}): {distance} against {original}");
                }

                Assert.Equal(WithinRange(original), WithinRange(distance));
                Assert.Equal(original >= FullTank, distance >= FullTank);
            }
        }
    }

    [Fact]
    public void EverySystemWithinRangeIsTheOriginals()
    {
        // Every pair of systems in all eight galaxies
        var game = new PrivateGame();
        int reachable = 0;
        for (int galaxy = 0; galaxy < 8; galaxy++)
        {
            var systems = Systems(game, galaxy);
            foreach (var from in systems)
            {
                foreach (var to in systems)
                {
                    int distance = Distance(game, from.X, from.Y, to.X, to.Y);
                    int original = OriginalDistance(from.X, from.Y, to.X, to.Y);
                    Assert.True(
                        WithinRange(original) == WithinRange(distance),
                        $"Galaxy {galaxy + 1}, ({from.X}, {from.Y}) to ({to.X}, {to.Y}): {distance} against {original}");
                    if (WithinRange(original))
                    {
                        Assert.Equal(original, distance);
                        reachable++;
                    }
                }
            }
        }

        // The number of ordered pairs (including each system to itself) that
        // are within 7.0 light years in the original
        Assert.Equal(OriginalReachablePairs, reachable);
    }

    /// <summary>Measured with <see cref="OriginalDistance"/> over all eight galaxies.</summary>
    private const int OriginalReachablePairs = 16564;
}

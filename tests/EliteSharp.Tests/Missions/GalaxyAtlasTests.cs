using EliteSharp.Game.Missions;

namespace EliteSharp.Tests.Missions;

public sealed class GalaxyAtlasTests
{
    [Theory]
    [InlineData(1, "LAVE", 7, 20, 173)]
    [InlineData(1, "DISO", 147, 11, 174)]
    [InlineData(2, "ORARRA", 193, 144, 33)]
    [InlineData(3, "CEERDI", 83, 215, 84)]
    [InlineData(3, "BIRERA", 36, 63, 72)]
    public void TheAtlasHasTheSystemsTheMissionsUse(int galaxy, string name, int index, int x, int y)
    {
        var system = Assert.Single(GalaxyAtlas.Find(galaxy - 1, name));
        Assert.Equal((galaxy - 1, index, x, y, name), (system.Galaxy, system.Index, system.X, system.Y, system.Name));
        Assert.Same(system, GalaxyAtlas.Systems(galaxy - 1)[index]);
    }

    [Fact]
    public void NamesAreFoundInAnyCase() => Assert.Single(GalaxyAtlas.Find(0, "lave"));

    [Fact]
    public void EachGalaxyHas256Systems()
    {
        for (int galaxy = 0; galaxy < GalaxyAtlas.GalaxyCount; galaxy++)
        {
            Assert.Equal(256, GalaxyAtlas.Systems(galaxy).Count);
        }
    }
}

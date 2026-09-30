using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering.Geometry;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Tests.Rendering;

/// <summary>Checks how the renderer decides whether a ship can be seen, and how much detail to draw.</summary>
public sealed class ShipVisibilityTests
{
    private const float FourByThree = 4f / 3f;
    private const float Widescreen = 16f / 9f;

    [Fact]
    public void ShipsAreVisibleAcrossTheWholeFieldOfView()
    {
        // Points just inside each edge of the view, at a range of distances
        foreach (float aspect in new[] { FourByThree, Widescreen, 21f / 9f })
        {
            var frustum = new ViewFrustum(aspect);
            float tanX = Camera.TanHalfFovY * aspect * 0.99f;
            float tanY = Camera.TanHalfFovY * 0.99f;
            foreach (float z in new[] { 10f, 1000f, 100_000f })
            {
                foreach (var direction in new[] { new Vector3(tanX, 0, 1), new Vector3(-tanX, 0, 1), new Vector3(0, tanY, 1), new Vector3(0, -tanY, 1), new Vector3(tanX, tanY, 1) })
                {
                    Assert.True(frustum.Intersects(direction * z, 1), $"aspect {aspect}: {direction * z} should be visible");
                }
            }
        }
    }

    [Fact]
    public void WideViewsShowShipsOutsideTheOriginalScreen()
    {
        // The original's screen reaches x / z = 128 / 256 on each side; a ship
        // beyond that (but within the original's 45-degree field of view) is
        // off the edge of a 4:3 view, but in a widescreen one
        var ship = new Vector3(0.6f * 2000, 0, 2000);
        Assert.False(new ViewFrustum(FourByThree).Intersects(ship, 1));
        Assert.True(new ViewFrustum(Widescreen).Intersects(ship, 1));
    }

    [Fact]
    public void ShipsOutsideTheViewAreCulled()
    {
        var frustum = new ViewFrustum(Widescreen);
        Assert.False(frustum.Intersects(new Vector3(0, 0, -500), 50));
        Assert.False(frustum.Intersects(new Vector3(5000, 0, 1000), 50));
        Assert.False(frustum.Intersects(new Vector3(0, 2000, 1000), 50));

        // A ship that is partly in view is drawn
        float edge = Camera.TanHalfFovY * Widescreen * 1000;
        Assert.True(frustum.Intersects(new Vector3(edge + 20, 0, 1000), 50));
    }

    [Fact]
    public void ThereIsNoDistanceBeyondWhichShipsDisappear()
    {
        var frustum = new ViewFrustum(Widescreen);
        foreach (float z in new[] { 1e3f, 1e5f, 1e7f })
        {
            Assert.True(frustum.Intersects(new Vector3(0, 0, z), 30));
            float screenRadius = ViewFrustum.ProjectedRadius(30, z, 720);
            Assert.InRange(ShipLevelOfDetail.LevelFor(screenRadius), 0, ShipGeometry.LevelCount - 1);
        }
    }

    [Fact]
    public void TheProjectedSizeMatchesTheCamera()
    {
        // A sphere whose radius fills half the viewport's height at the top
        // edge of the view is as tall on screen as half the viewport
        float distance = 1000;
        float radius = Camera.TanHalfFovY * distance;
        Assert.Equal(360, ViewFrustum.ProjectedRadius(radius, distance, 720), 0.01f);

        // And it scales with the size of the viewport
        Assert.Equal(2 * ViewFrustum.ProjectedRadius(10, distance, 720), ViewFrustum.ProjectedRadius(10, distance, 1440), 0.01f);
    }

    [Fact]
    public void EverySizeHasALevelOfDetail()
    {
        Assert.Equal(0, ShipLevelOfDetail.LevelFor(1000));
        Assert.Equal(ShipGeometry.LevelCount - 1, ShipLevelOfDetail.LevelFor(0));
        Assert.Equal(ShipGeometry.LevelCount - 1, ShipLevelOfDetail.LevelFor(0.001f));
        Assert.Equal(ShipGeometry.LevelCount - 1, ShipLevelOfDetail.LevelSizes.Count);
    }

    [Theory]
    [InlineData("constrictor", 720)]
    [InlineData("cobra-mk3", 720)]
    [InlineData("coriolis", 1080)]
    [InlineData("missile", 480)]
    public void TheLevelChangesSmoothlyAsAShipMovesAway(string model, float viewportHeight)
    {
        var asset = ShipMeshAsset.Load(Path.Combine(ShipCatalogue.AssetFolder, "Models", model + ".gltf"));
        var geometry = ShipGeometry.Build(asset);

        // Moving steadily away, the level only ever gets simpler, one step at a
        // time, and reaches the simplest level without the ship disappearing
        int previous = 0;
        var changes = new List<float>();
        for (float distance = geometry.Radius; distance < 1e6f; distance *= 1.01f)
        {
            int level = ShipLevelOfDetail.LevelFor(ViewFrustum.ProjectedRadius(geometry.Radius, distance, viewportHeight));
            Assert.InRange(level, previous, previous + 1);
            if (level != previous)
            {
                changes.Add(distance);
            }

            previous = level;
        }

        Assert.Equal(ShipGeometry.LevelCount - 1, previous);
        Assert.Equal(ShipGeometry.LevelCount - 1, changes.Count);
    }
}

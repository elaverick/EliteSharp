namespace EliteSharp.Rendering.Geometry;

/// <summary>
/// Chooses a ship's level of detail (see <see cref="ShipGeometry"/>) from its
/// size on the screen. Every size has a level, so a ship is always drawn as
/// its 3D geometry, however far away it is; it just has fewer lines.
/// </summary>
public static class ShipLevelOfDetail
{
    /// <summary>
    /// The sizes on the screen (the radius of the ship's bounding sphere, in
    /// pixels) at or below which each simpler level takes over: at this size or
    /// smaller, level i + 1 is used. The details on the faces go when they are
    /// only a few pixels across, and then the panel lines, and then all but the
    /// sharpest creases, where the lines would otherwise merge into a blur.
    /// </summary>
    public static readonly IReadOnlyList<float> LevelSizes = [24, 12, 7];

    /// <summary>The level of detail for a ship whose bounding sphere has the given radius on the screen, in pixels.</summary>
    public static int LevelFor(float screenRadius)
    {
        int level = 0;
        while (level < LevelSizes.Count && screenRadius <= LevelSizes[level])
        {
            level++;
        }

        return level;
    }
}

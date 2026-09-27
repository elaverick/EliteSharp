namespace EliteSharp.Game.Ships;

/// <summary>
/// A vertex in a ship blueprint (VERTEX macro): coordinates, the four faces that
/// the vertex belongs to, and the visibility distance beyond which it is hidden.
/// </summary>
public readonly record struct ShipVertex(int X, int Y, int Z, int Face1, int Face2, int Face3, int Face4, int Visibility);

/// <summary>
/// An edge in a ship blueprint (EDGE macro): the two vertices it joins, the two
/// faces either side of it and its visibility distance.
/// </summary>
public readonly record struct ShipEdge(int Vertex1, int Vertex2, int Face1, int Face2, int Visibility);

/// <summary>
/// A face in a ship blueprint (FACE macro): the face normal (before scaling by
/// the blueprint's normal scale factor) and the distance beyond which the face
/// is always treated as visible.
/// </summary>
public readonly record struct ShipFace(int NormalX, int NormalY, int NormalZ, int Visibility);

/// <summary>
/// The static data for a type of ship, as stored in the blueprints at XX21 in the
/// original. Each blueprint is extracted byte-for-byte from the assembled game
/// data by tools/gen_data.py.
/// </summary>
public sealed class ShipBlueprint(
    int blueprintNumber,
    string name,
    int byte0,
    int targetableArea,
    int lineHeapSize,
    int gunVertex,
    int explosionCountByte,
    int bounty,
    int visibilityDistance,
    int maxEnergy,
    int maxSpeed,
    int normalScale,
    int laserAndMissiles,
    ShipVertex[] vertices,
    ShipEdge[] edges,
    ShipFace[] faces)
{
    /// <summary>The blueprint's position in the XX21 table (1-33).</summary>
    public int BlueprintNumber { get; } = blueprintNumber;

    public string Name { get; } = name;

    /// <summary>
    /// Blueprint byte #0: bits 0-3 contain the maximum number of canisters released
    /// on demise, bits 4-7 contain the market item (less 1) when the ship is scooped.
    /// </summary>
    public int Byte0 { get; } = byte0;

    public int MaxCanisters => Byte0 & 0x0F;

    /// <summary>The market item when scooped (bits 4-7 of byte #0 plus 1), or 0 for none.</summary>
    public int ScoopedItem => Byte0 >> 4;

    /// <summary>Bytes #1-2: the targetable area (the square of the targeting radius).</summary>
    public int TargetableArea { get; } = targetableArea;

    /// <summary>Byte #5: the size of the ship line heap, i.e. 1 + 4 * the maximum number of visible edges.</summary>
    public int LineHeapSize { get; } = lineHeapSize;

    public int MaxVisibleEdges => (LineHeapSize - 1) / 4;

    /// <summary>Byte #6 / 4: the vertex from which the ship fires its lasers.</summary>
    public int GunVertex { get; } = gunVertex;

    /// <summary>Byte #7: the explosion count, stored as 4 * n + 6 for n exploding vertices.</summary>
    public int ExplosionCountByte { get; } = explosionCountByte;

    /// <summary>Bytes #10-11: the bounty in Cr * 10.</summary>
    public int Bounty { get; } = bounty;

    /// <summary>Byte #13: the distance (z_hi) beyond which the ship is shown as a dot.</summary>
    public int VisibilityDistance { get; } = visibilityDistance;

    /// <summary>Byte #14: the ship's maximum energy.</summary>
    public int MaxEnergy { get; } = maxEnergy;

    /// <summary>Byte #15: the ship's maximum speed.</summary>
    public int MaxSpeed { get; } = maxSpeed;

    /// <summary>Byte #18: face normals are scaled by 2^NormalScale.</summary>
    public int NormalScale { get; } = normalScale;

    /// <summary>Byte #19: bits 3-7 contain the laser power, bits 0-2 the number of missiles.</summary>
    public int LaserAndMissiles { get; } = laserAndMissiles;

    public int LaserPower => LaserAndMissiles >> 3;

    public int Missiles => LaserAndMissiles & 7;

    public IReadOnlyList<ShipVertex> Vertices { get; } = vertices;

    public IReadOnlyList<ShipEdge> Edges { get; } = edges;

    public IReadOnlyList<ShipFace> Faces { get; } = faces;
}

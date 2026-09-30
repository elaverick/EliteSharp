namespace EliteSharp.Rendering.Geometry;

/// <summary>A triangle, as indices into a list of points.</summary>
public readonly record struct Triangle(int A, int B, int C);

using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering.Geometry;

namespace EliteSharp.Tests.Rendering;

/// <summary>Checks the geometry that the renderer builds for every ship model.</summary>
public sealed class ShipGeometryTests
{
    private static string ModelFolder => Path.Combine(ShipCatalogue.AssetFolder, "Models");

    private static List<string> ModelNames =>
        [.. Directory.EnumerateFiles(ModelFolder, "*.gltf").Select(path => Path.GetFileNameWithoutExtension(path)).Order()];

    public static TheoryData<string> Models() => [.. ModelNames];

    private static ShipMeshAsset Load(string model) => ShipMeshAsset.Load(Path.Combine(ModelFolder, model + ".gltf"));

    /// <summary>The alloy plate is a flat plate, so its surface has both sides rather than enclosing a volume.</summary>
    private static bool IsTwoSided(string model) => model == "alloy-plate";

    [Fact]
    public void EveryShipTypeHasAModel()
    {
        var models = ModelNames.ToHashSet();
        Assert.All(ShipCatalogue.All, blueprint => Assert.Contains(blueprint.Model.Name, models));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void TheSurfaceIsClosedAndFacesOutwards(string model)
    {
        var asset = Load(model);
        var points = asset.Points;
        Assert.NotEmpty(asset.Surface);

        var directedEdges = new HashSet<(int, int)>();
        double volume = 0;
        foreach (var t in asset.Surface)
        {
            Assert.All(new[] { t.A, t.B, t.C }, i => Assert.InRange(i, 0, points.Count - 1));
            var normal = Vector3.Cross(points[t.B] - points[t.A], points[t.C] - points[t.A]);
            Assert.True(normal.Length() > 1e-3f, $"{model}: triangle {t} has no area");

            volume += Vector3.Dot(points[t.A], Vector3.Cross(points[t.B], points[t.C])) / 6.0;
            if (IsTwoSided(model))
            {
                // Each triangle has a twin facing the other way
                Assert.Contains(asset.Surface, other => other == new Triangle(t.A, t.C, t.B) || other == new Triangle(t.C, t.B, t.A) || other == new Triangle(t.B, t.A, t.C));
                continue;
            }

            // Each edge is used once in each direction, by the triangles on
            // either side, if the surface is closed and consistently wound
            Assert.True(directedEdges.Add((t.A, t.B)) & directedEdges.Add((t.B, t.C)) & directedEdges.Add((t.C, t.A)), $"{model}: an edge of {t} is used twice in the same direction");
        }

        Assert.All(directedEdges, e => Assert.Contains((e.Item2, e.Item1), directedEdges));
        if (IsTwoSided(model))
        {
            Assert.Equal(0, volume, 1e-3);
        }
        else
        {
            Assert.True(volume > 0, $"{model}: the surface faces inwards (volume {volume})");
        }
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void TheLinesMatchTheSurface(string model)
    {
        var asset = Load(model);
        var geometry = ShipGeometry.Build(asset);
        var points = asset.Points;
        var surfaceEdges = asset.Surface
            .SelectMany(t => new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            .Select(e => (Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2)))
            .GroupBy(e => e)
            .ToDictionary(g => g.Key, g => g.Count());
        var surfacePoints = asset.Surface.SelectMany(t => new[] { t.A, t.B, t.C }).ToHashSet();

        Assert.Equal(asset.Structure.Count + asset.Details.Count, geometry.Edges.Count);
        foreach (var edge in geometry.Edges)
        {
            var key = (Math.Min(edge.A, edge.B), Math.Max(edge.A, edge.B));
            switch (edge.Kind)
            {
                case ShipEdgeKind.Crease:
                    // A crease is where two of the surface's triangles meet
                    Assert.Equal(2, surfaceEdges.GetValueOrDefault(key));
                    Assert.InRange(edge.CreaseAngle, 0, 180);
                    break;

                case ShipEdgeKind.Protrusion:
                    // A protrusion sticks out of the surface
                    Assert.False(surfaceEdges.ContainsKey(key));
                    Assert.False(surfacePoints.Contains(edge.A) && surfacePoints.Contains(edge.B), $"{model}: protrusion {key} lies on the surface");
                    break;

                case ShipEdgeKind.Detail:
                    // A detail is drawn on one of the surface's faces (to within
                    // the rounding of the original's whole-number coordinates,
                    // which is well within the distance the renderer pushes the
                    // surface back so the lines on it show)
                    foreach (float along in new[] { 0.25f, 0.5f, 0.75f })
                    {
                        var point = Vector3.Lerp(points[edge.A], points[edge.B], along);
                        float distance = asset.Surface.Min(t => DistanceToTriangle(point, points[t.A], points[t.B], points[t.C]));
                        Assert.True(distance < 0.1f, $"{model}: detail {key} is {distance} from the surface");
                    }
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void EachLevelIsASimplerVersionOfTheOneBefore(string model)
    {
        var asset = Load(model);
        var geometry = ShipGeometry.Build(asset);
        Assert.Equal(ShipGeometry.LevelCount, geometry.Levels.Count);
        Assert.Equal(asset.Points.Max(p => p.Length()), geometry.Radius);

        for (int level = 0; level < ShipGeometry.LevelCount; level++)
        {
            var shipLevel = geometry.Levels[level];

            // Every level has the model's real surface, so its lines are hidden
            // by the same surface they lie on
            Assert.Same(asset.Points, shipLevel.Points);
            Assert.Equal(asset.Surface, shipLevel.Surface);

            // Every level has lines to draw, all between the model's points
            Assert.NotEmpty(shipLevel.Edges);
            Assert.All(shipLevel.Edges, e =>
            {
                Assert.InRange(e.A, 0, asset.Points.Count - 1);
                Assert.InRange(e.B, 0, asset.Points.Count - 1);
            });

            if (level > 0)
            {
                var previous = geometry.Levels[level - 1].Edges.ToHashSet();
                Assert.Subset(previous, shipLevel.Edges.ToHashSet());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void TheLevelsDropLinesInOrderOfImportance(string model)
    {
        var geometry = ShipGeometry.Build(Load(model));
        var level1 = geometry.Levels[1].Edges.ToHashSet();
        var level2 = geometry.Levels[2].Edges.ToHashSet();
        var level3 = geometry.Levels[3].Edges.ToHashSet();
        foreach (var edge in geometry.Edges)
        {
            var line = (edge.A, edge.B);
            if (edge.Kind == ShipEdgeKind.Protrusion || (edge.Kind == ShipEdgeKind.Crease && edge.CreaseAngle >= ShipGeometry.SharpCreaseAngle))
            {
                // Sharp creases and protrusions survive to the simplest level
                Assert.Contains(line, level3);
            }
            else if (edge.Kind == ShipEdgeKind.Crease && edge.CreaseAngle < ShipGeometry.GentleCreaseAngle && level2.Count < level1.Count)
            {
                // Nearly flat panel lines go first
                Assert.DoesNotContain(line, level2);
            }
        }
    }

    [Fact]
    public void TheSimplerLevelsHaveFewerLinesAcrossTheShips()
    {
        var geometries = ModelNames.Select(m => ShipGeometry.Build(Load(m))).ToList();
        int[] totals = [.. Enumerable.Range(0, ShipGeometry.LevelCount).Select(l => geometries.Sum(g => g.Levels[l].Edges.Count))];
        Assert.True(totals[0] > totals[1] && totals[1] > totals[2] && totals[2] > totals[3], string.Join(", ", totals));
    }

    [Fact]
    public void TheConstrictorKeepsItsShapeAtEveryLevel()
    {
        var geometry = ShipGeometry.Build(Load("constrictor"));

        // Its details (6) go first, then its flat panel lines, but its sharp
        // outline stays
        Assert.Equal(24, geometry.Levels[0].Edges.Count);
        Assert.Equal(18, geometry.Levels[1].Edges.Count);
        Assert.True(geometry.Levels[3].Edges.Count >= 8, $"{geometry.Levels[3].Edges.Count} lines");
    }

    /// <summary>The distance from a point to a triangle.</summary>
    private static float DistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        var projected = p - Vector3.Dot(p - a, normal) * normal;
        bool inside = Vector3.Dot(Vector3.Cross(b - a, projected - a), normal) >= -1e-4f
            && Vector3.Dot(Vector3.Cross(c - b, projected - b), normal) >= -1e-4f
            && Vector3.Dot(Vector3.Cross(a - c, projected - c), normal) >= -1e-4f;
        if (inside)
        {
            return MathF.Abs(Vector3.Dot(p - a, normal));
        }

        static float ToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            float t = Math.Clamp(Vector3.Dot(p - a, b - a) / (b - a).LengthSquared(), 0, 1);
            return Vector3.Distance(p, a + t * (b - a));
        }

        return MathF.Min(ToSegment(p, a, b), MathF.Min(ToSegment(p, b, c), ToSegment(p, c, a)));
    }
}

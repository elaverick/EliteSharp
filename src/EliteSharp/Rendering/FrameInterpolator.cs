using System.Numerics;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Rendering;

/// <summary>
/// Moves the 3D world smoothly between the game's frames, so the renderer can
/// draw as often as the display allows while the game runs at its own fixed
/// rate (16 main loop iterations a second by default).
///
/// Each frame from the game carries the world as it was in the frame before
/// (see <see cref="FrameData.PreviousWorld"/>), and says when it was published
/// and when the next one is due (see <see cref="FrameData.NextTime"/>).
/// Between the two, the renderer draws the world part of the way from the
/// previous frame to this one, so it arrives at this frame just as the next one
/// is due. This shows the world up to one frame later than the game has it,
/// which is the price of smooth motion.
///
/// What is drawn depends only on the latest frame and the time it is drawn,
/// not on how often the renderer draws or which frames it happened to see, so
/// drawing faster or slower only changes how many in-between steps are shown.
///
/// This only changes what is drawn: the game never sees it, so it can't change
/// the game. Only the ships, the planet, the sun and the stardust move
/// smoothly; everything else (such as laser beams, explosions and the HUD) is
/// drawn as it is in the latest frame, just as the original draws it. Nothing
/// moves smoothly across a cut (when the screen is cleared or the view
/// changes), and an object that jumps a long way in one frame just appears in
/// its new place. Stardust that the game recycles (when it goes off the edge
/// of the screen and starts again elsewhere) moves in from where it would
/// have been had it been there all along.
/// </summary>
public sealed class FrameInterpolator
{
    /// <summary>
    /// The furthest an object can move in one frame and still move smoothly,
    /// as the ratio between its distances from us in the two frames.
    /// </summary>
    private const float MaxDistanceRatio = 3;

    /// <summary>
    /// The cosine of the largest angle (30 degrees) through which an object can
    /// move around us in one frame and still move smoothly (our fastest roll
    /// turns everything by about 7 degrees a frame).
    /// </summary>
    private static readonly float MinDirectionCosine = MathF.Cos(MathF.PI / 6);

    private readonly SceneFrame _blended = new();
    private readonly Dictionary<int, int> _previousIndex = [];

    /// <summary>Whether to move smoothly between frames (if not, the latest frame is drawn as it is).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The 3D world to draw at the given time (on the game's clock), from the
    /// latest frame that the renderer has taken (see
    /// <see cref="FrameExchange.TakeLatest"/>). The result belongs to the
    /// interpolator, or is the frame's own world, and is only good until the
    /// next call.
    /// </summary>
    public SceneFrame World(FrameData frame, long now)
    {
        var from = frame.PreviousWorld;
        var to = frame.World;
        if (!Enabled || !frame.HasWorld || !frame.HasPreviousWorld ||
            from.Generation != to.Generation || from.Camera.View != to.Camera.View)
        {
            return to;
        }

        float t = Progress(frame, now);
        if (t >= 1)
        {
            return to;
        }

        Blend(from, to, t, _blended);
        return _blended;
    }

    /// <summary>
    /// How far to go from a frame's previous world to its own at the given
    /// time, from 0 (when it was published) to 1 (when the next frame is due).
    /// </summary>
    public static float Progress(FrameData frame, long now)
    {
        if (frame.NextTime <= frame.Time)
        {
            return 1;
        }

        return (float)Math.Clamp((double)(now - frame.Time) / (frame.NextTime - frame.Time), 0, 1);
    }

    /// <summary>
    /// Put the world part of the way (t, from 0 to 1) from one frame to the
    /// next into a third frame. Everything in the result is from the next
    /// frame, but the objects that are in both frames are moved back towards
    /// where they were in the first.
    /// </summary>
    public void Blend(SceneFrame from, SceneFrame to, float t, SceneFrame result)
    {
        result.CopyFrom(to);

        IndexById(from.Ships, s => s.Id);
        for (int i = 0; i < result.Ships.Count; i++)
        {
            var ship = result.Ships[i];
            if (_previousIndex.TryGetValue(ship.Id, out int index) && from.Ships[index] is var old && old.Model == ship.Model &&
                CanMoveSmoothly(old.Transform.Translation, ship.Transform.Translation))
            {
                result.Ships[i] = ship with { Transform = BlendTransform(old.Transform, ship.Transform, t) };
            }
        }

        IndexById(from.Planets, p => p.Id);
        for (int i = 0; i < result.Planets.Count; i++)
        {
            var planet = result.Planets[i];
            if (_previousIndex.TryGetValue(planet.Id, out int index) && from.Planets[index] is var old &&
                CanMoveSmoothly(old.Centre, planet.Centre) &&
                BlendAxes(old.Side, old.Roof, old.Nose, planet.Side, planet.Roof, planet.Nose, t) is (var side, var roof, var nose))
            {
                result.Planets[i] = planet with { Centre = Vector3.Lerp(old.Centre, planet.Centre, t), Side = side, Roof = roof, Nose = nose };
            }
        }

        IndexById(from.Suns, s => s.Id);
        for (int i = 0; i < result.Suns.Count; i++)
        {
            var sun = result.Suns[i];
            if (_previousIndex.TryGetValue(sun.Id, out int index) && from.Suns[index] is var old && CanMoveSmoothly(old.Centre, sun.Centre))
            {
                result.Suns[i] = sun with { Centre = Vector3.Lerp(old.Centre, sun.Centre, t) };
            }
        }

        // Stardust that has just been given a new place to start from moves in
        // from where it would have been a frame earlier (see Particle.Entry)
        IndexById(from.Particles, p => p.Stardust ? p.Id : 0);
        for (int i = 0; i < result.Particles.Count; i++)
        {
            var particle = result.Particles[i];
            if (!particle.Stardust)
            {
                continue;
            }

            Vector3? start = _previousIndex.TryGetValue(particle.Id, out int index) ? from.Particles[index].Position : particle.Entry;
            if (start is { } position && CanMoveSmoothly(position, particle.Position))
            {
                result.Particles[i] = particle with { Position = Vector3.Lerp(position, particle.Position, t) };
            }
        }
    }

    /// <summary>Index the objects with ids (other than 0) by their ids.</summary>
    private void IndexById<T>(List<T> objects, Func<T, int> id)
    {
        _previousIndex.Clear();
        for (int i = 0; i < objects.Count; i++)
        {
            if (id(objects[i]) is int key and not 0)
            {
                _previousIndex[key] = i;
            }
        }
    }

    /// <summary>
    /// Whether an object that moved from one place to another in one frame
    /// can move smoothly between them, rather than having jumped.
    /// </summary>
    public static bool CanMoveSmoothly(Vector3 from, Vector3 to)
    {
        float fromDistance = from.Length();
        float toDistance = to.Length();
        float near = MathF.Min(fromDistance, toDistance);
        float far = MathF.Max(fromDistance, toDistance);
        if (near < 1e-3f)
        {
            return far < 1e-3f;
        }

        return far <= near * MaxDistanceRatio && Vector3.Dot(from, to) >= MinDirectionCosine * fromDistance * toDistance;
    }

    /// <summary>
    /// A model-to-world transform part of the way from one to another, with
    /// the position moved in a straight line and the orientation turned, and
    /// the axes kept at right angles to each other and the same way round as
    /// in the second transform.
    /// </summary>
    public static Matrix4x4 BlendTransform(Matrix4x4 from, Matrix4x4 to, float t)
    {
        static Vector3 Row(Matrix4x4 m, int row) => row switch
        {
            0 => new Vector3(m.M11, m.M12, m.M13),
            1 => new Vector3(m.M21, m.M22, m.M23),
            _ => new Vector3(m.M31, m.M32, m.M33),
        };

        if (BlendAxes(Row(from, 0), Row(from, 1), Row(from, 2), Row(to, 0), Row(to, 1), Row(to, 2), t) is not (var side, var roof, var nose))
        {
            return to;
        }

        var position = Vector3.Lerp(from.Translation, to.Translation, t);
        return new Matrix4x4(
            side.X, side.Y, side.Z, 0,
            roof.X, roof.Y, roof.Z, 0,
            nose.X, nose.Y, nose.Z, 0,
            position.X, position.Y, position.Z, 1);
    }

    /// <summary>
    /// Orientation vectors (sidev, roofv and nosev) part of the way from one
    /// set to another, or null if they turned too far to tell which way they
    /// went.
    /// </summary>
    private static (Vector3 Side, Vector3 Roof, Vector3 Nose)? BlendAxes(
        Vector3 fromSide, Vector3 fromRoof, Vector3 fromNose, Vector3 toSide, Vector3 toRoof, Vector3 toNose, float t)
    {
        var nose = Vector3.Lerp(fromNose, toNose, t);
        if (nose.LengthSquared() < 1e-6f)
        {
            return null;
        }

        nose = Vector3.Normalize(nose);
        var roof = Vector3.Lerp(fromRoof, toRoof, t);
        roof -= nose * Vector3.Dot(roof, nose);
        if (roof.LengthSquared() < 1e-6f)
        {
            return null;
        }

        roof = Vector3.Normalize(roof);

        // sidev is at right angles to the other two, and points the same way
        // as in the second set (the original's axes are left-handed)
        var side = Vector3.Cross(roof, nose);
        if (Vector3.Dot(side, toSide) < 0)
        {
            side = -side;
        }

        return (side, roof, nose);
    }
}

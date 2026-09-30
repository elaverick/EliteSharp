using System.Numerics;

namespace EliteSharp.Rendering.Scene;

/// <summary>
/// The volume of space that the camera can see, for a viewport of the given
/// aspect ratio (width / height): everything in front of the near plane and
/// within the camera's field of view, which is the original's vertically and
/// as wide as the viewport horizontally (see <see cref="Camera"/>). There is
/// no far plane.
/// </summary>
public readonly struct ViewFrustum
{
    // The unit normals of the four sides (pointing out of the frustum, in view
    // space), each of which passes through the camera
    private readonly Vector3 _right, _left, _top, _bottom;

    public ViewFrustum(float aspect)
    {
        float tanX = Camera.TanHalfFovY * aspect;
        float tanY = Camera.TanHalfFovY;
        _right = Vector3.Normalize(new Vector3(1, 0, -tanX));
        _left = Vector3.Normalize(new Vector3(-1, 0, -tanX));
        _top = Vector3.Normalize(new Vector3(0, 1, -tanY));
        _bottom = Vector3.Normalize(new Vector3(0, -1, -tanY));
    }

    /// <summary>Whether any of a sphere (with its centre in view space) might be seen.</summary>
    public bool Intersects(Vector3 centre, float radius) =>
        centre.Z + radius > Camera.NearPlane
        && Vector3.Dot(centre, _right) < radius
        && Vector3.Dot(centre, _left) < radius
        && Vector3.Dot(centre, _top) < radius
        && Vector3.Dot(centre, _bottom) < radius;

    /// <summary>
    /// The radius on screen, in pixels, of a sphere of the given radius at the
    /// given distance from the camera, in a viewport of the given height in
    /// pixels (measured by distance rather than depth, so it doesn't change as
    /// the camera turns).
    /// </summary>
    public static float ProjectedRadius(float radius, float distance, float viewportHeight) =>
        radius / MathF.Max(distance, Camera.NearPlane) * (viewportHeight / 2) / Camera.TanHalfFovY;
}

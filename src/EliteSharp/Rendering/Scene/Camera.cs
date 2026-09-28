using System.Numerics;

namespace EliteSharp.Rendering.Scene;

/// <summary>
/// The camera for the 3D world. Elite's universe is centred on our ship, so
/// world space is our ship's own frame of reference (x to the right, y up and
/// z along our nose, in the original's units), and the camera sits at the
/// origin looking out of one of the four space views.
///
/// The projection matches the original's: Elite projects a point onto the
/// space view with screen y = 96 - 256 * y / z (and the same scale in x), so
/// the top edge of the 192-pixel-high view is at y / z = 96 / 256. We keep
/// that vertical field of view and let the horizontal field of view follow
/// the window's aspect ratio, so the world fills any window shape and the
/// original 4:3 view is the centre of a wider one.
/// </summary>
public readonly struct Camera(int view)
{
    /// <summary>tan(half the vertical field of view), from the original's projection.</summary>
    public const float TanHalfFovY = 96f / 256f;

    /// <summary>The distance to the near clipping plane, in world units.</summary>
    public const float NearPlane = 2f;

    /// <summary>The space view (0 = front, 1 = rear, 2 = left, 3 = right).</summary>
    public int View { get; } = view;

    /// <summary>The view matrix, which transforms world space into view space (x right, y up, z forward).</summary>
    public Matrix4x4 ViewMatrix => ViewRotation(View);

    /// <summary>
    /// The projection matrix for Vulkan's clip space (y down), using reversed
    /// depth with an infinite far plane: depth is 1 at the near plane and
    /// tends to 0 at infinity, which keeps the depth buffer precise across
    /// the huge range of distances between a ship's hull and the planet.
    /// </summary>
    public static Matrix4x4 Projection(float aspect)
    {
        float focal = 1f / TanHalfFovY;
        return new Matrix4x4(
            focal / aspect, 0, 0, 0,
            0, -focal, 0, 0,
            0, 0, 0, 1,
            0, 0, NearPlane, 0);
    }

    /// <summary>
    /// The rotation from world space to the space of one of the four views.
    /// This is the same transformation that PLUT applies to each ship in the
    /// original before drawing it:
    ///
    ///   front: (x, y, z)        rear: (-x, y, -z)
    ///   left:  (z, y, -x)       right: (-z, y, x)
    /// </summary>
    public static Matrix4x4 ViewRotation(int view) => view switch
    {
        // Each column is one of the view's axes (right, up, forward) in world space
        1 => new Matrix4x4(
            -1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, -1, 0,
            0, 0, 0, 1),
        2 => new Matrix4x4(
            0, 0, -1, 0,
            0, 1, 0, 0,
            1, 0, 0, 0,
            0, 0, 0, 1),
        3 => new Matrix4x4(
            0, 0, 1, 0,
            0, 1, 0, 0,
            -1, 0, 0, 0,
            0, 0, 0, 1),
        _ => Matrix4x4.Identity,
    };

    /// <summary>Transform a point or direction from a view's space into world space.</summary>
    public static Vector3 ViewToWorld(int view, Vector3 value) =>
        Vector3.TransformNormal(value, Matrix4x4.Transpose(ViewRotation(view)));
}

using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Game;

/// <summary>
/// Keeping the 3D world up to date. The drawing routines (LL9, PLANET, SUN,
/// STARS and so on) still do all the calculations of the original that the
/// rest of the game depends on, but rather than drawing on the screen, they
/// describe what they are drawing to the 3D world, as 3D objects in world
/// space, which the renderer then draws with its own camera.
///
/// The original draws everything in the space of the current view: PLUT
/// rotates each ship into the view's frame of reference before LL9 draws it.
/// The 3D world is in our ship's frame of reference instead, so everything is
/// rotated back out of the view's space here, and the camera looks out of the
/// view instead.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>
    /// The radius of the planet and sun. PLANET projects the radius on screen
    /// as 96 * 256 * 256 / z pixels, and the projection is 256 * x / z, so the
    /// radius in space is 96 * 256.
    /// </summary>
    private const float PlanetRadius = 96 * 256;

    /// <summary>
    /// The distance at which things that the original draws from the edges of
    /// the screen (such as laser beams) start, in front of the camera.
    /// </summary>
    private const float ScreenEdgeDistance = 8;

    /// <summary>The distance to which our laser beams reach.</summary>
    private const float LaserRange = 4096;

    /// <summary>The 3D world.</summary>
    private readonly World _world = new();

    /// <summary>The owner of our laser beams in the 3D world.</summary>
    private readonly object _laserOwner = new();

    /// <summary>The random seed for the sun's fringe, which changes each time the sun is drawn.</summary>
    private int _sunSeed;

    /// <summary>
    /// The view that the objects being drawn are in. In flight, PLUT rotates
    /// each ship into the current space view before LL9 draws it, and the
    /// stardust and our laser beams are always in the current view, but the
    /// title screen, the mission briefings, the hangar and the escape pod
    /// sequence draw their ships without PLUT, so those are in the front view.
    /// </summary>
    private int _drawView;

    /// <summary>The view that PLUT has just rotated INWK into, for the next call to LL9 (or null).</summary>
    private int? _plutView;

    /// <summary>
    /// Note which view the objects about to be drawn are in. The camera looks
    /// out of the same view.
    /// </summary>
    private void BeginWorldDrawing(int view)
    {
        _drawView = view;
        _world.CameraView = view;
    }

    /// <summary>Connect the 3D world to the screen, so each frame includes a snapshot of it.</summary>
    private void ConnectWorld() => _hud.WorldSnapshot = _world.Snapshot;

    /// <summary>Rotate a point or direction from the current view's space into world space.</summary>
    private Vector3 ViewToWorld(float x, float y, float z) => Camera.ViewToWorld(_drawView, new Vector3(x, y, z));

    /// <summary>A direction in world space, as a unit vector, from one of INWK's orientation vectors.</summary>
    private Vector3 ViewDirectionToWorld(IntVector3 vector) => Vector3.Normalize(ViewToWorld(vector.X, vector.Y, vector.Z));

    /// <summary>The position in world space of the ship in INWK.</summary>
    private Vector3 CurrentShipPosition() => ViewToWorld(_currentShip.X, _currentShip.Y, _currentShip.Z);

    /// <summary>
    /// The model-to-world transform of the ship in INWK: its orientation
    /// vectors as the rows of the rotation, and its position.
    /// </summary>
    private Matrix4x4 CurrentShipTransform()
    {
        var side = ViewDirectionToWorld(_currentShip.Side);
        var roof = ViewDirectionToWorld(_currentShip.Roof);
        var nose = ViewDirectionToWorld(_currentShip.Nose);
        var position = CurrentShipPosition();
        return new Matrix4x4(
            side.X, side.Y, side.Z, 0,
            roof.X, roof.Y, roof.Z, 0,
            nose.X, nose.Y, nose.Z, 0,
            position.X, position.Y, position.Z, 1);
    }

    /// <summary>
    /// The point in world space that is at the given distance in front of the
    /// camera and that appears at the given point on the original's screen.
    /// </summary>
    private Vector3 ScreenPointToWorld(float screenX, float screenY, float distance) =>
        ViewToWorld((screenX - CentreX) * distance / 256, (CentreY - screenY) * distance / 256, distance);

    /// <summary>Remove an object from the screen (that is, from the 3D world).</summary>
    private void RemoveFromScreen(object owner) => _world.Remove(owner);

    /// <summary>
    /// Add the planet or sun in INWK to the 3D world (called by PLANET once it
    /// has worked out the planet or sun's size on the original's screen).
    /// </summary>
    private void SetWorldPlanetOrSun(bool large)
    {
        var centre = CurrentShipPosition();
        if ((_shipType & 1) != 0)
        {
            // SUN: the fringe gets wider as the sun gets bigger on-screen
            int radius = _circleRadius;
            int fringeMask = (radius >= 96 ? 4 : 0) | (radius >= 40 ? 2 : 0) | (radius >= 16 ? 1 : 0);
            _sunSeed++;
            _world.SetSun(_sunOwner, new SunInstance(centre, PlanetRadius, fringeMask, _sunSeed));
            return;
        }

        // PL9 leaves out the meridian and equator if the planet is small on
        // screen, and the crater if it is facing away from us, and neither
        // are drawn if the planet is very large on-screen
        bool crater = _shipType == ShipType.PlanetWithCrater;
        bool showFeatures = !large && (crater ? _currentShip.Roof.Z >= 0 : _circleRadius >= 6);
        _world.SetPlanet(_currentShip.DisplayOwner, new PlanetInstance(
            centre,
            PlanetRadius,
            ViewDirectionToWorld(_currentShip.Nose),
            ViewDirectionToWorld(_currentShip.Roof),
            ViewDirectionToWorld(_currentShip.Side),
            crater,
            showFeatures,
            _colour));
    }
}

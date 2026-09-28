using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;
using EliteSharp.Rendering.Scene;

namespace EliteSharp.Game;

/// <summary>
/// Keeping the 3D world up to date. The drawing routines (LL9, PLANET, SUN,
/// STARS and so on) still do everything the original does, including building
/// the 2D images that the classic renderer draws and that the hangar uses, but
/// they also describe what they are drawing to the 3D world, as 3D objects in
/// world space, which the 3D renderer then draws with its own camera.
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

    /// <summary>The owner of our laser beams' image.</summary>
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
    /// Whether the most recent objects drawn were in flight (as opposed to on
    /// the title screen, in a briefing or in the hangar), in which case the 3D
    /// view is framed by the space view's border.
    /// </summary>
    private bool _drawingInFlight;

    /// <summary>
    /// Note which view the objects about to be drawn are in, and whether we
    /// are in flight. The camera looks out of the same view.
    /// </summary>
    private void BeginWorldDrawing(int view, bool inFlight)
    {
        _drawView = view;
        _drawingInFlight = inFlight;
        _world.CameraView = view;
    }

    /// <summary>
    /// How the 3D world is drawn. This can be changed while the game is
    /// running (with Alt+V), to compare the 3D renderer with the classic one.
    /// </summary>
    public RendererKind Renderer
    {
        get => _renderer;
        set => _renderer = value;
    }

    private volatile RendererKind _renderer;

    /// <summary>
    /// Connect the 3D world to the screen, so each frame includes a snapshot
    /// of it (unless the classic renderer is being used, in which case the
    /// world's 2D images are drawn instead).
    /// </summary>
    private void ConnectWorld()
    {
        _screen.WorldSnapshot = () =>
        {
            if (_renderer != RendererKind.World3D)
            {
                return null;
            }

            return _world.Snapshot();
        };
    }

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

    /// <summary>Remove an object from the screen, both its 2D image and its 3D counterpart.</summary>
    private void RemoveFromScreen(object owner)
    {
        _screen.RemoveImage(owner);
        _world.Remove(owner);
    }

    /// <summary>
    /// The scale that turns the face normals of the ship in INWK into points on
    /// its faces, for the GPU's hidden line removal. LL9 part 5 scales the face
    /// normals down by XX17, which is the blueprint's normal scale plus the
    /// number of times the ship's position is halved to bring z_hi to zero, and
    /// the position by just the second of these, so relative to the position,
    /// the normals are scaled by 2^-S. If XX17 is 4 or more, the original
    /// ignores the normals and uses the position alone.
    /// </summary>
    private float NormalOffsetScale(ShipBlueprint blueprint)
    {
        int scaleShifts = blueprint.NormalScale;
        for (int z = (Math.Abs(_currentShip.Z) & 0xFFFF) >> 8; z != 0; z >>= 1)
        {
            scaleShifts++;
        }

        return scaleShifts >= 4 ? 0 : 1f / (1 << blueprint.NormalScale);
    }

    /// <summary>Whether to compare the GPU's face visibility test with LL9's (the "facecheck" test command).</summary>
    private bool _faceCheck;

    /// <summary>
    /// Compare LL9's face visibility for the ship in INWK with the test that
    /// the 3D renderer's vertex shader does (in WorldShaders.WireVertex), and
    /// trace any faces where they disagree. The GPU does the exact geometric
    /// test, whereas LL9 works with 8-bit values (the ship's position is
    /// rounded to as little as 1/256 of its distance, and the normals are
    /// shifted down to a few bits), so they disagree about faces that are seen
    /// almost edge-on, which is where the original's faces flicker.
    /// </summary>
    private void CheckFaceVisibility(ShipBlueprint blueprint, Matrix4x4 transform, float normalOffsetScale)
    {
        if (!_faceCheck || _trace == null || !Matrix4x4.Invert(transform, out var inverse))
        {
            return;
        }

        var camera = Vector3.Transform(Vector3.Zero, inverse);
        int mismatches = 0;
        var details = new List<string>();
        for (int f = 0; f < blueprint.Faces.Count; f++)
        {
            var face = blueprint.Faces[f];
            var normal = new Vector3(face.NormalX, face.NormalY, face.NormalZ);
            bool gpu = face.Visibility < _shipDistance || Vector3.Dot(normal, camera - normal * normalOffsetScale) > 0;
            bool cpu = FaceVisibility[f] != 0;
            if (gpu != cpu)
            {
                mismatches++;
                float margin = Vector3.Dot(normal, camera - normal * normalOffsetScale) / normal.Length();
                details.Add($"face {f} cpu={cpu} gpu={gpu} margin={margin:F1}");
            }
        }

        Trace($"FACECHECK {blueprint.Name} faces={blueprint.Faces.Count} mismatches={mismatches} distance={camera.Length():F0} {string.Join("; ", details)}");
    }

    /// <summary>
    /// Add the planet or sun in INWK to the 3D world (called by PLANET once it
    /// has drawn the planet or sun in 2D).
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

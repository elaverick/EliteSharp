using System.Numerics;
using EliteSharp.Game.Ships;
using EliteSharp.Rendering;

namespace EliteSharp.Game;

/// <summary>
/// Moving ships and planets through space, and the view transformations.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>How far a ship moves along its nose in each iteration of the main loop, for each unit of its speed.</summary>
    private const float DistancePerSpeed = 1.5f;

    /// <summary>
    /// How far a ship turns in each iteration of the main loop when it pitches
    /// or rolls (MVS5), in radians.
    /// </summary>
    private const float ShipTurnAngle = 1 / 16f;

    /// <summary>
    /// MVEIT: move the ship in INWK in space, applying its own speed and
    /// rotation, and our pitch, roll and speed.
    /// </summary>
    private void MoveShip()
    {
        // Part 1: tidy the orientation vectors every 16 iterations
        bool skipTactics = (_currentShip.Flags & 0b10100000) != 0;
        if (!skipTactics)
        {
            if (((_mainLoopCounter ^ _currentSlot) & 15) == 0)
            {
                OrthonormaliseOrientation();
            }

            // Part 2 (MV3): call the tactics routine
            if (_shipType >= 128)
            {
                MovePlanetOrSun();
                return;
            }

            if ((_currentShip.Ai & 0x80) != 0)
            {
                if (_shipType == ShipType.Missile || ((_mainLoopCounter ^ _currentSlot) & 7) == 0)
                {
                    // MV26
                    ApplyTactics();
                }
            }
        }
        else if (_shipType >= 128)
        {
            MovePlanetOrSun();
            return;
        }

        // MV30: remove the ship from the scanner, so we can move it
        DrawOnScanner();

        // Part 3: move the ship along its nose vector by its speed
        _currentShip.Position += _currentShip.Nose * (_currentShip.Speed * DistancePerSpeed);

        // Part 4: apply acceleration
        int speed = (_currentShip.Speed + _currentShip.Acceleration) & 0xFF;
        if ((speed & 0x80) != 0)
        {
            speed = 0;
        }

        int maxSpeed = _blueprint?.MaxSpeed ?? 0;
        if (speed >= maxSpeed)
        {
            speed = maxSpeed;
        }

        _currentShip.Speed = speed;
        _currentShip.Acceleration = 0;

        // Part 5: rotate the ship's location in space by our pitch and roll
        _currentShip.Position = Vector3.Transform(_currentShip.Position, OurRotation());

        // Part 6 onwards
        ApplyOurMovement();
    }

    /// <summary>
    /// The rotation of the world around us from our roll and pitch in this
    /// iteration of the main loop (MVEIT part 5 and MVS4): rolling by alpha
    /// turns the world about the z-axis by -alpha, and pitching by beta then
    /// turns it about the x-axis by beta.
    /// </summary>
    private Matrix4x4 OurRotation() =>
        Matrix4x4.CreateRotationZ(-_roll / 256f) * Matrix4x4.CreateRotationX(_pitch / 256f);

    /// <summary>
    /// MVEIT parts 6 to 9 (MV45): move the ship towards us by our speed, rotate
    /// its orientation by our pitch and roll and its own pitch and roll, and
    /// redraw it on the scanner.
    /// </summary>
    private void ApplyOurMovement()
    {
        // Part 6: move the ship in the z-axis by our speed
        _currentShip.Position.Z -= _speed;

        // The sun doesn't need rotating
        if ((_shipType & 0b10000001) == 129)
        {
            return;
        }

        // Part 7: rotate the orientation vectors by our pitch and roll
        var rotation = OurRotation();
        _currentShip.Nose = Vector3.TransformNormal(_currentShip.Nose, rotation);
        _currentShip.Roof = Vector3.TransformNormal(_currentShip.Roof, rotation);
        _currentShip.Side = Vector3.TransformNormal(_currentShip.Side, rotation);

        // Part 8: apply the ship's own pitch and roll
        int pitch = _currentShip.PitchCounter;
        int magnitude = pitch & 0x7F;
        if (magnitude != 0)
        {
            // Dampen the pitch counter unless it is 127
            if (magnitude != 0x7F)
            {
                magnitude--;
            }

            _currentShip.PitchCounter = magnitude | (pitch & 0x80);
            RotatePair(ref _currentShip.Roof, ref _currentShip.Nose, (pitch & 0x80) != 0 ? -ShipTurnAngle : ShipTurnAngle);
        }

        // MV8
        int roll = _currentShip.RollCounter;
        magnitude = roll & 0x7F;
        if (magnitude != 0)
        {
            if (magnitude != 0x7F)
            {
                magnitude--;
            }

            _currentShip.RollCounter = magnitude | (roll & 0x80);
            RotatePair(ref _currentShip.Roof, ref _currentShip.Side, (roll & 0x80) != 0 ? -ShipTurnAngle : ShipTurnAngle);
        }

        // Part 9 (MV5): redraw on the scanner, unless the ship is exploding or killed
        if ((_currentShip.Flags & 0b10100000) != 0)
        {
            // MVD1
            _currentShip.Flags &= ~Ship.FlagScanner;
            return;
        }

        _currentShip.Flags |= Ship.FlagScanner;
        DrawOnScanner();
    }

    /// <summary>
    /// MVS5: rotate a pair of orientation vectors by the given angle (in
    /// radians) in the plane that they share, turning a towards b.
    /// </summary>
    private static void RotatePair(ref Vector3 a, ref Vector3 b, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        (a, b) = (a * cos + b * sin, b * cos - a * sin);
    }

    /// <summary>
    /// MV40: rotate the planet or sun's location in space by our pitch and
    /// roll, then join MVEIT at MV45.
    /// </summary>
    private void MovePlanetOrSun()
    {
        _currentShip.Position = Vector3.Transform(_currentShip.Position, OurRotation());
        ApplyOurMovement();
    }

    /// <summary>
    /// TIDY: orthonormalise the orientation vectors of the ship in INWK, so
    /// that rounding errors don't accumulate as it rotates: normalise nosev,
    /// make roofv perpendicular to it, and set sidev to roofv x nosev (as the
    /// original does, which is the opposite way round to the orientations
    /// that ZINF and the launch routines set up).
    /// </summary>
    private void OrthonormaliseOrientation()
    {
        var nose = Vector3.Normalize(_currentShip.Nose);
        var roof = Vector3.Normalize(_currentShip.Roof - nose * Vector3.Dot(_currentShip.Roof, nose));
        _currentShip.Nose = nose;
        _currentShip.Roof = roof;
        _currentShip.Side = Vector3.Cross(roof, nose);
    }

    /// <summary>
    /// PLUT: transform the ship in INWK so that it is seen from the current
    /// view (front, rear, left or right).
    /// </summary>
    private void TransformForView()
    {
        _plutView = _view;
        _currentShip.Position = ToView(_currentShip.Position, _view);
        _currentShip.Nose = ToView(_currentShip.Nose, _view);
        _currentShip.Roof = ToView(_currentShip.Roof, _view);
        _currentShip.Side = ToView(_currentShip.Side, _view);
    }

    /// <summary>
    /// Rotate a vector from the front view into another view: the rear view
    /// flips the x- and z-axes, the left view looks along -x (so x = z and
    /// z = -x), and the right view looks along x (so x = -z and z = x).
    /// </summary>
    private static Vector3 ToView(Vector3 v, int view) => view switch
    {
        1 => new Vector3(-v.X, v.Y, -v.Z),
        2 => new Vector3(v.Z, v.Y, -v.X),
        3 => new Vector3(-v.Z, v.Y, v.X),
        _ => v,
    };

    /// <summary>LOOK1: switch to a new space view.</summary>
    private void SwitchView(int view)
    {
        SetSpacePalette(SpacePalette.Space);
        if (_viewType != 0)
        {
            // LQ
            _view = view;
            ClearScreen(0);
            DrawCrosshairs();
            if ((_energyBomb & 0x80) != 0)
            {
                DrawBombBolt();
            }

            InitialiseStardust();
            return;
        }

        if (view == _view)
        {
            return;
        }

        _view = view;
        ClearScreen(0);
        FlipStardust();
        if ((_energyBomb & 0x80) != 0)
        {
            DrawBombBolt();
        }

        WipeScanner();
        DrawCrosshairs();
    }

    /// <summary>
    /// SIGHT: draw the laser crosshairs. The original draws two crosses of
    /// sizes 20 and 10 using EOR, so the inner parts cancel out, leaving four
    /// separate arms, which is what is drawn here.
    /// </summary>
    private void DrawCrosshairs()
    {
        int laser = _lasers[_view];
        if (laser == 0)
        {
            return;
        }

        int index = laser switch
        {
            PulseLaserPower => 0,
            PulseLaserPower + 128 => 1,
            MilitaryLaserPower => 2,
            _ => 3,
        };

        var colour = SightColours[index];
        int centreY = CentreY;
        _hud.DrawLine(108, centreY, 117, centreY, colour);
        _hud.DrawLine(138, centreY, 147, centreY, colour);
        _hud.DrawLine(128, 76, 128, 85, colour);
        _hud.DrawLine(128, 107, 128, 116, colour);
    }
}

namespace EliteSharp.Game;

/// <summary>
/// The game's sound calls, which pass sound effects to the sound engine.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>NOISE: make a sound effect, unless sound is disabled.</summary>
    private void MakeSound(int y)
    {
        if (_soundDisabled != 0)
        {
            return;
        }

        _sound?.Noise(y);
    }

    /// <summary>BEEP: make a short, high beep.</summary>
    private void Beep() => MakeSound(SoundBeep);

    /// <summary>BOOP: make a long, low beep.</summary>
    private void Boop() => MakeSound(SoundBoop);

    /// <summary>LASNO: make the sound of our laser firing.</summary>
    private void LaserSound()
    {
        MakeSound(SoundLaser);
        MakeSound(SoundLaser2);
    }

    /// <summary>ELASNO: make the sound of us being hit by lasers.</summary>
    private void HitByLaserSound()
    {
        MakeSound(9);
        MakeSound(SoundLaser2);
    }
}

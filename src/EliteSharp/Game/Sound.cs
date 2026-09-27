namespace EliteSharp.Game;

/// <summary>
/// The game's sound calls, which pass sound effects to the sound engine.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>NOISE: make a sound effect, unless sound is disabled.</summary>
    private void NOISE(int y)
    {
        if (DNOIZ != 0)
        {
            return;
        }

        _sound?.Noise(y);
    }

    /// <summary>BEEP: make a short, high beep.</summary>
    private void BEEP() => NOISE(sobeep);

    /// <summary>BOOP: make a long, low beep.</summary>
    private void BOOP() => NOISE(soboop);

    /// <summary>LASNO: make the sound of our laser firing.</summary>
    private void LASNO()
    {
        NOISE(solaser);
        NOISE(solas2);
    }

    /// <summary>ELASNO: make the sound of us being hit by lasers.</summary>
    private void ELASNO()
    {
        NOISE(9);
        NOISE(solas2);
    }
}

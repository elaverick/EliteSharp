using EliteSharp.Game;

namespace EliteSharp.Tests.Game;

/// <summary>
/// A clock for the game that doesn't really wait: each time the game sleeps,
/// the clock jumps straight to the time it is waiting for, so the game runs as
/// fast as it can, and the same way every time. Anything that happens while
/// the game sleeps (a renderer drawing frames, or keys being pressed) is done
/// by <see cref="Sleeping"/>, on the game's thread, at the same points in the
/// game every time.
/// </summary>
internal sealed class VirtualClock : IGameClock
{
    public long Now { get; private set; }

    /// <summary>Called when the game sleeps, with the time it starts sleeping and the time it wakes.</summary>
    public Action<long, long>? Sleeping { get; set; }

    public void SleepUntil(long ticks)
    {
        if (ticks <= Now)
        {
            return;
        }

        Sleeping?.Invoke(Now, ticks);
        Now = ticks;
    }
}

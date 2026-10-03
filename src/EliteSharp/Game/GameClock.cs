using System.Diagnostics;

namespace EliteSharp.Game;

/// <summary>
/// The clock that paces the game. Times are in <see cref="Stopwatch"/> ticks
/// (<see cref="Stopwatch.Frequency"/> of them per second), and the game only
/// ever waits for this clock, never for the renderer, so how often frames are
/// drawn can't change how fast the game runs. Tests can drive the game with a
/// virtual clock that doesn't really wait.
/// </summary>
public interface IGameClock
{
    /// <summary>The current time, in ticks.</summary>
    long Now { get; }

    /// <summary>Wait until the given time (returning straight away if it has passed).</summary>
    void SleepUntil(long ticks);
}

/// <summary>
/// The real clock, which is <see cref="Stopwatch.GetTimestamp"/>, so the
/// renderer can compare the times on the game's frames with its own.
/// </summary>
public sealed class RealTimeClock : IGameClock
{
    public static RealTimeClock Instance { get; } = new();

    private RealTimeClock()
    {
    }

    public long Now => Stopwatch.GetTimestamp();

    public void SleepUntil(long ticks)
    {
        while (true)
        {
            long remaining = ticks - Now;
            if (remaining <= 0)
            {
                return;
            }

            double ms = remaining * 1000.0 / Stopwatch.Frequency;
            if (ms > 2)
            {
                Thread.Sleep((int)(ms - 1));
            }
            else
            {
                Thread.Yield();
            }
        }
    }
}

/// <summary>
/// Ticks at a fixed rate, such as the BBC's vertical sync or the main loop's
/// simulation rate. Each tick is one period after the last, however late the
/// last one was, so the game keeps to its rate on average; but if the game
/// falls more than a period behind (say the machine stalls), the timer starts
/// again from now rather than rushing through the ticks it missed.
/// </summary>
public sealed class FixedRateTimer(IGameClock clock)
{
    /// <summary>The time of the latest tick that has been scheduled.</summary>
    public long Next { get; private set; }

    /// <summary>Schedule the next tick, a period after the last, and return its time.</summary>
    public long Schedule(long period)
    {
        long now = clock.Now;
        if (Next < now - period)
        {
            Next = now;
        }

        Next += period;
        return Next;
    }
}

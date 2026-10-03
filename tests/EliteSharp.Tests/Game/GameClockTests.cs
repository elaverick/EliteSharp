using EliteSharp.Game;

namespace EliteSharp.Tests.Game;

/// <summary>The fixed-rate timer that paces the main loop and the vertical sync.</summary>
public sealed class GameClockTests
{
    private const long Period = 1000;

    [Fact]
    public void TicksArePeriodApart()
    {
        var clock = new VirtualClock();
        var timer = new FixedRateTimer(clock);
        for (int i = 1; i <= 10; i++)
        {
            long next = timer.Schedule(Period);
            Assert.Equal(i * Period, next);
            clock.SleepUntil(next);
        }
    }

    [Fact]
    public void ALateTickDoesNotDelayTheOnesAfterIt()
    {
        // Waking up late (say because the renderer kept the machine busy)
        // doesn't push the following ticks back, so the game keeps to its rate
        var clock = new VirtualClock();
        var timer = new FixedRateTimer(clock);
        clock.SleepUntil(timer.Schedule(Period));
        clock.SleepUntil(timer.Schedule(Period) + Period / 2);
        Assert.Equal(3 * Period, timer.Schedule(Period));
    }

    [Fact]
    public void FallingMoreThanAPeriodBehindStartsAgainFromNow()
    {
        var clock = new VirtualClock();
        var timer = new FixedRateTimer(clock);
        clock.SleepUntil(timer.Schedule(Period));
        clock.SleepUntil(10 * Period);
        Assert.Equal(11 * Period, timer.Schedule(Period));
    }
}

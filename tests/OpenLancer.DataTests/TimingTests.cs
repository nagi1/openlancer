using System;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>The measures of the timing gates (see <see cref="Timing"/>). These tests need no game data.</summary>
public sealed class TimingTests
{
    private static Func<TimeSpan> Tries(int[] milliseconds, Action counted)
    {
        var next = 0;
        return () =>
        {
            counted();
            return TimeSpan.FromMilliseconds(milliseconds[next++]);
        };
    }

    [Fact]
    public void AGateGivesTheBestOfItsTries()
    {
        var count = 0;

        var best = Timing.Gate(TimeSpan.FromSeconds(1), Tries([60, 20, 40], () => count++), tries: 3);

        Assert.Equal(TimeSpan.FromMilliseconds(20), best);
        Assert.Equal(3, count);
    }

    /// <summary>A try over the limit, for example during a burst of other work, makes the gate try again. The gate stops
    /// at the first try under the limit.</summary>
    [Fact]
    public void AGateTriesAgainWhileItsBestTryIsOverTheLimit()
    {
        var count = 0;

        var best = Timing.Gate(TimeSpan.FromMilliseconds(30), Tries([50, 40, 20, 10], () => count++));

        Assert.Equal(TimeSpan.FromMilliseconds(20), best);
        Assert.Equal(3, count);
    }

    /// <summary>Slow code stays slow. The gate stops when its patience ends and gives the best try over the
    /// limit.</summary>
    [Fact]
    public void AGateStopsWhenItsPatienceEnds()
    {
        var count = 0;

        var best = Timing.Gate(TimeSpan.FromMilliseconds(10), Tries([50, 40], () => count++), patience: TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(50), best);
        Assert.Equal(1, count);
    }

    /// <summary>The warm-up runs first and is not measured, so the slow first run does not count.</summary>
    [Fact]
    public void AWarmGateDoesNotMeasureTheWarmUp()
    {
        int[] sleeps = [200, 60, 20, 60];
        var runs = 0;

        var best = Timing.WarmGate(TimeSpan.FromSeconds(1), 3, () => Thread.Sleep(sleeps[runs++]));

        Assert.Equal(4, runs);
        Assert.InRange(best, TimeSpan.FromMilliseconds(19), TimeSpan.FromMilliseconds(59));
    }

    /// <summary>A measure runs with the priority Highest of its thread. After the measure, the thread and the process
    /// have their priorities again.</summary>
    [Fact]
    public void AMeasureRunsWithAHighPriorityAndGivesItBack()
    {
        using var process = Process.GetCurrentProcess();
        var processBefore = process.PriorityClass;
        var threadBefore = Thread.CurrentThread.Priority;
        var during = ThreadPriority.Normal;

        Timing.Measure(() => during = Thread.CurrentThread.Priority);

        process.Refresh();
        Assert.Equal(ThreadPriority.Highest, during);
        Assert.Equal(threadBefore, Thread.CurrentThread.Priority);
        Assert.Equal(processBefore, process.PriorityClass);
    }
}

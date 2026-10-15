using System;
using System.Collections.Generic;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The fake players and the fake time of one test of the cutscene clock. A step costs <see cref="StepCost"/> of the fake
/// time, so a budget of 12 ms gives 12 steps in a frame. The fake time moves only with the steps, the builds and
/// <see cref="Spend"/>.
/// </summary>
internal sealed class CutsceneFakes
{
    private TimeSpan now;

    public TimeSpan BuildCost { get; set; }

    public TimeSpan StepCost { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>The next builds throw this exception.</summary>
    public Exception? BuildFailure { get; set; }

    /// <summary>The players of the next builds throw at this step, counted from 1.</summary>
    public int? FailAtStep { get; set; }

    public int BuildCalls { get; private set; }

    public List<FakeCutscenePlayer> Built { get; } = [];

    /// <summary>The fake time.</summary>
    public TimeSpan Now => now;

    public CutsceneClock Clock() => new(Build, () => now);

    /// <summary>Moves the fake time, as the wall time passes between frames.</summary>
    public void Spend(TimeSpan time) => now += time;

    private FakeCutscenePlayer Build()
    {
        BuildCalls++;
        now += BuildCost;
        if (BuildFailure != null)
            throw BuildFailure;
        var player = new FakeCutscenePlayer(this, FailAtStep);
        Built.Add(player);
        return player;
    }
}

/// <summary>A player that records its steps. It throws at the step of <see cref="CutsceneFakes.FailAtStep"/>.</summary>
internal sealed class FakeCutscenePlayer(CutsceneFakes fakes, int? failAtStep) : ICutscenePlayer
{
    /// <summary>The steps that ended without a failure.</summary>
    public List<double> Steps { get; } = [];

    /// <summary>All calls of <see cref="Step"/>, also the calls that throw.</summary>
    public int Calls { get; private set; }

    public bool Disposed { get; private set; }

    public double Time { get; private set; }

    public void Step(double seconds)
    {
        Assert.False(Disposed, "The clock stepped a disposed player.");
        Calls++;
        fakes.Spend(fakes.StepCost);
        if (Steps.Count + 1 == failAtStep)
            throw new InvalidOperationException("The script failed.");
        Steps.Add(seconds);
        Time += seconds;
    }

    public void Dispose()
    {
        Assert.False(Disposed, "The clock disposed a player two times.");
        Disposed = true;
    }
}

using OpenLancer.Data.Missions.Run;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The shared trace of the dry run and the live view (section 4.5 of the M6c design).</summary>
public sealed class MissionTraceTests
{
    private static TraceChange C(TraceChangeKind kind, string trigger) => new(kind, trigger);

    private static TraceStep Step(int number, params TraceChange[] changes) => new(number, $"cause {number}", changes)
    {
        Pending = [],
    };

    [Fact]
    public void TheRunStatesComeFromTheChanges()
    {
        var trace = new MissionTrace();
        trace.Append(Step(1, C(TraceChangeKind.Ran, "t_start"), C(TraceChangeKind.Started, "t_wait"),
            C(TraceChangeKind.Started, "t_stop"), C(TraceChangeKind.Started, "t_repeat")));
        trace.Append(Step(2, C(TraceChangeKind.Stopped, "t_stop"), C(TraceChangeKind.Ran, "T_REPEAT"),
            C(TraceChangeKind.Rearmed, "t_repeat")));

        var wait = trace.StateOf("t_wait")!;
        var stop = trace.StateOf("t_stop")!;
        var repeat = trace.StateOf("t_repeat")!;

        Assert.True(wait.Waits);
        Assert.Equal(1, wait.ActiveSinceStep);
        Assert.False(stop.Waits);
        Assert.True(stop.Stopped);
        Assert.True(repeat.Waits);
        Assert.Equal(1, repeat.RanLast);
        Assert.False(repeat.HasRun);
        Assert.Equal(TraceChangeKind.Rearmed, repeat.LastChange!.Kind);
        Assert.Equal(2, repeat.LastStep);
        Assert.Null(trace.StateOf("t_other"));
    }

    [Fact]
    public void RanShowsForTheLastStepOnlyThenHasRun()
    {
        var trace = new MissionTrace();
        trace.Append(Step(1, C(TraceChangeKind.Ran, "t_start")));
        Assert.Equal(1, trace.RanNow(trace.StateOf("t_start")!));

        trace.Append(Step(2, C(TraceChangeKind.Started, "t_other")));

        var start = trace.StateOf("t_start")!;
        Assert.Equal(0, trace.RanNow(start));
        Assert.True(start.HasRun);
        Assert.Equal("t_other", trace.LastTrigger);
    }

    [Fact]
    public void AFoldedRowCountsItsRuns()
    {
        var trace = new MissionTrace();
        trace.Append(Step(1, C(TraceChangeKind.Ran, "t_tick") with { Count = 300 }));

        Assert.Equal(300, trace.StateOf("t_tick")!.RanLast);
        Assert.Equal("Ran t_tick, 300 times", trace.Steps[0].Changes[0].LogText);
    }

    [Fact]
    public void TheStartsNextOrderComesFromThePendingStarts()
    {
        var trace = new MissionTrace();
        trace.Append(Step(1, C(TraceChangeKind.Ran, "t_start")) with { Pending = ["t_b", "t_a"] });

        Assert.Equal(1, trace.StateOf("t_b")!.StartsNext);
        Assert.Equal(2, trace.StateOf("t_a")!.StartsNext);

        trace.Append(Step(2, C(TraceChangeKind.Started, "t_b"), C(TraceChangeKind.Started, "t_a")));
        Assert.Equal(0, trace.StateOf("t_a")!.StartsNext);
        Assert.True(trace.StateOf("t_a")!.Waits);
    }

    [Fact]
    public void TruncateGivesTheStatesOfTheFirstSteps()
    {
        var trace = new MissionTrace();
        trace.Append(Step(1, C(TraceChangeKind.Started, "t_wait")));
        trace.Append(Step(2, C(TraceChangeKind.Ran, "t_wait")));

        trace.Truncate(1);

        Assert.Single(trace.Steps);
        Assert.True(trace.StateOf("t_wait")!.Waits);
        Assert.Equal(0, trace.StateOf("t_wait")!.Runs);
    }

    [Fact]
    public void ATriggerThatWaitsForTenMinutesStalls()
    {
        var trace = new MissionTrace(live: true);
        trace.Append(new TraceStep(1, "", [C(TraceChangeKind.Started, "t_wait")]) { Time = 12 });
        var wait = trace.StateOf("t_wait")!;

        Assert.Null(trace.StallMinutes(wait, 12 + 599));
        Assert.Equal(10, trace.StallMinutes(wait, 12 + 600));
        Assert.Equal(11, trace.StallMinutes(wait, 12 + 719));
        Assert.Null(new MissionTrace().StallMinutes(wait, 10_000));
    }

    [Fact]
    public void RanShowsForOneSecondInTheLiveView()
    {
        var trace = new MissionTrace(live: true);
        trace.Append(new TraceStep(1, "", [C(TraceChangeKind.Ran, "t_go")]) { Time = 5 });
        var go = trace.StateOf("t_go")!;

        Assert.Equal(1, trace.RanNow(go, 5.5));
        Assert.Equal(0, trace.RanNow(go, 6.1));
        Assert.Equal("0:05.0", trace.Steps[0].Title);
        Assert.Equal("1:05.3", TraceStep.Clock(65.39));
    }

    [Fact]
    public void AResetClearsTheLiveTrace()
    {
        var trace = new MissionTrace(live: true);
        trace.Append(new TraceStep(1, "", [C(TraceChangeKind.Started, "t_wait")]) { Time = 1 });

        trace.Reset();

        Assert.Empty(trace.Steps);
        Assert.Empty(trace.States);
    }

    [Fact]
    public void ADroppedCountGivesItsRow()
    {
        var trace = new MissionTrace(live: true);
        trace.Append(new TraceStep(1, "", [TraceChange.Dropped(42)]) { Time = 1 });

        Assert.Equal("The game dropped 42 changes.", trace.Steps[0].Changes[0].LogText);
        Assert.Empty(trace.States);
    }
}

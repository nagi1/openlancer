using System.Linq;
using LibreLancer.Missions;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The queue of the mission observer of a play (section 6.2 of the M6c design): the lines of the mission
/// events, the reset after a load, the bound of the queue and the dropped count.</summary>
public sealed class MissionObserverQueueTests
{
    [Fact]
    public void TheChangesComeInALineWithTheResetOfTheirLoad()
    {
        var observer = new MissionObserver();
        observer.MissionLoaded("Mission_02");
        observer.TriggerChanged("t_start", MissionTriggerChange.Ran);
        observer.TriggerChanged("t_next", MissionTriggerChange.Started);

        var line = Assert.Single(observer.Take());
        observer.TriggerChanged("t_next", MissionTriggerChange.Stopped);
        var next = Assert.Single(observer.Take());

        Assert.Equal(("Mission_02", true, 0), (line.Mission, line.Reset, line.Dropped));
        Assert.Equal([("t_start", MissionChangeKind.Ran), ("t_next", MissionChangeKind.Started)],
            line.Changes.Select(c => (c.Trigger, c.Change)));
        Assert.Equal(("Mission_02", false), (next.Mission, next.Reset));
        Assert.Equal(MissionChangeKind.Stopped, Assert.Single(next.Changes).Change);
        Assert.True(next.Changes[0].Ms >= line.Changes[0].Ms);
        Assert.Empty(observer.Take());
    }

    [Fact]
    public void ALoadWithoutAChangeGivesAResetLineAndANewLoadStartsANewLine()
    {
        var observer = new MissionObserver();
        observer.MissionLoaded("Mission_02");
        observer.TriggerChanged("t_old", MissionTriggerChange.Started);
        observer.MissionLoaded("Mission_02");
        observer.MissionLoaded("Mission_03");

        var lines = observer.Take();

        Assert.Equal([("Mission_02", true, 1), ("Mission_02", true, 0), ("Mission_03", true, 0)],
            lines.Select(l => (l.Mission, l.Reset, l.Changes.Count)));
    }

    [Fact]
    public void ALineHasAtMost500Changes()
    {
        var observer = new MissionObserver();
        observer.MissionLoaded("Mission_02");
        for (var i = 0; i < 1200; i++)
            observer.TriggerChanged($"t_{i}", MissionTriggerChange.Started);

        var lines = observer.Take();

        Assert.Equal([500, 500, 200], lines.Select(l => l.Changes.Count));
        Assert.Equal([true, false, false], lines.Select(l => l.Reset));
        Assert.Equal("t_1199", lines[^1].Changes[^1].Trigger);
    }

    [Fact]
    public void AFullQueueCountsTheDroppedChangesAndTheNextLineGivesTheCount()
    {
        var observer = new MissionObserver();
        observer.MissionLoaded("Mission_02");
        for (var i = 0; i < MissionObserver.Capacity + 7; i++)
            observer.TriggerChanged("t_tick", MissionTriggerChange.Ran);

        var lines = observer.Take();
        observer.TriggerChanged("t_after", MissionTriggerChange.Ran);
        var after = Assert.Single(observer.Take());

        Assert.Equal(MissionObserver.Capacity, lines.Sum(l => l.Changes.Count));
        Assert.Equal(7, lines.Sum(l => l.Dropped));
        // The queue takes changes again after a take, and the count starts again.
        Assert.Equal((0, "t_after"), (after.Dropped, after.Changes.Single().Trigger));
    }

    [Fact]
    public void AChangeBeforeTheFirstLoadHasNoLine()
    {
        var observer = new MissionObserver();
        observer.TriggerChanged("t_lost", MissionTriggerChange.Ran);
        observer.MissionLoaded("Mission_02");

        var line = Assert.Single(observer.Take());

        Assert.Empty(line.Changes);
        Assert.True(line.Reset);
    }
}

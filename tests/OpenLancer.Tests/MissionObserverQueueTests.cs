using System.Linq;
using LibreLancer.Missions;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The queue of the mission observer of a play (section 6.2 of the M6c design): the lines of the mission
/// events, the reset after a load, the bound of the queue and the dropped count.</summary>
public sealed class MissionObserverQueueTests
{
    /// <summary>The writer of the mission lines writes each line of the observer as an event line. A closed output ends
    /// the writes and does not throw on the timer thread. After an error event, no mission line follows (review fix of
    /// M6c-2).</summary>
    [Fact]
    public void TheMissionLinesEndAtAClosedOutputAndAfterAnErrorEvent()
    {
        var observer = new MissionObserver();
        var output = new System.IO.StringWriter();
        var lines = new MissionLines(observer, output);
        observer.MissionLoaded("Mission_02");
        observer.TriggerChanged("t_start", MissionTriggerChange.Ran);

        lines.Write();
        var written = output.ToString().Trim();
        Assert.IsType<PlayEvent.MissionChanges>(PlayProtocol.ReadEvent(written));
        lines.Close();
        observer.TriggerChanged("t_next", MissionTriggerChange.Started);
        lines.Write();
        Assert.Equal(written, output.ToString().Trim());

        var closed = new MissionLines(observer, new ClosedWriter());
        closed.Write();
        Assert.True(closed.Closed);
        closed.Write();
    }

    /// <summary>An output whose pipe the editor closed.</summary>
    private sealed class ClosedWriter : System.IO.TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) => throw new System.IO.IOException("The pipe is being closed.");
    }

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

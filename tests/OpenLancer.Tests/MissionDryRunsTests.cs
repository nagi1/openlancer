using System.Linq;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The dry runs of the Mission tab (sections 4.2 and 7.1 of the M6c design): the fold after each new version of
/// a script, also after an Undo and a Redo, and the renames of the inputs. The tests use the mission editor of a session
/// and a clock of the test.</summary>
public sealed class MissionDryRunsTests
{
    private double now;

    private MissionDryRuns Runs() => new(() => now);

    [Fact]
    public void ARenameMovesTheInputsAndItsUndoAndRedoMoveThemAgain()
    {
        using var m = Create();
        var runs = Runs();
        m.Missions.ScriptsChanged += runs.ScriptsChanged;
        var analysis = m.Missions.Model(Script).Analysis;
        runs.Add(analysis, DryRunInput.StartMission());
        runs.Add(analysis, DryRunInput.Heartbeats(1));
        runs.Add(analysis, DryRunInput.RunTrigger("ol_end"));

        m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));
        var renamed = m.Missions.Model(Script).Analysis;
        Assert.True(runs.Sync(renamed, inTransaction: false));
        var run = runs.Of(Script)!;

        Assert.Equal("ol_finish", run.Inputs[^1].Trigger);
        Assert.Same(renamed.Index, run.Index);
        Assert.Contains(run.State.Trace.Steps[^1].Changes, c => c.LogText == "Ran ol_finish");
        Assert.DoesNotContain(run.State.Trace.Steps.SelectMany(s => s.Changes), c => c.LogText!.StartsWith("Missing"));

        m.Session.Undo();
        Assert.True(runs.Sync(m.Missions.Model(Script).Analysis, inTransaction: false));
        Assert.Equal("ol_end", run.Inputs[^1].Trigger);
        Assert.Contains(run.State.Trace.Steps[^1].Changes, c => c.LogText == "Ran ol_end");

        m.Session.Redo();
        Assert.True(runs.Sync(m.Missions.Model(Script).Analysis, inTransaction: false));
        Assert.Equal("ol_finish", run.Inputs[^1].Trigger);
    }

    [Fact]
    public void AnEditFoldsTheInputsOverTheNewModel()
    {
        using var m = Create();
        var runs = Runs();
        m.Missions.ScriptsChanged += runs.ScriptsChanged;
        var analysis = m.Missions.Model(Script).Analysis;
        runs.Add(analysis, DryRunInput.StartMission());
        var version = runs.Version;

        // ol_wave now starts with the mission too, so the first step starts it.
        m.Apply(new SetTriggerStart(T("ol_wave"), true));
        var edited = m.Missions.Model(Script).Analysis;

        Assert.False(runs.Sync(analysis, inTransaction: false));
        Assert.True(runs.Sync(edited, inTransaction: false));
        Assert.True(runs.Version > version);
        Assert.Contains("ol_wave", runs.Of(Script)!.State.Machine.Active.Select(t => t.Nickname));
        Assert.False(runs.Sync(edited, inTransaction: false));
    }

    [Fact]
    public void WhileADragIsOpenTheFoldRunsAtMostEach100Ms()
    {
        using var m = Create();
        var runs = Runs();
        var analysis = m.Missions.Model(Script).Analysis;
        runs.Add(analysis, DryRunInput.StartMission());
        m.Apply(new SetTriggerStart(T("ol_wave"), true));
        var first = m.Missions.Model(Script).Analysis;
        Assert.True(runs.Sync(first, inTransaction: true));

        m.Apply(new SetTriggerStart(T("ol_wave"), false));
        var second = m.Missions.Model(Script).Analysis;
        now += 0.05;
        Assert.False(runs.Sync(second, inTransaction: true));
        now += 0.06;

        Assert.True(runs.Sync(second, inTransaction: true));
        Assert.Same(second.Index, runs.Of(Script)!.Index);
    }

    [Fact]
    public void OnlyANewStepStartsTheFlashAndFollow()
    {
        using var m = Create();
        var runs = Runs();
        var analysis = m.Missions.Model(Script).Analysis;
        now = 12;

        runs.On = true;
        Assert.Equal((0, double.NegativeInfinity), (runs.Steps, runs.ChangedAt));
        runs.Add(analysis, DryRunInput.StartMission());
        Assert.Equal((1, 12.0), (runs.Steps, runs.ChangedAt));

        // A fold after an edit makes the views again, but it adds no step: no new flash, no frame of Follow.
        now = 12.5;
        m.Apply(new SetTriggerStart(T("ol_wave"), true));
        var version = runs.Version;
        Assert.True(runs.Sync(m.Missions.Model(Script).Analysis, inTransaction: false));
        Assert.Equal((version + 1, 1, 12.0), (runs.Version, runs.Steps, runs.ChangedAt));
    }

    [Fact]
    public void BackAndResetEndTheFlashAndAddNoStep()
    {
        using var m = Create();
        var runs = Runs();
        var analysis = m.Missions.Model(Script).Analysis;
        now = 12;

        runs.On = true;
        runs.Add(analysis, DryRunInput.StartMission());
        var version = runs.Version;
        runs.Back(analysis);
        runs.SetTimers(analysis, TimerBound.Longest);
        runs.Reset(analysis);

        Assert.Equal(version + 3, runs.Version);
        Assert.Equal((1, double.NegativeInfinity), (runs.Steps, runs.ChangedAt));
        Assert.Empty(runs.Of(Script)!.Inputs);
        Assert.Equal(TimerBound.Longest, runs.Of(Script)!.Timers);
        runs.Clear();
        Assert.False(runs.On);
        Assert.Null(runs.Of(Script));
    }
}

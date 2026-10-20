using System.Linq;
using System.Text;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>The dry run: the inputs, the fold, the snapshots, Back and the renames (section 4.2 of the M6c design), on
/// synthetic scripts.</summary>
public sealed class DryRunTests
{
    /// <summary>A start trigger that starts a timer, a comm trigger and a repeatable tick. The timer starts a kill
    /// count, and the kill count ends the mission. A checkpoint names a restore trigger.</summary>
    private const string Script = """
        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_timer
        Act_ActTrig = t_comm
        Act_ActTrig = t_tick

        [Trigger]
        nickname = t_timer
        Cnd_Timer = 2
        Act_ActTrig = t_kills
        Act_Save = t_restore, 1

        [Trigger]
        nickname = t_comm
        Cnd_CommComplete = ol_line_1
        Act_PlayMusic = ol_music

        [Trigger]
        nickname = t_tick
        repeatable = true
        Cnd_Timer = 0.1

        [Trigger]
        nickname = t_kills
        Cnd_Destroyed = ol_wing, 2
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_restore
        Cnd_SpaceEnter = no_params
        Act_ActTrig = t_kills

        [Trigger]
        nickname = t_space
        Cnd_InSpace = true
        """;

    private static readonly LineSpec Comm = LineSpec.Of("Cnd_CommComplete", ("line", "ol_line_1"));

    private static readonly LineSpec Kill = LineSpec.Of("Cnd_Destroyed", ("target", "ol_wing"));

    /// <summary>The trace as text: each step title, each log row, the active list and the heartbeats.</summary>
    private static string Text(DryRunState state)
    {
        var text = new StringBuilder();
        foreach (var step in state.Trace.Steps)
        {
            text.Append(step.Title).Append(" [").Append(step.Heartbeats).Append("]\n");
            foreach (var change in step.Changes)
                text.Append("  ").Append(change.Kind).Append(' ').Append(change.LogText).Append('\n');
            text.Append("  pending: ").Append(string.Join(",", step.Pending ?? [])).Append('\n');
        }
        text.Append("active: ").Append(string.Join(",", state.Machine.Active.Select(t => t.Nickname)));
        return text.ToString();
    }

    private static DryRun RunOf(string text, params DryRunInput[] inputs)
    {
        var run = new DryRun(Index(text));
        foreach (var input in inputs)
            run.Add(input);
        return run;
    }

    [Fact]
    public void TheSameInputsGiveTheSameTrace()
    {
        var index = Index(Script);
        DryRunInput[] inputs =
        [
            DryRunInput.StartMission(), DryRunInput.Heartbeats(1), DryRunInput.Event(Comm), DryRunInput.Heartbeats(60),
            DryRunInput.Event(Kill), DryRunInput.Event(Kill),
        ];

        var first = Text(DryRun.Fold(index, inputs));
        var second = Text(DryRun.Fold(index, inputs));

        Assert.Equal(first, second);
        Assert.Contains("The mission ends with success at trigger 't_kills'.", first);
    }

    [Fact]
    public void BackGivesTheTraceWithoutTheLastInput()
    {
        var run = RunOf(Script, DryRunInput.StartMission(), DryRunInput.Heartbeats(1), DryRunInput.Event(Comm));
        var before = Text(DryRun.Fold(run.Index, run.Inputs.Take(2).ToList()));

        Assert.True(run.Back());

        Assert.Equal(2, run.Inputs.Count);
        Assert.Equal(before, Text(run.State));
    }

    [Fact]
    public void BackAfter1000InputsGivesTheSameTraceAsAFullFold()
    {
        var run = RunOf(Script, DryRunInput.StartMission());
        for (var i = 1; i < 1000; i++)
            run.Add(i % 3 == 0 ? DryRunInput.Event(Comm) : DryRunInput.Heartbeats(i % 7 + 1));

        Assert.True(run.Back());

        Assert.Equal(999, run.Inputs.Count);
        Assert.Equal(Text(DryRun.Fold(run.Index, run.Inputs)), Text(run.State));
    }

    [Fact]
    public void AStartOfANewRunRemovesTheInputsBeforeItAndKeepsTheWorld()
    {
        var run = RunOf(Script, DryRunInput.PlayerSystem("ol_system"), DryRunInput.StartMission(), DryRunInput.Heartbeats(5),
            DryRunInput.Fact(LineSpec.Of("Cnd_InSpace", ("inSpace", true)), true), DryRunInput.PlayerInSpace(false));

        run.Add(DryRunInput.LoadCheckpoint("t_restore"));

        Assert.Equal([DryRunInputKind.PlayerSystem, DryRunInputKind.PlayerInSpace, DryRunInputKind.Fact,
            DryRunInputKind.LoadCheckpoint], run.Inputs.Select(i => i.Kind));
        Assert.Equal(["t_restore"], run.State.Machine.Active.Select(t => t.Nickname));
        Assert.Equal("ol_system", run.State.World.PlayerSystem);

        run.Add(DryRunInput.StartMission());
        Assert.Equal(DryRunInputKind.StartMission, run.Inputs[^1].Kind);
        Assert.DoesNotContain(run.Inputs, i => i.Kind == DryRunInputKind.LoadCheckpoint);

        run.Add(DryRunInput.StartAt("t_kills"));
        Assert.Equal(DryRunInputKind.StartAt, run.Inputs[^1].Kind);
        Assert.DoesNotContain(run.Inputs, i => i.Kind == DryRunInputKind.StartMission);
    }

    [Fact]
    public void AFoldOverAnIndexWithoutTheTriggerGivesMissing()
    {
        var state = DryRun.Fold(Index(Story), [DryRunInput.LoadCheckpoint("t_gone"), DryRunInput.RunTrigger("t_none")]);

        Assert.Equal(["Missing: t_gone"], state.Trace.Steps[0].Changes.Select(c => c.LogText));
        Assert.Equal(["Missing: t_none"], state.Trace.Steps[1].Changes.Select(c => c.LogText));
    }

    [Fact]
    public void ARenameMovesTheInputsAndAnUndoMovesThemBack()
    {
        var renamed = Script.Replace("t_restore", "t_reload");
        var run = RunOf(Script, DryRunInput.LoadCheckpoint("t_restore"), DryRunInput.RunTrigger("T_RESTORE"));

        run.Rebuild(Index(renamed), [("t_restore", "t_reload")]);

        Assert.Equal(["t_reload", "t_reload"], run.Inputs.Select(i => i.Trigger));
        Assert.DoesNotContain(run.State.Trace.Steps.SelectMany(s => s.Changes), c => c.LogText!.StartsWith("Missing"));
        Assert.Equal(["Ran t_reload"], run.State.Trace.Steps[1].Changes.Where(c => c.Kind == TraceChangeKind.Ran)
            .Select(c => c.LogText));

        run.Rebuild(Index(Script), [("t_reload", "t_restore")]);

        Assert.Equal(["t_restore", "t_restore"], run.Inputs.Select(i => i.Trigger));
        Assert.Equal("Step 1: Load checkpoint t_restore", run.State.Trace.Steps[0].Title);
    }

    [Fact]
    public void RunTriggerOnATriggerThatDoesNotWaitGivesNotActive()
    {
        var run = RunOf(Script, DryRunInput.StartMission(), DryRunInput.RunTrigger("t_kills"));

        var step = run.State.Trace.Steps[^1];

        Assert.Equal("Step 2: Run trigger t_kills", step.Title);
        Assert.Equal(["Not active: t_kills"], step.Changes.Select(c => c.LogText));
    }

    [Fact]
    public void AnEventThatNoTriggerWaitsForGivesItsStep()
    {
        var run = RunOf(Script, DryRunInput.StartMission(), DryRunInput.Event(Kill));

        Assert.Equal(["No trigger waits for this event."], run.State.Trace.Steps[^1].Changes.Select(c => c.LogText));
    }

    [Fact]
    public void AnEventNamesTheSentenceOfItsLineAsTheCause()
    {
        var run = RunOf(Script, DryRunInput.StartMission(), DryRunInput.Heartbeats(1), DryRunInput.Event(Comm));

        var step = run.State.Trace.Steps[^1];

        Assert.Equal("Step 3: Comm line ol_line_1 ends", step.Title);
        Assert.Equal(["Ran t_comm", "Play music: ol_music"], step.Changes.Select(c => c.LogText));
    }

    [Fact]
    public void NextTimerStopsAtTheTimer()
    {
        var run = RunOf(Script.Replace("Cnd_Timer = 0.1", "Cnd_Timer = 900"), DryRunInput.StartMission(),
            DryRunInput.Heartbeats(1), DryRunInput.NextTimer());

        var step = run.State.Trace.Steps[^1];

        Assert.Equal("Step 3: Next timer", step.Title);
        Assert.Equal(61, step.Heartbeats);
        Assert.Contains(step.Changes, c => c.LogText == "Ran t_timer");
        Assert.Contains(step.Changes, c => c.LogText == "Checkpoint: t_restore");
        Assert.Contains(step.Changes, c => c.LogText == "Started t_kills");
        Assert.Empty(step.Pending!);
    }

    [Fact]
    public void ThreeHundredRunsOfARepeatableTriggerGiveOneLogRow()
    {
        var run = RunOf(Script, DryRunInput.StartMission(), DryRunInput.Heartbeats(1), DryRunInput.Heartbeats(900));

        var rows = run.State.Trace.Steps[^1].Changes.Where(c => c.Trigger == "t_tick").ToList();

        Assert.Equal("Ran t_tick, 300 times", Assert.Single(rows, c => c.Kind == TraceChangeKind.Ran).LogText);
        Assert.Equal(300, run.State.Machine.StateOf(run.Index.Trigger("t_tick")!).Runs);
    }

    [Fact]
    public void AFactStaysUntilTheModderClearsIt()
    {
        var fact = LineSpec.Of("Cnd_InSpace", ("inSpace", "true"));
        var script = Script.Replace("Act_ActTrig = t_tick", "Act_ActTrig = t_tick\nAct_ActTrig = t_space");
        var run = RunOf(script, DryRunInput.StartMission(), DryRunInput.Fact(fact, true));

        Assert.Equal("Step 2: Is true: the player is in space", run.State.Trace.Steps[^1].Title);
        run.Add(DryRunInput.Heartbeats(1));
        run.Add(DryRunInput.Heartbeats(1));

        Assert.Equal(TriggerStatus.Complete, run.State.Machine.StateOf(run.Index.Trigger("t_space")!).Status);
        Assert.True(run.State.World.Facts.Count == 1);
        run.Add(DryRunInput.Fact(fact, false));
        Assert.Empty(run.State.World.Facts);
    }

    [Fact]
    public void ALongerTimerBoundFoldsAgain()
    {
        var run = RunOf("[Trigger]\nnickname = t_range\nInitState = ACTIVE\nCnd_Timer = 1, 3\n", DryRunInput.StartMission(),
            DryRunInput.Heartbeats(60));
        Assert.Equal(TriggerStatus.Complete, run.State.Machine.StateOf(run.Index.Trigger("t_range")!).Status);

        run.SetTimers(TimerBound.Longest);

        Assert.Equal(TriggerStatus.Active, run.State.Machine.StateOf(run.Index.Trigger("t_range")!).Status);
        Assert.Equal(2, run.Inputs.Count);
    }

    [Fact]
    public void TheCheckpointsAreTheRestoreTriggersOfTheScript()
    {
        Assert.Equal(["t_restore"], DryRun.Checkpoints(Index(Script)).Select(t => t.Nickname));
    }

    [Fact]
    public void AnEndStopsTheRunAndItsRowsShowTheEnd()
    {
        var run = RunOf(Script, DryRunInput.LoadCheckpoint("t_restore"), DryRunInput.Event(LineSpec.Of("Cnd_SpaceEnter")),
            DryRunInput.Heartbeats(1), DryRunInput.Event(Kill), DryRunInput.Event(Kill), DryRunInput.Event(Comm));

        Assert.NotNull(run.State.Machine.End);
        Assert.Equal("The mission ends with success at trigger 't_kills'.", run.State.Trace.Steps[4].Changes[^1].LogText);
        Assert.Equal(["No trigger waits for this event."], run.State.Trace.Steps[5].Changes.Select(c => c.LogText));
    }
}

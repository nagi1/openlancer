using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
using OpenLancer.Runtime.Play;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The live trace of the mission of the OpenLancer game (sections 4.5, 6.3 and 7.2 of the M6c design, build step 2.4):
/// the steps of the mission events, the play numbers and the sequence numbers of the live trace query, the stalls, the
/// Run log of the live view and the Console notes. The model needs no window.
/// </summary>
public sealed class MissionLiveTests
{
    private const string Script = """
        [Mission]

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_wait

        [Trigger]
        nickname = t_wait
        Cnd_BaseEnter = ol_base
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_lose
        Cnd_Destroyed = ol_ship
        Act_ChangeState = FAIL, 1
        """;

    /// <summary>The name of the test script in the Console texts. A script without a story slot shows its file.</summary>
    private static readonly string Slot = MissionFiles.Display(Path);

    private double now;

    private MissionLive Live() => new(() => now);

    private static PlayEvent.MissionChanges Event(bool reset, params (long Ms, string Trigger, MissionChangeKind Change)[] changes) =>
        new("Mission_99", reset, 0, changes.Select(c => new MissionChange(c.Ms, c.Trigger, c.Change)).ToList());

    private static MissionAnalysis Analysis(string text = Script)
    {
        using var folder = new MissionFolder();
        folder.Write(Path, text);
        return MissionRun.Execute(folder.Inputs()).Of(Path)!;
    }

    /// <summary>One step for each time of the game, with the changes in order. The title of a step is its time.</summary>
    [Fact]
    public void TheChangesOfOneTimeMakeOneStep()
    {
        var live = Live();

        live.Take(Event(true, (120, "t_start", MissionChangeKind.Ran), (120, "t_wait", MissionChangeKind.Started),
            (65_300, "t_wait", MissionChangeKind.Ran)));

        Assert.Equal(["0:00.1", "1:05.3"], live.Trace.Steps.Select(s => s.Title));
        Assert.Equal(["Ran t_start", "Started t_wait"], live.Trace.Steps[0].Changes.Select(c => c.LogText));
        Assert.True(live.Trace.Live);
        Assert.Equal("Mission_99", live.Mission);
        Assert.Equal(1, live.Play);
    }

    /// <summary>A reset starts a new play: the trace of the play before goes, and the play number grows. The setup
    /// triggers of the test layer stay with their play.</summary>
    [Fact]
    public void AResetStartsANewPlay()
    {
        var live = Live();
        live.Take(Event(true, (100, "t_start", MissionChangeKind.Ran)), new HashSet<string> { "ol_play_setup" });
        live.Take(Event(false, (200, "t_wait", MissionChangeKind.Started)), new HashSet<string> { "ol_other" });

        Assert.Equal(["ol_play_setup"], live.Setup);
        live.Take(Event(true, (50, "t_lose", MissionChangeKind.Started)));

        Assert.Equal(2, live.Play);
        Assert.Equal(["t_lose"], live.Trace.States.Keys);
        Assert.Empty(live.Setup);
    }

    /// <summary>The live trace query gives the changes of the play after a sequence number. A request with another play
    /// number gets all changes of the current play. Clear removes the trace, but the numbers of the play go on.</summary>
    [Fact]
    public void TheQueryGivesTheChangesAfterASequenceNumber()
    {
        var live = Live();
        live.Take(Event(true, (100, "t_start", MissionChangeKind.Ran), (100, "t_wait", MissionChangeKind.Started)));
        live.Take(Event(false, (900, "t_wait", MissionChangeKind.Ran)));

        Assert.Equal([1L, 2, 3], live.Since(1, 0).Select(c => c.Sequence));
        Assert.Equal(["t_wait"], live.Since(1, 2).Select(c => c.Trigger));
        Assert.Equal(0.9, live.Since(1, 2)[0].Time, 6);
        Assert.Equal(3, live.Since(0, 2).Count);

        live.Clear();
        Assert.Null(live.Mission);
        Assert.Empty(live.Since(1, 0));
        live.Take(Event(false, (1000, "t_lose", MissionChangeKind.Started)));

        Assert.Equal("Mission_99", live.Mission);
        Assert.Equal(1, live.Play);
        Assert.Equal(4, Assert.Single(live.Since(1, 3)).Sequence);
    }

    /// <summary>The game counts the changes that it dropped. The Run log gives the count as a row.</summary>
    [Fact]
    public void ADroppedCountGivesItsRow()
    {
        var live = Live();

        live.Take(new PlayEvent.MissionChanges("Mission_99", true, 12, [new MissionChange(100, "t_start", MissionChangeKind.Ran)]));

        Assert.Equal(12, live.Dropped);
        Assert.Equal(["The game dropped 12 changes.", "Ran t_start"], live.Trace.Steps[0].Changes.Select(c => c.LogText));
    }

    /// <summary>A trigger that waits for 10 minutes stalls once in a play.</summary>
    [Fact]
    public void ATriggerStallsOnceInAPlay()
    {
        var live = Live();
        live.Take(Event(true, (0, "t_wait", MissionChangeKind.Started), (0, "t_start", MissionChangeKind.Ran)));

        now = 599;
        Assert.Empty(live.NewStalls());
        now = 600;
        Assert.Equal(["t_wait"], live.NewStalls());
        now = 700;
        Assert.Empty(live.NewStalls());
    }

    /// <summary>The Run log of the live view: a trigger of the test layer shows as "Play setup", and a trigger that the
    /// graph does not have shows as "Not in the graph".</summary>
    [Fact]
    public void TheLiveRunLogNamesTheSetupTriggersAndTheTriggersOutsideTheGraph()
    {
        var analysis = Analysis();
        var live = Live();
        live.Take(Event(true, (100, "ol_play_setup", MissionChangeKind.Ran), (100, "t_wait", MissionChangeKind.Started),
            (100, "t_old_name", MissionChangeKind.Started)), new HashSet<string> { "ol_play_setup" });

        var rows = RunPanel.LiveLogRows(analysis, live.Trace, live.Setup).Select(r => r.Text).ToList();

        // t_wait is a member of a "Mission success" prefab instance, so its name shows the prefab first (UI rule 5).
        Assert.Equal(["0:00.1", "Play setup: ol_play_setup", "Started Mission success: t_wait", "Not in the graph: t_old_name"],
            rows);
    }

    /// <summary>The end notes of the Console: a trigger that ends the mission with success or with fail.</summary>
    [Fact]
    public void ATriggerThatEndsTheMissionGivesTheEndNote()
    {
        var analysis = Analysis();

        var notes = LiveNotes.Ends(analysis, Event(false, (100, "t_wait", MissionChangeKind.Ran),
            (100, "t_lose", MissionChangeKind.Started), (200, "t_lose", MissionChangeKind.Ran))).ToList();

        Assert.Equal([$"{Slot} ends with success at trigger 't_wait'.", $"{Slot} ends with fail at trigger 't_lose'."],
            notes);
    }

    /// <summary>The stall note names the trigger and the slot. A condition without effect in OpenLancer gets its own
    /// text. The origin is the base game when no mod changes the script, else the last mod that changes it.</summary>
    [Fact]
    public void TheStallNoteAndItsOrigin()
    {
        var analysis = Analysis();
        var trigger = analysis.Index.Trigger("t_wait")!;

        Assert.Equal($"Trigger 't_wait' of {Slot} waits for 10 minutes. Look in the graph for the event that it waits for.",
            LiveNotes.Stall(analysis, trigger));
        var script = analysis.Script;
        Assert.Equal(MessageOrigin.BaseGame, LiveNotes.StallOrigin(script with { State = MissionScriptState.BaseGame }));
        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.File),
            LiveNotes.StallOrigin(script with { State = MissionScriptState.Changed, ModId = "act" }));

        var noEffect = Index(Script, coverage: new NoEffectCoverage()).Trigger("t_wait")!;
        Assert.Equal($"Trigger 't_wait' of {Slot} waits for 10 minutes. Its condition has no effect in OpenLancer, so it " +
                     "never runs here. The retail game runs it.", LiveNotes.Stall(analysis, noEffect));
        Assert.Empty(SteLint.Problems(LiveNotes.Stall(analysis, noEffect)));
    }

    /// <summary>The engine marks Cnd_BaseEnter as a condition without effect.</summary>
    private sealed class NoEffectCoverage : IEngineCoverage
    {
        public EngineMark Of(string name) => MissionIndex.Is(name, "Cnd_BaseEnter") ? EngineMark.NoEffect : EngineMark.Runs;

        public IReadOnlyList<RuleDifference> RuleDifferences(MissionIndex index, MissionTrigger trigger) => [];
    }
}

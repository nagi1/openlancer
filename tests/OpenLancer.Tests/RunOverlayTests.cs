using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>The run overlay of the mission graph (section 4.5 of the M6c design, build step 1.3): the chips of each run
/// state, the counts of the collapsed frames, the start links that flash and "Follow". The model needs no
/// window.</summary>
public sealed class RunOverlayTests
{
    /// <summary>The chip of the play start has its own kind, so it does not look like the chip "Waits" of a run state
    /// (review fix of M6c-2).</summary>
    [Fact]
    public void ThePlayStartChipIsNotARunState()
    {
        Assert.Equal(RunChipKind.PlayStart, GraphView.PlayStartChip.Kind);
        Assert.NotEqual(RunChipKind.Waits, GraphView.PlayStartChip.Kind);
    }

    private const string Script = """
        [Mission]

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_wait
        Act_ActTrig = t_comm

        [Trigger]
        nickname = t_wait
        Cnd_BaseEnter = ol_base
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_comm
        Cnd_CommComplete = ol_line
        Act_DeactTrig = t_wait
        """;

    private static MissionAnalysis Analysis(string text)
    {
        using var folder = new MissionFolder();
        folder.Write(Path, text);
        return MissionRun.Execute(folder.Inputs()).Of(Path)!;
    }

    private static TraceStep Step(int number, params TraceChange[] changes) => new(number, "", changes) { Pending = [] };

    private static IReadOnlyList<string> Texts(TriggerTrace state, MissionTrace trace, double now = 0) =>
        [.. RunOverlay.ChipsOf(state, trace, now).Select(c => c.Text)];

    [Fact]
    public void EachRunStateHasItsChip()
    {
        var trace = new MissionTrace();
        trace.Append(Step(1, new TraceChange(TraceChangeKind.Ran, "t_start"), new TraceChange(TraceChangeKind.Started, "t_wait"),
            new TraceChange(TraceChangeKind.Started, "t_stop"), new TraceChange(TraceChangeKind.Ran, "t_tick") { Count = 3 },
            new TraceChange(TraceChangeKind.Rearmed, "t_tick")) with { Pending = ["t_b", "t_a"] });

        Assert.Equal(["Ran"], Texts(trace.StateOf("t_start")!, trace));
        Assert.Equal(["Waits"], Texts(trace.StateOf("t_wait")!, trace));
        Assert.Equal(["Waits", "Ran 3 times"], Texts(trace.StateOf("t_tick")!, trace));
        Assert.Equal(["Starts next (2)"], Texts(trace.StateOf("t_a")!, trace));

        trace.Append(Step(2, new TraceChange(TraceChangeKind.Stopped, "t_stop")));

        Assert.Equal(["Has run"], Texts(trace.StateOf("t_start")!, trace));
        Assert.Equal(["Stopped"], Texts(trace.StateOf("t_stop")!, trace));
        Assert.All(RunOverlay.ChipsOf(trace.StateOf("t_stop")!, trace), c => Assert.NotEqual(' ', c.Glyph));
    }

    [Fact]
    public void ATriggerThatWaitsForTenMinutesShowsTheStallChip()
    {
        var trace = new MissionTrace(live: true);
        trace.Append(new TraceStep(1, "", [new TraceChange(TraceChangeKind.Started, "t_wait")]) { Time = 0 });

        Assert.Equal(["Waits"], Texts(trace.StateOf("t_wait")!, trace, 30));
        Assert.Equal(["Waits 10 min"], Texts(trace.StateOf("t_wait")!, trace, 600));
    }

    [Fact]
    public void TheOverlayOfADryRunMarksTheNodesTheLinksAndTheLastTrigger()
    {
        var analysis = Analysis(Script);
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var state = DryRun.Fold(analysis.Index, [DryRunInput.StartMission()]);

        var overlay = RunOverlay.Of(graph, state.Trace);
        RunNodeState Node(string name) => overlay.Of(graph.NodeOf(analysis.Index.Trigger(name)!)!)!;

        Assert.True(Node("t_start").Ran);
        Assert.False(Node("t_start").Waits);
        Assert.Equal(["Starts next (1)"], Node(OrderOf(analysis, "t_wait", "t_comm")[0]).Chips.Select(c => c.Text));
        var starts = graph.Links.Where(l => l.Kind == MissionLinkKind.Start && l.From.Trigger?.Nickname == "t_start").ToList();
        Assert.NotEmpty(starts);
        Assert.All(starts, l => Assert.Contains(l.Id, overlay.Links));
        Assert.Equal(graph.NodeOf(analysis.Index.Trigger("t_start")!)!.Key, overlay.Follow);

        var next = DryRun.Fold(analysis.Index, [DryRunInput.StartMission(), DryRunInput.Heartbeats(1)]);
        var after = RunOverlay.Of(graph, next.Trace);

        Assert.True(after.Of(graph.NodeOf(analysis.Index.Trigger("t_wait")!)!)!.Waits);
        Assert.Equal(["Has run"], after.Of(graph.NodeOf(analysis.Index.Trigger("t_start")!)!)!.Chips.Select(c => c.Text));
        Assert.Empty(after.Links);
    }

    [Fact]
    public void ACollapsedFrameCountsItsWaitingMembers()
    {
        var analysis = Analysis(Script);
        var open = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var act = open.Acts.First(a => a.Triggers.Any(t => t.Nickname == "t_wait"));
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic,
            Collapse: new Dictionary<string, bool> { [act.Key] = true }));
        var state = DryRun.Fold(analysis.Index, [DryRunInput.StartMission(), DryRunInput.Heartbeats(1)]);

        var overlay = RunOverlay.Of(graph, state.Trace);
        var frame = graph.Nodes.Single(n => n.Frame?.Key == act.Key);
        var waiting = act.Triggers.Count(t => t.Nickname is "t_wait" or "t_comm");

        Assert.Equal([$"{waiting} wait"], overlay.Of(frame)!.Chips.Select(c => c.Text));
        Assert.True(overlay.Of(frame)!.Waits);
    }

    [Fact]
    public void TheRunStateOfTheTriggerPage()
    {
        var analysis = Analysis(Script);
        var state = DryRun.Fold(analysis.Index, [DryRunInput.StartMission(), DryRunInput.Heartbeats(1)]);

        var section = MissionPageModel.RunSection(state.Trace, analysis.Index.Trigger("t_wait")!, analysis);
        var start = MissionPageModel.RunSection(state.Trace, analysis.Index.Trigger("t_start")!, analysis);

        Assert.Equal("Run", section.Title);
        Assert.Equal(["State", "Became active at", "Ran", "Last change", "Cause"], section.Fields.Select(f => f.Label));
        Assert.Equal(["Waits"], section.Fields[0].Chips.Select(c => c.Text));
        Assert.Equal("Step 2", section.Fields[1].Value);
        Assert.Equal("0 times", section.Fields[2].Value);
        // t_wait is a member of a "Mission success" prefab instance, so its name shows the prefab first (UI rule 5).
        Assert.Equal("Started Mission success: t_wait", section.Fields[3].Value);
        Assert.Equal("Step 2: Heartbeat", section.Fields[4].Value);
        Assert.Equal("1 time", start.Fields.Single(f => f.Label == "Ran").Value);
    }

    /// <summary>The live view of a mission play (section 6.3 of the M6c design) uses the same overlay: the outline of a
    /// trigger that waits, "Ran" for one second with the flash and the start links, then "Has run", and "Stopped".</summary>
    [Fact]
    public void TheLiveOverlayShowsTheStatesOfTheGame()
    {
        var analysis = Analysis(Script);
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var now = 0.0;
        var live = new MissionLive(() => now);
        live.Take(new OpenLancer.Runtime.Play.PlayEvent.MissionChanges("Mission_99", true, 0,
        [
            new(100, "t_start", OpenLancer.Runtime.Play.MissionChangeKind.Ran),
            new(100, "t_wait", OpenLancer.Runtime.Play.MissionChangeKind.Started),
            new(100, "t_comm", OpenLancer.Runtime.Play.MissionChangeKind.Started),
        ]));
        RunNodeState? Node(RunOverlay overlay, string name) => overlay.Of(graph.NodeOf(analysis.Index.Trigger(name)!)!);

        now = 0.5;
        var first = RunOverlay.Of(graph, live.Trace, live.Now);

        Assert.True(Node(first, "t_start")!.Ran);
        Assert.Equal(["Ran"], Node(first, "t_start")!.Chips.Select(c => c.Text));
        Assert.True(Node(first, "t_wait")!.Waits);
        Assert.Equal(["Waits"], Node(first, "t_wait")!.Chips.Select(c => c.Text));
        Assert.NotEmpty(first.Links);

        now = 2;
        live.Take(new OpenLancer.Runtime.Play.PlayEvent.MissionChanges("Mission_99", false, 0,
            [new(1900, "t_wait", OpenLancer.Runtime.Play.MissionChangeKind.Stopped)]));
        var later = RunOverlay.Of(graph, live.Trace, live.Now);

        Assert.False(Node(later, "t_start")!.Ran);
        Assert.Equal(["Has run"], Node(later, "t_start")!.Chips.Select(c => c.Text));
        Assert.Equal(["Stopped"], Node(later, "t_wait")!.Chips.Select(c => c.Text));
        Assert.Empty(later.Links);
    }

    /// <summary>During a mission play, each trigger with a rule that OpenLancer runs in another way gets the chip "Rules
    /// differ" with its reasons. The dry run shows the retail rules, so it has no such chip. The chip of a stall tells
    /// when the condition has no effect in OpenLancer.</summary>
    [Fact]
    public void TheLiveOverlayOfAPlayMarksTheRuleDifferences()
    {
        var analysis = Analysis(Script);
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var trace = new MissionTrace(live: true);
        trace.Append(new TraceStep(1, "", [new TraceChange(TraceChangeKind.Started, "t_wait")]) { Time = 0 });
        var engine = new DifferentRules("t_wait", "t_comm");

        var play = RunOverlay.Of(graph, trace, 1, engine);

        var wait = play.Of(graph.NodeOf(analysis.Index.Trigger("t_wait")!)!)!;
        Assert.Equal(["Waits", "Rules differ"], wait.Chips.Select(c => c.Text));
        Assert.Equal(RunOverlay.RulesDifferTip + " " + RuleDifferences.Reason(RuleDifference.Repeats),
            wait.Chips[1].Tooltip);
        Assert.Equal(["Rules differ"], play.Of(graph.NodeOf(analysis.Index.Trigger("t_comm")!)!)!.Chips.Select(c => c.Text));
        Assert.DoesNotContain(RunOverlay.Of(graph, trace, 1).Nodes.Values.SelectMany(n => n.Chips),
            c => c.Kind == RunChipKind.RulesDiffer);

        var stall = RunOverlay.ChipsOf(trace.StateOf("t_wait")!, trace, 660, noEffect: true).Single();
        Assert.Equal("Waits 11 min", stall.Text);
        Assert.Equal(RunOverlay.StallTip(11) + " " + RunOverlay.NoEffectStallTip, stall.Tooltip);
    }

    /// <summary>The section "Run" of the Trigger page follows the time of the live view: "Ran" for one second, then "Has
    /// run". The times of the game show as "{m}:{ss}.{t}". The Mission page makes the section again each second.</summary>
    [Fact]
    public void TheRunSectionOfTheLiveViewFollowsTheTime()
    {
        var analysis = Analysis(Script);
        var trace = new MissionTrace(live: true);
        trace.Append(new TraceStep(1, "", [new TraceChange(TraceChangeKind.Started, "t_wait")]) { Time = 60 });
        trace.Append(new TraceStep(2, "", [new TraceChange(TraceChangeKind.Ran, "t_wait")]) { Time = 65.3 });
        var trigger = analysis.Index.Trigger("t_wait")!;

        var now = MissionPageModel.RunSection(trace, trigger, analysis, 65.5);
        var later = MissionPageModel.RunSection(trace, trigger, analysis, 67);

        Assert.Equal("Ran", now.Fields[0].Value);
        Assert.Equal("Has run", later.Fields[0].Value);
        Assert.Equal("1:00.0", now.Fields.Single(f => f.Label == "Became active at").Value);
        Assert.Equal("1:05.3", now.Fields.Single(f => f.Label == "Cause").Value);
    }

    /// <summary>The rules of a fake engine: each named trigger repeats in another way.</summary>
    private sealed class DifferentRules(params string[] names) : IEngineCoverage
    {
        public EngineMark Of(string name) => EngineMark.Runs;

        public IReadOnlyList<RuleDifference> RuleDifferences(MissionIndex index, MissionTrigger trigger) =>
            names.Contains(trigger.Nickname) ? [RuleDifference.Repeats] : [];
    }

    /// <summary>The two triggers in their order of the next drain: smallest game id first.</summary>
    private static List<string> OrderOf(MissionAnalysis analysis, params string[] names) =>
        names.OrderBy(n => LibreLancer.Data.FLHash.CreateID(n)).ToList();
}

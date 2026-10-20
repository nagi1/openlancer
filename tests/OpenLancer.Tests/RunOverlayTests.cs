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

        var section = MissionPageModel.RunSection(state.Trace, analysis.Index.Trigger("t_wait")!);
        var start = MissionPageModel.RunSection(state.Trace, analysis.Index.Trigger("t_start")!);

        Assert.Equal("Run", section.Title);
        Assert.Equal(["State", "Became active at", "Ran", "Last change", "Cause"], section.Fields.Select(f => f.Label));
        Assert.Equal(["Waits"], section.Fields[0].Chips.Select(c => c.Text));
        Assert.Equal("Step 2", section.Fields[1].Value);
        Assert.Equal("0 times", section.Fields[2].Value);
        Assert.Equal("Started t_wait", section.Fields[3].Value);
        Assert.Equal("Step 2: Heartbeat", section.Fields[4].Value);
        Assert.Equal("1 time", start.Fields.Single(f => f.Label == "Ran").Value);
    }

    /// <summary>The two triggers in their order of the next drain: smallest game id first.</summary>
    private static List<string> OrderOf(MissionAnalysis analysis, params string[] names) =>
        names.OrderBy(n => LibreLancer.Data.FLHash.CreateID(n)).ToList();
}

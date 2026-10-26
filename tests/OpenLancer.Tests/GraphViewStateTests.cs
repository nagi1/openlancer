using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.GraphTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The session state of the mission graph view (sections 6.4 to 6.7 of the M6a design). The tests check collapse and
/// open, lines, drags, the lens, and the link between a selection and the node that shows it.
/// </summary>
public sealed class GraphViewStateTests
{
    private static readonly MissionAnalysis Mission = Analysis();

    private static MissionGraph Graph(GraphViewState state, bool showLines = false) =>
        MissionGraph.Build(Mission, state.Options(Path, showLines, false, false, null));

    [Fact]
    public void ACollapseOfTheUserGoesIntoTheOptionsAndChangesTheVersion()
    {
        var state = new GraphViewState();
        var version = state.Version;

        state.SetCollapsed(Path, "p:spawnWave|t_spawn", false);

        Assert.NotEqual(version, state.Version);
        Assert.Contains("t_spawn", Graph(state).Nodes.Select(n => n.Title));
    }

    [Fact]
    public void AnotherLensForgetsTheCollapseOfTheUser()
    {
        var state = new GraphViewState();
        state.SetCollapsed(Path, "p:spawnWave|t_spawn", false);

        state.Lens = GraphLens.Logic;
        state.Lens = GraphLens.Beats;

        Assert.DoesNotContain("t_spawn", Graph(state).Nodes.Select(n => n.Title));
    }

    [Fact]
    public void LinesAndAllRowsOfOneNodeGoIntoTheOptions()
    {
        var state = new GraphViewState(GraphLens.Logic);
        var key = MissionGraph.KeyOf(Mission.Index.Trigger("t_start")!);

        state.SetLines(Path, key, true);

        Assert.True(state.ShowsLines(Path, key));
        Assert.True(((GraphNode)Graph(state).Block(key)!).Lines);
        state.SetLines(Path, key, false);
        Assert.False(((GraphNode)Graph(state).Block(key)!).Lines);
    }

    [Fact]
    public void TheDragsOfANodeAddUp()
    {
        var state = new GraphViewState();

        state.Dragged(Path, "t:2", new Vector2(10, 5));
        state.Dragged(Path, "t:2", new Vector2(-3, 1));

        Assert.Equal(new Vector2(7, 6), state.Offsets(Path)["t:2"]);
        Assert.Empty(state.Offsets(@"DATA\MISSIONS\OTHER\other.ini"));
    }

    [Fact]
    public void AStateKeepsTheIdsOfTheNodesOfAMission()
    {
        var state = new GraphViewState();
        var first = Graph(state);
        state.Lens = GraphLens.Logic;
        var second = Graph(state);

        Assert.Equal(first.Block("t:2")!.Id, second.Block("t:2")!.Id);
    }

    [Fact]
    public void TheNodeOfASelectionIsItsTriggerOrTheCollapsedFrameThatHoldsIt()
    {
        var graph = Graph(new GraphViewState());
        var kill = Mission.Index.Trigger("t_kill")!;
        var start = Mission.Index.Trigger("t_start")!;

        Assert.Equal("t_start", ((GraphNode)GraphViewState.BlockOf(graph,
            new MissionRef(Path, MissionItemKind.Trigger, start.Section.Ref))!).Title);
        Assert.Equal("Spawn wave", ((GraphNode)GraphViewState.BlockOf(graph,
            new MissionRef(Path, MissionItemKind.Row, kill.Section.Ref, Row: 2))!).Title);
        Assert.Equal("Spawn wave", ((GraphNode)GraphViewState.BlockOf(graph,
            new MissionRef(Path, MissionItemKind.PrefabInstance, Key: "spawnWave|t_spawn"))!).Title);
        Assert.IsType<GraphFrame>(GraphViewState.BlockOf(graph, new MissionRef(Path, MissionItemKind.Act, Key: "a:1")));
        Assert.Null(GraphViewState.BlockOf(graph, new MissionRef(Path, MissionItemKind.Label, Key: "enemy1")));
    }

    /// <summary>The play start shows in the node of its trigger, so a click on the chip of the toolbar selects and frames
    /// it in the graph (UI rule 6, review fix of M6c-2). The mission start has no node.</summary>
    [Fact]
    public void ThePlayStartShowsInTheNodeOfItsTrigger()
    {
        var graph = Graph(new GraphViewState());
        var item = MissionRef.PlayStartOf(Path);

        Assert.Equal("t_start", ((GraphNode)GraphViewState.BlockOf(graph, item, "t_start")!).Title);
        Assert.Null(GraphViewState.BlockOf(graph, item, null));
        Assert.Null(GraphViewState.BlockOf(graph, item, "t_gone"));
        Assert.Null(GraphViewState.BlockOf(graph, item));
    }

    [Fact]
    public void TheSelectionOfANodeIsItsTriggerItsPrefabOrItsAct()
    {
        var graph = Graph(new GraphViewState());
        var spawn = graph.Nodes.Single(n => n.Title == "Spawn wave");
        var notStarted = graph.Nodes.Single(n => n.Kind == GraphNodeKind.Act);

        Assert.Equal(new MissionRef(Path, MissionItemKind.PrefabInstance, Key: "spawnWave|t_spawn"),
            GraphViewState.RefOf(Path, spawn));
        Assert.Equal(new MissionRef(Path, MissionItemKind.Act, Key: "a:0"), GraphViewState.RefOf(Path, notStarted));
        Assert.Equal(MissionItemKind.Trigger, GraphViewState.RefOf(Path, graph.Nodes.First(n => n.Title == "t_start"))!.Kind);
        Assert.Null(GraphViewState.RefOf(Path, graph.Nodes.Single(n => n.Kind == GraphNodeKind.Missing)));
    }

    [Fact]
    public void TheBenchGivesTheMedianAndTheHighPercentile()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).Reverse().ToList();

        Assert.Equal((50.5, 95.0), MissionBench.Stats(values));
        Assert.Equal((2.0, 3.0), MissionBench.Stats([3, 1, 2]));
        Assert.Equal((0.0, 0.0), MissionBench.Stats([]));
    }
}

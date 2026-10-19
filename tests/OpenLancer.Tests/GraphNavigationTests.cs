using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.GraphTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The navigation of the mission graph (section 6.6 of the M6a design) on a synthetic mission. The tests check the
/// search, the path from a start trigger, the sources of each pin, the neighbors, the system filter and the minimap.
/// </summary>
public sealed class GraphNavigationTests
{
    private static readonly MissionAnalysis Mission = Analysis();

    private static MissionGraph Graph(GraphLens lens = GraphLens.Logic) =>
        MissionGraph.Build(Mission, new GraphOptions(lens));

    private static MissionTrigger Trigger(string nickname) => Mission.Index.Trigger(nickname)!;

    [Fact]
    public void TheSearchFindsNicknamesAndValuesInFileOrder()
    {
        var logic = Graph();
        var beats = Graph(GraphLens.Beats);

        Assert.Equal(["t_spawn", "t_kill"], GraphNavigation.Search(logic, "enemy1").Select(n => n.Title));
        Assert.Equal(["Spawn wave"], GraphNavigation.Search(beats, "enemy1").Select(n => n.Title));
        Assert.Equal(["t_restore", "t_save"], GraphNavigation.Search(logic, "T_RESTORE").Select(n => n.Title).Order());
        Assert.Empty(GraphNavigation.Search(logic, " "));
        Assert.Empty(GraphNavigation.Search(logic, "nothing_has_this"));
    }

    [Fact]
    public void ThePathFromAStartTriggerIsTheShortestPath()
    {
        var path = GraphNavigation.PathFromStart(Mission.Index, Trigger("t_end"))!;

        Assert.Equal(["t_start", "t_wait", "t_kill", "t_save", "t_end"], path.Triggers.Select(t => t.Nickname));
        Assert.Equal(4, path.Links.Count);
        Assert.All(path.Links, l => Assert.Equal(MissionLinkKind.Start, l.Kind));
        Assert.Equal(["t_start", "t_wait", "t_kill", "t_save", "t_restore"],
            GraphNavigation.PathFromStart(Mission.Index, Trigger("t_restore"))!.Triggers.Select(t => t.Nickname));
        Assert.Equal(MissionLinkKind.Load,
            GraphNavigation.PathFromStart(Mission.Index, Trigger("t_restore"))!.Links[^1].Kind);
        Assert.Single(GraphNavigation.PathFromStart(Mission.Index, Trigger("t_start"))!.Triggers);
        Assert.Null(GraphNavigation.PathFromStart(Mission.Index, Trigger("t_lonely")));
    }

    [Fact]
    public void TheSourcesOfEachPinListTheLinesThatEndThere()
    {
        var end = GraphNavigation.Sources(Mission.Index, Trigger("t_end"));
        var restore = GraphNavigation.Sources(Mission.Index, Trigger("t_restore"));
        var stopped = Analysis(Script + "\n\n[Trigger]\nnickname = t_stopper\nCnd_True = no_params\nAct_DeactTrig = t_wait\n");

        Assert.Equal(["t_save", "t_restore", "t_lonely"], end.Start.Select(l => l.Source.Nickname));
        Assert.Equal(["t_save"], end.Watches.Select(l => l.TargetName));
        Assert.Empty(end.Stop);
        Assert.Equal(["t_save"], restore.Load.Select(l => l.Source.Nickname));
        Assert.Equal(["t_stopper"], GraphNavigation.Sources(stopped.Index, stopped.Index.Trigger("t_wait")!).Stop
            .Select(l => l.Source.Nickname));
        Assert.Equal(4, end.All.Count());
    }

    [Fact]
    public void TheNeighborsAreTheNodesOneLinkAway()
    {
        var graph = Graph();
        var wait = graph.NodeOf(Trigger("t_wait"))!;

        Assert.Equal(["t_kill", "t_start", "t_wait"],
            GraphNavigation.Neighbors(graph, wait).Select(n => n.Title).Order());
    }

    [Fact]
    public void TheSystemFilterFadesTheNodesOfOtherSystems()
    {
        var graph = Graph();
        var beats = Graph(GraphLens.Beats);

        Assert.True(GraphNavigation.OutsideSystem(graph.NodeOf(Trigger("t_save"))!, "sys_a"));
        Assert.False(GraphNavigation.OutsideSystem(graph.NodeOf(Trigger("t_start"))!, "SYS_A"));
        // A trigger without a system is not of another system.
        Assert.False(GraphNavigation.OutsideSystem(graph.NodeOf(Trigger("t_lonely"))!, "sys_a"));
        Assert.False(GraphNavigation.OutsideSystem(beats.NodeOf(Trigger("t_kill"))!, "sys_a"));
    }

    [Fact]
    public void TheMinimapMapsTheGraphIntoItsRectangle()
    {
        var map = new GraphMinimap((new Vector2(0, 0), new Vector2(1000, 500)), (new Vector2(10, 10), new Vector2(110, 110)));

        Assert.Equal(0.1f, map.Scale, 4);
        Assert.Equal(new Vector2(10, 35), map.ToScreen(Vector2.Zero));
        Assert.Equal(new Vector2(110, 85), map.ToScreen(new Vector2(1000, 500)));
        Assert.Equal(new Vector2(500, 250), map.ToCanvas(new Vector2(60, 60)));
    }

    [Fact]
    public void AClickOnTheMinimapFindsTheNearestNode()
    {
        var graph = Graph();
        var layout = GraphLayout.Of(graph);
        var wait = graph.NodeOf(Trigger("t_wait"))!;
        var (min, max) = layout.RectOf(wait);

        Assert.Equal(wait, GraphNavigation.Nearest(graph, layout, (min + max) / 2));
        Assert.Equal(wait, GraphNavigation.Nearest(graph, layout, max + new Vector2(0, 1)));
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.GraphTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The compound layout of the mission graph (section 6.7 of the M6a design): the same graph gives the same positions,
/// back links do not change the layers, the members of a prefab stay in one block, a collapse keeps the order of the
/// blocks, and a drag offset stays after a layout.
/// </summary>
public sealed class GraphLayoutTests
{
    private static readonly MissionAnalysis Mission = Analysis();

    private static MissionGraph Graph(GraphLens lens = GraphLens.Logic, Dictionary<string, bool>? collapse = null,
        bool showLines = false, MissionAnalysis? analysis = null) =>
        MissionGraph.Build(analysis ?? Mission, new GraphOptions(lens, showLines, collapse));

    private static GraphBlock Trigger(MissionGraph graph, string nickname) =>
        graph.Block(MissionGraph.KeyOf(graph.Index.Trigger(nickname)!))!;

    [Fact]
    public void TheSameGraphGivesTheSamePositions()
    {
        var first = GraphLayout.Of(Graph());
        var second = GraphLayout.Of(Graph());

        Assert.Equal(first.Positions.OrderBy(p => p.Key), second.Positions.OrderBy(p => p.Key));
        Assert.Equal(first.Bounds, second.Bounds);
    }

    [Fact]
    public void TheLayersFollowTheStartLinksInTheFrame()
    {
        var graph = Graph();
        var layout = GraphLayout.Of(graph);

        Assert.Equal(0, layout.LayerOf(Trigger(graph, "t_start")));
        Assert.Equal(1, layout.LayerOf(Trigger(graph, "t_wait")));
        // The spawn wave holds t_kill, which t_wait starts, so it is one layer after t_wait.
        Assert.Equal(2, layout.LayerOf(graph.Block("p:spawnWave|t_spawn")!));
        Assert.Equal(3, layout.LayerOf(Trigger(graph, "t_after")));
        Assert.Equal(["t:2", "t:5", "p:spawnWave|t_spawn", "t:6"], layout.OrderIn(graph.Acts[0]));
    }

    [Fact]
    public void ABackLinkDoesNotChangeTheLayers()
    {
        var back = Analysis(Script.Replace("Act_ActTrig = t_missing", "Act_ActTrig = t_missing\nAct_ActTrig = t_start"));
        var plain = GraphLayout.Of(Graph());
        var withBack = GraphLayout.Of(Graph(analysis: back));
        var graph = Graph();

        Assert.Equal(graph.Acts[0].Blocks.Select(plain.LayerOf), Graph(analysis: back).Acts[0].Blocks.Select(withBack.LayerOf));
    }

    [Fact]
    public void TheMembersOfAPrefabStayInItsBlock()
    {
        var graph = Graph();
        var layout = GraphLayout.Of(graph);
        var (min, max) = layout.RectOf(graph.Block("p:spawnWave|t_spawn")!);

        foreach (var member in new[] { "t_spawn", "t_kill" })
        {
            var (a, b) = layout.RectOf(Trigger(graph, member));
            Assert.True(a.X >= min.X && a.Y >= min.Y && b.X <= max.X && b.Y <= max.Y, member);
        }
        // The plain triggers of the act are outside the frame.
        var (start, _) = layout.RectOf(Trigger(graph, "t_start"));
        Assert.True(start.X < min.X);
    }

    [Fact]
    public void ACollapseKeepsTheOrderOfTheBlocks()
    {
        var open = Graph();
        var closed = Graph(collapse: new Dictionary<string, bool> { ["p:spawnWave|t_spawn"] = true });
        var openLayout = GraphLayout.Of(open);
        var closedLayout = GraphLayout.Of(closed);

        Assert.Equal(openLayout.OrderIn(open.Acts[0]), closedLayout.OrderIn(closed.Acts[0]));
        // The collapsed frame is one node, so it is smaller than the open frame.
        Assert.True(closedLayout.SizeOf(closed.Block("p:spawnWave|t_spawn")!).X <
                    openLayout.SizeOf(open.Block("p:spawnWave|t_spawn")!).X);
        Assert.True(closedLayout.Positions.ContainsKey("p:spawnWave|t_spawn|node"));
    }

    [Fact]
    public void ShowLinesKeepsTheOrderAndMakesTheNodesWider()
    {
        var summary = Graph();
        var lines = Graph(showLines: true);

        Assert.Equal(GraphLayout.Of(summary).OrderIn(summary.Acts[1]), GraphLayout.Of(lines).OrderIn(lines.Acts[1]));
        Assert.Equal(MissionGraph.LinesWidth, GraphLayout.Of(lines).SizeOf(Trigger(lines, "t_start")).X);
    }

    [Fact]
    public void ADragOffsetStaysAfterALayout()
    {
        var key = MissionGraph.KeyOf(Mission.Index.Trigger("t_wait")!);
        var offsets = new Dictionary<string, Vector2> { [key] = new(50, 70) };
        var graph = Graph();
        var collapsed = Graph(collapse: new Dictionary<string, bool> { ["p:checkpoint|t_save"] = true });

        Assert.Equal(GraphLayout.Of(graph).Positions[key] + new Vector2(50, 70), GraphLayout.Of(graph, offsets).Positions[key]);
        Assert.Equal(GraphLayout.Of(collapsed).Positions[key] + new Vector2(50, 70),
            GraphLayout.Of(collapsed, offsets).Positions[key]);
    }

    [Fact]
    public void TheActsSitSideBySideAndTheNotStartedFrameIsLast()
    {
        var graph = Graph();
        var layout = GraphLayout.Of(graph);
        var rects = graph.Acts.Select(layout.RectOf).ToList();

        for (var i = 1; i < rects.Count; i++)
            Assert.True(rects[i].Min.X > rects[i - 1].Max.X);
        Assert.Equal(GraphFrameKind.NotStarted, graph.Acts[^1].Kind);
        Assert.All(rects, r => Assert.Equal(0, r.Min.Y));
        Assert.True(layout.Bounds.Max.X >= rects[^1].Max.X);
    }

    [Fact]
    public void ALongLayerWrapsIntoMoreColumns()
    {
        var many = string.Concat(Enumerable.Range(0, GraphLayout.MaxColumn + 5).Select(i =>
            $"\n\n[Trigger]\nnickname = t_free{i}\nCnd_True = no_params\nAct_ActTrig = t_end\n"));
        var graph = Graph(analysis: Analysis(Script + many));
        var layout = GraphLayout.Of(graph);
        var xs = graph.Acts[^1].Blocks.Select(b => layout.PositionOf(b).X).Distinct().Count();

        Assert.Equal(2, xs);
    }
}

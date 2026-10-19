using System;
using System.Diagnostics;
using System.Linq;
using OpenLancer.Data.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the mission graph (sections 17.2 and 18.4 of the M6a design) over the retail scripts M10 and M06. M10
/// has the most triggers (408), and M06 has the most links (701). The tests count nodes and links. They hold no retail
/// text.
/// </summary>
public sealed class MissionGraphRetailTests(ITestOutputHelper output)
{
    /// <summary>The layout gate of section 18.4: the layout of M10 and of M06 at both levels stays under 100 ms. Other
    /// builds can run on the machine at the same time, so run the test again alone before you judge a failure.</summary>
    [Theory]
    [InlineData("M10", false)]
    [InlineData("M10", true)]
    [InlineData("M06", false)]
    [InlineData("M06", true)]
    public void TheLayoutStaysUnderTheGate(string slot, bool showLines)
    {
        var analysis = RetailMissions.Slot(slot);
        // A first layout warms the code, so the gate measures the layout and not the start of the program.
        GraphLayout.Of(MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic, showLines)));

        foreach (var lens in new[] { GraphLens.Beats, GraphLens.Logic })
        {
            var build = Stopwatch.StartNew();
            var graph = MissionGraph.Build(analysis, new GraphOptions(lens, showLines));
            build.Stop();
            var watch = Stopwatch.StartNew();
            var layout = GraphLayout.Of(graph);
            watch.Stop();

            output.WriteLine($"{slot} {lens} {(showLines ? "lines" : "summary")}: {graph.Nodes.Count} nodes, " +
                             $"{graph.Links.Count} links. Build {build.Elapsed.TotalMilliseconds:F1} ms, layout " +
                             $"{watch.Elapsed.TotalMilliseconds:F1} ms.");
            Assert.Equal(graph.Nodes.Count, graph.Nodes.Count(n => layout.Positions.ContainsKey(n.Key)));
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(100),
                $"The layout of {slot} took {watch.Elapsed.TotalMilliseconds:F1} ms.");
        }
    }

    /// <summary>The node and link counts of each lens (section 17.2 step 2.1 of the M6a design). The Logic lens shows
    /// each trigger. The other lenses collapse the prefab frames and the frame "Not started by the mission".</summary>
    [Fact]
    public void TheNodeCountsOfEachLens()
    {
        string Counts(string slot) => string.Join(" ", Enum.GetValues<GraphLens>().Select(lens =>
        {
            var graph = MissionGraph.Build(RetailMissions.Slot(slot), new GraphOptions(lens));
            return $"{lens}:{graph.Nodes.Count}/{graph.Links.Count}/{graph.Nodes.Count(n => n.Faded)}";
        }));

        var m10 = Counts("M10");
        var m06 = Counts("M06");
        output.WriteLine($"M10 {m10}");
        output.WriteLine($"M06 {m06}");
        var m10Logic = MissionGraph.Build(RetailMissions.Slot("M10"), new GraphOptions(GraphLens.Logic));
        Assert.Equal(408, m10Logic.Nodes.Count(n => n.Kind == GraphNodeKind.Trigger));
        // Each lens: the nodes that show, the links and the faded nodes.
        Assert.Equal("Beats:279/511/0 Logic:415/576/0 Battle:304/544/157 Conversation:282/514/181 " +
                     "Cutscene:279/511/262 Objectives:279/511/216", m10);
        Assert.Equal("Beats:245/534/0 Logic:344/703/0 Battle:253/539/157 Conversation:292/595/74 " +
                     "Cutscene:245/534/214 Objectives:245/534/175", m06);
    }
}

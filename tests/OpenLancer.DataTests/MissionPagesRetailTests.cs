using System;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the pages, the tree and the cross-links of the mission studio (sections 9, 10 and 7.3 of the M6a
/// design): each item of the 14 retail story scripts gets a page, each mission gets a tree, and each trigger gets its
/// cross-link targets. No test can draw ImGui, so these tests run the models of the views over all retail items. They
/// count items. They hold no retail text.
/// </summary>
public sealed class MissionPagesRetailTests(ITestOutputHelper output)
{
    [Fact]
    public void EachRetailItemHasAPage()
    {
        var run = RetailMissions.Run();
        var pages = 0;
        foreach (var analysis in run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!))
        {
            var context = new PageContext(run, analysis, true, false);
            var script = analysis.Script.Path;
            var items = analysis.Document.Sections.Select(s => MissionWorkspace.RefOf(s.Ref, -1)).ToList();
            items.AddRange(analysis.Index.Triggers.SelectMany(t => t.Rows.Select(r =>
                new MissionRef(script, MissionItemKind.Row, t.Section.Ref, Row: r.Entry))));
            items.AddRange(analysis.Index.Labels.Keys.Select(l => new MissionRef(script, MissionItemKind.Label, Key: l)));
            items.AddRange(analysis.Prefabs!.Instances.Select(i => new MissionRef(script, MissionItemKind.PrefabInstance, Key: i.Key)));
            items.AddRange(MissionPrefabs.Retail.Definitions.Select(d => new MissionRef(script, MissionItemKind.PrefabKind, Key: d.Id)));
            items.AddRange(analysis.Acts!.Acts.Select(a => new MissionRef(script, MissionItemKind.Act, Key: $"a:{a.Number}"))
                .Append(new MissionRef(script, MissionItemKind.Act, Key: "a:0")));
            foreach (var item in items)
            {
                var page = MissionPageModel.For(context, item);
                Assert.False(string.IsNullOrEmpty(page.Kind), item.ToString());
                pages++;
            }
        }

        output.WriteLine($"{pages} pages.");
        Assert.True(pages > 20_000, $"Only {pages} pages.");
    }

    [Fact]
    public void EachRetailMissionHasATreeWithItsTriggers()
    {
        var run = RetailMissions.Run();
        foreach (var analysis in run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!))
        {
            var tree = HierarchyModel.ForMission(analysis, _ => "", true, null);
            var triggers = analysis.Index.Triggers.Count(t => tree.NodeOf(new MissionRef(analysis.Script.Path,
                MissionItemKind.Trigger, t.Section.Ref)) != null);

            Assert.Equal(analysis.Index.Triggers.Count, triggers);
        }
    }

    [Fact]
    public void EachRetailTriggerHasItsCrossLinkTargets()
    {
        var run = RetailMissions.Run();
        var targets = 0;
        foreach (var analysis in run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!))
        {
            var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic, ShowLines: true));
            foreach (var trigger in analysis.Index.Triggers)
            {
                var item = new MissionRef(analysis.Script.Path, MissionItemKind.Trigger, trigger.Section.Ref);
                foreach (var target in CrossLinks.Targets(analysis, CrossLinks.SourceLines(analysis, item)))
                {
                    Assert.NotNull(CrossLinks.SourceOf(graph, target.Line));
                    targets++;
                }
            }
        }

        output.WriteLine($"{targets} cross-link targets.");
        Assert.True(targets > 5_000, $"Only {targets} targets.");
    }
}

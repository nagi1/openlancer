using System.Linq;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The Hierarchy in cutscene mode (section 7.4 of the M8 design): the cutscene row, the entities in the groups of the
/// Timeline, the search with the entity types, and a selection of a cutscene element by its value.
/// </summary>
public sealed class CutsceneHierarchyTests
{
    private static HierarchyModel Tree(TimelineModel? timeline = null, HierarchyModel? previous = null) =>
        HierarchyModel.ForCutscene(timeline ?? TimelineModelTests.Model(), previous);

    private static string[] Names(HierarchyNode group) => group.Children.Select(c => c.Name).ToArray();

    [Fact]
    public void TheTreeHasTheCutsceneRowAndTheEntitiesInTheGroupsOfTheTimeline()
    {
        var tree = Tree();

        Assert.Equal(new CutsceneRoot(TimelineModelTests.Path), tree.Root.Item);
        Assert.Equal("test", tree.Root.Name);
        Assert.Equal(["Cameras", "Characters", "Effects", "Markers"], tree.Root.Children.Select(g => g.Name));
        // The monitors come first in the cameras, the main monitor with its detail.
        var cameras = tree.Root.Children[0];
        Assert.Equal(["Cam_Monitor", "Cam_Monitor_min_spec", "Cam_A", "Cam_B", "Cam_C", "Cam_D"], Names(cameras));
        Assert.Equal("main", cameras.Children[0].Detail);
        Assert.Equal(ObjectKinds.Thn.Monitor, cameras.Children[1].Kind);
        Assert.Equal("ignored monitor only", cameras.Children.Single(c => c.Name == "Cam_C").Detail);
        Assert.Equal(TimelineModel.IgnoredMonitorMark, cameras.Children.Single(c => c.Name == "Cam_C").Tooltip);
        // Each entity of the script is an item, and the target that names no entity is not.
        Assert.Equal(9, tree.Root.Total);
        Assert.All(tree.Root.Children.SelectMany(g => g.Children), n => Assert.IsType<ThnRef>(n.Item));
    }

    [Fact]
    public void AnEqualElementFindsItsRow()
    {
        var tree = Tree();
        var node = tree.Root.Children[1].Children[0];
        var element = (ThnRef)node.Item!;

        // A selection that the Timeline or the Cutscene view makes is a new value with the same path, kind and id.
        var copy = new ThnRef(element.Path, element.Kind, element.Id);

        Assert.Same(node, tree.NodeOf(copy));
        Assert.True(tree.Reveal(copy));
        Assert.True(tree.IndexOf(copy) > 0);
        Assert.Same(tree.Root, tree.NodeOf(new CutsceneRoot(TimelineModelTests.Path)));
    }

    [Fact]
    public void ATypeWordKeepsTheEntitiesOfThatType()
    {
        var tree = Tree();

        tree.SetSearch("t:camera");
        Assert.Equal(4, tree.Root.Matches);
        tree.SetSearch("t:monitor min");
        Assert.Equal(1, tree.Root.Matches);
        tree.SetSearch("TRENT");
        Assert.Equal(1, tree.Root.Matches);
        tree.SetSearch("t:station");
        Assert.Equal(0, tree.Root.Matches);
    }

    [Fact]
    public void TheSameTimelineGivesTheSameTreeAndANewTimelineKeepsTheOpenGroupsAndTheSearch()
    {
        var timeline = TimelineModelTests.Model();
        var tree = Tree(timeline);
        tree.SetOpen(tree.Root.Children[2], true);
        tree.SetSearch("t:effect");

        Assert.Same(tree, Tree(timeline, tree));

        // An edit gives a new timeline of the same cutscene.
        var edited = Tree(TimelineModelTests.Model(TimelineModelTests.Script.Replace("duration = 30", "duration = 31")),
            tree);
        Assert.NotSame(tree, edited);
        Assert.Equal("t:effect", edited.Search);
        edited.SetSearch("");
        Assert.True(edited.IsOpen(edited.Root.Children[2]));
        Assert.False(edited.IsOpen(edited.Root.Children[1]));
    }
}

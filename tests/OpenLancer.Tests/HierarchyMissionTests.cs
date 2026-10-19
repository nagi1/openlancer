using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Missions;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The tree of a mission in the Hierarchy (section 9 of the M6a design): the groups, the acts with their prefabs and
/// triggers, the cast, the labels, the links, the prefabs, the items without a system, the search and the marks.
/// </summary>
public sealed class HierarchyMissionTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    private const string Text = """
        [Mission]

        [NPC]
        nickname = npc_one
        individual_name = 1001

        [MsnShip]
        nickname = s_one
        npc = npc_one
        system = sys_a
        position = 1, 2, 3
        label = l_wing

        [MsnShip]
        nickname = s_player
        rel_pos = 0, Player, 500

        [MsnShip]
        nickname = s_lost
        position = 1, 2, 3

        [ObjList]
        nickname = ol_one
        GotoVec = goto, 0, 0, 0, 100
        GotoVec = goto, 10, 0, 0, 100

        [Constants]
        value = 1

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        system = sys_a
        Cnd_True = no_params
        Act_SpawnShip = s_one, ol_one
        Act_CallThorn = scripts\a.thn, s_one
        Act_ActTrig = t_end

        [Trigger]
        nickname = t_end
        system = sys_a
        Cnd_True = no_params
        Act_RevertCam = no_params
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_lonely
        Cnd_True = no_params
        """;

    private static readonly MissionAnalysis Mission = Analysis();

    private static MissionAnalysis Analysis()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Text);
        return MissionRun.Execute(folder.Inputs()).Of(Script)!;
    }

    private static string TextOf(int id) => id switch { 1001 => "Invented pilot", 21800 => "Invented title", _ => "" };

    private static HierarchyModel Tree(bool showBaseGame = false, HierarchyModel? previous = null) =>
        HierarchyModel.ForMission(Mission, TextOf, showBaseGame, previous);

    private static HierarchyNode Group(HierarchyModel tree, string id) => tree.Root.Children.Single(c => c.Id == id);

    [Fact]
    public void TheTreeHasTheGroupsOfTheMissionAndNoEmptyGroup()
    {
        var tree = Tree();

        Assert.Equal(("M01a: Invented title", MissionRef.Of(Script)), (tree.Root.Name, tree.Root.Item));
        Assert.Equal(["logic", "cast", "labels", "orders", "links", "prefabs", "near-player", "unknown", "other"],
            tree.Root.Children.Select(c => c.Id));
    }

    [Fact]
    public void TheLogicGroupHasTheActsWithTheirPrefabsAndTriggers()
    {
        var logic = Group(Tree(), "logic");

        Assert.Equal(["Act 1: sys_a", "Not started by the mission (1)"], logic.Children.Select(c => c.Name));
        var act = logic.Children[0];
        Assert.Equal(MissionItemKind.Act, ((MissionRef)act.Item!).Kind);
        // The space cutscene claims t_start and t_end, so both are rows of its prefab.
        var prefab = Assert.Single(act.Children);
        Assert.Equal(("Play space cutscene", "t_start"), (prefab.Name, prefab.Detail));
        Assert.Equal(["t_start", "t_end"], prefab.Children.Select(c => c.Name));
        // t_lonely has no actions, so the prefab "Watched state" holds it.
        var watched = logic.Children[1].Children.Single();
        Assert.Equal(("Watched state", "t_lonely"), (watched.Name, watched.Children.Single().Name));
    }

    [Fact]
    public void TheCastAndTheLabelsListTheirItems()
    {
        var tree = Tree();
        var ships = Group(tree, "cast").Children.Single(c => c.Id == "cast/ships");

        Assert.Equal(["s_one", "s_player", "s_lost"], ships.Children.Select(c => c.Name));
        Assert.Equal("Invented pilot", ships.Children[0].Detail);
        var label = Group(tree, "labels").Children.Single();
        Assert.Equal(("l_wing", "1 member"), (label.Name, label.Detail));
        // A ship in a label has two rows. Its row in the cast is its row.
        var ship = new MissionRef(Script, MissionItemKind.Ship, Mission.Document.OfKind(MissionSectionKind.Ship).First().Ref);
        Assert.Equal(ship, label.Children.Single().Item);
        Assert.Equal("cast/ships", tree.NodeOf(ship)!.Parent!.Id);
    }

    [Fact]
    public void TheOtherGroupsListTheirItems()
    {
        var tree = Tree();

        Assert.Equal(("ol_one", "Patrol"), (Group(tree, "orders").Children.Single().Name, Group(tree, "orders").Children.Single().Detail));
        var cutscene = Group(tree, "links").Children.Single(c => c.Id == "links/cutscenes").Children.Single();
        Assert.Equal(("scripts\\a.thn", "t_start", MissionItemKind.Row), (cutscene.Name, cutscene.Detail, ((MissionRef)cutscene.Item!).Kind));
        Assert.Contains(Group(tree, "prefabs").Children, c => c.Name == "Play space cutscene (1)");
        Assert.Equal(["s_player"], Group(tree, "near-player").Children.Select(c => c.Name));
        Assert.Equal(["s_lost"], Group(tree, "unknown").Children.Select(c => c.Name));
        Assert.Equal(["Constants"], Group(tree, "other").Children.Select(c => c.Detail));
    }

    [Fact]
    public void TheSearchMatchesNamesAndValuesAndTypes()
    {
        var tree = Tree();

        tree.SetSearch("ol_one");
        Assert.True(tree.NodeOf(new MissionRef(Script, MissionItemKind.Trigger, Mission.Index.Trigger("t_start")!.Section.Ref))!
            .Matches > 0);
        Assert.Equal(0, tree.NodeOf(new MissionRef(Script, MissionItemKind.Trigger, Mission.Index.Trigger("t_end")!.Section.Ref))!
            .Matches);
        tree.SetSearch("t:ship");
        Assert.Equal(3, Group(tree, "cast").Matches);
    }

    [Fact]
    public void TheMarksFollowTheProblemsAndTheToggle()
    {
        var hidden = Tree();
        var shown = Tree(showBaseGame: true);
        var lonely = new MissionRef(Script, MissionItemKind.Trigger, Mission.Index.Trigger("t_lonely")!.Section.Ref);

        // No mod changes the script, so the problem of t_lonely is a base-game problem.
        Assert.Equal(RowProblem.None, hidden.NodeOf(lonely)!.Problem);
        Assert.Equal(RowProblem.Muted, shown.NodeOf(lonely)!.Problem);
        Assert.NotSame(hidden, shown);
    }

    [Fact]
    public void ATreeOfTheSameAnalysisIsTheTreeBeforeAndKeepsItsOpenGroups()
    {
        var first = Tree();
        first.SetOpen(Group(first, "cast"), true);

        Assert.Same(first, Tree(previous: first));
        var other = Tree(showBaseGame: true, previous: first);
        Assert.True(other.IsOpen(Group(other, "cast")));
    }
}

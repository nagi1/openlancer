using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The cross-highlighting of the Mission tab (sections 7.3 and 18.1 of the M6a design). The tests check the highlight
/// set of a trigger with a spawn, an order list, an arrival object and a distance volume. They check the source of a
/// connector line at each level, the triggers of a world item, and the geometry of the connector lines.
/// </summary>
public sealed class CrossLinksTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    private const string Text = """
        [Mission]

        [MsnShip]
        nickname = s_one
        system = sys_a
        position = 10, 0, 20
        arrival_obj = gate_a
        label = l_wing

        [MsnShip]
        nickname = s_two
        system = sys_b
        position = 30, 0, 20
        label = l_wing

        [ObjList]
        nickname = ol_one
        GotoVec = goto, 0, 0, 0, 100

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        system = sys_a
        Cnd_True = no_params
        Act_SpawnShip = s_one, ol_one
        Act_GiveObjList = l_wing, ol_one
        Act_LockDock = Player, gate_b, lock
        Act_ActTrig = t_near

        [Trigger]
        nickname = t_near
        system = sys_a
        Cnd_DistVec = inside, s_one, 100, 200, 300, 400
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_free
        Cnd_True = no_params
        Act_RelocateShip = s_two, 1, 2, 3
        """;

    private static readonly MissionAnalysis Mission = Run();

    private static MissionAnalysis Run()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Text);
        var world = World(new Dictionary<MissionValueKind, string[]> { [MissionValueKind.SystemObject] = ["gate_a", "gate_b"] });
        return MissionRun.Execute(folder.Inputs(world: world)).Of(Script)!;
    }

    private static MissionTrigger Trigger(string name) => Mission.Index.Trigger(name)!;

    private static MissionRef RefOf(MissionTrigger trigger) => new(Script, MissionItemKind.Trigger, trigger.Section.Ref);

    private static MissionRef SectionRef(string nickname, MissionSectionKind kind) =>
        MissionRef.OfSection(Mission.Document.OfKind(kind).Single(s => s.Nickname == nickname).Ref, -1);

    [Fact]
    public void TheHighlightOfATriggerHasItsShipsOrdersObjectsAndPlaces()
    {
        var start = Trigger("t_start");
        var targets = CrossLinks.Targets(Mission, CrossLinks.SourceLines(Mission, RefOf(start)));
        var (items, objects) = CrossLinks.Highlight(targets);
        var spawn = start.ActionsOf("Act_SpawnShip").Single();

        Assert.Equal(new HashSet<MissionRef>
        {
            SectionRef("s_one", MissionSectionKind.Ship), SectionRef("s_two", MissionSectionKind.Ship),
            SectionRef("ol_one", MissionSectionKind.OrderList),
            new(Script, MissionItemKind.Row, start.Section.Ref, Row: spawn.Entry),
        }, items);
        // The gate of the lock line, and the arrival object of the spawned ship.
        Assert.Equal(["gate_a", "gate_b"], objects.Order());
        Assert.Equal("sys_b", targets.First(t => t.Item == SectionRef("s_two", MissionSectionKind.Ship)).System);
    }

    [Fact]
    public void TheHighlightOfARowHasOnlyTheItemsOfItsLine()
    {
        var near = Trigger("t_near");
        var row = new MissionRef(Script, MissionItemKind.Row, near.Section.Ref, Row: near.Condition!.Entry);

        var (items, objects) = CrossLinks.Highlight(CrossLinks.Targets(Mission, CrossLinks.SourceLines(Mission, row)));

        Assert.Equal(new HashSet<MissionRef> { SectionRef("s_one", MissionSectionKind.Ship), row }, items);
        Assert.Equal(["gate_a"], objects);
    }

    [Fact]
    public void ThePrefabOfASpawnWaveHasTheItemsOfAllItsTriggers()
    {
        var instance = Mission.Prefabs!.Instances.Single(i => i.Definition.Id == "spawnWave");
        var lines = CrossLinks.SourceLines(Mission, new MissionRef(Script, MissionItemKind.PrefabInstance, Key: instance.Key));

        Assert.Equal(instance.Triggers.Sum(t => t.Rows.Count), lines.Count);
    }

    [Fact]
    public void TheLineStartsAtTheRowOrAtTheChipOfItsGroup()
    {
        var spawn = Trigger("t_start").ActionsOf("Act_SpawnShip").Single();
        var summary = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Logic));
        var lines = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Logic, ShowLines: true));
        var key = MissionGraph.KeyOf(Trigger("t_start"));

        Assert.Equal(new CrossLinkSource(key, spawn.Entry, null), CrossLinks.SourceOf(lines, spawn));
        Assert.Equal(new CrossLinkSource(key, null, "spawn"), CrossLinks.SourceOf(summary, spawn));
        // A condition has no chip, so its line starts at the node.
        Assert.Equal(new CrossLinkSource(MissionGraph.KeyOf(Trigger("t_near")), null, null),
            CrossLinks.SourceOf(summary, Trigger("t_near").Condition!));
    }

    [Fact]
    public void ALineOfACollapsedFrameStartsAtItsNode()
    {
        var beats = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Beats));
        var spawn = Trigger("t_start").ActionsOf("Act_SpawnShip").Single();

        Assert.Equal(beats.NodeOf(Trigger("t_start"))!.Key, CrossLinks.SourceOf(beats, spawn)!.Node);
        Assert.Null(CrossLinks.SourceOf(beats, spawn)!.Group);
    }

    [Fact]
    public void TheTriggersOfAWorldItemAreTheTriggersThatNameIt()
    {
        string[] Users(object item) => MissionUses.TriggersOf(item is MissionRef mission
                ? CrossLinks.LinesUsing(Mission, mission)
                : CrossLinks.LinesNaming(Mission, [(string)item]))
            .Select(t => t.Nickname).ToArray();

        Assert.Equal(["t_start", "t_near"], Users(SectionRef("s_one", MissionSectionKind.Ship)));
        // The second ship has a move line, and its label has a line too.
        Assert.Equal(["t_start", "t_free"], Users(SectionRef("s_two", MissionSectionKind.Ship)));
        Assert.Equal(["t_start"], Users(SectionRef("ol_one", MissionSectionKind.OrderList)));
        Assert.Equal(["t_start"], Users("gate_b"));
        Assert.Equal(["t_start"], Users(new MissionRef(Script, MissionItemKind.Label, Key: "l_wing")));
        Assert.Empty(Users("gate_c"));
    }

    /// <summary>An object and the base of its dock are one world item. So a line that names both counts once (section
    /// 7.3 of the M6a design, world to graph).</summary>
    [Fact]
    public void TheNicknamesOfAWorldItemGiveEachLineOnce()
    {
        var lines = CrossLinks.LinesNaming(Mission, ["gate_b", "GATE_B", "gate_c", ""]);

        Assert.Equal(["Act_LockDock"], lines.Select(l => l.Key));
    }

    /// <summary>An object of a system gets the system that the caller finds for it. So a line to an object in another
    /// system ends at the chip of that system. A placed line of a trigger without a system line gets the system of the
    /// item that it names (section 7.3, item 6).</summary>
    [Fact]
    public void AnObjectAndAPlacedLineGetTheSystemOfTheirItem()
    {
        var start = CrossLinks.Targets(Mission, CrossLinks.SourceLines(Mission, RefOf(Trigger("t_start"))),
            name => name == "gate_b" ? "sys_c" : null);
        var free = CrossLinks.Targets(Mission, CrossLinks.SourceLines(Mission, RefOf(Trigger("t_free"))));

        Assert.Equal("sys_c", start.Single(t => t.Object == "gate_b").System);
        Assert.Null(start.First(t => t.Object == "gate_a").System);
        Assert.Equal("sys_b", free.Single(t => t.Item?.Kind == MissionItemKind.Row).System);
    }

    /// <summary>Two lines of one source to one item give one connector line, so a collapsed frame draws one line to
    /// each item.</summary>
    [Fact]
    public void TheConnectorLinesHaveOneLineForEachSourceAndItem()
    {
        var targets = CrossLinks.Targets(Mission, CrossLinks.SourceLines(Mission, RefOf(Trigger("t_start"))));
        var beats = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Beats));
        var logic = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Logic));
        var ship = SectionRef("s_one", MissionSectionKind.Ship);

        // The spawn line and the order line both name the first ship.
        Assert.Equal(2, targets.Count(t => Equals(t.Item, ship)));
        // In the open frame, the spawn chip and the order chip each start a line. The collapsed frame starts one line.
        Assert.Equal(2, CrossLinks.Lines(logic, targets).Count(l => Equals(l.Target.Item, ship)));
        Assert.Single(CrossLinks.Lines(beats, targets), l => Equals(l.Target.Item, ship));
    }

    [Fact]
    public void TheOutlineOfAWorldItemHasItsNodesAndTheirPrefabFrames()
    {
        var users = MissionUses.TriggersOf(CrossLinks.LinesUsing(Mission, SectionRef("s_one", MissionSectionKind.Ship)));
        var logic = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Logic));
        var beats = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Beats));
        var wave = logic.Frames.Single(f => f.Instance?.Definition.Id == "spawnWave");

        var outline = CrossLinks.OutlineOf(logic, users);

        Assert.Contains(MissionGraph.KeyOf(Trigger("t_start")), outline);
        Assert.Contains(MissionGraph.KeyOf(Trigger("t_near")), outline);
        // The spawn trigger is in the open frame of the spawn wave, so the frame gets the outline too. An act frame
        // does not.
        Assert.Contains(wave.Key, outline);
        Assert.DoesNotContain(logic.Frames.Where(f => f.Kind != GraphFrameKind.Prefab), f => outline.Contains(f.Key));
        // A collapsed frame is one node, and that node gets the outline.
        Assert.Contains(beats.NodeOf(Trigger("t_start"))!.Key, CrossLinks.OutlineOf(beats, users));
        Assert.Empty(CrossLinks.OutlineOf(logic, []));
    }

    [Fact]
    public void AConnectorLineEndsAtTheEdgesWithArrows()
    {
        var graph = (new Vector2(0, 0), new Vector2(400, 300));
        var scene = (new Vector2(410, 0), new Vector2(800, 300));
        var row = (new Vector2(50, 100), new Vector2(350, 120));

        Assert.Equal(new ConnectorLine(new Vector2(350, 110), new Vector2(600, 150), false, false),
            CrossLinks.Connect(row, graph, new Vector2(600, 150), scene));
        var outside = CrossLinks.Connect(row, graph, new Vector2(1200, 110), scene);
        Assert.True(outside.ToArrow);
        Assert.Equal(800, outside.To.X, 2);
        var hidden = CrossLinks.Connect((new Vector2(50, 400), new Vector2(350, 420)), graph, new Vector2(600, 150), scene);
        Assert.True(hidden.FromArrow);
        Assert.Equal(new Vector2(350, 300), hidden.From);
    }

    [Fact]
    public void AClickNearALineHitsIt()
    {
        Assert.Equal(3, CrossLinks.Distance(new Vector2(50, 3), Vector2.Zero, new Vector2(100, 0)), 3);
        Assert.Equal(5, CrossLinks.Distance(new Vector2(103, 4), Vector2.Zero, new Vector2(100, 0)), 3);
    }
}

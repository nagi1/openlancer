using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The gestures of the mission graph (section 8.2 of the M6b design) on the synthetic script of
/// <see cref="MissionEdits"/>: each gesture gives its batch of mission commands, and the batch gives the lines of W. The
/// tests also check the reasons of the rejected links.
/// </summary>
public sealed class GraphEditModelTests
{
    /// <summary>The gesture model of the test script now, at the lines level, with all frames open.</summary>
    private static GraphEditModel Model(MissionEdits m) =>
        new(MissionGraph.Build(m.Missions.Model(Script).Analysis, new GraphOptions(GraphLens.Logic, ShowLines: true)));

    private static GraphNode Node(GraphEditModel model, string trigger) =>
        model.Graph.TriggerNode(model.Graph.Index.Trigger(trigger)!)!;

    private static GraphEnd Pin(GraphEditModel model, string trigger, GraphPinKind kind) =>
        new(Node(model, trigger), Node(model, trigger).Pin(kind)!);

    private static GraphEnd Add(GraphEditModel model, string trigger) => new(Node(model, trigger), null);

    /// <summary>The end of the row pin of a line of a trigger.</summary>
    private static GraphEnd Row(GraphEditModel model, string trigger, int entry) =>
        new(Node(model, trigger), Node(model, trigger).Pins.Single(p => p.Kind == GraphPinKind.Row && p.Line!.Entry == entry));

    private static void Run(MissionEdits m, MissionBatch? batch)
    {
        Assert.NotNull(batch);
        var result = m.Missions.Apply(batch);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void ADragFromThePlusPinStartsOrStopsTheTargetAndTheEndsCanComeInAnyOrder()
    {
        using var m = Create();
        var model = Model(m);

        var start = model.Link(Add(model, "ol_end"), Pin(model, "ol_wave", GraphPinKind.Start));
        var stop = model.Link(Pin(model, "ol_start", GraphPinKind.Stop), Add(model, "ol_end"));

        Assert.Equal(new LinkTriggers(T("ol_end"), "ol_wave", LinkKind.Start), Assert.Single(start.Batch!.Commands));
        Assert.Equal(new LinkTriggers(T("ol_end"), "ol_start", LinkKind.Stop), Assert.Single(stop.Batch!.Commands));
        Run(m, start.Batch);
        Assert.Equal("Act_ActTrig = ol_wave", m.Lines("ol_end")[^1]);
    }

    [Fact]
    public void ALinkToTheNodeItselfIsValid()
    {
        using var m = Create();
        var model = Model(m);

        var gesture = model.Link(Add(model, "ol_end"), Pin(model, "ol_end", GraphPinKind.Start));

        Run(m, gesture.Batch);
        Assert.Equal("Act_ActTrig = ol_end", m.Lines("ol_end")[^1]);
    }

    [Fact]
    public void ADragFromARowChangesTheTriggerOfItsLine()
    {
        using var m = Create();
        var model = Model(m);

        // Line 3 of ol_start is "Act_ActTrig = ol_wave".
        var gesture = model.Link(Row(model, "ol_start", 3), Pin(model, "ol_end", GraphPinKind.Start));

        var command = Assert.IsType<RetargetLink>(Assert.Single(gesture.Batch!.Commands));
        Assert.Equal("ol_end", command.Target);
        Assert.Equal("Act_ActTrig", command.Row.ExpectKey);
        Run(m, gesture.Batch);
        Assert.Equal("Act_ActTrig = ol_end", m.Lines("ol_start")[3]);
    }

    [Fact]
    public void ATargetThatIsNotValidGivesItsReason()
    {
        using var m = Create(Text + "\n[Trigger]\nnickname = ol_watch\nCnd_WatchTrigger = ol_end, ON\nAct_ActTrig = ol_start\n");
        var model = Model(m);

        Assert.Equal(GraphEditModel.OutputToInput,
            model.Link(Add(model, "ol_end"), Add(model, "ol_wave")).Reason);
        Assert.Equal(GraphEditModel.OutputToInput,
            model.Link(Pin(model, "ol_end", GraphPinKind.Start), Pin(model, "ol_wave", GraphPinKind.Stop)).Reason);
        Assert.Equal(GraphEditModel.StateOnly,
            model.Link(Add(model, "ol_start"), Pin(model, "ol_watch", GraphPinKind.Watches)).Reason);
        Assert.Equal(GraphEditModel.StartToStart,
            model.Link(Row(model, "ol_start", 3), Pin(model, "ol_end", GraphPinKind.Stop)).Reason);
        Assert.Equal(GraphEditModel.StateToWatches,
            model.Link(Pin(model, "ol_end", GraphPinKind.State), Pin(model, "ol_wave", GraphPinKind.Start)).Reason);
    }

    [Fact]
    public void ADragFromTheStatePinToAWatchesPinAsksForTheStateAndSetsTheWatchCondition()
    {
        using var m = Create(Text + "\n[Trigger]\nnickname = ol_watch\nCnd_WatchTrigger = ol_end, ON\nAct_ActTrig = ol_start\n");
        var model = Model(m);
        var state = Pin(model, "ol_end", GraphPinKind.State);
        var watcher = Pin(model, "ol_watch", GraphPinKind.Watches);

        var gesture = model.Link(state, watcher);
        Assert.True(gesture.AsksState);
        Assert.Null(gesture.Batch);
        Run(m, model.Watch(state, watcher, "COMPLETE"));

        Assert.Equal("Cnd_WatchTrigger = ol_end, COMPLETE", m.Lines("ol_watch")[1]);
        Assert.Equal(["ON", "OFF", "INACTIVE", "COMPLETE"], GraphEditModel.WatchStates.Select(s => s.Word));
    }

    [Fact]
    public void RemoveLinkRemovesBothLinesOfARestartAndTheLinesOfAStart()
    {
        using var m = Create();
        var model = Model(m);
        var restart = model.Graph.Links.First(l => l.From == Node(model, "ol_wave") && l.To == Node(model, "ol_end"));
        var start = model.Graph.Links.First(l => l.From == Node(model, "ol_start") && l.To == Node(model, "ol_end"));

        var batch = model.Unlink(restart)!;
        Assert.Equal(new UnlinkTriggers(T("ol_wave"), "ol_end", LinkKind.Restart), Assert.Single(batch.Commands));
        Run(m, batch);
        Run(m, model.Unlink(start));

        Assert.DoesNotContain(m.Lines("ol_wave"), l => l.EndsWith("ol_end"));
        Assert.Equal(["nickname = ol_start", "InitState = ACTIVE", "Cnd_True = no_params", "Act_ActTrig = ol_wave"],
            m.Lines("ol_start"));
    }

    [Fact]
    public void AWatchLinkDoesNotGoWithRemoveLink()
    {
        using var m = Create(Text + "\n[Trigger]\nnickname = ol_watch\nCnd_WatchTrigger = ol_end, ON\nAct_ActTrig = ol_start\n");
        var model = Model(m);
        var watch = model.Graph.Links.Single(l => l.Kind == MissionLinkKind.Watch);

        Assert.Null(model.Unlink(watch));
        Assert.Equal(GraphEditModel.WatchLink, GraphEditModel.UnlinkRefusal(watch));
    }

    [Fact]
    public void InsertTriggerPutsANewTriggerOnAStartLinkAndOneUndoTakesItBack()
    {
        using var m = Create();
        var model = Model(m);
        var link = model.Graph.Links.First(l => l.From == Node(model, "ol_start") && l.To == Node(model, "ol_wave"));
        var before = m.State();

        Run(m, model.InsertOnLink(link, GraphEditModel.InsertCondition(InsertEvent.Delay)));

        Assert.Equal("Act_ActTrig = ol_test_trigger_1", m.Lines("ol_start")[3]);
        Assert.Equal(["nickname = ol_test_trigger_1", "Cnd_Timer = 3", "Act_ActTrig = ol_wave"], m.Lines("ol_test_trigger_1"));
        m.Session.Undo();
        Assert.Equal(before, m.State());
        var restart = model.Graph.Links.First(l => l.From == Node(model, "ol_wave") && l.To == Node(model, "ol_end") &&
                                                   l.Kind == MissionLinkKind.Stop);
        Assert.Equal(GraphEditModel.StartLinkOnly, GraphEditModel.InsertRefusal(restart));
    }

    [Fact]
    public void DeleteRemovesEachSelectedTriggerInOneBatchAndKeepsTheLinesThatNameThem()
    {
        using var m = Create();
        var model = Model(m);

        var before = m.State();
        var batch = model.Delete([Node(model, "ol_wave"), Node(model, "ol_end")])!;
        Run(m, batch);

        Assert.Equal("Delete 2 triggers", batch.Label);
        Assert.False(m.Has("ol_wave"));
        Assert.False(m.Has("ol_end"));
        Assert.Contains("Act_ActTrig = ol_wave", m.Lines("ol_start"));
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void DuplicateRenameAndTheNodeMenuGiveTheirCommands()
    {
        using var m = Create();
        var model = Model(m);
        var wave = model.Graph.Index.Trigger("ol_wave")!;

        Run(m, model.Duplicate([(wave, null)]));
        Run(m, GraphEditModel.Rename(model.Graph.Index.Trigger("ol_end")!, "ol_finish"));
        Run(m, model.SetStart(wave, true));
        Run(m, model.SetRepeats(wave, true));

        Assert.True(m.Has("ol_wave_2"));
        Assert.Contains("Act_ActTrig = ol_finish", m.Lines("ol_start"));
        Assert.Equal(["system = ol_system", "nickname = ol_wave", "InitState = ACTIVE", "repeatable = true"],
            m.Lines("ol_wave").Take(4));
        Assert.Null(GraphEditModel.Rename(wave, "ol_wave"));
    }

    [Fact]
    public void CopyAndPasteGiveNewNamesToNamesInUseAndTheLinksInsideFollow()
    {
        using var m = Create();
        var model = Model(m);
        var text = model.Copy([model.Graph.Index.Trigger("ol_end")!, model.Graph.Index.Trigger("ol_wave")!]);

        var batch = model.Paste(text, out var problem)!;
        Run(m, batch);

        Assert.Null(problem);
        Assert.Equal("Paste 2 triggers", batch.Label);
        // The pasted trigger keeps its line before the nickname, and its links name the pasted copy.
        Assert.Equal(["system = ol_system", "nickname = ol_wave_2", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1",
            "Act_DeactTrig = ol_end_2", "Act_ActTrig = ol_end_2"], m.Lines("ol_wave_2"));
        Assert.True(m.Has("ol_end_2"));
        Assert.Null(model.Paste("[Mission]\n", out var none));
        Assert.Equal(GraphEditModel.NoTriggers, none);
    }

    [Fact]
    public void ADropOfALibraryItemAddsAnActionSetsAConditionOrAddsATrigger()
    {
        using var m = Create();
        var model = Model(m);
        var items = MissionLibrary.Sections(null).SelectMany(s => s.Items).ToList();
        var spawn = items.Single(i => i.Key == "Act_SpawnShip" && i.Kind == LibraryKind.Action);
        var destroyed = items.Single(i => i.Key == "Cnd_Destroyed" && i.Kind == LibraryKind.Condition);
        var delay = items.Single(i => i.Id == "afterDelay");

        Run(m, model.DropOnNode(spawn, Node(model, "ol_end")));
        Run(m, model.DropOnNode(destroyed, Node(model, "ol_wave")));
        Run(m, model.DropOnEmpty(delay));

        Assert.Equal("Act_SpawnShip = none", m.Lines("ol_end")[^1]);
        Assert.Equal("Cnd_Destroyed = none", m.Lines("ol_wave")[2]);
        Assert.Equal(["nickname = ol_test_trigger_1", "Cnd_Timer = 3"], m.Lines("ol_test_trigger_1"));
        Assert.Null(model.DropOnEmpty(spawn));
        Assert.Equal("Drop an action on a trigger.", GraphEditModel.EmptyDropRefusal(spawn));
    }

    [Fact]
    public void ARowDragMovesTheActionInItsTriggerOrToAnotherTriggerAndCtrlCopiesIt()
    {
        using var m = Create();
        var model = Model(m);
        var wave = model.Graph.Index.Trigger("ol_wave")!;
        var end = model.Graph.Index.Trigger("ol_end")!;
        var spawn = wave.Actions.First();

        Run(m, model.MoveRow(spawn, wave, null, copy: false));
        Assert.Equal("Act_SpawnShip = ol_ship_1", m.Lines("ol_wave")[^1]);

        model = Model(m);
        var lockDock = model.Graph.Index.Trigger("ol_end")!.Actions.First();
        Run(m, model.MoveRow(lockDock, model.Graph.Index.Trigger("ol_start")!, null, copy: true));
        Assert.Equal("Act_LockDock = Player, ol_station, lock", m.Lines("ol_start")[^1]);
        Assert.Contains("Act_LockDock = Player, ol_station, lock", m.Lines("ol_end"));

        model = Model(m);
        lockDock = model.Graph.Index.Trigger("ol_end")!.Actions.First();
        Run(m, model.MoveRow(lockDock, model.Graph.Index.Trigger("ol_wave")!, 3, copy: false));
        Assert.Equal("Act_LockDock = Player, ol_station, lock", m.Lines("ol_wave")[3]);
        Assert.DoesNotContain("Act_LockDock = Player, ol_station, lock", m.Lines("ol_end"));
        Assert.Null(Model(m).MoveRow(Model(m).Graph.Index.Trigger("ol_end")!.Condition!, end, null, copy: false));
    }

    [Fact]
    public void QuickAddFromAPinAddsATriggerAndLinksToItInOneBatch()
    {
        using var m = Create();
        var model = Model(m);
        var atOnce = MissionLibrary.Sections(null)[0].Items.Single(i => i.Id == "atOnce");
        var context = new QuickAddContext(Add(model, "ol_end"), null, QuickAddFilter.Triggers);
        var before = m.State();

        var batch = model.QuickAdd(context, atOnce)!;
        Run(m, batch);

        Assert.Equal(2, batch.Commands.Count);
        Assert.Equal(["nickname = ol_test_trigger_1", "Cnd_True = no_params"], m.Lines("ol_test_trigger_1"));
        Assert.Equal("Act_ActTrig = ol_test_trigger_1", m.Lines("ol_end")[^1]);
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void QuickAddShowsOnlyTheItemsThatFitItsPlace()
    {
        using var m = Create();
        var model = Model(m);
        var sections = MissionLibrary.Sections(null);
        var trigger = model.Graph.Index.Trigger("ol_end")!;

        var fromPin = MissionLibrary.Search(sections, "", i => GraphEditModel.Fits(
            new QuickAddContext(Add(model, "ol_end"), null, QuickAddFilter.Triggers), i));
        var actions = MissionLibrary.Search(sections, "spawn",
            i => GraphEditModel.Fits(new QuickAddContext(null, trigger, QuickAddFilter.Actions, "spawn"), i));
        var noTrigger = MissionLibrary.Search(sections, "",
            i => GraphEditModel.Fits(new QuickAddContext(null, null, QuickAddFilter.All), i));

        Assert.All(fromPin, i => Assert.True(i.MakesTrigger));
        Assert.Contains(actions, i => i.Key == "Act_SpawnShip");
        Assert.All(actions, i => Assert.Equal("spawn", MissionSchema.Retail.Name(i.Key)!.Group));
        Assert.DoesNotContain(noTrigger, i => i.Kind == LibraryKind.Action);
        Run(m, model.QuickAdd(new QuickAddContext(null, trigger, QuickAddFilter.Actions), actions[0]));
        Assert.Equal(actions[0].Key, m.Lines("ol_end")[^1].Split(' ')[0]);
    }
    [Fact]
    public void AResultSelectsItsNewItemAndASelectionFollowsARenameAndARowMove()
    {
        using var m = Create();
        var result = m.Apply(new AddTrigger(TestScript) { Nickname = "ol_new", Condition = LineSpec.Raw("Cnd_True", "no_params") });
        var analysis = m.Missions.Model(Script).Analysis;

        var selected = MissionWorkspace.SelectionOf(analysis, result.Select!);
        Assert.Equal(new MissionRef(Script, MissionItemKind.Trigger, analysis.Index.Trigger("ol_new")!.Section.Ref), selected);

        var end = analysis.Index.Trigger("ol_end")!.Section.Ref;
        var line = new MissionRef(Script, MissionItemKind.Row, end, Row: 3);
        var rename = m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));
        var renamed = MissionWorkspace.Followed(line, rename.Renames.Select(r => (r.From, r.To)).ToList(), []);
        Assert.Equal("ol_finish", renamed!.Section!.Id.Key);
        Assert.Equal(3, renamed.Row);
        var moved = MissionWorkspace.Followed(renamed, [], [new RowMove(T("ol_finish"), 3, 4)]);
        Assert.Equal(4, moved!.Row);
        Assert.Null(MissionWorkspace.Followed(moved, [], []));
    }
    /// <summary>The model of the test script with the acts of its graph file, as the view builds it.</summary>
    private static GraphEditModel FileModel(MissionEdits m)
    {
        var model = m.Missions.Model(Script);
        return new(MissionGraph.Build(model.Analysis, new GraphOptions(GraphLens.Logic, ShowLines: true) { Acts = model.Acts }));
    }

    [Fact]
    public void ADragOfNodesWritesTheirPlacesAndADropIntoAnotherActFrameMovesTheTrigger()
    {
        using var m = Create();
        m.Apply(new AddAct(TestScript) { Name = "The end", Members = ["ol_end"] });
        var model = FileModel(m);
        var endFrame = model.Graph.Acts.Single(f => f.Act?.Name == "The end");

        var batch = model.Moved([(Node(model, "ol_wave"), new System.Numerics.Vector2(50, 60), endFrame),
            (Node(model, "ol_start"), new System.Numerics.Vector2(-10, 0), null)])!;
        Run(m, batch);

        Assert.Equal(2, batch.Commands.Count);
        var graph = m.Missions.Graph(Script);
        Assert.Equal([new GraphNodePlace("ol_wave", 50, 60), new GraphNodePlace("ol_start", -10, 0)], graph.Nodes);
        Assert.Equal(["ol_end", "ol_wave"], graph.Act("act_2")!.Members);
        var layout = GraphLayout.Of(FileModel(m).Graph, null, GraphLayout.PlacesOf(FileModel(m).Graph, graph));
        Assert.Equal(new System.Numerics.Vector2(50, 60), layout.PositionOf(Node(FileModel(m), "ol_wave")));
    }

    [Fact]
    public void NewActFromSelectionAndDeleteOfAnActFrame()
    {
        using var m = Create();
        var model = FileModel(m);

        Run(m, model.NewAct([Node(model, "ol_end")]));
        model = FileModel(m);
        Assert.Equal(["Act 1", "Act 2"], model.Graph.Acts.Where(f => f.Kind == GraphFrameKind.Act).Select(f => f.Title));
        var second = model.Graph.Acts.First(f => f.Act?.Number == 2);
        Run(m, model.Delete([second]));

        Assert.Equal(["act_1"], m.Missions.Graph(Script).Acts.Select(a => a.Nickname));
        Assert.True(m.Has("ol_end"));
    }

    [Fact]
    public void ADropOnAnEmptyPlaceAndCtrlDPlaceTheNewNode()
    {
        using var m = Create();
        var model = FileModel(m);
        var item = MissionLibrary.Sections(null)[0].Items.Single(i => i.Id == "atOnce");

        Run(m, model.DropOnEmpty(item, new GraphPoint(400, 500), model.ActRefOf(model.Graph.ActsOf().Acts[0])));
        Run(m, FileModel(m).Duplicate([(FileModel(m).Graph.Index.Trigger("ol_end")!, new GraphPoint(10, 900))]));

        var graph = m.Missions.Graph(Script);
        Assert.Equal([new GraphNodePlace("ol_test_trigger_1", 400, 500), new GraphNodePlace("ol_end_2", 10, 900)], graph.Nodes);
        Assert.Contains("ol_test_trigger_1", graph.Act("act_1")!.Members);
    }

    /// <summary>Four triggers on a main path with three systems: act 1 has ol_a, act 2 has ol_b, act 3 has ol_c and
    /// ol_d. Trigger ol_a starts ol_b, ol_c and ol_d.</summary>
    private const string ThreeActs = """
        [Mission]
        npc_ship_file = missions\ol_test\npcships.ini

        [Trigger]
        nickname = ol_a
        system = sys_a
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = ol_b
        Act_ActTrig = ol_c
        Act_ActTrig = ol_d

        [Trigger]
        nickname = ol_b
        system = sys_b
        Cnd_True = no_params
        Act_ActTrig = ol_c

        [Trigger]
        nickname = ol_c
        system = sys_c
        Cnd_True = no_params
        Act_ActTrig = ol_d

        [Trigger]
        nickname = ol_d
        system = sys_c
        Cnd_True = no_params
        Act_ChangeState = SUCCEED
        """;

    /// <summary>The graph of a script with the acts of its graph file, and the frames of some acts collapsed, by act
    /// number.</summary>
    private static MissionGraph Collapsed(MissionEdits m, params int[] acts)
    {
        var model = m.Missions.Model(Script);
        var open = MissionGraph.Build(model.Analysis, new GraphOptions(GraphLens.Logic) { Acts = model.Acts });
        var collapse = open.Acts.Where(f => f.Act != null && acts.Contains(f.Act.Number)).ToDictionary(f => f.Key, _ => true);
        return MissionGraph.Build(model.Analysis, new GraphOptions(GraphLens.Logic, Collapse: collapse) { Acts = model.Acts });
    }

    [Fact]
    public void InsertTriggerOnALinkIntoACollapsedFrameKeepsTheStartOfEachTarget()
    {
        using var m = Create(ThreeActs);
        var graph = Collapsed(m, 3);
        var link = graph.Links.Single(l => l.From.Trigger?.Nickname == "ol_a" && l.To.Kind == GraphNodeKind.Act);
        Assert.Equal(["ol_c", "ol_d"], link.Sources.Select(s => s.TargetName).Order());

        var batch = new GraphEditModel(graph).InsertOnLink(link, GraphEditModel.InsertCondition(InsertEvent.Delay));
        Run(m, batch);

        // The source starts the new trigger once, and the new trigger starts both targets of the link.
        Assert.Equal(["nickname = ol_a", "system = sys_a", "InitState = ACTIVE", "Cnd_True = no_params", "Act_ActTrig = ol_b",
            "Act_ActTrig = ol_test_trigger_1"], m.Lines("ol_a"));
        Assert.Equal(["nickname = ol_test_trigger_1", "Cnd_Timer = 3", "Act_ActTrig = ol_c", "Act_ActTrig = ol_d"],
            m.Lines("ol_test_trigger_1"));
    }

    [Fact]
    public void InsertTriggerRefusesALinkOfSeveralTriggersToSeveralTriggers()
    {
        // Act 4 has ol_b1 and ol_b2, and each of them starts its own trigger of act 5.
        using var m = Create("[Mission]\n\n" +
                             "[Trigger]\nnickname = ol_a\nsystem = sys_a\nInitState = ACTIVE\nCnd_True = no_params\n" +
                             "Act_ActTrig = ol_b1\nAct_ActTrig = ol_b2\n\n" +
                             "[Trigger]\nnickname = ol_b1\nsystem = sys_b\nCnd_True = no_params\nAct_ActTrig = ol_c1\n\n" +
                             "[Trigger]\nnickname = ol_b2\nsystem = sys_b\nCnd_True = no_params\nAct_ActTrig = ol_c2\n\n" +
                             "[Trigger]\nnickname = ol_c1\nsystem = sys_c\nCnd_True = no_params\n\n" +
                             "[Trigger]\nnickname = ol_c2\nsystem = sys_c\nCnd_True = no_params\n");
        m.Apply(new AddAct(TestScript) { Name = "B", Members = ["ol_b1", "ol_b2"] });
        m.Apply(new AddAct(TestScript) { Name = "C", Members = ["ol_c1", "ol_c2"] });
        var graph = Collapsed(m, 4, 5);
        var link = graph.Links.First(l => l.Sources.Select(s => s.Source).Distinct().Count() > 1 &&
                                          l.Sources.Select(s => s.TargetName).Distinct().Count() > 1);

        Assert.Equal(GraphEditModel.SeveralLinks, GraphEditModel.InsertRefusal(link));
        Assert.Null(new GraphEditModel(graph).InsertOnLink(link, GraphEditModel.InsertCondition(InsertEvent.AtOnce)));
        Assert.Equal("Cnd_CommComplete", GraphEditModel.InsertCondition(InsertEvent.CommLine, commLine: "ol_line").Key);
    }

    [Fact]
    public void DeleteOfAnActFrameAndItsTriggerRemovesThatActOnTheFirstActEdit()
    {
        using var m = Create(ThreeActs);
        var graph = MissionGraph.Build(m.Missions.Model(Script).Analysis, new GraphOptions(GraphLens.Logic));
        var second = graph.Acts.Single(f => f.Act?.Number == 2);

        Run(m, new GraphEditModel(graph).Delete([graph.TriggerNode(graph.Index.Trigger("ol_b")!)!, second]));

        // The acts stay proposed until the first act edit, so the act numbers of the selection are the numbers before
        // the delete.
        Assert.Equal(["act_1", "act_3"], m.Missions.Graph(Script).Acts.Select(a => a.Nickname));
        Assert.Equal(["ol_c", "ol_d"], m.Missions.Graph(Script).Acts[1].Members);
        Assert.False(m.Has("ol_b"));
    }

    [Fact]
    public void CtrlDDuplicatesEachSelectedTriggerInOneBatch()
    {
        using var m = Create();
        var model = Model(m);

        var batch = model.Duplicate([(model.Graph.Index.Trigger("ol_wave")!, null), (model.Graph.Index.Trigger("ol_end")!, null)]);
        Run(m, batch);

        Assert.Equal("Duplicate 2 triggers", batch!.Label);
        Assert.True(m.Has("ol_wave_2"));
        Assert.True(m.Has("ol_end_2"));
        Assert.Null(model.Duplicate([]));
    }

    [Fact]
    public void APasteGivesEachTriggerOfARepeatedNameAFreeNameAndAReadProblemItsLine()
    {
        using var m = Create();
        var model = Model(m);

        Run(m, model.Paste("[Trigger]\nnickname = ol_x\nCnd_True = no_params\nAct_ActTrig = ol_x\n\n" +
                           "[Trigger]\nnickname = ol_x\nCnd_True = no_params\n", out var problem));
        var bad = model.Paste("[Trigger]\nnickname = ol_y\nno value here\n", out var badProblem);

        Assert.Null(problem);
        // A value inside the pasted set names the first trigger of the name, as the game finds the first trigger.
        Assert.Equal(["nickname = ol_x", "Cnd_True = no_params", "Act_ActTrig = ol_x"], m.Lines("ol_x"));
        Assert.Equal(["nickname = ol_x_2", "Cnd_True = no_params"], m.Lines("ol_x_2"));
        Assert.Null(bad);
        Assert.StartsWith("The editor cannot read the text of the clipboard.", badProblem);
    }

    [Fact]
    public void AfterAnEditEachNodeThatStaysKeepsItsPlace()
    {
        using var m = Create();
        var before = Model(m).Graph;
        var layout = GraphLayout.Of(before);

        // A new trigger between ol_end and ol_wave changes the layers of the automatic layout.
        Run(m, new MissionBatch("Add", [
            new AddTrigger(TestScript) { Nickname = "ol_new", Condition = LineSpec.Raw("Cnd_True", "no_params") },
            new LinkTriggers(T("ol_end"), "ol_new", LinkKind.Start), new LinkTriggers(T("ol_new"), "ol_wave", LinkKind.Start)]));
        var after = Model(m).Graph;
        var kept = GraphLayout.Kept(before, layout, after);
        var moved = GraphLayout.Of(after, null, kept);

        foreach (var name in new[] { "ol_start", "ol_wave", "ol_end" })
        {
            Assert.Equal(layout.PositionOf(before.TriggerNode(before.Index.Trigger(name)!)!),
                moved.PositionOf(after.TriggerNode(after.Index.Trigger(name)!)!));
        }

        Assert.False(kept.ContainsKey(after.TriggerNode(after.Index.Trigger("ol_new")!)!.Key));
    }
}

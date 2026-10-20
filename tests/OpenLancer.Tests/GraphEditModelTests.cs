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

        Run(m, model.Duplicate(wave));
        Run(m, model.Rename(model.Graph.Index.Trigger("ol_end")!, "ol_finish"));
        Run(m, model.SetStart(wave, true));
        Run(m, model.SetRepeats(wave, true));

        Assert.True(m.Has("ol_wave_2"));
        Assert.Contains("Act_ActTrig = ol_finish", m.Lines("ol_start"));
        Assert.Equal(["system = ol_system", "nickname = ol_wave", "InitState = ACTIVE", "repeatable = true"],
            m.Lines("ol_wave").Take(4));
        Assert.Null(model.Rename(wave, "ol_wave"));
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
}

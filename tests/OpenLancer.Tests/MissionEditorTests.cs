using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;
using Xunit;
using OpenLancer.Data.Ini;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The mission commands of section 4.9 of the M6b design: the lines of W after each command, the change report, and
/// each refusal with its code and its text. The script uses invented names.
/// </summary>
public sealed class MissionEditorTests
{
    [Fact]
    public void AddTriggerWritesItsLinesInOrderAtTheEndWithAMadeName()
    {
        using var m = Create();

        var result = m.Apply(new AddTrigger(TestScript)
        {
            System = "ol_system",
            StartsWithMission = true,
            Condition = LineSpec.Of("Cnd_Timer", ("seconds", 5)),
            Repeats = true,
            Actions = [LineSpec.Of("Act_ActTrig", ("trigger", "ol_end"))],
        });

        Assert.Equal(["nickname = ol_test_trigger_1", "system = ol_system", "InitState = ACTIVE", "Cnd_Timer = 5",
            "repeatable = true", "Act_ActTrig = ol_end"], m.Lines("ol_test_trigger_1"));
        Assert.Equal([T("ol_test_trigger_1").ToString()], result.Adds);
        Assert.Equal(T("ol_test_trigger_1").ToString(), result.Select);
        var inverse = Assert.IsType<CommandGroup>(result.Inverse);
        Assert.Equal(new RemoveTrigger(T("ol_test_trigger_1")), Assert.Single(inverse.Commands));
        Assert.Equal([T("ol_test_trigger_1").ToString()], inverse.ExpectBodies.Keys);
        Assert.Equal("Add trigger", result.UndoLabel);
        Assert.Equal("Trigger", m.Session.OpenDocument(Script)!.Working[^1].Name);
    }

    [Fact]
    public void AddTriggerRefusesAWrongKeyAndANameThatIsTakenOrNotValid()
    {
        using var m = Create();

        Assert.Equal("Act_ActTrig is an action. Use \"Add action\".",
            m.Refuse(new AddTrigger(TestScript) { Condition = LineSpec.Of("Act_ActTrig", ("trigger", "x")) },
                MissionErrors.WrongPlace).Message);
        Assert.Equal("Cnd_True is a condition. A trigger has one condition. Use \"Set condition\".",
            m.Refuse(new AddTrigger(TestScript) { Actions = [LineSpec.Raw("Cnd_True", "no_params")] },
                MissionErrors.WrongPlace).Message);
        Assert.Equal("'Act_Fly' is not an action or a condition of the game. Pick one from the list.",
            m.Refuse(new AddTrigger(TestScript) { Actions = [LineSpec.Raw("Act_Fly")] }, MissionErrors.UnknownKey).Message);
        var taken = m.Refuse(new AddTrigger(TestScript) { Nickname = "OL_WAVE" }, MissionErrors.NameTaken);
        Assert.Equal($"Script {Script} already has a trigger 'OL_WAVE'. Pick another name.", taken.Message);
        Assert.Equal(EditErrorKind.NameTaken, taken.Kind);
        Assert.Equal("'12' is not a valid name. Use letters, digits, '_' and '-', with at most 47 characters. A name " +
                     "cannot be a number.", m.Refuse(new AddTrigger(TestScript) { Nickname = "12" }, MissionErrors.NameInvalid).Message);
        var error = m.Refuse(new AddTrigger(TestScript)
        {
            Actions = [LineSpec.Of("Act_ActTrig", ("trigger", "ol_end")), LineSpec.Raw("Act_Fly")],
        }, MissionErrors.UnknownKey);
        Assert.Equal(0, error.Index);
    }

    [Fact]
    public void ATriggerNameWithTheGameIdOfAnotherTriggerRefuses()
    {
        var (first, second) = DataListsTests.FlHashPair();
        using var m = Create(Text + $"\n[Trigger]\nnickname = {first}\nCnd_True = no_params\n");

        var error = m.Refuse(new AddTrigger(TestScript) { Nickname = second }, MissionErrors.NameIdTaken);

        Assert.Equal($"The game gives '{second}' the same id as trigger '{first}'. The game then keeps only the first of " +
                     "the two triggers. Pick another name.", error.Message);
    }

    [Fact]
    public void RemoveTriggerKeepsTheLinesThatNameItAsMissingOrRemovesThem()
    {
        using var m = Create();

        var kept = m.Apply(new RemoveTrigger(T("ol_end")));

        Assert.False(m.Has("ol_end"));
        Assert.Contains("Act_ActTrig = ol_end", m.Lines("ol_start")!);
        Assert.Equal([("Act_ActTrig", true), ("Act_DeactTrig", true), ("Act_ActTrig", true)],
            kept.Missing.Select(x => (x.Key, x.BlocksSave)));
        var delete = Assert.Single(kept.Deletes);
        Assert.Equal(T("ol_end").ToString(), delete.Ref);
        m.Session.Undo();

        var removed = m.Apply(new RemoveTrigger(T("ol_end")) { References = ReferencesMode.Remove });

        Assert.Empty(removed.Missing);
        Assert.Equal(["nickname = ol_start", "InitState = ACTIVE", "Cnd_True = no_params", "Act_ActTrig = ol_wave"],
            m.Lines("ol_start"));
        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1"],
            m.Lines("ol_wave"));
    }

    [Fact]
    public void RenameTriggerChangesEachLineThatNamesItAndReportsTheRename()
    {
        using var m = Create();
        var changes = new List<MissionScriptsChange>();
        m.Missions.ScriptsChanged += changes.Add;

        var result = m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));

        Assert.Equal(["nickname = ol_finish", "Cnd_Destroyed = ol_ship_1, -1", "Act_LockDock = Player, ol_station, lock",
            "Act_ChangeState = SUCCEED"], m.Lines("ol_finish"));
        Assert.Equal(["Act_ActTrig = ol_wave", "Act_ActTrig = ol_finish"], m.Lines("ol_start")!.Skip(3));
        Assert.Equal(["Act_SpawnShip = ol_ship_1", "Act_DeactTrig = ol_finish", "Act_ActTrig = ol_finish"],
            m.Lines("ol_wave")!.Skip(3));
        var rename = Assert.Single(result.Renames);
        Assert.Equal((T("ol_end").ToString(), T("ol_finish").ToString()), (rename.From, rename.To));
        Assert.Equal(3, result.Changes.Count(c => c.StartsWith("line:", StringComparison.Ordinal)));
        var change = Assert.Single(changes);
        Assert.Equal((T("ol_end"), T("ol_finish")), Assert.Single(change.Renames));
        Assert.Equal(2, change.Versions[Script]);
    }

    [Fact]
    public void RenameTriggerWithoutReferencesLeavesMissingLines()
    {
        using var m = Create();

        var result = m.Apply(new RenameTrigger(T("ol_end"), "ol_finish") { ChangeReferences = false });

        Assert.Contains("Act_ActTrig = ol_end", m.Lines("ol_start")!);
        Assert.Equal(3, result.Missing.Count);
    }

    [Fact]
    public void DuplicateTriggerCopiesItToTheEnd()
    {
        using var m = Create();

        var result = m.Apply(new DuplicateTrigger(T("ol_wave")) { NewName = "ol_wave_2" });

        Assert.Equal(["system = ol_system", "nickname = ol_wave_2", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1",
            "Act_DeactTrig = ol_end", "Act_ActTrig = ol_end"], m.Lines("ol_wave_2"));
        Assert.Equal(T("ol_wave_2").ToString(), result.Select);
        Assert.Equal(new RemoveTrigger(T("ol_wave_2")), Assert.Single(((CommandGroup)result.Inverse!).Commands));
    }

    [Fact]
    public void TheStateCommandsWriteTheStateLinesAfterTheNicknameLine()
    {
        using var m = Create();

        m.Apply(new SetTriggerStart(T("ol_wave"), true));
        m.Apply(new SetTriggerRepeats(T("ol_wave"), true));
        m.Apply(new SetTriggerSystem(T("ol_wave")) { System = "ol_system_2" });

        Assert.Equal(["system = ol_system", "nickname = ol_wave", "InitState = ACTIVE", "repeatable = true",
            "system = ol_system_2", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1", "Act_DeactTrig = ol_end",
            "Act_ActTrig = ol_end"], m.Lines("ol_wave"));

        m.Apply(new SetTriggerStart(T("ol_wave"), false));
        m.Apply(new SetTriggerRepeats(T("ol_wave"), false));
        m.Apply(new SetTriggerSystem(T("ol_wave")));

        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1",
            "Act_DeactTrig = ol_end", "Act_ActTrig = ol_end"], m.Lines("ol_wave"));
        Assert.Equal(EditStatus.NoChange, m.Missions.Apply(new SetTriggerStart(T("ol_wave"), false)).Status);
    }

    [Fact]
    public void SetConditionReplacesTheEffectiveConditionOrAddsOne()
    {
        using var m = Create(Text + "\n[Trigger]\nnickname = ol_bare\nAct_ActTrig = ol_end\n");

        var replaced = m.Apply(new SetCondition(T("ol_wave"), LineSpec.Of("Cnd_Destroyed", ("target", "ol_ship_1"))));
        m.Apply(new SetCondition(T("ol_bare"), LineSpec.Of("Cnd_True")));

        Assert.Equal("Cnd_Destroyed = ol_ship_1", m.Lines("ol_wave")![2]);
        Assert.Equal(new SetCondition(T("ol_wave"), LineSpec.Raw("Cnd_Timer", "3")),
            Assert.Single(((CommandGroup)replaced.Inverse!).Commands));
        Assert.Equal(["nickname = ol_bare", "Cnd_True =", "Act_ActTrig = ol_end"], m.Lines("ol_bare"));
        m.Refuse(new SetCondition(T("ol_wave"), LineSpec.Raw("Act_ActTrig", "x")), MissionErrors.WrongPlace);
    }

    [Fact]
    public void SetConditionValueSetsOneNamedValueAndFillsAGap()
    {
        using var m = Create();

        m.Apply(new SetConditionValue(T("ol_end"), "mode", "explode"));
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 7.5));

        Assert.Equal("Cnd_Destroyed = ol_ship_1, -1, EXPLODE", m.Lines("ol_end")![1]);
        Assert.Equal("Cnd_Timer = 7.5", m.Lines("ol_wave")![2]);
        Assert.Equal("Cnd_Destroyed has no value 'speed'. Use one of these values: target, count, mode, killer.",
            m.Refuse(new SetConditionValue(T("ol_end"), "speed", 1), MissionErrors.NoSuchValue).Message);
    }

    [Fact]
    public void AddActionGoesAfterTheConditionAtTheEndOrBeforeARow()
    {
        using var m = Create();

        var result = m.Apply(new AddAction(T("ol_start"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_x"))) { Before = 3 });
        m.Apply(new AddAction(T("ol_start"), LineSpec.Of("Act_ChangeState", ("state", "fail"))));

        Assert.Equal(["nickname = ol_start", "InitState = ACTIVE", "Cnd_True = no_params", "Act_ActTrig = ol_x",
            "Act_ActTrig = ol_wave", "Act_ActTrig = ol_end", "Act_ChangeState = FAIL"], m.Lines("ol_start"));
        Assert.Equal([Row("ol_start", 3).Reference], result.Adds);
        Assert.Contains(result.RowMoves, r => r.From == 3 && r.To == 4);
        Assert.Equal("An action cannot go before the nickname line or before the condition, because the game ignores it " +
                     "there. Pick a place after the condition.",
            m.Refuse(new AddAction(T("ol_start"), LineSpec.Raw("Act_ActTrig", "x")) { Before = 2 }, MissionErrors.BadPlace).Message);
    }

    [Fact]
    public void RemoveReorderAndSetActionChangeOnlyActionLines()
    {
        using var m = Create();

        m.Apply(new RemoveAction(Row("ol_wave", 4) with { ExpectKey = "Act_DeactTrig" }));
        m.Apply(new ReorderAction(Row("ol_wave", 4)) { Before = 3 });
        m.Apply(new SetActionLine(Row("ol_wave", 4), LineSpec.Of("Act_SpawnFormation", ("formation", "ol_form"))));

        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 3", "Act_ActTrig = ol_end",
            "Act_SpawnFormation = ol_form"], m.Lines("ol_wave"));
        Assert.Equal("Line 2 of Trigger 'ol_wave' is not an action. Pick an action line.",
            m.Refuse(new RemoveAction(Row("ol_wave", 2)), MissionErrors.NotAnAction).Message);
        Assert.Equal($"Line 3 of Trigger 'ol_wave' is not 'Act_LockDock' now. The script changed after you read it. Read the " +
                     "item again, then try again.",
            m.Refuse(new RemoveAction(Row("ol_wave", 3) with { ExpectKey = "Act_LockDock" }), MissionErrors.RowChanged).Message);
        Assert.Equal("Trigger 'ol_wave' has no line 9. Read the item again, then try again.",
            m.Refuse(new RemoveAction(Row("ol_wave", 9)), MissionErrors.RowNotFound).Message);
    }

    [Fact]
    public void SetActionValueSetsAPositionAndAddsTheGapWithTheNoneWord()
    {
        using var m = Create();

        m.Apply(new SetActionValue(Row("ol_wave", 3), "position", MissionValue.Of(1, 2.5, -3)));

        Assert.Equal("Act_SpawnShip = ol_ship_1, no_ol, 1, 2.5, -3", m.Lines("ol_wave")![3]);
        Assert.Equal("'[1, 2]' is not a position. Position needs three numbers.",
            m.Refuse(new SetActionValue(Row("ol_wave", 3), "position", MissionValue.Of(1, 2)), MissionErrors.ValueKind).Message);
    }

    [Fact]
    public void LinkTriggersWritesTheLinesOfTheKind()
    {
        using var m = Create();

        m.Apply(new LinkTriggers(T("ol_end"), "ol_wave", LinkKind.Start));
        m.Apply(new LinkTriggers(T("ol_end"), "ol_start", LinkKind.Restart) { Before = 3 });

        Assert.Equal(["nickname = ol_end", "Cnd_Destroyed = ol_ship_1, -1", "Act_LockDock = Player, ol_station, lock",
            "Act_DeactTrig = ol_start", "Act_ActTrig = ol_start", "Act_ChangeState = SUCCEED", "Act_ActTrig = ol_wave"],
            m.Lines("ol_end"));
    }

    [Fact]
    public void UnlinkTriggersRemovesTheLinesOfTheLink()
    {
        using var m = Create();

        m.Apply(new UnlinkTriggers(T("ol_wave"), "ol_end", LinkKind.Restart));

        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1"],
            m.Lines("ol_wave"));
        Assert.Equal("Trigger 'ol_wave' has no stop link to 'ol_end'. Read the trigger again, then try again.",
            m.Refuse(new UnlinkTriggers(T("ol_wave"), "ol_end", LinkKind.Stop), MissionErrors.LinkNotFound).Message);
    }

    [Fact]
    public void RetargetLinkGivesALinkAnotherTrigger()
    {
        using var m = Create();

        var result = m.Apply(new RetargetLink(Row("ol_start", 4), "ol_wave"));

        Assert.Equal("Act_ActTrig = ol_wave", m.Lines("ol_start")![4]);
        Assert.Equal([Row("ol_start", 4).Reference], result.Changes);
        Assert.Equal("Line 2 of Trigger 'ol_start' names no trigger. Pick a line that starts, stops or watches a trigger.",
            m.Refuse(new RetargetLink(Row("ol_start", 2), "x"), MissionErrors.NotALink).Message);
    }

    /// <summary>The example of section 4.13 of the M6b design.</summary>
    [Fact]
    public void InsertTriggerOnLinkPutsANewTriggerBetweenTheTwoTriggers()
    {
        using var m = Create();

        var result = m.Apply(new InsertTriggerOnLink(Row("ol_start", 3) with { ExpectKey = "Act_ActTrig" },
            LineSpec.Of("Cnd_Timer", ("seconds", 3))) { Nickname = "ol_launch_delay" });

        Assert.Equal("Act_ActTrig = ol_launch_delay", m.Lines("ol_start")![3]);
        Assert.Equal(["nickname = ol_launch_delay", "Cnd_Timer = 3", "Act_ActTrig = ol_wave"], m.Lines("ol_launch_delay"));
        Assert.Equal([T("ol_launch_delay").ToString()], result.Adds);
        Assert.Equal([Row("ol_start", 3).Reference], result.Changes);
        // The group of the command joins the group of the batch, as in the result example of the design.
        var inverse = (CommandGroup)result.Inverse!;
        Assert.Equal(2, inverse.Commands.Count);
        Assert.Equal(new RetargetLink(Row("ol_start", 3) with { ExpectKey = "Act_ActTrig" }, "ol_wave"), inverse.Commands[0]);
        Assert.Equal(new RemoveTrigger(T("ol_launch_delay")), inverse.Commands[1]);
        Assert.Equal([T("ol_launch_delay").ToString(), T("ol_start").ToString()], inverse.ExpectBodies.Keys.Order());
    }

    [Fact]
    public void TheGenericLineCommandsChangeAnyLineExceptTheKeyLine()
    {
        using var m = Create();
        var form = Item("MsnFormation", "ol_form");

        m.Apply(new InsertLine(form, LineSpec.Of("label", ("label", "ol_group"))) { Before = 1 });
        m.Apply(new SetLine(new RowRef(form, 2), LineSpec.Of("position", ("position", MissionValue.Of(5, 6, 7)))));
        m.Apply(new SetLineValue(new RowRef(form, 3), "ol_shape_2") { Index = 0 });
        m.Apply(new MoveLine(new RowRef(form, 1)));
        m.Apply(new RemoveLine(new RowRef(form, 3)));

        Assert.Equal(["nickname = ol_form", "position = 5, 6, 7", "formation = ol_shape_2", "label = ol_group"], m.Lines(form));
        Assert.Equal("nickname is the name of the item. Use Rename to change it.",
            m.Refuse(new RemoveLine(new RowRef(form, 0)), MissionErrors.KeyLine).Message);
        m.Refuse(new InsertLine(form, LineSpec.Raw("nickname", "x")), MissionErrors.KeyLine);
        Assert.Equal("The game does not read 'speed' in a [MsnFormation] section. Pick a field of the page.",
            m.Refuse(new InsertLine(form, LineSpec.Raw("speed", "1")), MissionErrors.NotAKey).Message);
    }

    [Fact]
    public void TheSectionCommandsAddRemoveRestoreAndReplaceSections()
    {
        using var m = Create();
        var form = Item("MsnFormation", "ol_form");

        var added = m.Apply(new AddSection(TestScript, "MsnSolar", [LineSpec.Of("system", ("system", "ol_system"))]));
        var removed = m.Apply(new RemoveSection(form));
        Assert.False(m.Has(form));
        var restore = Assert.IsType<RestoreSection>(Assert.Single(((CommandGroup)removed.Inverse!).Commands));
        m.Apply(restore);
        m.Apply(new ReplaceSectionBody(form, [new RawEntry("nickname", ["ol_form"]), new RawEntry("formation", ["ol_shape"])]));

        Assert.Equal(["nickname = ol_test_solar_1", "system = ol_system"], m.Lines(Item("MsnSolar", "ol_test_solar_1")));
        Assert.Equal(Item("MsnSolar", "ol_test_solar_1").ToString(), Assert.Single(added.Adds));
        Assert.Equal(["nickname = ol_form", "formation = ol_shape"], m.Lines(form));
        m.Refuse(new ReplaceSectionBody(form, [new RawEntry("nickname", ["ol_other"])]), MissionErrors.KeyLine);
        Assert.Equal("The active mod adds [MsnSolar] nickname = ol_test_solar_1, so the game data has no lines for it. " +
                     "Remove the section instead.",
            m.Refuse(new RevertSection(Item("MsnSolar", "ol_test_solar_1")), MissionErrors.NotInBase).Message);
        m.Apply(new RevertSection(form));
        Assert.Equal(["nickname = ol_form", "position = 0, 0, 0", "formation = ol_shape", "ship = ol_ship_2"], m.Lines(form));
    }

    [Fact]
    public void ABodyGuardOfAGroupRefusesWhenTheSectionChanged()
    {
        using var m = Create();
        var result = m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 9));
        m.Apply(new SetConditionValue(T("ol_end"), "count", 2));
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 8));

        var error = m.Refuse(result.Inverse!, MissionErrors.SectionChanged);

        Assert.Equal(EditErrorKind.StaleRevision, error.Kind);
        Assert.Equal("[Trigger] nickname = ol_wave changed after the edit that this inverse reverts. Read the item again, " +
                     "then change it back with the mission commands.", error.Message);
    }

    [Fact]
    public void CreateScriptMakesAWholeFileThatASaveWritesAndDeleteScriptRemovesIt()
    {
        using var m = Create();
        const string path = @"DATA\MISSIONS\OL_NEW\ol_new.ini";

        var created = m.Apply(new CreateScript(path) { ShipBuildFile = @"DATA\MISSIONS\OL_NEW\npcships.ini" });

        Assert.Equal(["npc_ship_file = MISSIONS\\OL_NEW\\npcships.ini"], m.Lines(Item("Mission", null, path)));
        Assert.Equal([new ScriptRef(path).ToString(), new ScriptRef(@"DATA\MISSIONS\OL_NEW\npcships.ini").ToString()],
            created.Adds);
        Assert.Contains(path, m.Missions.ScriptPaths());
        var taken = m.Refuse(new CreateScript(path), MissionErrors.PathInUse);
        Assert.Equal("The editor cannot make a script at DATA\\MISSIONS\\OL_NEW\\ol_new.ini, because the edit view already " +
                     "has a file at this path. Pick another path.", taken.Message);
        // MCP1 section 7.5 gives the kind name_taken to a path in use. A path outside the missions is not valid.
        Assert.Equal(EditErrorKind.NameTaken, taken.Kind);
        Assert.Equal(EditErrorKind.InvalidArgument, m.Refuse(new CreateScript(@"DATA\SHIPS\x.ini"), MissionErrors.BadPath).Kind);
        var saved = m.Session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Contains("[Mission]", m.Ws.ReadModFile(@"files\DATA\MISSIONS\OL_NEW\ol_new.ini"));

        m.Apply(new DeleteScript(new ScriptRef(path)));
        Assert.DoesNotContain(path, m.Missions.ScriptPaths());
        Assert.True(m.Session.Save().Saved);
        Assert.False(File.Exists(Path.Combine(m.Ws.ModFolder("act"), @"files\DATA\MISSIONS\OL_NEW\ol_new.ini")));
        Assert.False(File.Exists(Path.Combine(m.Ws.ModFolder("act"), @"files\DATA\MISSIONS\OL_NEW\npcships.ini")));
        Assert.Equal($"Script {Script} is not a whole file of the active mod. The editor cannot delete a script of the game " +
                     "data or of another mod.", m.Refuse(new DeleteScript(TestScript), MissionErrors.NotWholeFile).Message,
            ignoreCase: true);
    }

    [Fact]
    public void SetLineTextWritesTheTextAndThePlaceholder()
    {
        using var m = Create();

        var result = m.Apply(new SetLineText(Row("ol_end", 3), "text", "The station is safe."));

        Assert.Equal("Act_ChangeState = SUCCEED, $str:ol_test.ol_end.act_changestate", m.Lines("ol_end")![3]);
        Assert.Equal("The station is safe.", m.Session.Text(new StringPlaceholder(StringKind.String, "ol_test.ol_end.act_changestate")));
        m.Apply(result.Inverse!);
        Assert.Equal("Act_ChangeState = SUCCEED", m.Lines("ol_end")![3]);
        Assert.Null(m.Session.Text(new StringPlaceholder(StringKind.String, "ol_test.ol_end.act_changestate")));
        Assert.Equal("Result is not a text id, so it cannot have text of the mod. Pick a value that is a text id.",
            m.Refuse(new SetLineText(Row("ol_end", 3), "state", "x"), MissionErrors.NotTextId).Message);
    }

    [Fact]
    public void ScriptsChangedComesForAnEditAnUndoARedoAndARestore()
    {
        using var m = Create();
        var changes = new List<MissionScriptsChange>();
        m.Missions.ScriptsChanged += changes.Add;

        m.Apply(new RenameTrigger(T("ol_wave"), "ol_wave_a"));
        m.Session.Undo();
        m.Session.Redo();
        m.Missions.Preview(new MissionBatch("Preview", [new RemoveTrigger(T("ol_end"))]));

        Assert.Equal([2L, 3L, 4L], changes.Select(c => c.Versions[Script]));
        Assert.Equal((T("ol_wave"), T("ol_wave_a")), Assert.Single(changes[0].Renames));
        Assert.Equal((T("ol_wave_a"), T("ol_wave")), Assert.Single(changes[1].Renames));
        Assert.Equal(4, m.Missions.Version(Script));

        Assert.Empty(m.Session.WriteRecovery());
        m.Session.Abandon();
        var again = m.Ws.Session("act");
        var restored = new List<MissionScriptsChange>();
        again.Missions.ScriptsChanged += restored.Add;
        Assert.Empty(again.RestoreRecovery());
        Assert.Equal(2, Assert.Single(restored).Versions[Script]);
        Assert.Contains("nickname = ol_wave_a", Lines(again, "ol_wave_a"));
    }

    private static string[] Lines(EditSession session, string trigger)
    {
        var document = session.OpenDocument(Script)!;
        return document.Working[MissionDraftLines.TriggerIndex(document, trigger)].Select(MissionEdits.Line).ToArray();
    }

    [Fact]
    public void ANewSectionGoesToTheEndOfAPatchedFileSoThePatchMovesNoSection()
    {
        using var m = Create();

        var added = m.Apply(new AddSection(TestScript, "MsnSolar", [LineSpec.Of("system", ("system", "ol_system"))])
        {
            Before = Item("Trigger", "ol_wave"),
        });
        var restored = m.Apply(new RestoreSection(TestScript, 0, new RawSection("ObjList", [new RawEntry("nickname", ["ol_new_orders"])])));
        // A ship keeps its place, because the writer gives it ol.before.
        m.Apply(new RestoreSection(TestScript, 3, new RawSection("MsnShip",
            [new RawEntry("nickname", ["ol_new_ship"]), new RawEntry("npc", ["ol_npc_1"])])));
        var document = m.Session.OpenDocument(Script)!;
        var saved = m.Session.Save();

        Assert.Equal(MissionDraft.NewPlaceNote("MsnSolar"), Assert.Single(added.Notes));
        Assert.Equal(MissionDraft.NewPlaceNote("ObjList"), Assert.Single(restored.Notes));
        Assert.Equal(["MsnSolar", "ObjList"], document.Working.TakeLast(2).Select(s => s.Name));
        Assert.Equal("MsnShip", document.Working[3].Name);
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        var patch = m.Ws.ReadModPatch(Script);
        Assert.DoesNotContain("ol.delete", patch);
        Assert.Equal(3, patch.Split("ol.new = true").Length - 1);
        Assert.Contains("ol.before = MsnShip, ol_ship_1", patch);
    }

    [Fact]
    public void ANewBuildMakesTheMissingShipBuildFileAndTheInverseDeletesItAgain()
    {
        const string newShips = @"DATA\MISSIONS\OL_TEST\ol_new_ships.ini";
        using var m = Create(text: MissionEdits.Text.Replace(@"missions\ol_test\npcships.ini", @"missions\ol_test\ol_new_ships.ini"));
        var before = m.State();

        var result = m.Apply(new AddMissionShip(TestScript) { Nickname = "ol_ship_9", NewBuild = new ShipBuildSpec("ol_arch", "ol_loadout") });

        Assert.True(m.Session.OpenDocument(newShips)!.State.Exists);
        Assert.Equal("NPCShipArch", Assert.Single(m.Session.OpenDocument(newShips)!.Working).Name);
        var inverse = m.Apply(result.Inverse!);
        Assert.Equal(EditStatus.Applied, inverse.Status);
        Assert.False(m.Session.OpenDocument(newShips)!.State.Exists);
        Assert.Equal(before, m.State());
        m.Session.Undo();
        var saved = m.Session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Contains("[NPCShipArch]", m.Ws.ReadModFile(@"files\" + newShips));
    }

    [Fact]
    public void AWholeFileThatTakesThePlaceOfAFileOfTheGameDataCannotGo()
    {
        using var m = Create(modFiles: new() { [@"files\" + Script] = MissionEdits.Text });

        var error = m.Refuse(new DeleteScript(TestScript), MissionErrors.ReplacesLowerFile);

        Assert.StartsWith($"The active mod replaces {Script} of the game data or of a lower mod with this file.", error.Message);
    }

    [Fact]
    public void ARemovedBuildLeavesMissingLinesInEachScriptThatNamesIt()
    {
        using var m = Create();

        var result = m.Missions.Apply(new RemoveSection(Item("NPCShipArch", "ol_build_1", Ships)));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("npc_ship_arch", Assert.Single(result.Missing).Key);
    }

    [Fact]
    public void AValueIndexOutsideTheLineIsNotAValueOfTheLine()
    {
        using var m = Create();

        var error = m.Refuse(new SetLineValue(Row("ol_wave", 2), MissionValue.Of("5")) { Index = 9 }, MissionErrors.NoSuchValue);

        Assert.Equal(EditErrorKind.InvalidArgument, error.Kind);
    }

    [Fact]
    public void AcceptBaseWritesTheHashOfTheBaseNowAndItsInverseTheOldHash()
    {
        const string oldHash = "0123456789abcdef";
        using var m = Create(modFiles: new()
        {
            [@"patches\" + Script] = $"[Trigger]\nnickname = ol_end\nol.replace = true\nol.base = {oldHash}\nCnd_True = no_params\n",
        });
        var end = Item("Trigger", "ol_end");
        var document = m.Session.OpenDocument(Script)!;
        var hash = SectionHash.Of(document.Base[document.IndexOf(end.Section)]);

        var accepted = m.Apply(new AcceptBase(end));
        Assert.True(m.Session.Save().Saved);
        Assert.Contains($"ol.base = {hash}\r\n", m.Ws.ReadModPatch(Script));
        m.Refuse(new AcceptBase(end), MissionErrors.NoBaseDifference);
        m.Apply(accepted.Inverse!);
        Assert.True(m.Session.Save().Saved);

        Assert.Contains($"ol.base = {oldHash}\r\n", m.Ws.ReadModPatch(Script));
    }

    [Fact]
    public void AnUndoOfANewScriptChangesItsVersion()
    {
        using var m = Create();
        const string path = @"DATA\MISSIONS\OL_NEW\ol_new.ini";
        var changes = new List<MissionScriptsChange>();
        m.Missions.ScriptsChanged += changes.Add;

        m.Apply(new CreateScript(path));
        m.Session.Undo();
        m.Session.Redo();
        m.Apply(new DeleteScript(new ScriptRef(path)));

        Assert.Equal([2L, 3L, 4L, 5L], changes.Select(c => c.Versions[path]));
        Assert.DoesNotContain(path, m.Missions.ScriptPaths());
    }

    [Fact]
    public void AFileThatACommandOnlyReadsKeepsItsVersion()
    {
        using var m = Create();
        var changes = new List<MissionScriptsChange>();
        m.Missions.ScriptsChanged += changes.Add;

        // The loadout of a build of the ship build file of the script reads the script and changes only the build file.
        m.Apply(new SetShipLoadout(Item("MsnShip", "ol_ship_1"), "ol_loadout_2", LoadoutScope.Shared));

        Assert.Equal(1, m.Missions.Version(Script));
        Assert.DoesNotContain(changes, c => c.Versions.ContainsKey(Script));
    }

    [Fact]
    public void ARequestInAnotherLetterCaseKeepsTheLetterCaseOfTheFiles()
    {
        using var m = Create();

        var model = m.Missions.Model(Script.ToLowerInvariant().Replace('\\', '/'));

        Assert.Equal(Script, model.Script);
        Assert.Equal(Script, m.Session.OpenDocument(Script)!.Path);
    }

    [Fact]
    public void EachRefusalHasItsCode()
    {
        using var m = Create(text: MissionEdits.Text + "\n\n[MsnSolar]\nnickname = ol_twin\n\n[MsnSolar]\nnickname = ol_twin\n\n" +
                                   "[ObjList]\nGotoVec = goto_cruise, 0, 0, 1000, 100\n");

        m.Refuse(new SetTriggerStart(T("ol_wave", @"DATA\MISSIONS\OL_NONE\none.ini"), true), MissionErrors.ScriptNotFound);
        m.Refuse(new RemoveItem(Item("MsnShip", "ol_nobody")), MissionErrors.ItemNotFound);
        m.Refuse(new InsertLine(Item("ObjList", null), LineSpec.Raw("Delay", "1")), MissionErrors.ReadOnlySection);
        Assert.Equal("Two [MsnSolar] sections have the nickname 'ol_twin'. A patch cannot find one of them. Change the " +
                     "script in a whole file of the mod.",
            m.Refuse(new SetItemKey(Item("MsnSolar", "ol_twin"), "system", [LineSpec.Raw("system", "ol_system")]),
                MissionErrors.RepeatedNickname).Message);
        m.Refuse(new AcceptBase(Item("Trigger", "ol_start")), MissionErrors.NoBaseDifference);
        m.Refuse(new RemoveMissionShip(Item("MsnFormation", "ol_form")), MissionErrors.NotAnItem);
        m.Apply(new RemoveLine(Row("ol_wave", 2)));
        m.Refuse(new SetConditionValue(T("ol_wave"), "seconds", 3), MissionErrors.NoCondition);
        Assert.Equal(MissionErrors.BadJson, Assert.Throws<EditException>(() => MissionJson.ReadBatch("{")).Error.Detail);
    }

    /// <summary>The places of new lines come from the one class of the trigger rules (section 4.1 of the M6b design,
    /// decision D549 b). The dry run of M6c uses it too.</summary>
    [Fact]
    public void TheTriggerRulesGiveThePlacesOfNewLines()
    {
        using var m = Create();
        var document = m.Session.OpenDocument(Script)!;
        // system, nickname, Cnd_Timer, Act_SpawnShip, Act_DeactTrig, Act_ActTrig
        var wave = document.Working[MissionDraftLines.TriggerIndex(document, "ol_wave")].ToList();
        var noCondition = wave.Where(e => !OpenLancer.Data.Missions.TriggerRules.IsCondition(e.Name)).ToList();

        Assert.Equal(1, OpenLancer.Data.Missions.TriggerRules.NicknameLine(wave));
        Assert.Equal(2, OpenLancer.Data.Missions.TriggerRules.EffectiveCondition(wave));
        Assert.Equal(2, OpenLancer.Data.Missions.TriggerRules.StatePlace(wave));
        Assert.Equal(2, OpenLancer.Data.Missions.TriggerRules.ConditionPlace(wave));
        Assert.False(OpenLancer.Data.Missions.TriggerRules.ActionPlace(wave, 2));
        Assert.True(OpenLancer.Data.Missions.TriggerRules.ActionPlace(wave, 3));
        Assert.Equal(-1, OpenLancer.Data.Missions.TriggerRules.EffectiveCondition(noCondition));
        Assert.Equal(2, OpenLancer.Data.Missions.TriggerRules.ConditionPlace(noCondition));
    }
}

using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The edit bindings of the mission pages (section 8.4 of the M6b design) on the synthetic script of
/// <see cref="MissionEdits"/>: each binding gives the right command, the command gives the lines of W, and a read-only
/// section gives its reason.
/// </summary>
public sealed class MissionPageEditTests
{
    /// <summary>The page of an item now, with the read-only reasons of the session.</summary>
    private static MissionPageModel Page(MissionEdits m, MissionRef item)
    {
        var result = MissionRun.Execute(m.Missions.RunInputs());
        var analysis = m.Missions.Model(Script).Analysis;
        return MissionPageModel.For(new PageContext(result, analysis, false, false)
        {
            ReadOnlyOf = m.Missions.ReadOnlyReason,
        }, item);
    }

    private static MissionRef TriggerItem(MissionEdits m, string name) =>
        new(Script, MissionItemKind.Trigger, m.Missions.Model(Script).Index.Trigger(name)!.Section.Ref);

    private static MissionRef SectionItem(MissionEdits m, MissionSectionKind kind, string name) =>
        MissionRef.OfSection(m.Missions.Model(Script).Index.Item(kind, name)!.Ref, -1);

    private static PageField Field(MissionPageModel page, string section, string label) =>
        page.Section(section)!.Fields.First(f => f.Label == label);

    private static void Run(MissionEdits m, MissionBatch? batch)
    {
        Assert.NotNull(batch);
        var result = m.Missions.Apply(batch);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void TheTriggerComponentAndTheEventGiveTheirCommands()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_wave"));

        var starts = Field(page, "trigger", "Starts with the mission").Edit!;
        var repeats = Field(page, "trigger", "Repeats").Edit!;
        var @event = Field(page, "startsWhen", "Event").Edit!;
        var delay = Field(page, "startsWhen", "Delay").Edit!;

        Assert.Equal((EditWidget.Bool, "false"), (starts.Widget, starts.Value));
        Assert.Equal(new SetTriggerStart(T("ol_wave"), true), Assert.Single(starts.Make!(true)!.Commands));
        Assert.Equal(new SetTriggerRepeats(T("ol_wave"), true), Assert.Single(repeats.Make!(true)!.Commands));
        Assert.Equal((EditWidget.Words, "Cnd_Timer"), (@event.Widget, @event.Value));
        Assert.Equal(MissionSchema.Retail.Conditions.Count, @event.Choices.Count);
        Assert.Equal("Start", @event.Choices[0].Group);
        Assert.Equal((EditWidget.Number, "3"), (delay.Widget, delay.Value));
        Run(m, @event.Make!("Cnd_Destroyed"));
        Assert.Equal("Cnd_Destroyed = none", m.Lines("ol_wave")[2]);
        Run(m, Field(Page(m, TriggerItem(m, "ol_wave")), "startsWhen", "Target").Edit!.Make!("ol_ship_2"));
        Assert.Equal("Cnd_Destroyed = ol_ship_2", m.Lines("ol_wave")[2]);
    }

    [Fact]
    public void AnActionValueAndTheRowMenuGiveTheirCommands()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_wave"));
        var spawn = page.Section("group.spawn")!.Fields;

        var ship = spawn.First(f => f.Label == "Ship").Edit!;
        Assert.Equal((EditWidget.Picker, "ol_ship_1", "mission_ship+mission_formation"), (ship.Widget, ship.Value, ship.List));
        Run(m, ship.Make!("ol_ship_2"));
        Assert.Equal("Act_SpawnShip = ol_ship_2", m.Lines("ol_wave")[3]);

        page = Page(m, TriggerItem(m, "ol_wave"));
        var heading = page.Section("group.spawn")!.Fields.First(f => f.Heading);
        Assert.Equal(["Move down", "Duplicate", "Remove", "Show in raw INI"], heading.Actions.Select(a => a.Label));
        Assert.IsType<RawLineLink>(heading.Actions[^1].Link);
        Run(m, heading.Actions.Single(a => a.Label == "Move down").Batch);
        Assert.Equal("Act_SpawnShip = ol_ship_2", m.Lines("ol_wave")[4]);
        page = Page(m, TriggerItem(m, "ol_wave"));
        Run(m, page.Section("group.spawn")!.Fields.First(f => f.Heading).Actions.Single(a => a.Label == "Remove").Batch);
        Assert.DoesNotContain(m.Lines("ol_wave"), l => l.StartsWith("Act_SpawnShip"));
    }

    [Fact]
    public void StartedByAddsAndRemovesStartLinesInTheOtherTrigger()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_end"));
        var started = page.Section("startedBy")!;
        var stopped = page.Section("stoppedBy")!;

        Assert.Equal(["ol_start", "ol_wave", "Add"], started.Fields.Select(f => f.Label));
        Assert.Equal(EditWidget.Triggers, started.Fields[^1].Edit!.Widget);
        Run(m, started.Fields[0].Actions.Single().Batch);
        Assert.DoesNotContain("Act_ActTrig = ol_end", m.Lines("ol_start"));
        Run(m, Page(m, TriggerItem(m, "ol_end")).Section("stoppedBy")!.Fields[^1].Edit!.Add!("ol_start"));
        Assert.Equal("Act_DeactTrig = ol_end", m.Lines("ol_start")[^1]);
        Assert.Equal(["ol_wave", "Add"], stopped.Fields.Select(f => f.Label));
    }

    [Fact]
    public void TheAdvancedSectionMovesALineBeforeTheNicknameAndSetsTheSystemHint()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_wave"));
        var advanced = page.Section("advanced")!;

        var before = advanced.Fields.Single(f => f.Struck);
        Assert.Equal(["Move it after the nickname", "Remove the line"], before.Actions.Select(a => a.Label));
        Run(m, before.Actions[0].Batch);
        Assert.Equal(["nickname = ol_wave", "system = ol_system"], m.Lines("ol_wave").Take(2));
        Run(m, Field(Page(m, TriggerItem(m, "ol_end")), "advanced", "System").Edit!.Make!("ol_system"));
        Assert.Equal("system = ol_system", m.Lines("ol_end")[1]);
        var order = Page(m, TriggerItem(m, "ol_end")).Section("advanced")!.Fields.Where(f => f.Actions.Count > 0).ToList();
        Assert.Equal(["Move down"], order[0].Actions.Select(a => a.Label));
    }

    [Fact]
    public void TheShipPageSetsItsKeysItsLabelsAndItsLoadoutForThisShipOnly()
    {
        using var m = Create();
        var page = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1"));

        var position = Field(page, "ship", "Start position").Edit!;
        Assert.True(position.PickInScene);
        Run(m, position.Make!(MissionValue.Of(1, 2, 3)));
        Assert.Contains("position = 1, 2, 3", m.Lines(Item("MsnShip", "ol_ship_1")));

        var labels = Field(page, "ship", "Labels");
        Run(m, labels.Edit!.MakeList!(labels.List!.Add(1, new ListFieldRow("label", ["ol_new"], null) { Changed = true })));
        Assert.Contains("label = ol_new", m.Lines(Item("MsnShip", "ol_ship_1")));

        var build = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1")).Section("build")!;
        Assert.Equal("Used by 2 ships", build.Fields.Single(f => f.Label == "Shared").Value);
        Run(m, build.Actions.Single(a => a.Label == "This ship only").Batch);
        var after = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1")).Section("build")!;
        Assert.Equal("Used by 1 ship", after.Fields.Single(f => f.Label == "Shared").Value);
    }

    [Fact]
    public void ThePageMenuOfAShipDuplicatesDeletesAndRenamesIt()
    {
        using var m = Create();
        var page = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_3"));

        Assert.Equal(["Duplicate", "Delete"], page.PageActions.Select(a => a.Label));
        Assert.IsType<RemoveMissionShip>(Assert.Single(page.PageActions[1].Batch!.Commands));
        Run(m, page.Rename!("ol_ship_c"));
        Assert.True(m.Has(Item("MsnShip", "ol_ship_c")));
        Assert.Null(page.Rename!("ol_ship_3"));
    }

    [Fact]
    public void TheFormationMembersAndTheOrdersAreListsWithOneCommand()
    {
        using var m = Create();
        var formation = Page(m, SectionItem(m, MissionSectionKind.Formation, "ol_form"));
        var members = formation.Section("members")!.Fields.Single(f => f.List != null);

        var batch = members.Edit!.MakeList!(members.List!.Add(1, new ListFieldRow("ship", ["ol_ship_3"], null) { Changed = true }))!;
        var command = Assert.IsType<SetFormationMembers>(Assert.Single(batch.Commands));
        Assert.Equal(Item("MsnFormation", "ol_form"), command.Formation);
        Assert.Equal(["ol_ship_2", "ol_ship_3"], command.Ships);
        Run(m, batch);

        var orders = Page(m, SectionItem(m, MissionSectionKind.OrderList, "ol_orders"));
        var list = orders.Section("orders")!.Fields.Single(f => f.List != null);
        var point = Field(orders, "orders", "Point 1").Edit!;
        Assert.True(point.PickInScene);
        Run(m, point.Make!(MissionValue.Of(5, 6, 7)));
        Assert.Equal("GotoVec = goto_cruise, 5, 6, 7, 100", m.Lines(Item("ObjList", "ol_orders"))[1]);
        list = Page(m, SectionItem(m, MissionSectionKind.OrderList, "ol_orders")).Section("orders")!.Fields.Single(f => f.List != null);
        Run(m, list.Edit!.MakeList!(list.List!.Remove(0)));
        Assert.Equal(["nickname = ol_orders"], m.Lines(Item("ObjList", "ol_orders")));
    }

    [Fact]
    public void TheLabelPageAddsAndRemovesMembers()
    {
        using var m = Create();
        var page = Page(m, new MissionRef(Script, MissionItemKind.Label, Key: "ol_wing"));
        var members = page.Section("members")!.Fields;

        Assert.Equal(["Mission ship", "Mission ship", "Add"], members.Select(f => f.Label));
        Run(m, members[^1].Edit!.Add!("ol_form"));
        Assert.Contains("label = ol_wing", m.Lines(Item("MsnFormation", "ol_form")));
        Run(m, members[0].Actions.Single().Batch);
        Assert.DoesNotContain("label = ol_wing", m.Lines(Item("MsnShip", "ol_ship_1")));
    }

    [Fact]
    public void TheMissionPageSetsTheShipBuildFile()
    {
        using var m = Create();
        var page = Page(m, MissionRef.Of(Script));

        var file = Field(page, "files", "Ship build file").Edit!;
        Assert.Equal(@"missions\ol_test\npcships.ini", file.Value);
        Run(m, file.Make!(@"missions\ol_test\ships_b.ini"));
        Assert.Equal([@"npc_ship_file = missions\ol_test\ships_b.ini"], m.Lines(Item("Mission", null)));
    }

    [Fact]
    public void AReadOnlySectionGivesItsReasonToEachBindingAndThePage()
    {
        using var m = Create(Text + "\n[MsnShip]\nnickname = ol_ship_3\n");
        var twin = m.Missions.Model(Script).Document.OfKind(MissionSectionKind.Ship).Last();

        var reason = m.Missions.ReadOnlyReason(twin.Ref);
        var page = Page(m, MissionRef.OfSection(twin.Ref, -1));

        Assert.NotNull(reason);
        Assert.Equal(reason, page.ReadOnly);
        Assert.Empty(page.PageActions);
        Assert.Null(m.Missions.ReadOnlyReason(SectionItem(m, MissionSectionKind.Ship, "ol_ship_1").Section!));
        var context = new PageContext(MissionRun.Execute(m.Missions.RunInputs()), m.Missions.Model(Script).Analysis, false, false);
        var readOnly = MissionPageModel.For(context, TriggerItem(m, "ol_wave"));
        Assert.All(readOnly.Sections.SelectMany(s => s.Fields).Select(f => f.Edit).OfType<FieldEdit>(),
            e => Assert.Equal(MissionPageModel.FieldNote, e.ReadOnly));
        Assert.Null(readOnly.Section("stoppedBy")?.Fields.FirstOrDefault(f => f.Label == "Add"));
    }
    /// <summary>"Pick in Scene" takes a point on the plane of the camera pivot (section 8.9 of the M6b design).</summary>
    [Fact]
    public void PickInSceneTakesThePointOnThePlaneOfThePivot()
    {
        var eye = new System.Numerics.Vector3(0, 100, 0);
        var down = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(1, -1, 0));

        Assert.Equal(new System.Numerics.Vector3(50, 50, 0),
            Editor.Assets.ScenePlacement.PivotPlanePoint(eye, down, new System.Numerics.Vector3(0, 50, 0)));
        // A ray that does not meet the plane in front of the camera gives the point at the distance of the pivot.
        Assert.Equal(new System.Numerics.Vector3(0, 150, 0), Editor.Assets.ScenePlacement.PivotPlanePoint(eye,
            System.Numerics.Vector3.UnitY, new System.Numerics.Vector3(0, 50, 0)));
        var pick = new ScenePick("Start position", _ => { });
        Assert.Equal("Click a point in the Scene to set Start position. Esc cancels.", pick.Hint);
    }
}

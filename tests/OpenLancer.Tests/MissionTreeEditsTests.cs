using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Hierarchy;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The edits of the tree of a mission in the Hierarchy (sections 8.4 and 8.9 of the M6b design): the "+" menu, F2, Ctrl+D,
/// Delete, the drags of rows and the drop of a ship tile on the group "Cast". All names are invented.
/// </summary>
public sealed class MissionTreeEditsTests
{
    private static MissionAnalysis Analysis(MissionEdits m) => m.Missions.Model(Script).Analysis;

    private static MissionRef Row(MissionEdits m, MissionSectionKind kind, string name) =>
        MissionRef.OfSection(Analysis(m).Index.Item(kind, name)!.Ref, -1);

    private static MissionRef TriggerRow(MissionEdits m, string name) =>
        new(Script, MissionItemKind.Trigger, Analysis(m).Index.Trigger(name)!.Section.Ref);

    private static MissionEditResult Run(MissionEdits m, MissionBatch? batch)
    {
        Assert.NotNull(batch);
        var result = m.Missions.Apply(batch);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
        return result;
    }

    [Fact]
    public void EachItemOfThePlusMenuAddsItsItemAndSelectsIt()
    {
        using var m = Create();

        foreach (var (kind, _) in MissionTreeEdits.CreateKinds)
        {
            Assert.Null(MissionTreeEdits.CreateRefusal(Analysis(m), kind));
            var result = Run(m, MissionTreeEdits.Create(Analysis(m), kind));
            Assert.NotNull(result.Select);
        }

        Assert.Equal(["nickname = ol_test_trigger_1", "Cnd_True = no_params"], m.Lines("ol_test_trigger_1"));
        Assert.Equal("npc = ol_npc_1", m.Lines(Item("MsnShip", "ol_test_ship_1"))[1]);
        Assert.True(m.Has(Item("MsnRandEnc", "ol_test_encounter_1")));
        Assert.Equal(2, m.Missions.Graph(Script).Acts.Count);
    }

    [Fact]
    public void ANewMissionShipNeedsAPilotOfTheScript()
    {
        using var m = Create("[Mission]\n\n[Trigger]\nnickname = ol_only\nCnd_True = no_params\n");

        Assert.Equal("The script has no pilot. Drag a ship from the Assets panel onto the group Cast.",
            MissionTreeEdits.CreateRefusal(Analysis(m), "ship"));
        Assert.Null(MissionTreeEdits.Create(Analysis(m), "ship"));
    }

    [Fact]
    public void RenameDuplicateAndDeleteUseTheCommandsOfTheGraph()
    {
        using var m = Create();

        Run(m, MissionTreeEdits.Rename(Analysis(m), TriggerRow(m, "ol_end"), "ol_finish", null));
        Run(m, MissionTreeEdits.Rename(Analysis(m), Row(m, MissionSectionKind.OrderList, "ol_orders"), "ol_route", null));
        Run(m, MissionTreeEdits.Duplicate(Analysis(m), TriggerRow(m, "ol_wave")));
        Run(m, MissionTreeEdits.Duplicate(Analysis(m), Row(m, MissionSectionKind.Formation, "ol_form")));
        Run(m, MissionTreeEdits.Delete(Analysis(m), Row(m, MissionSectionKind.Ship, "ol_ship_3"), null));
        Run(m, MissionTreeEdits.Delete(Analysis(m), TriggerRow(m, "ol_wave_2"), null));

        Assert.Contains("Act_ActTrig = ol_finish", m.Lines("ol_start"));
        Assert.True(m.Has(Item("ObjList", "ol_route")));
        Assert.False(m.Has("ol_wave_2"));
        Assert.Equal(2, Analysis(m).Document.OfKind(MissionSectionKind.Formation).Count());
        // The ship goes with its NPC, because no other ship names it (decision D669).
        Assert.False(m.Has(Item("MsnShip", "ol_ship_3")));
        Assert.False(m.Has(Item("NPC", "ol_npc_shared")));
        Assert.Null(MissionTreeEdits.Rename(Analysis(m), TriggerRow(m, "ol_start"), "ol_start", null));
    }

    [Fact]
    public void AnActRowRenamesAndDeletesItsAct()
    {
        using var m = Create();
        var act = new MissionRef(Script, MissionItemKind.Act, Key: "a:1");

        Run(m, MissionTreeEdits.Rename(Analysis(m), act, "The launch", null));
        Assert.Equal("The launch", Assert.Single(m.Missions.Graph(Script).Acts).Name);
        Run(m, MissionTreeEdits.Delete(Analysis(m), act, m.Missions.Model(Script).Acts));

        Assert.Empty(m.Missions.Graph(Script).Acts);
    }

    [Fact]
    public void ARowDropsOnAFormationALabelOrAnAct()
    {
        using var m = Create();

        Run(m, MissionTreeEdits.Drop(Analysis(m), Row(m, MissionSectionKind.Ship, "ol_ship_3"),
            Row(m, MissionSectionKind.Formation, "ol_form"), null));
        Run(m, MissionTreeEdits.Drop(Analysis(m), Row(m, MissionSectionKind.Formation, "ol_form"),
            new MissionRef(Script, MissionItemKind.Label, Key: "ol_wing"), null));
        m.Apply(new AddAct(TestScript) { Name = "The end" });
        Run(m, MissionTreeEdits.Drop(Analysis(m), TriggerRow(m, "ol_end"), new MissionRef(Script, MissionItemKind.Act, Key: "a:2"),
            m.Missions.Model(Script).Acts));

        Assert.Equal(["ship = ol_ship_2", "ship = ol_ship_3"],
            m.Lines(Item("MsnFormation", "ol_form")).Where(l => l.StartsWith("ship")));
        Assert.Contains("label = ol_wing", m.Lines(Item("MsnFormation", "ol_form")));
        Assert.Equal(["ol_end"], m.Missions.Graph(Script).Acts[1].Members);
        Assert.DoesNotContain("ol_end", m.Missions.Graph(Script).Acts[0].Members);
        Assert.Equal("Only a mission ship can join a formation.", MissionTreeEdits.DropRefusal(Analysis(m),
            TriggerRow(m, "ol_end"), Row(m, MissionSectionKind.Formation, "ol_form")));
        Assert.Equal("Only a trigger can join an act.", MissionTreeEdits.DropRefusal(Analysis(m),
            Row(m, MissionSectionKind.Ship, "ol_ship_1"), new MissionRef(Script, MissionItemKind.Act, Key: "a:1")));
        // A ship that the formation has already gives no change.
        Assert.Null(MissionTreeEdits.Drop(Analysis(m), Row(m, MissionSectionKind.Ship, "ol_ship_2"),
            Row(m, MissionSectionKind.Formation, "ol_form"), null));
    }

    [Fact]
    public void AShipTileOnTheCastAddsAMissionShipInOneUndoEntry()
    {
        using var m = Create();
        var before = m.State();

        var result = Run(m, MissionTreeEdits.AddShip(Analysis(m), "ol_arch", "ol_loadout", null, "ol_faction"));

        var ship = Analysis(m).Document.OfKind(MissionSectionKind.Ship).Last();
        var npc = Analysis(m).Index.Item(MissionSectionKind.Npc, ship.Section.First(e => e.Name == "npc")[0].ToString()!)!;
        Assert.Contains(npc.Section, e => e.Name == "affiliation" && e[0].ToString() == "ol_faction");
        Assert.Equal(ItemRefs.Canonical(m.Session, ship.Ref), result.Select);
        Assert.Contains(m.Session.OpenDocument(Ships)!.Working, s => s.Any(e => e.Name == "ship_archetype" && e[0].ToString() == "ol_arch") &&
                                                                     s.Any(e => e.Name == "loadout" && e[0].ToString() == "ol_loadout") &&
                                                                     s.Count(e => e.Name == "nickname") == 1 &&
                                                                     s.First(e => e.Name == "nickname")[0].ToString() != "ol_build_1");
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }
}

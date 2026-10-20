using System;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The mission ships, the items, the formations and the orders (section 4.9 of the M6b design, MSD section
/// 3.6).</summary>
public sealed class MissionShipTests
{
    [Fact]
    public void AddMissionShipWritesThreeSectionsInTwoFilesInOneEntry()
    {
        using var m = Create();
        var before = m.State();

        var result = m.Apply(new AddMissionShip(TestScript)
        {
            Nickname = "ol_escort",
            Pilot = new PilotSpec { Faction = "ol_faction", Voice = "ol_voice", Head = "ol_head", Body = "ol_body" },
            NewBuild = new ShipBuildSpec("ol_arch", "ol_loadout") { Level = "d3", NpcClasses = ["lawful", "FIGHTER"] },
            Position = MissionValue.Of(10, 0, -10),
            System = "ol_system",
            Labels = ["ol_wing"],
            Orders = "ol_orders",
        });

        Assert.Equal(["nickname = ol_escort", "system = ol_system", "npc = ol_test_npc_1", "label = ol_wing",
            "position = 10, 0, -10", "init_objectives = ol_orders"], m.Lines(Item("MsnShip", "ol_escort")));
        Assert.Equal(["nickname = ol_test_npc_1", "space_costume = ol_head, ol_body", "voice = ol_voice",
            "npc_ship_arch = ol_test_build_1", "affiliation = ol_faction"], m.Lines(Item("NPC", "ol_test_npc_1")));
        Assert.Equal(["nickname = ol_test_build_1", "ship_archetype = ol_arch", "loadout = ol_loadout", "level = d3",
            "npc_class = lawful, FIGHTER"], m.Lines(Item("NPCShipArch", "ol_test_build_1", Ships)));
        Assert.Equal(3, result.Adds.Count);
        Assert.Equal(Item("MsnShip", "ol_escort").ToString(), result.Select);
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void ANewBuildWithoutAShipBuildFileRefuses()
    {
        using var m = Create(Text.Replace(@"npc_ship_file = missions\ol_test\npcships.ini", ""));

        var error = m.Refuse(new AddMissionShip(TestScript) { NewBuild = new ShipBuildSpec("ol_arch", "ol_loadout") },
            MissionErrors.NoShipBuildFile);

        Assert.Equal("The mission has no ship build file. Set \"Ship build file\" on the Mission page first.", error.Message);
        m.Apply(new AddMissionShip(TestScript) { Npc = "ol_npc_1" });
    }

    [Fact]
    public void RemoveMissionShipRemovesItsNpcAndItsBuildWhenNoOtherItemUsesThem()
    {
        using var m = Create();
        m.Apply(new AddMissionShip(TestScript) { Nickname = "ol_alone", NewBuild = new ShipBuildSpec("ol_arch", "ol_loadout") });

        var alone = m.Apply(new RemoveMissionShip(Item("MsnShip", "ol_alone")));
        var shared = m.Apply(new RemoveMissionShip(Item("MsnShip", "ol_ship_3")));
        var used = m.Apply(new RemoveMissionShip(Item("MsnShip", "ol_ship_1")));

        Assert.Equal(["Target", "Cascade", "Cascade"], alone.Deletes.Select(d => d.Why.ToString()));
        Assert.Null(m.Lines(Item("NPCShipArch", "ol_test_build_1", Ships)));
        // The build of ol_ship_3 is in the shared file, so it stays.
        Assert.Equal(2, shared.Deletes.Count);
        Assert.NotNull(m.Lines(Item("NPCShipArch", "ol_shared_build", SharedShips)));
        // ol_ship_2 still uses ol_npc_1.
        Assert.Single(used.Deletes);
        Assert.NotNull(m.Lines(Item("NPC", "ol_npc_1")));
        Assert.Equal(["Act_SpawnShip", "Cnd_Destroyed"], used.Missing.Select(x => x.Key).Order());
    }

    [Fact]
    public void ARequiredPartRefusesItsDelete()
    {
        using var m = Create();

        var npc = m.Refuse(new RemoveItem(Item("NPC", "ol_npc_1")), MissionErrors.RequiredBy);
        var build = m.Refuse(new RemoveItem(Item("NPCShipArch", "ol_build_1", Ships)), MissionErrors.RequiredBy);

        Assert.Equal("NPC 'ol_npc_1' is in use by 2 ships. Remove them or change them first.", npc.Message);
        Assert.Equal("Ship build 'ol_build_1' is in use by 1 NPC. Remove them or change them first.", build.Message);
    }

    [Fact]
    public void SetShipLoadoutSharedChangesTheBuildAndThisShipMakesACopy()
    {
        using var m = Create();

        m.Apply(new SetShipLoadout(Item("MsnShip", "ol_ship_1"), "ol_loadout_2", LoadoutScope.Shared));
        Assert.Equal("loadout = ol_loadout_2", m.Lines(Item("NPCShipArch", "ol_build_1", Ships))![2]);
        m.Session.Undo();

        var result = m.Apply(new SetShipLoadout(Item("MsnShip", "ol_ship_1"), "ol_loadout_2", LoadoutScope.ThisShip));

        Assert.Equal("loadout = ol_loadout", m.Lines(Item("NPCShipArch", "ol_build_1", Ships))![2]);
        Assert.Equal(["nickname = ol_test_build_1", "ship_archetype = ol_arch", "loadout = ol_loadout_2", "level = d5"],
            m.Lines(Item("NPCShipArch", "ol_test_build_1", Ships)));
        // ol_ship_2 also uses ol_npc_1, so ol_ship_1 gets a copy of the NPC.
        Assert.Equal("npc = ol_test_npc_1", m.Lines(Item("MsnShip", "ol_ship_1"))![2]);
        Assert.Equal("npc_ship_arch = ol_test_build_1", m.Lines(Item("NPC", "ol_test_npc_1"))![1]);
        Assert.Equal(2, result.Adds.Count);
    }

    [Fact]
    public void ASharedChangeOfABuildInTheSharedFileNeedsAConfirmation()
    {
        using var m = Create();
        var command = new SetShipLoadout(Item("MsnShip", "ol_ship_3"), "ol_loadout_2", LoadoutScope.Shared);

        var error = m.Refuse(command, MissionErrors.SharedBuildChange);
        var confirmed = m.Missions.Apply(new MissionBatch("Change the build", [command]) { Confirm = true });

        Assert.Equal("Ship build 'ol_shared_build' is in a shared file. Random encounters can use it, so this change can " +
                     "change them too. Use \"This ship only\", or confirm the change.", error.Message);
        Assert.Equal(EditStatus.Applied, confirmed.Status);
        Assert.Equal("loadout = ol_loadout_2", m.Lines(Item("NPCShipArch", "ol_shared_build", SharedShips))![2]);
    }

    [Fact]
    public void ALabelOfARetailFormFormationGoesBeforeItsFormationLine()
    {
        using var m = Create();

        m.Apply(new AddLabel(Item("MsnFormation", "ol_form"), "ol_group"));
        m.Apply(new RemoveLabel(Item("MsnShip", "ol_ship_2"), "ol_wing"));

        Assert.Equal(["nickname = ol_form", "position = 0, 0, 0", "label = ol_group", "formation = ol_shape",
            "ship = ol_ship_2"], m.Lines(Item("MsnFormation", "ol_form")));
        Assert.Equal(["nickname = ol_ship_2", "npc = ol_npc_1"], m.Lines(Item("MsnShip", "ol_ship_2")));
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule == "M09");
    }

    [Fact]
    public void SetFormationMembersMovesALaterShipBeforeTheFormationWithOlBefore()
    {
        using var m = Create(Text + "\n[MsnShip]\nnickname = ol_ship_4\nnpc = ol_npc_1\n");

        m.Apply(new SetFormationMembers(Item("MsnFormation", "ol_form"), ["ol_ship_2", "ol_ship_4"]));

        var document = m.Session.OpenDocument(Script)!;
        var form = document.IndexOf(Item("MsnFormation", "ol_form").Section);
        Assert.Equal("ol_ship_4", Data.Editing.SectionIdentity.KeyOf(document.Working[form - 1]));
        Assert.Equal(["ship = ol_ship_2", "ship = ol_ship_4"], m.Lines(Item("MsnFormation", "ol_form"))!.Skip(3));
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule == "M08");
        var saved = m.Session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        // Only the ship moves: a delete, and a new section before the formation. The formation has new lines.
        var patch = m.Ws.ReadModPatch(Script);
        Assert.Contains("ol.before = MsnFormation, ol_form", patch);
        Assert.Equal(3, patch.Split("nickname = ").Length - 1);
    }

    [Fact]
    public void TheItemCommandsAddDuplicatePlaceAndMoveItems()
    {
        using var m = Create();

        m.Apply(new AddItem(TestScript, "solar") { Nickname = "ol_buoy", Lines = [LineSpec.Of("system", ("system", "ol_system"))] });
        m.Apply(new DuplicateItem(Item("MsnShip", "ol_ship_1")) { NewName = "ol_ship_1b" });
        m.Apply(new PlaceMissionShip(Item("MsnShip", "ol_ship_1")) { Near = new NearSpec("ol_station") { Heading = 90 } });
        m.Apply(new SetItemKey(Item("MsnShip", "ol_ship_2"), "cargo",
            [LineSpec.Of("cargo", ("cargo", "ol_good")), LineSpec.Of("cargo", ("cargo", "ol_good_2"))]));
        m.Apply(new MoveSection(Item("MsnShip", "ol_ship_3")) { Before = Item("MsnShip", "ol_ship_1") });

        Assert.Equal(["nickname = ol_buoy", "system = ol_system"], m.Lines(Item("MsnSolar", "ol_buoy")));
        Assert.Equal("npc = ol_npc_1", m.Lines(Item("MsnShip", "ol_ship_1b"))![2]);
        Assert.Equal(["nickname = ol_ship_1", "system = ol_system", "npc = ol_npc_1", "label = ol_wing", "rel_pos = 90, ol_station"],
            m.Lines(Item("MsnShip", "ol_ship_1")));
        Assert.Equal(["nickname = ol_ship_2", "npc = ol_npc_1", "label = ol_wing", "cargo = ol_good", "cargo = ol_good_2"],
            m.Lines(Item("MsnShip", "ol_ship_2")));
        Assert.Equal("The place of a [Trigger] section in the file has no effect in the game. Only a ship or a formation " +
                     "section can move.", m.Refuse(new MoveSection(T("ol_wave").Item), MissionErrors.NotMovable).Message);
        Assert.Equal("nickname is the name of the item. Use Rename to change it.",
            m.Refuse(new SetItemKey(Item("MsnShip", "ol_ship_2"), "nickname", [LineSpec.Raw("nickname", "x")]),
                MissionErrors.KeyLine).Message);
    }

    [Fact]
    public void TheOrderCommandsWriteTheDirectives()
    {
        using var m = Create();
        var orders = Item("ObjList", "ol_orders");

        m.Apply(new AddWaypoint(orders, MissionValue.Of(1, 2, 3)));
        m.Apply(new AddOrder(orders, LineSpec.Of("Delay", ("seconds", 4))) { Before = 1 });
        Assert.Equal(["nickname = ol_orders", "Delay = 4", "GotoVec = goto_cruise, 0, 0, 1000, 100",
            "GotoVec = goto_no_cruise, 1, 2, 3, 100"], m.Lines(orders));

        m.Apply(new SetOrders(orders, [LineSpec.Of("Idle"), LineSpec.Of("Delay", ("seconds", 2))]));

        Assert.Equal(["nickname = ol_orders", "Idle =", "Delay = 2"], m.Lines(orders));
        Assert.Equal("'Fly' is not an order of the game. Pick an order from the list.",
            m.Refuse(new AddOrder(orders, LineSpec.Raw("Fly")), MissionErrors.NotADirective).Message);
    }
}

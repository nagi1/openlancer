using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The page models of each item kind of a mission (sections 10.3 to 10.17 and 18.1 of the M6a design) on a synthetic
/// script. The named sections and fields exist, and a field without an effect does not show.
/// </summary>
public sealed class MissionPagesTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    private const string Text = """
        [Mission]

        [NPC]
        nickname = npc_one
        affiliation = fc_invented
        voice = voice_invented
        space_costume = head_a, body_a
        individual_name = 1001
        npc_ship_arch = build_one

        [MsnShip]
        nickname = s_one
        npc = npc_one
        system = sys_a
        position = 10, 0, 20
        orientation = 1, 0, 0, 0
        label = l_wing
        cargo = good_a, 2
        cargo = good_b, 3

        [MsnShip]
        nickname = s_near
        npc = npc_one
        system = sys_a
        rel_pos = 90, station_a, 1000
        arrival_obj = gate_a
        hit_points = 500

        [MsnFormation]
        nickname = f_one
        system = sys_a
        position = 0, 0, 0
        formation = shape_a
        ship = s_one

        [MsnSolar]
        nickname = sol_one
        system = sys_a
        archetype = arch_a
        position = 1, 2, 3

        [MsnLoot]
        nickname = loot_one
        archetype = crate_a
        rel_pos_obj = station_a
        rel_pos_offset = 5, 5, 5

        [ObjList]
        nickname = ol_one
        GotoVec = goto, 0, 0, 0, 100
        GotoVec = goto, 10, 0, 0, 100

        [NNObjective]
        nickname = o_nav
        type = navmarker, sys_a, 2001, 2002, 7, 8, 9

        [Dialog]
        nickname = d_one
        system = sys_a
        line = s_one, Player, comm_a
        line = s_near, Player, comm_b

        [MsnRandEnc]
        nickname = enc_one
        encounter_type = SP_Attack
        attacker_rep_name = fc_invented
        target_ship_name = Player
        activation_type = time, 5, 30
        formation = shape_a, 5, 1, 2, class_a, 100
        num_forms = 1, 2
        label = l_attackers
        odd_key = 1

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        system = sys_a
        Cnd_True = no_params
        Act_SpawnShip = s_one, ol_one
        Act_SetVibe = s_one, Player, -0.9
        Act_StartDialog = d_one
        Act_SetNNObj = o_nav
        Act_CallThorn = scripts\a.thn, s_one
        Act_AddRTC = scenes\offer.ini
        Act_EnableEnc = enc_one
        Act_ActTrig = t_kill

        [Trigger]
        repeatable = true
        nickname = t_kill
        system = sys_a
        Cnd_True = no_params
        Cnd_Destroyed = s_one
        Act_RevertCam = no_params
        Act_ChangeState = SUCCEED
        """;

    private static readonly (MissionRunResult Result, MissionAnalysis Analysis) Run = Execute();

    /// <summary>The ship builds of the mission ship file of the folder.</summary>
    internal const string ShipBuilds = """
        [NPCShipArch]
        nickname = build_one
        ship_archetype = ship_a
        loadout = load_a
        pilot = pilot_a
        npc_class = lawful, d5
        """;

    private static (MissionRunResult, MissionAnalysis) Execute()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Text);
        folder.Write(@"DATA\MISSIONS\npcships.ini", ShipBuilds);
        var result = MissionRun.Execute(folder.Inputs());
        return (result, result.Of(Script)!);
    }

    private static MissionPageModel Page(MissionRef item, bool showBaseGame = false) =>
        MissionPageModel.For(new PageContext(Run.Result, Run.Analysis, showBaseGame, false)
        {
            Text = id => id == 1001 ? "Invented pilot" : "",
            SystemName = s => s == "sys_a" ? "Invented System" : null,
        }, item);

    private static MissionSection Section(MissionSectionKind kind, string nickname) =>
        Run.Analysis.Document.OfKind(kind).Single(s => s.Nickname == nickname);

    private static MissionRef RefOf(MissionSectionKind kind, string nickname) =>
        MissionRef.OfSection(Section(kind, nickname).Ref, -1);

    private static MissionTrigger Trigger(string nickname) => Run.Analysis.Index.Trigger(nickname)!;

    private static IEnumerable<string> Labels(MissionPageModel page, string section) =>
        page.Section(section)!.Fields.Select(f => f.Label);

    [Fact]
    public void TheTriggerPageHasTheTriggerSectionTheEventAndOneSectionForEachGroup()
    {
        var trigger = Trigger("t_start");
        var page = Page(new MissionRef(Script, MissionItemKind.Trigger, trigger.Section.Ref));

        Assert.Equal(("t_start", "Trigger"), (page.Title, page.Kind));
        Assert.Equal(["Start", "sys_a"], page.HeaderChips);
        Assert.Equal(["Starts with the mission", "Repeats"], Labels(page, "trigger"));
        Assert.False(page.Section("trigger")!.Advanced);
        Assert.Equal("Starts at once.", page.Section("startsWhen")!.Fields[0].Value);
        // The groups follow the order of section 13.3.
        Assert.Equal(["group.starts", "group.spawn", "group.sides", "group.comms", "group.objective", "group.cutscene",
            "group.world"], page.Sections.Select(s => s.Id).Where(id => id.StartsWith("group.")));
        var spawn = page.Section("group.spawn")!.Fields;
        Assert.True(spawn[0].Heading);
        Assert.Equal("Line 5", spawn[0].Label);
        Assert.Equal(["Ship", "Orders"], spawn.Skip(1).Select(f => f.Label));
        Assert.Equal(RefOf(MissionSectionKind.Ship, "s_one"), spawn[1].Link);
        // A name shows with the reference widget, read-only in M6a (section 10.1 of the M6a design).
        Assert.Equal("s_one", spawn[1].Reference!.Nickname);
        // Nothing starts the start trigger, so the page has no section "Started by".
        Assert.Null(page.Section("startedBy"));
        Assert.Equal(["System", "Lines in file order"], Labels(page, "advanced").Take(2));
        Assert.True(page.Section("advanced")!.Advanced);
        Assert.Equal(MissionSchema.Retail.Section("Trigger")!.Key("system")!.Help, page.Section("advanced")!.Fields[0].Help);
    }

    [Fact]
    public void TheTriggerPageStrikesTheIgnoredLinesAndListsItsSources()
    {
        var kill = Trigger("t_kill");
        var page = Page(new MissionRef(Script, MissionItemKind.Trigger, kill.Section.Ref));

        // The first condition is struck through, and the repeatable line before the nickname is in Advanced.
        Assert.Contains(page.Section("startsWhen")!.Fields, f => f.Struck && f.Value == "At once");
        Assert.Contains(page.Section("advanced")!.Fields, f => f.Struck && f.Value.StartsWith("Repeats"));
        Assert.Equal("No", page.Section("trigger")!.Fields[1].Value);
        var started = page.Section("startedBy")!;
        Assert.Equal(("t_start", true), (started.Fields.Single().Label, started.Fields.Single().Link is MissionRef));
    }

    [Fact]
    public void ALineSelectionOpensTheSectionOfItsGroup()
    {
        var trigger = Trigger("t_start");
        var setVibe = trigger.ActionsOf("Act_SetVibe").Single();

        var page = Page(new MissionRef(Script, MissionItemKind.Row, trigger.Section.Ref, Row: setVibe.Entry));

        Assert.Equal("Trigger", page.Kind);
        Assert.Equal("group.sides", page.FocusSection);
    }

    [Fact]
    public void ACutsceneLineHasTheCutsceneLinkPage()
    {
        var trigger = Trigger("t_start");
        var call = trigger.ActionsOf("Act_CallThorn").Single();

        var page = Page(new MissionRef(Script, MissionItemKind.Row, trigger.Section.Ref, Row: call.Entry));

        Assert.Equal("Cutscene link", page.Kind);
        Assert.Equal(["Cutscene file", "Anchor", "Camera back"], page.Completeness!.Steps.Select(s => s.Name));
        Assert.Contains("Anchor", Labels(page, "cutscene"));
        Assert.IsType<CutsceneLink>(page.Section("cutscene")!.Fields.Last().Link);
    }

    [Fact]
    public void ABaseSceneLineHasTheBaseSceneLinkPage()
    {
        var trigger = Trigger("t_start");
        var add = trigger.ActionsOf("Act_AddRTC").Single();

        var page = Page(new MissionRef(Script, MissionItemKind.Row, trigger.Section.Ref, Row: add.Entry));

        Assert.Equal("Base scene link", page.Kind);
        Assert.Equal(["Scene file", "Path length"], page.Completeness!.Steps.Select(s => s.Name));
        Assert.Equal(["t_start"], Labels(page, "addedBy"));
        Assert.Empty(page.Section("removedBy")!.Fields);
    }

    [Fact]
    public void TheShipPageHasItsThreeComponentsAndHidesTheFieldsWithoutAnEffect()
    {
        var page = Page(RefOf(MissionSectionKind.Ship, "s_one"));

        Assert.Equal("Mission ship", page.Kind);
        Assert.Equal(["Pilot and voice", "Ship build", "Archetype, loadout and pilot"],
            page.Completeness!.Steps.Select(s => s.Name));
        Assert.True(page.Completeness.Complete);
        Assert.Equal(["Start position", "Rotation", "System", "Labels", "Cargo", "Hull points"], Labels(page, "ship"));
        Assert.Equal(MissionSchema.Retail.Notes["hullPointsNotSet"], page.Section("ship")!.Fields.Last().Help);
        Assert.Equal(2, page.Section("ship")!.Fields.Single(f => f.Label == "Cargo").List!.Rows.Count);
        // A position has one field for each number, as the editable field has.
        Assert.Equal([("X", "10"), ("Y", "0"), ("Z", "20")],
            page.Section("ship")!.Fields.Single(f => f.Label == "Start position").Parts);
        // The costume has a named field for each slot, not one comma-separated text.
        Assert.Equal(["Faction", "Voice", "Costume", "Head", "Body", "Name text", "Ship build"], Labels(page, "pilot"));
        Assert.True(page.Section("pilot")!.Fields.Single(f => f.Label == "Costume").Heading);
        // The ship build comes from the mission ship file of the run, with the labels of the rule data.
        Assert.Equal(["Ship archetype", "Loadout", "Pilot", "NPC classes"], Labels(page, "build"));
        // Each class of the npc_class line is one row of the list field.
        Assert.Equal(2, page.Section("build")!.Fields.Single(f => f.Label == "NPC classes").List!.Rows.Count);
        Assert.Equal("Invented pilot (1001)", page.Lead.Single().Value);
        // The formation takes the ship, so a note says that a spawn of the ship spawns nothing.
        Assert.Contains(page.Section("ship")!.Notes, n => n.StartsWith("Formation f_one takes this ship."));
        Assert.Contains(page.Section("usedBy")!.Fields, f => f.Label == "t_start");
    }

    [Fact]
    public void AShipNearAnAnchorShowsThePlaceAndNotThePosition()
    {
        var page = Page(RefOf(MissionSectionKind.Ship, "s_near"));
        var labels = Labels(page, "ship").ToList();

        Assert.Contains("Placed near", labels);
        // The place near an anchor has a named field for each value.
        Assert.Equal(["Heading", "Anchor", "Range"], labels.SkipWhile(l => l != "Placed near").Skip(1).Take(3));
        Assert.DoesNotContain("Start position", labels);
        Assert.DoesNotContain("Rotation", labels);
        Assert.Contains("Arrives through", labels);
        Assert.Equal("500", page.Section("ship")!.Fields.Single(f => f.Label == "Hull points").Value);
    }

    [Fact]
    public void TheCastPagesHaveTheirSections()
    {
        Assert.Equal(["formation", "members", "usedBy", "problems"],
            Page(RefOf(MissionSectionKind.Formation, "f_one")).Sections.Select(s => s.Id));
        Assert.Single(Page(RefOf(MissionSectionKind.Formation, "f_one")).Section("members")!.Fields.Single().List!.Rows);
        Assert.Equal(["Archetype", "Position", "System"], Labels(Page(RefOf(MissionSectionKind.Solar, "sol_one")), "solar"));
        Assert.Equal(["Archetype", "Placed near", "Offset"], Labels(Page(RefOf(MissionSectionKind.Loot, "loot_one")), "loot"));
        Assert.Equal("NPC", Page(RefOf(MissionSectionKind.Npc, "npc_one")).Kind);
    }

    [Fact]
    public void TheOtherPagesHaveTheirSections()
    {
        var orders = Page(RefOf(MissionSectionKind.OrderList, "ol_one"));
        Assert.Equal(2, orders.Section("orders")!.Fields.Single().List!.Rows.Count);
        Assert.StartsWith("Given by", orders.Section("usedBy")!.Title);
        Assert.Equal(["Patrol"], orders.Lead.Select(f => f.Value));

        var objective = Page(RefOf(MissionSectionKind.Objective, "o_nav"));
        Assert.Equal(["Type", "Text", "Text", "System", "Position"], Labels(objective, "objective"));
        Assert.Equal(["t_start"], Labels(objective, "setBy"));

        var conversation = Page(RefOf(MissionSectionKind.Conversation, "d_one"));
        Assert.Equal(["Comm lines exist", "Speakers in the system"], conversation.Completeness!.Steps.Select(s => s.Name));
        Assert.Equal(2, conversation.Section("lines")!.Fields.Single().List!.Rows.Count);
        Assert.StartsWith("Started by", conversation.Section("usedBy")!.Title);

        var encounter = Page(RefOf(MissionSectionKind.RandomEncounter, "enc_one"));
        Assert.Equal(["t_start"], Labels(encounter, "armedBy"));
        Assert.Equal("Random encounter", encounter.Kind);
        // The keys of the encounter have their named fields (section 10.16 of the M6a design). An unknown key goes to
        // Advanced.
        var keys = Labels(encounter, "encounter").ToList();
        foreach (var label in new[] { "Encounter type", "Attacker faction", "Target", "Starts by", "Formations", "Form count", "Labels" })
            Assert.Contains(label, keys);
        Assert.Equal(["odd_key"], Labels(encounter, "advanced"));
    }

    [Fact]
    public void TheLabelActAndPrefabPagesHaveTheirSections()
    {
        var label = Page(new MissionRef(Script, MissionItemKind.Label, Key: "l_wing"));
        Assert.Equal(["Mission ship"], Labels(label, "members"));

        var act = Page(new MissionRef(Script, MissionItemKind.Act, Key: "a:1"));
        Assert.Equal(["Name", "Walkthrough"], Labels(act, "act"));
        Assert.Equal(["t_start", "t_kill"], Labels(act, "triggers"));

        var instance = Run.Analysis.Prefabs!.Instances.First(i => i.Definition.Id == "spaceCutscene");
        var prefab = Page(new MissionRef(Script, MissionItemKind.PrefabInstance, Key: instance.Key));
        Assert.Equal("Behavior prefab", prefab.Kind);
        Assert.NotNull(prefab.Completeness);
        Assert.Equal(instance.Definition.Fields.Select(f => f.Label), Labels(prefab, "fields"));
        // Each field of a prefab has a help text, and the core trigger has its role.
        Assert.All(instance.Definition.Fields, f =>
            Assert.Equal(f.Help, prefab.Section("fields")!.Fields.First(field => field.Label == f.Label).Help));
        Assert.Equal("Play", prefab.Section("members")!.Fields[0].Label);
        Assert.NotEmpty(prefab.IniSections);
        Assert.Equal("INI (all triggers)", prefab.IniTitle);
        var patrol = Run.Analysis.Prefabs!.Instances.Single(i => i.Definition.Id == "patrol");
        Assert.Equal("INI (all sections)", Page(new MissionRef(Script, MissionItemKind.PrefabInstance, Key: patrol.Key)).IniTitle);

        var kind = Page(new MissionRef(Script, MissionItemKind.PrefabKind, Key: "spaceCutscene"));
        Assert.Equal(["whatItIs", "whatItWrites", "fields", "steps", "uses"], kind.Sections.Select(s => s.Id));
        Assert.Single(kind.Section("uses")!.Fields);
    }

    [Fact]
    public void TheMissionPageAndAnotherSectionKeepTheirPages()
    {
        Assert.Equal("Mission", Page(MissionRef.Of(Script)).Kind);
        var header = Run.Analysis.Document.OfKind(MissionSectionKind.Mission).Single();
        var other = Page(new MissionRef(Script, MissionItemKind.OtherSection, header.Ref));
        Assert.NotNull(other.Section("problems"));
    }
}

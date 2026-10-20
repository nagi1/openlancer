using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The read queries of the mission layer (section 4.12 of the M6b design) on the synthetic script.</summary>
public sealed class MissionQueriesTests
{
    private const string ScriptText = """
        [Mission]
        npc_ship_file = missions\ol_test\npcships.ini

        [NPC]
        nickname = ol_npc_1
        npc_ship_arch = ol_build_1
        affiliation = ol_faction
        voice = ol_voice

        [MsnShip]
        nickname = ol_ship_1
        system = ol_system
        npc = ol_npc_1
        label = ol_wing
        position = 100, 0, -200

        [Trigger]
        nickname = ol_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = ol_wave

        [Trigger]
        nickname = ol_wave
        Cnd_Timer = 3
        Act_SpawnShip = ol_ship_1
        Act_ActTrig = ol_end

        [Trigger]
        nickname = ol_end
        Cnd_Destroyed = ol_ship_1, -1
        Act_LockDock = Player, ol_station, lock
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = ol_lonely
        Act_ActTrig = ol_end
        """;

    [Fact]
    public void ListScriptsGivesTheScriptWithItsCountsAndVersion()
    {
        using var m = Create(ScriptText);

        var row = Assert.Single(m.Missions.Queries.ListScripts());

        // The result gives the path as the session stores it, and a path compares without letter case.
        Assert.Equal($"msn:{Script}", row.Ref, ignoreCase: true);
        Assert.Equal((4, 1L, MissionScriptState.BaseGame), (row.Triggers, row.Version, row.State));
        // The base game has the trigger without a condition, so its problem does not show.
        Assert.Equal(0, row.Errors);
    }

    [Fact]
    public void GetMissionGivesTheStartTriggersTheActsTheCastAndTheProblems()
    {
        using var m = Create(ScriptText);
        m.Apply(new AddTrigger(TestScript) { Nickname = "ol_new" });

        var summary = m.Missions.Queries.GetMission(TestScript);

        Assert.Equal([$"trg:{Script}#ol_start"], summary.StartTriggers);
        Assert.Contains($"trg:{Script}#ol_wave", summary.Acts.SelectMany(a => a.Triggers));
        Assert.Contains($"trg:{Script}#ol_lonely", summary.NotStarted);
        Assert.Equal([($"sec:{Script}#NPC/ol_npc_1", MissionSectionKind.Npc), ($"sec:{Script}#MsnShip/ol_ship_1", MissionSectionKind.Ship)],
            summary.Cast.Select(c => (c.Ref, c.Kind)));
        Assert.Equal("ol_system", summary.Cast[1].System);
        var problem = Assert.Single(summary.Problems, p => p.Rule == "M01");
        Assert.Equal(($"trg:{Script}#ol_new", OriginKind.Mod), (problem.Ref, problem.Origin));
        Assert.DoesNotContain(summary.Problems, p => p.Origin == OriginKind.BaseGame);
        Assert.Contains(m.Missions.Queries.GetProblems(TestScript, baseGame: true), p => p.Origin == OriginKind.BaseGame);
    }

    [Fact]
    public void GetTriggerGivesTheLinesWithNamedValuesNamesAndLinks()
    {
        using var m = Create(ScriptText);

        var trigger = m.Missions.Queries.GetTrigger(T("ol_end"));

        Assert.Equal(($"trg:{Script}#ol_end", false, "ol_end"), (trigger.Ref, trigger.StartsWithMission, trigger.Nickname));
        Assert.NotNull(trigger.Event);
        Assert.Equal([$"trg:{Script}#ol_wave", $"trg:{Script}#ol_lonely"], trigger.LinksIn.Select(l => l.Trigger));
        Assert.Equal($"line:{Script}#Trigger/ol_wave/3", trigger.LinksIn[0].Line);
        var destroyed = trigger.Lines[1];
        Assert.Equal(($"line:{Script}#Trigger/ol_end/1", "Cnd_Destroyed", "ol_ship_1", "-1"),
            (destroyed.Ref, destroyed.Key, destroyed.Values["target"], destroyed.Values["count"]));
        var name = Assert.Single(destroyed.Names);
        Assert.Equal(($"sec:{Script}#MsnShip/ol_ship_1", MissionReferenceState.Found), (name.Ref, name.State));
        Assert.Equal(["Player", "ol_station", "lock"], trigger.Lines[2].Texts);
        Assert.Equal(EditErrorKind.NotFound,
            Assert.Throws<EditException>(() => m.Missions.Queries.GetTrigger(T("ol_none"))).Error.Kind);
    }

    [Fact]
    public void GetItemGivesTheLinesAndTheUsersOfAnItemALabelAndAShipBuild()
    {
        using var m = Create(ScriptText);
        var queries = m.Missions.Queries;

        var ship = queries.GetItem(Item("MsnShip", "ol_ship_1"));
        var label = queries.GetItem(new LabelRef(Script, "ol_wing"));
        var build = queries.GetItem(Item("NPCShipArch", "ol_build_1", Ships));

        Assert.Equal("100, 0, -200", ship.Lines[4].Values["position"]);
        Assert.Equal([$"line:{Script}#Trigger/ol_wave/2", $"line:{Script}#Trigger/ol_end/1"], ship.Users);
        Assert.Equal([$"sec:{Script}#MsnShip/ol_ship_1"], label.Members);
        Assert.Equal(("ol_arch", "NPCShipArch"), (build.Lines[1].Values["ship_archetype"], build.Type));
        Assert.Equal([$"line:{Script}#NPC/ol_npc_1/1"], build.Users);
    }

    [Fact]
    public void FindReferencesFindsTheLinesOfAScriptItemAndOfAWorldName()
    {
        using var m = Create(ScriptText);
        var queries = m.Missions.Queries;

        Assert.Equal([$"line:{Script}#Trigger/ol_wave/3", $"line:{Script}#Trigger/ol_lonely/1"],
            queries.FindReferences($"trg:{Script}#ol_end").Select(r => r.Line));
        var station = Assert.Single(queries.FindReferences("obj:ol_station"));
        Assert.Equal(($"line:{Script}#Trigger/ol_end/2", "Act_LockDock", 1), (station.Line, station.Key, station.Value));
        Assert.Equal(EditErrorKind.InvalidArgument,
            Assert.Throws<EditException>(() => queries.FindReferences("zone:Zone_x")).Error.Kind);
    }

    [Fact]
    public void ThePaletteHasEachNameWithItsMarks()
    {
        using var m = Create(ScriptText);

        var palette = m.Missions.Queries.ListPalette();

        var schema = MissionSchema.Retail;
        Assert.Equal(schema.Conditions.Count + schema.Actions.Count + schema.Directives.Count, palette.Count);
        var destroyed = palette.Single(n => n.Name == "Cnd_Destroyed");
        Assert.Equal((MissionNameKind.Condition, 1), (destroyed.Kind, destroyed.BaseCount));
        Assert.Contains(destroyed.Values, v => v.Name == "target");
        Assert.True(palette.Single(n => n.Name == "Cnd_RumorHeard").NoRetailExample);
        Assert.Contains(palette.Single(n => n.Name == "Act_ChangeState").Values[0].Words, w => w == "SUCCEED");
    }

    [Fact]
    public void ThePrefabsAndAPrefabInstance()
    {
        using var m = Create(ScriptText);
        var queries = m.Missions.Queries;

        var prefabs = queries.ListPrefabs();
        var instance = Assert.Single(queries.GetMission(TestScript).Instances, i => i.Core == $"trg:{Script}#ol_wave");
        var view = queries.GetPrefabInstance(instance.Ref);

        Assert.Equal(MissionPrefabs.Retail.Definitions.Count, prefabs.Count);
        Assert.All(prefabs, p => Assert.StartsWith("prefab:builtin:", p.Ref));
        Assert.Equal((instance.Ref, instance.Prefab), (view.Ref, view.Prefab));
        Assert.NotEmpty(view.Fields);
        Assert.NotEmpty(view.Steps);
    }

    /// <summary>A read result feeds a command: the reference of a line of a query goes into a command.</summary>
    [Fact]
    public void AReadResultFeedsACommand()
    {
        using var m = Create(ScriptText);
        var trigger = m.Missions.Queries.GetTrigger(T("ol_wave"));
        var line = trigger.Lines.Single(l => l.Key == "Act_SpawnShip");

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            label = "No spawn",
            commands = new object[] { new { kind = "remove_action", row = new { @ref = line.Ref, expect_key = line.Key } } },
        });
        var result = m.Missions.Apply(MissionJson.ReadBatch(json));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.DoesNotContain("Act_SpawnShip = ol_ship_1", m.Lines("ol_wave"));
        Assert.Equal(trigger.Version + 1, m.Missions.Queries.GetTrigger(T("ol_wave")).Version);
    }
}

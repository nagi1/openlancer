using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The fields of matched prefab instances (section 5.8 of the M6b design): each write rule writes the lines that its
/// field reads, a field that shows structure refuses with <c>FieldNotWritable</c>, and "Save as prefab" and variants make
/// prefab assets (section 5.9). All names are invented.
/// </summary>
public sealed class MatchedFieldWriteTests
{
    /// <summary>A script with a spawn wave, a success, a checkpoint, a patrol and an escort that the matcher
    /// finds.</summary>
    private const string Text = """
        [Mission]

        [NPC]
        nickname = ol_npc_1
        npc_ship_arch = ol_build_1

        [MsnShip]
        nickname = ol_ship_1
        npc = ol_npc_1

        [MsnShip]
        nickname = ol_ship_2
        npc = ol_npc_1

        [MsnFormation]
        nickname = ol_form
        position = 0, 0, 0
        formation = ol_shape
        ship = ol_ship_2

        [ObjList]
        nickname = ol_orders
        GotoVec = goto_cruise, 0, 0, 1000, 100
        GotoVec = goto_cruise, 500, 0, 1000, 100

        [ObjList]
        nickname = ol_follow
        Follow = Player, 300

        [Trigger]
        nickname = ol_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = ol_wave
        Act_ActTrig = ol_save

        [Trigger]
        nickname = ol_wave
        Cnd_Timer = 3
        Act_SpawnFormation = ol_form
        Act_GiveObjList = ol_form, ol_orders
        Act_SetVibe = ol_form, Player, -0.9
        Act_ActTrig = ol_wave_end

        [Trigger]
        nickname = ol_wave_end
        Cnd_Destroyed = ol_form, -1
        Act_ActTrig = ol_win

        [Trigger]
        nickname = ol_win
        Cnd_True = no_params
        Act_AdjAcct = 1000
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = ol_save
        Cnd_True = no_params
        Act_Save = ol_restore

        [Trigger]
        nickname = ol_restore
        Cnd_True = no_params
        """;

    private static MissionEdits Create() => MissionEdits.Create(Text);

    private static InstanceRef I(string id) => new(Script, id);

    /// <summary>Writes a field, checks the lines, then checks that the inverse gives the state before.</summary>
    private static void Write(MissionEdits m, string instance, string field, string[] rows, string trigger, params string[] lines)
    {
        var before = m.State();
        var result = m.Apply(new SetPrefabField(I(instance), field, rows));

        Assert.Equal(lines, m.Lines(trigger.Contains('/') ? ItemRef.Parse(trigger) : T(trigger).Item));
        m.Apply(result.Inverse!);
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void TheMatcherFindsTheInstancesOfTheTest()
    {
        using var m = Create();

        var ids = m.Missions.Model(Script).Prefabs!.Instances.Select(i => i.Id).ToList();

        Assert.Contains("spawnWave/ol_wave", ids);
        Assert.Contains("success/ol_win", ids);
        Assert.Contains("checkpoint/ol_save", ids);
        Assert.Contains("patrol/ol_orders", ids);
        Assert.Contains("escort/ol_follow", ids);
    }

    [Fact]
    public void AValueWritesTheValueOfItsAction() =>
        WithScript(m => Write(m, "success/ol_win", "reward", ["2500"], "ol_win",
            "nickname = ol_win", "Cnd_True = no_params", "Act_AdjAcct = 2500", "Act_ChangeState = SUCCEED"));

    [Fact]
    public void AValueWithoutItsLineAddsTheLine() =>
        WithScript(m => Write(m, "success/ol_win", "endScene", [@"missions\ol\ol_room.thn"], "ol_win",
            "nickname = ol_win", "Cnd_True = no_params", "Act_AdjAcct = 1000", "Act_ChangeState = SUCCEED",
            @"Act_AddRTC = missions\\ol\\ol_room.thn"));

    [Fact]
    public void AConditionWritesTheConditionOfTheRoleTrigger() =>
        WithScript(m => Write(m, "spawnWave/ol_wave", "spawnWhen", ["Cnd_Timer = 8"], "ol_wave",
            "nickname = ol_wave", "Cnd_Timer = 8", "Act_SpawnFormation = ol_form", "Act_GiveObjList = ol_form, ol_orders",
            "Act_SetVibe = ol_form, Player, -0.9", "Act_ActTrig = ol_wave_end"));

    [Fact]
    public void AListOfConditionsWritesOneConditionForEachTrigger()
    {
        using var m = Create();
        Write(m, "spawnWave/ol_wave", "endsWhen", ["Cnd_Destroyed = ol_form, 2"], "ol_wave_end",
            "nickname = ol_wave_end", "Cnd_Destroyed = ol_form, 2", "Act_ActTrig = ol_win");

        var error = m.Refuse(new SetPrefabField(I("spawnWave/ol_wave"), "endsWhen", ["Cnd_True = no_params", "Cnd_True = no_params"]),
            MissionErrors.FieldValue);
        Assert.Contains("so it takes 1 rows", error.Message);
    }

    [Fact]
    public void AListOfValuesWritesTheLinesOfItsActions() =>
        WithScript(m => Write(m, "spawnWave/ol_wave", "ships", ["Act_SpawnShip = ol_ship_1", "Act_SpawnFormation = ol_form"], "ol_wave",
            "nickname = ol_wave", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1", "Act_SpawnFormation = ol_form",
            "Act_GiveObjList = ol_form, ol_orders", "Act_SetVibe = ol_form, Player, -0.9", "Act_ActTrig = ol_wave_end"));

    [Fact]
    public void TheLinesOfAGroupWriteTheLinesOfTheGroupAndALineOfAnotherGroupRefuses()
    {
        using var m = Create();
        Write(m, "spawnWave/ol_wave", "sides", ["Act_SetVibe = ol_form, Player, -0.5", "Act_SetRep = Player, ol_faction, 0.2"],
            "ol_wave", "nickname = ol_wave", "Cnd_Timer = 3", "Act_SpawnFormation = ol_form", "Act_GiveObjList = ol_form, ol_orders",
            "Act_SetVibe = ol_form, Player, -0.5", "Act_SetRep = Player, ol_faction, 0.2", "Act_ActTrig = ol_wave_end");

        m.Refuse(new SetPrefabField(I("spawnWave/ol_wave"), "sides", ["Act_SpawnShip = ol_ship_1"]), MissionErrors.FieldValue);
    }

    [Fact]
    public void TheLinesOfKeysWriteTheOrdersOfTheCoreSection() =>
        WithScript(m => Write(m, "patrol/ol_orders", "waypoints", ["GotoVec = goto_cruise, 1, 2, 3, 100"], "sec:" + Script + "#ObjList/ol_orders",
            "nickname = ol_orders", "GotoVec = goto_cruise, 1, 2, 3, 100"));

    [Fact]
    public void AKeyWritesTheValueOfTheLineOfTheCoreSection()
    {
        using var m = Create();
        Write(m, "escort/ol_follow", "follows", ["ol_ship_1"], "sec:" + Script + "#ObjList/ol_follow", "nickname = ol_follow",
            "Follow = ol_ship_1, 300");
        Write(m, "escort/ol_follow", "distance", ["500"], "sec:" + Script + "#ObjList/ol_follow", "nickname = ol_follow",
            "Follow = Player, 500");
    }

    [Fact]
    public void AFieldThatShowsStructureRefuses()
    {
        using var m = Create();

        var error = m.Refuse(new SetPrefabField(I("checkpoint/ol_save"), "savesAt", ["ol_start"]), MissionErrors.FieldNotWritable);

        Assert.Equal("Saves at shows the triggers of the prefab. Change it in the graph.", error.Message);
        m.Refuse(new SetPrefabField(I("checkpoint/ol_save"), "speed", ["1"]), MissionErrors.FieldNotFound);
        m.Refuse(new SetPrefabField(I("checkpoint/ol_none"), "savesAt", ["1"]), MissionErrors.InstanceNotFound);
    }

    [Fact]
    public void EachFieldOfTheCatalogWithAWriteRuleFitsItsSource()
    {
        var writable = MissionPrefabs.Retail.Definitions.SelectMany(d => d.Fields).Where(f => f.Write != PrefabWrite.None).ToList();

        Assert.True(writable.Count >= 30);
        Assert.All(MissionPrefabs.Retail.Definitions.SelectMany(d => d.Fields).Where(f => f.From.Trigger),
            f => Assert.Equal(PrefabWrite.None, f.Write));
    }

    [Fact]
    public void SaveAsPrefabMakesAnAssetAndTheInstanceBecomesALinkedInstanceWithNoOverride()
    {
        using var m = Create();
        var lines = new[] { "ol_wave", "ol_wave_end" }.Select(m.Lines).ToList();

        var result = m.Apply(new CreatePrefabAsset("ol_saved") { Name = "Saved wave", Source = I("spawnWave/ol_wave") });

        Assert.Equal(lines, new[] { "ol_wave", "ol_wave_end" }.Select(m.Lines));
        var template = m.Missions.Prefab(PrefabRef.ModOf("ol_saved"))!.Template!;
        Assert.Equal(("Saved wave", "builtin:spawnWave"), (template.Name, template.VariantOf));
        Assert.Equal(["core", "end"], template.Sections.Select(s => s.Role));
        Assert.Contains("spawnWhen", template.Fields.Select(f => f.Id));
        Assert.Contains("ships", template.Fields.Select(f => f.Id));
        Assert.Equal([PrefabRow.Line("Cnd_Timer", "3")], template.Field("spawnWhen")!.Default);
        Assert.Contains(template.Section("core")!.Lines, l => l.Key == "Act_ActTrig" && l.Value == "{instance}_end");
        var record = m.Missions.Graph(Script).Instance("ol_wave")!;
        Assert.Equal([new InstanceMember("core", "ol_wave"), new InstanceMember("end", "ol_wave_end")], record.Members);
        // The start of the outside trigger ol_win is a port, so the prefab names no trigger of this mission. The record
        // links the port to ol_win, as Unity keeps a reference to a scene object on the instance.
        Assert.Equal([new PrefabPort("end_start", "Start trigger after 'end'", "end")], template.Ports);
        Assert.Contains(template.Section("end")!.Lines, l => l.Key == "Act_ActTrig" && l.Value == "{port:end_start}");
        Assert.DoesNotContain(template.Sections.SelectMany(s => s.Lines), l => l.Value.Contains("ol_win"));
        Assert.Equal([new InstancePort("end_start", "ol_win")], record.Ports);
        Assert.Empty(record.Overrides);
        var linked = m.Missions.Model(Script).Prefabs!.Instances.Single(i => i.Linked?.Record.Id == "ol_wave");
        Assert.Equal(0, linked.Linked!.OverrideCount);
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule is "P01" or "P02" or "P03");
        // The selection stays on the instance (review finding 3 of the studio review of M6b-3).
        Assert.Equal(new InstanceRef(Script, "ol_wave").ToString(), result.Select);

        m.Apply(new SetPrefabField(new InstanceRef(Script, "ol_wave"), "spawnWhen", ["Cnd_Timer = 9"]));

        Assert.Contains("Cnd_Timer = 9", m.Lines("ol_wave"));
    }

    [Fact]
    public void SaveAsPrefabOfAnOrderPrefabGivesItsKeysAsFields()
    {
        using var m = Create();

        m.Apply(new CreatePrefabAsset("ol_saved_escort") { Source = I("escort/ol_follow") });

        var template = m.Missions.Prefab(PrefabRef.ModOf("ol_saved_escort"))!.Template!;
        Assert.Equal(MissionPrefabKind.Order, template.Kind);
        Assert.Equal(["follows", "distance"], template.Fields.Select(f => f.Id));
        Assert.Equal("{follows}, {distance}", template.Section("core")!.Lines.Single(l => l.Key == "Follow").Value);
        Assert.Equal(["nickname = ol_follow", "Follow = Player, 300"], m.Lines(Item("ObjList", "ol_follow")));
    }

    [Fact]
    public void AVariantOfABuiltInPrefabCopiesItsTemplate()
    {
        using var m = Create();

        m.Apply(new CreatePrefabAsset("ol_wave_v") { VariantOf = PrefabRef.BuiltinOf("spawnWave") });

        var variant = m.Missions.Prefab(PrefabRef.ModOf("ol_wave_v"))!;
        Assert.True(variant.Active);
        Assert.Equal(("Spawn wave variant", "builtin:spawnWave"), (variant.Template!.Name, variant.Template.VariantOf));
        Assert.Equal(BuiltinPrefabs.Template("spawnWave")!.ContentHash, variant.Template.ContentHash);
    }

    private static void WithScript(System.Action<MissionEdits> test)
    {
        using var m = Create();
        test(m);
    }
}

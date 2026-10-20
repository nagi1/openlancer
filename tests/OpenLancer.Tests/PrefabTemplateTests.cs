using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The template format of the behavior prefabs and its compile (sections 5.3 and 5.4 of the M6b design): the problems
/// of the format check, the compile that gives the same lines each time, the lines that repeat for list rows and ports,
/// and the 17 built-in templates. All names are invented.
/// </summary>
public sealed class PrefabTemplateTests
{
    /// <summary>The template of the example of section 5.3 of the design, with a list field and a lines field.</summary>
    private const string Ambush = PrefabTemplate.Header + """

        [Prefab]
        id = ol_gate_ambush
        name = Gate ambush
        kind = beat
        variant_of = builtin:spawnWave
        format = 1

        [Field]
        id = formation
        label = Enemy formation
        kind = formation
        per_instance = true
        default = none

        [Field]
        id = delay
        label = Spawn after
        kind = number
        unit = s
        default = 5

        [Field]
        id = marks
        label = Marked ships
        kind = list
        column = ship, object, Ship
        column = mark, wholeNumber, Mark
        default = ol_ship_a, 1

        [Field]
        id = sides
        label = Sides
        kind = lines
        key = Act_SetVibe

        [Port]
        id = done
        label = When all are destroyed
        role = end

        [Trigger]
        role = spawn
        nickname = {instance}_spawn
        Cnd_Timer = {delay}
        Act_SpawnFormation = {formation}
        Act_MarkObj = {marks.ship}, {marks.mark}
        {sides} =
        Act_ActTrig = {instance}_end

        [Trigger]
        role = end
        nickname = {instance}_end
        Cnd_Destroyed = {formation}, -1
        Act_ActTrig = {port:done}

        """;

    private static PrefabTemplate Read(string text)
    {
        var template = PrefabTemplate.Read(text, out var problems);
        Assert.Empty(problems);
        return template!;
    }

    private static IReadOnlyList<string> Problems(string text)
    {
        PrefabTemplate.Read(text, out var problems);
        return problems.Select(p => p.ToString()).ToList();
    }

    private static string[] Lines(CompiledSection section) => section.Entries.Select(e => e.ToString()).ToArray();

    [Fact]
    public void ATemplateReadsAndWritesItsParts()
    {
        var template = Read(Ambush);

        Assert.Equal(("ol_gate_ambush", "Gate ambush", MissionPrefabKind.Beat, "builtin:spawnWave"),
            (template.Id, template.Name, template.Kind, template.VariantOf));
        Assert.Equal(["formation", "delay", "marks", "sides"], template.Fields.Select(f => f.Id));
        Assert.True(template.Field("formation")!.PerInstance);
        Assert.Equal("s", template.Field("delay")!.Unit);
        Assert.Equal(["ship", "mark"], template.Field("marks")!.Columns.Select(c => c.Id));
        Assert.Equal(new PrefabPort("done", "When all are destroyed", "end"), Assert.Single(template.Ports));
        Assert.Equal(["spawn", "end"], template.Sections.Select(s => s.Role));
        Assert.Equal("{instance}_spawn", template.Section("spawn")!.NicknamePattern);
        Assert.Equal(Ambush.Replace("\r\n", "\n").Trim(), template.Write().Trim());
        Assert.Equal(template, Read(template.Write()));
    }

    [Fact]
    public void TheHashChangesWithTheLinesAndNotWithTheName()
    {
        var template = Read(Ambush);

        Assert.Equal(16, template.ContentHash.Length);
        Assert.Equal(template.ContentHash, (template with { Name = "Other name" }).ContentHash);
        Assert.NotEqual(template.ContentHash, Read(Ambush.Replace("default = 5", "default = 6")).ContentHash);
    }

    [Fact]
    public void AFileWithoutPrefabFirstOrWithAnotherFormatGivesAProblem()
    {
        Assert.Equal(["Line 1 of the prefab: A prefab file starts with a [Prefab] section."],
            Problems("[Trigger]\nrole = a\nnickname = x\n"));
        Assert.Equal(["Line 4 of the prefab: The prefab has format 2. This editor reads only format 1."],
            Problems("[Prefab]\nid = x\nname = X\nformat = 2\n"));
        Assert.Null(PrefabTemplate.Read("[Prefab]\nid = x\nformat = 2\n", out _));
    }

    [Fact]
    public void ASlotThatNamesNoFieldARepeatedRoleAndAnUnknownKeyGiveProblems()
    {
        var text = Ambush
            .Replace("Cnd_Timer = {delay}", "Cnd_Timer = {wait}")
            .Replace("role = end", "role = spawn")
            .Replace("Act_SpawnFormation = {formation}", "Act_SpawnFormation = {formation}\nInitState = ACTIVE\nspeed = 5");

        var problems = Problems(text);

        Assert.Contains(problems, p => p.EndsWith("The slot {wait} names no field of the prefab."));
        Assert.Contains(problems, p => p.EndsWith("The role 'spawn' repeats. Each template section has its own role."));
        Assert.Contains(problems, p => p.EndsWith("The game does not read 'speed' in a [Trigger] section. Pick a field of the page."));
        Assert.All(problems, p => Assert.StartsWith("Line ", p));
        Assert.Contains(Problems(Ambush.Replace("role = end\n\n[Trigger]", "role = finish\n\n[Trigger]")),
            p => p.EndsWith("Port 'done' names the role 'finish', which no template section has."));
    }

    [Fact]
    public void TheOtherProblemsOfTheFormatCheck()
    {
        var problems = Problems(Ambush
            .Replace("kind = number", "kind = speed")
            .Replace("column = mark, wholeNumber, Mark", "")
            .Replace("column = ship, object, Ship", "")
            .Replace("Act_MarkObj = {marks.ship}, {marks.mark}", "Act_MarkObj = {marks.ship}, {port:done}")
            .Replace("[Trigger]\nrole = end", "[MsnSpawn]\nrole = end"));

        Assert.Contains(problems, p => p.EndsWith("'speed' is not a field kind. Use a value kind of the mission schema, list, triggers, condition or lines."));
        Assert.Contains(problems, p => p.Contains("List field 'marks' has no 'column' line."));
        Assert.Contains(problems, p => p.EndsWith("A line can repeat for one list or one port only. Split the line."));
        Assert.Contains(problems, p => p.Contains("[MsnSpawn] is not a section type of a mission script."));
    }

    [Fact]
    public void ADefaultOfTheWrongKindGivesAProblem()
    {
        var problems = Problems(Ambush.Replace("default = 5", "default = soon"));

        Assert.Contains(problems, p => p.EndsWith("'soon' is not a number. Spawn after needs a number."));
    }

    [Fact]
    public void TheCompileGivesTheSameLinesEachTime()
    {
        var template = Read(Ambush);
        var input = new PrefabInput("ol_wave_1")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>> { ["formation"] = [PrefabRow.Of("ol_form")] },
            Ports = new Dictionary<string, IReadOnlyList<string>> { ["done"] = ["ol_after"] },
        };

        var first = PrefabCompiler.Compile(template, input);
        var second = PrefabCompiler.Compile(template, input);

        Assert.Equal(first.Select(Lines).SelectMany(l => l), second.Select(Lines).SelectMany(l => l));
        Assert.Equal(["nickname = ol_wave_1_spawn", "Cnd_Timer = 5", "Act_SpawnFormation = ol_form", "Act_MarkObj = ol_ship_a, 1",
            "Act_ActTrig = ol_wave_1_end"], Lines(first[0]));
        Assert.Equal(["nickname = ol_wave_1_end", "Cnd_Destroyed = ol_form, -1", "Act_ActTrig = ol_after"], Lines(first[1]));
        Assert.Equal(("spawn", "Trigger", "ol_wave_1_spawn"), (first[0].Role, first[0].Type, first[0].Nickname));
        Assert.Equal(PrefabCompiler.LinesHash(PrefabCompiler.Sections(first)), PrefabCompiler.LinesHash(PrefabCompiler.Sections(second)));
    }

    [Fact]
    public void ListRowsAndPortsRepeatLinesAndAnEmptyListRemovesThem()
    {
        var template = Read(Ambush);
        var input = new PrefabInput("w")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>>
            {
                ["formation"] = [PrefabRow.Of("ol_form")],
                ["marks"] = [PrefabRow.Of("ol_a", "1"), PrefabRow.Of("ol_b", "0")],
                ["sides"] = [PrefabRow.Line("Act_SetVibe", "ol_a", "Player", "-0.9"), PrefabRow.Line("Act_SetVibe", "ol_b", "Player", "-0.9")],
            },
            Ports = new Dictionary<string, IReadOnlyList<string>> { ["done"] = ["ol_x", "ol_y"] },
        };

        var compiled = PrefabCompiler.Compile(template, input);
        var empty = PrefabCompiler.Compile(template, input with
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>> { ["formation"] = [PrefabRow.Of("ol_form")], ["marks"] = [] },
            Ports = new Dictionary<string, IReadOnlyList<string>>(),
        });

        Assert.Equal(["nickname = w_spawn", "Cnd_Timer = 5", "Act_SpawnFormation = ol_form", "Act_MarkObj = ol_a, 1",
            "Act_MarkObj = ol_b, 0", "Act_SetVibe = ol_a, Player, -0.9", "Act_SetVibe = ol_b, Player, -0.9",
            "Act_ActTrig = w_end"], Lines(compiled[0]));
        Assert.Equal(["nickname = w_end", "Cnd_Destroyed = ol_form, -1", "Act_ActTrig = ol_x", "Act_ActTrig = ol_y"],
            Lines(compiled[1]));
        Assert.Equal(["nickname = w_spawn", "Cnd_Timer = 5", "Act_SpawnFormation = ol_form", "Act_ActTrig = w_end"], Lines(empty[0]));
        Assert.Equal(["nickname = w_end", "Cnd_Destroyed = ol_form, -1"], Lines(empty[1]));
    }

    [Fact]
    public void TheMembersOfTheRecordKeepTheirNicknamesAndTheLinesThatNameThem()
    {
        var input = new PrefabInput("w")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>> { ["formation"] = [PrefabRow.Of("ol_form")] },
            Members = new Dictionary<string, string> { ["end"] = "ol_old_end" },
        };

        var compiled = PrefabCompiler.Compile(Read(Ambush), input);

        Assert.Equal("Act_ActTrig = ol_old_end", Lines(compiled[0])[^1]);
        Assert.Equal("nickname = ol_old_end", Lines(compiled[1])[0]);
    }

    [Fact]
    public void ARemovedMemberIsNotInTheCompile()
    {
        var input = new PrefabInput("w")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>> { ["formation"] = [PrefabRow.Of("ol_form")] },
            Removed = new HashSet<string> { "end" },
        };

        var compiled = PrefabCompiler.Compile(Read(Ambush), input);

        Assert.Equal("spawn", Assert.Single(compiled).Role);
    }

    [Fact]
    public void ABadValueRefusesTheCompileWithTheFieldAndTheReason()
    {
        var template = Read(Ambush);

        var kind = Assert.Throws<EditException>(() => PrefabCompiler.Compile(template, new PrefabInput("w")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>> { ["delay"] = [PrefabRow.Of("soon")] },
        })).Error;
        var line = Assert.Throws<EditException>(() => PrefabCompiler.Compile(template, new PrefabInput("w")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>> { ["sides"] = [PrefabRow.Line("Act_SetVibe", "ol_a")] },
        })).Error;
        var name = Assert.Throws<EditException>(() => PrefabCompiler.Compile(template, new PrefabInput(new string('w', 44)))).Error;

        Assert.Equal((MissionErrors.FieldValue, "The value of field 'Spawn after' is not valid. 'soon' is not a number. Spawn after needs a number."),
            (kind.Detail, kind.Message));
        Assert.Equal(MissionErrors.FieldValue, line.Detail);
        Assert.Contains("Sides", line.Message);
        Assert.Equal(MissionErrors.NameInvalid, name.Detail);
        Assert.Contains("Give the instance a shorter id.", name.Message);
    }

    [Fact]
    public void AnOptionalValueWithoutAValueRemovesItsLines()
    {
        var template = BuiltinPrefabs.Template("spawnWave")!;
        var with = PrefabCompiler.Compile(template, new PrefabInput("w")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>>
            {
                ["ships"] = [PrefabRow.Of("ol_form")], ["orders"] = [PrefabRow.Of("ol_orders")],
            },
        });
        var without = PrefabCompiler.Compile(template, new PrefabInput("w")
        {
            Values = new Dictionary<string, IReadOnlyList<PrefabRow>> { ["ships"] = [PrefabRow.Of("ol_form")] },
        });

        Assert.Contains("Act_GiveObjList = ol_form, ol_orders", Lines(with[0]));
        Assert.DoesNotContain(Lines(without[0]), l => l.StartsWith("Act_GiveObjList"));
    }

    /// <summary>The script that each built-in instance compiles into: the names that its synthetic values name.</summary>
    private const string Cast = """
        [Mission]

        [NPC]
        nickname = ol_npc
        npc_ship_arch = ol_build

        [MsnShip]
        nickname = ol_ship_1
        npc = ol_npc
        position = 0, 0, 0

        [MsnShip]
        nickname = ol_ship_2
        npc = ol_npc

        [MsnFormation]
        nickname = ol_form
        position = 0, 0, 0
        formation = ol_shape
        ship = ol_ship_2

        [ObjList]
        nickname = ol_orders
        Delay = 5

        [NNObjective]
        nickname = ol_objective
        type = ids, 1000
        state = HIDDEN

        [Trigger]
        nickname = ol_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ChangeState = SUCCEED

        """;

    /// <summary>A synthetic value of a kind that names an item of <see cref="Cast"/>, or a valid value.</summary>
    private static PrefabRow Synthetic(MissionValueKind kind) => kind switch
    {
        MissionValueKind.Formation or MissionValueKind.Group => PrefabRow.Of("ol_form"),
        MissionValueKind.Object => PrefabRow.Of("ol_ship_1"),
        MissionValueKind.Trigger => PrefabRow.Of("ol_start"),
        MissionValueKind.ObjList => PrefabRow.Of("ol_orders"),
        MissionValueKind.Objective => PrefabRow.Of("ol_objective"),
        MissionValueKind.Position => PrefabRow.Of("1", "2", "3"),
        MissionValueKind.Number or MissionValueKind.WholeNumber or MissionValueKind.TextId => PrefabRow.Of("1000"),
        MissionValueKind.SpaceCutscene => PrefabRow.Of(@"missions\\ol\\ol_scene.thn"),
        MissionValueKind.BaseScene => PrefabRow.Of(@"missions\\ol\\ol_room.thn"),
        _ => PrefabRow.Of("ol_" + PrefabFieldDef.KindName(kind).ToLowerInvariant()),
    };

    /// <summary>The synthetic rows of a field: one row of each list, and the first key of a lines field.</summary>
    private static IReadOnlyList<PrefabRow> Synthetic(PrefabFieldDef field) => field.Kind switch
    {
        PrefabFieldKind.Condition => field.Default,
        PrefabFieldKind.Lines => [(field.Keys.FirstOrDefault() ?? "Act_SetVibe") switch
        {
            "Act_SetRep" => PrefabRow.Line("Act_SetRep", "Player", "ol_faction", "0.5"),
            "Act_LockDock" => PrefabRow.Line("Act_LockDock", "Player", "ol_ship_1", "lock"),
            var key => PrefabRow.Line(key, "ol_ship_1", "Player", "-0.9"),
        }],
        PrefabFieldKind.List => [new PrefabRow(null, field.Columns.SelectMany(c => Synthetic(c.Kind).Texts).ToList())],
        PrefabFieldKind.Triggers => [PrefabRow.Of("ol_start")],
        _ => [Synthetic(field.ValueKind)],
    };

    [Fact]
    public void TheSeventeenBuiltInTemplatesLoad()
    {
        Assert.Equal(17, BuiltinPrefabs.Ids.Count);
        Assert.Equal(MissionPrefabs.Retail.Definitions.Select(d => d.Id), BuiltinPrefabs.Ids);
        foreach (var id in BuiltinPrefabs.Ids)
        {
            var template = BuiltinPrefabs.Template(id)!;
            var definition = MissionPrefabs.Retail.Definition(id)!;
            Assert.Equal((definition.Name, definition.Kind), (template.Name, template.Kind));
            Assert.Equal(template, Read(BuiltinPrefabs.Text(id)!));
            Assert.NotEmpty(template.Sections);
        }
    }

    /// <summary>Each built-in template compiles with synthetic field values, and the script with its sections passes the
    /// checks: no error of any rule.</summary>
    [Fact]
    public void EachBuiltInTemplateCompilesWithSyntheticValuesAndPassesTheChecks()
    {
        foreach (var id in BuiltinPrefabs.Ids)
        {
            var template = BuiltinPrefabs.Template(id)!;
            var input = new PrefabInput("ol_" + id.ToLowerInvariant())
            {
                Values = template.Fields.ToDictionary(f => f.Id, Synthetic),
                Ports = template.Ports.ToDictionary(p => p.Id, _ => (IReadOnlyList<string>)["ol_start"]),
            };

            var compiled = PrefabCompiler.Compile(template, input);

            Assert.Equal(template.Sections.Count, compiled.Count);
            var text = Cast + string.Join("\n", compiled.Select(c =>
                $"[{c.Type}]\n" + string.Join("\n", c.Entries.Select(e => e.ToString())) + "\n"));
            var errors = Check(text).Where(p => p.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{id}: {string.Join("\n", errors.Select(e => e.Message))}");
            Assert.Equal(compiled.Select(Lines).SelectMany(l => l), PrefabCompiler.Compile(template, input).Select(Lines).SelectMany(l => l));
        }
    }

    /// <summary>Each built-in template compiles with its default values, so a drop from the library can place it.</summary>
    [Fact]
    public void EachBuiltInTemplateCompilesWithItsDefaults()
    {
        foreach (var id in BuiltinPrefabs.Ids)
            Assert.NotEmpty(PrefabCompiler.Compile(BuiltinPrefabs.Template(id)!, new PrefabInput("ol_x")));
    }
}

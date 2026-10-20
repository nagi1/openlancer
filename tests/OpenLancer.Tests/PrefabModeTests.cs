using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using Xunit;
using static OpenLancer.Tests.MissionEdits;
using static OpenLancer.Tests.PrefabInstanceTests;

namespace OpenLancer.Tests;

/// <summary>
/// Prefab mode and the propagation (section 5.7 of the M6b design, decision D675): the trigger, line and link commands
/// on the script of a template, the field commands, and the compile of each linked instance of the asset in one undo
/// entry. The gate of section 11.4: a propagation to 50 linked instances in 5 scripts takes less than 1 s. All names are
/// invented.
/// </summary>
public sealed class PrefabModeTests(ITestOutputHelper output)
{
    private static readonly ScriptRef Gate = new(GateRef.Script);

    private static TriggerRef Role(string role) => new(GateRef.Script, role);

    private static PrefabTemplate Template(MissionEdits m) => m.Missions.Prefab(GateRef)!.Template!;

    [Fact]
    public void TheRoleFormGivesTheTemplateBack()
    {
        foreach (var template in BuiltinPrefabs.Ids.Select(BuiltinPrefabs.Template).Append(PrefabTemplate.Read(PrefabInstanceTests.Gate, out _)))
        {
            var sections = PrefabMode.RoleForm(template!);
            var back = PrefabTemplate.Read(PrefabMode.TemplateText(sections, template!), out var problems);

            Assert.Empty(problems);
            Assert.Equal(template, back);
        }
        var gate = PrefabMode.RoleForm(PrefabTemplate.Read(PrefabInstanceTests.Gate, out _)!);
        var spawn = gate.Single(s => s.Name == "Trigger" && PrefabMode.RoleOf(s) == "spawn");
        Assert.Equal("end", MissionIndex.Text(spawn.Single(e => e.Name == "Act_ActTrig")[0]));
    }

    [Fact]
    public void AnEditOfTheTemplateChangesEachInstanceInOneUndoEntry()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2", Two, "ol_form_b");
        var before = State(m);

        var result = m.Apply(new AddAction(Role("spawn"), LineSpec.Raw("Act_PlayMusic", "ol_music")));

        Assert.Contains("Act_PlayMusic = ol_music", m.Lines("ol_w1_spawn"));
        Assert.Contains("Act_PlayMusic = ol_music", m.Lines(T("ol_w2_spawn", Two).Item));
        Assert.Contains(Template(m).Section("spawn")!.Lines, l => l.Key == "Act_PlayMusic" && l.Value == "ol_music");
        Assert.Contains("Prefab 'Gate ambush' changed 2 instances in 2 missions.", result.Notes);
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule is "P01" or "P03");
        Assert.DoesNotContain(m.Missions.Model(Two).Problems, p => p.Rule is "P01" or "P03");

        m.Session.Undo();

        Assert.Equal(before, State(m));
    }

    [Fact]
    public void ALinkInsideTheTemplateNamesTheMadeNicknameOfTheMember()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");

        m.Apply(new LinkTriggers(Role("end"), "spawn", LinkKind.Start));

        Assert.Contains(Template(m).Section("end")!.Lines, l => l.Key == "Act_ActTrig" && l.Value == "{instance}_spawn");
        Assert.Contains("Act_ActTrig = ol_w1_spawn", m.Lines("ol_w1_end"));
    }

    [Fact]
    public void ANewTriggerInTheTemplateIsANewMemberOfEachInstance()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");

        m.Apply(new AddTrigger(Gate) { Nickname = "alarm", Condition = LineSpec.Raw("Cnd_True", "no_params") });

        Assert.Equal(["nickname = ol_w1_alarm", "Cnd_True = no_params"], m.Lines("ol_w1_alarm"));
        Assert.Contains(new InstanceMember("alarm", "ol_w1_alarm"), m.Missions.Graph(Script).Instance("ol_w1")!.Members);

        m.Apply(new RemoveTrigger(Role("alarm")));

        Assert.False(m.Has("ol_w1_alarm"));
        Assert.Null(Template(m).Section("alarm"));
    }

    [Fact]
    public void ARenameOfARoleKeepsTheMembersAndThePorts()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");

        m.Apply(new RenameTrigger(Role("end"), "finish"));

        var template = Template(m);
        Assert.NotNull(template.Section("finish"));
        Assert.Equal("finish", template.Port("done")!.Role);
        Assert.Contains(new InstanceMember("finish", "ol_w1_end"), m.Missions.Graph(Script).Instance("ol_w1")!.Members);
        Assert.True(m.Has("ol_w1_end"));
        Assert.Contains("Act_ActTrig = ol_w1_end", m.Lines("ol_w1_spawn"));
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule is "P01" or "P03");
    }

    [Fact]
    public void TheFieldCommandsChangeTheFieldsAndTheInstancesCompile()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));

        m.Apply(new SetPrefabFieldDefault(GateRef, "delay", ["6"]));

        Assert.Contains("Cnd_Timer = 8", m.Lines("ol_w1_spawn"));
        Assert.Contains("Cnd_Timer = 6", m.Lines("ol_w2_spawn"));

        m.Apply(new UnbindValue(new RowRef(Role("spawn").Item, 1)) { Value = "seconds" });

        Assert.Contains(Template(m).Section("spawn")!.Lines, l => l.Key == "Cnd_Timer" && l.Value == "6");
        Assert.Contains("Cnd_Timer = 6", m.Lines("ol_w1_spawn"));

        m.Apply(new BindValue(new RowRef(Role("spawn").Item, 1), "delay") { Value = "seconds" });

        Assert.Contains("Cnd_Timer = 8", m.Lines("ol_w1_spawn"));
        m.Refuse(new RemovePrefabField(GateRef, "delay"), MissionErrors.FieldInUse);

        m.Apply(new AddPrefabField(GateRef, "music", "Music", "sound") { Default = ["ol_music"] });
        m.Apply(new AddAction(Role("spawn"), LineSpec.Raw("Act_PlayMusic", "{music}")));

        Assert.Contains("Act_PlayMusic = ol_music", m.Lines("ol_w2_spawn"));
        m.Apply(new SetPrefabField(I("ol_w2"), "music", ["ol_other"]));
        Assert.Contains("Act_PlayMusic = ol_other", m.Lines("ol_w2_spawn"));
    }

    [Fact]
    public void AnEditThatGivesTheTemplateAProblemRefuses()
    {
        using var m = PrefabInstanceTests.Create();

        var error = m.Refuse(new AddAction(Role("spawn"), LineSpec.Raw("Act_PlayMusic", "{nothing}")), MissionErrors.PrefabProblems);

        Assert.Contains("The slot {nothing} names no field of the prefab.", error.Message);
    }

    [Fact]
    public void ABuiltInPrefabAndAPrefabOfALowerModRefuse()
    {
        using var m = MissionEdits.Create(lowerMod: new Dictionary<string, string>
        {
            [@"prefabs\missions\ol_low.ini"] = PrefabInstanceTests.Gate.Replace("ol_gate", "ol_low"),
        });

        m.Refuse(new AddAction(new TriggerRef("prefab:builtin:spawnWave", "spawn"), LineSpec.Raw("Act_PlayMusic", "ol_music")),
            MissionErrors.BuiltInPrefab);
        var error = m.Refuse(new AddAction(new TriggerRef("prefab:mod:ol_low", "spawn"), LineSpec.Raw("Act_PlayMusic", "ol_music")),
            MissionErrors.LowerModPrefab);
        Assert.Equal("Prefab 'Gate ambush' belongs to mod 'low'. Only a prefab of the active mod can change.", error.Message);
        Assert.Equal(PrefabModeModel.LowerModBanner("low"), m.Missions.PrefabMode(PrefabRef.ModOf("ol_low"))!.ReadOnlyReason);
        Assert.Equal(PrefabModeModel.BuiltInBanner, m.Missions.PrefabMode(PrefabRef.BuiltinOf("spawnWave"))!.ReadOnlyReason);
        Assert.Contains(m.Missions.Prefabs(), p => p.Ref.Id == "ol_low" && p is { Active: false, ModId: "low" });
    }

    [Fact]
    public void ThePrefabModeModelCountsTheInstancesAndShowsTheTemplate()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2");
        Place(m, "ol_w3", Two, "ol_form_b");

        var model = m.Missions.PrefabMode(GateRef)!;

        Assert.Equal((3, 2), (model.Instances, model.Scripts));
        Assert.Equal("Changes here change 3 instances in 2 missions of mod act.", model.BarText("act"));
        Assert.Null(model.ReadOnlyReason);
        Assert.Equal(["spawn", "end"], model.Analysis!.Index.Triggers.Select(t => t.Nickname));
        Assert.Equal(GateRef.Script, model.Analysis.Script.Path);
        Assert.Same(model.Analysis, m.Missions.PrefabMode(GateRef)!.Analysis);
    }

    /// <summary>In the graph of prefab mode, a port is an output pin (section 8.6 of the M6b design): the line of the port
    /// gives the Starts pin of its trigger, and no "Missing" node. A link between two roles shows as a link.</summary>
    [Fact]
    public void APortOfTheTemplateIsAnOutputPinWithoutAMissingNode()
    {
        using var m = PrefabInstanceTests.Create();

        var graph = MissionGraph.Build(m.Missions.PrefabMode(GateRef)!.Analysis!, new GraphOptions(GraphLens.Logic));

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == GraphNodeKind.Missing);
        var spawn = graph.TriggerNode(graph.Index.Trigger("spawn")!)!;
        var end = graph.TriggerNode(graph.Index.Trigger("end")!)!;
        Assert.NotNull(end.Pin(GraphPinKind.Starts));
        Assert.DoesNotContain(graph.Links, l => l.From == end);
        Assert.Contains(graph.Links, l => l.From == spawn && l.To == end);
    }

    /// <summary>The steps of a prefab in prefab mode (section 8.6 of the M6b design, UI rule 12): a field that no line
    /// holds and a file with a problem are missing steps.</summary>
    [Fact]
    public void TheStepsOfAPrefabNameAnUnusedFieldAndAProblemOfTheFile()
    {
        using var m = PrefabInstanceTests.Create(new Dictionary<string, string>
        {
            [@"prefabs\missions\ol_bad.ini"] = PrefabInstanceTests.Gate.Replace("id = ol_gate", "id = ol_bad").Replace("kind = beat", "kind = wave"),
        });
        m.Apply(new AddPrefabField(GateRef, "music", "Music", "sound") { Default = ["ol_music"] });

        var steps = MissionCompleteness.OfPrefab(m.Missions.PrefabMode(GateRef)!).Steps;
        var bad = MissionCompleteness.OfPrefab(m.Missions.PrefabMode(PrefabRef.ModOf("ol_bad"))!).Steps;

        Assert.Equal([CompletenessState.Done, CompletenessState.Done, CompletenessState.Missing], steps.Select(s => s.State));
        Assert.Equal("Field 'Music' is in no line, so it changes nothing. Make it the value of a line, or remove it.", steps[2].Detail);
        Assert.Equal(new MissionRef(GateRef.Script, MissionItemKind.Mission, Key: "field:music"), steps[2].GoTo);
        var file = Assert.Single(bad);
        Assert.Equal((CompletenessState.Missing, true), (file.State, file.IsError));
        Assert.Equal("Line 5 of the prefab: 'wave' is not a prefab kind. Use 'beat' or 'order'.", file.Detail);
    }

    /// <summary>A command of each field kind of prefab mode on the asset ol_gate with the instance ol_w1.</summary>
    internal static readonly Dictionary<string, Func<MissionCommand>> Commands = new()
    {
        ["add_prefab_field"] = () => new AddPrefabField(GateRef, "music", "Music", "sound") { Default = ["ol_music"], Help = "The music." },
        ["remove_prefab_field"] = () => new CommandGroup(
        [
            new UnbindValue(new RowRef(Role("spawn").Item, 1)) { Value = "seconds" },
            new RemovePrefabField(GateRef, "delay"),
        ]),
        ["set_prefab_field_default"] = () => new SetPrefabFieldDefault(GateRef, "delay", ["7"]),
        ["bind_value"] = () => new CommandGroup(
        [
            new AddPrefabField(GateRef, "next", "Next", "trigger") { Default = ["ol_start"] },
            new BindValue(new RowRef(Role("spawn").Item, 3), "next") { Value = "trigger" },
        ]),
        ["unbind_value"] = () => new UnbindValue(new RowRef(Role("spawn").Item, 2)) { Value = "formation" },
        ["add_action_in_prefab_mode"] = () => new AddAction(Role("spawn"), LineSpec.Raw("Act_PlayMusic", "ol_music")),
        ["rename_role"] = () => new RenameTrigger(Role("end"), "finish"),
        ["remove_role"] = () => new RemoveTrigger(Role("end")),
    };

    public static IEnumerable<object[]> Kinds() => Commands.Keys.Select(k => new object[] { k });

    /// <summary>Each command of prefab mode and its inverse give the state before (section 4.8 of the M6b design):
    /// the template and the instances.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachCommandOfPrefabModeAndItsInverseGiveTheStateBefore(string kind)
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2", Two, "ol_form_b");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));
        // The asset in its canonical form, as the editor writes it.
        m.Apply(new SetPrefabFieldDefault(GateRef, "delay", ["6"]));
        m.Apply(new SetPrefabFieldDefault(GateRef, "delay", ["5"]));
        var before = State(m);

        var result = m.Missions.Apply(new MissionBatch("Test", [Commands[kind]()]) { Confirm = true });
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => $"{e.Detail}: {e.Message}")));
        var after = State(m);
        Assert.NotEqual(before, after);
        var json = MissionJson.Write(result.Inverse!);
        var inverse = m.Missions.Apply(new MissionBatch("Back", [MissionJson.ReadCommand(json)]) { Confirm = true });

        Assert.True(inverse.Status == EditStatus.Applied, string.Join("\n", inverse.Errors.Select(e => $"{e.Detail}: {e.Message}")));
        Assert.Equal(before, State(m));
        m.Session.Undo();
        Assert.Equal(after, State(m));
        m.Session.Undo();
        Assert.Equal(before, State(m));
        m.Session.Redo();
        Assert.Equal(after, State(m));
    }

    /// <summary>The gate of section 11.4 of the M6b design: a propagation to 50 linked instances in 5 scripts takes
    /// less than 1 s. The gate takes the median of three runs, so that one pause of a busy machine does not fail
    /// it.</summary>
    [Fact]
    public void APropagationToFiftyInstancesInFiveScriptsTakesLessThanOneSecond()
    {
        var scripts = Enumerable.Range(1, 5).Select(n => $@"DATA\MISSIONS\OL_P{n}\ol_p{n}.ini").ToList();
        using var m = MissionEdits.Create(setup: ws =>
        {
            foreach (var script in scripts)
                ws.WriteBase(script, "[Mission]\n\n[MsnFormation]\nnickname = ol_form\nposition = 0, 0, 0\n");
        }, modFiles: new Dictionary<string, string> { [@"prefabs\missions\ol_gate.ini"] = PrefabInstanceTests.Gate });
        var places = scripts.SelectMany(s => Enumerable.Range(1, 10).Select(n => (MissionCommand)new PlacePrefabInstance(new ScriptRef(s), GateRef)
        {
            Id = $"ol_w{n}", Values = [new PrefabFieldInput("formation", ["ol_form"])],
        })).ToList();
        Assert.Equal(EditStatus.Applied, m.Missions.Apply(new MissionBatch("Place", places)).Status);
        Assert.Equal(50, m.Missions.PrefabMode(GateRef)!.Instances);

        var times = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = m.Missions.Apply(new AddAction(Role("spawn"), LineSpec.Raw("Act_PlayMusic", $"ol_music_{i}")));
            watch.Stop();
            Assert.Equal(EditStatus.Applied, result.Status);
            Assert.Contains("Prefab 'Gate ambush' changed 50 instances in 5 missions.", result.Notes);
            times.Add(watch.ElapsedMilliseconds);
        }
        times.Sort();
        output.WriteLine($"The propagation to 50 instances in 5 scripts: {string.Join(", ", times)} ms");
        Assert.True(times[1] < 1000, $"The propagation took {times[1]} ms.");
        Assert.All(scripts, s => Assert.Contains("Act_PlayMusic = ol_music_2", m.Lines(T("ol_w10_spawn", s).Item)));
    }
}

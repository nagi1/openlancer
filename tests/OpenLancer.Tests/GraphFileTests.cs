using System.IO;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The graph file of a mission script (section 6.8 of the M6b design): its format, the node places, the acts after the
/// first act edit, and the records that follow a rename or a delete of their trigger. All names are invented.
/// </summary>
public sealed class GraphFileTests
{
    private const string Text = GraphFile.Header + """

        [Graph]
        script = DATA\MISSIONS\OL_TEST\ol_test.ini
        format = 1

        [Node]
        trigger = ol_wave
        pos = 1200, 340

        [Act]
        nickname = act_ambush
        name = The ambush
        text = The escort meets the player at the gate. Two waves attack.
        member = ol_start
        member = ol_wave

        [Instance]
        id = ol_wave_1
        prefab = mod:ol_gate_ambush

        """;

    private static string GraphText(MissionEdits m) => m.Session.ModFileText(GraphFile.PathOf(Script)) ?? "";

    [Fact]
    public void AGraphFileReadsAndWritesItsRecords()
    {
        var file = GraphFile.Read(Text, out var problems);

        Assert.Empty(problems);
        Assert.Equal(new GraphNodePlace("ol_wave", 1200, 340), Assert.Single(file.Nodes));
        var act = Assert.Single(file.Acts);
        Assert.Equal(("act_ambush", "The ambush", "The escort meets the player at the gate. Two waves attack."),
            (act.Nickname, act.Name, act.Text));
        Assert.Equal(["ol_start", "ol_wave"], act.Members);
        Assert.Equal("Instance", Assert.Single(file.Others).Type);
        Assert.Equal(Text.Replace("\r\n", "\n").Trim(), file.Write(Script)!.Trim());
        Assert.Equal(file, GraphFile.Read(file.Write(Script), out _));
    }

    [Fact]
    public void AFileWithoutARecordIsNotWritten()
    {
        Assert.Null(GraphFile.Empty.Write(Script));
        using var m = Create();

        m.Apply(new MoveNodes(TestScript, [new NodePlace("ol_wave", 10, 20)]));
        Assert.Contains("trigger = ol_wave", GraphText(m));
        m.Apply(new MoveNodes(TestScript, [new NodePlace("ol_wave", 0, 0) { Remove = true }]));

        Assert.Null(m.Session.ModFileText(GraphFile.PathOf(Script)));
        m.Session.Save();
        Assert.False(File.Exists(Path.Combine(m.Ws.ModFolder("act"), "graphs", "DATA", "MISSIONS", "OL_TEST", "ol_test.ini")));
    }

    [Fact]
    public void AFileOfAnotherFormatAndABadLineGiveProblems()
    {
        var other = GraphFile.Read("[Graph]\nformat = 2\n[Node]\ntrigger = x\npos = 1, 2\n", out var problems);
        var bad = GraphFile.Read("[Node]\ntrigger = x\npos = 1\nno value here\n", out var badProblems);

        Assert.False(other.HasRecords);
        Assert.Equal("Line 2 of the graph file: The graph file has format 2. This editor reads format 1.", Assert.Single(problems));
        Assert.Equal(2, badProblems.Count);
        Assert.Empty(bad.Nodes);
    }

    [Fact]
    public void ANodeOfAMissingTriggerGivesANoteAndGoesAtTheNextWriteAndSave()
    {
        using var m = Create(modFiles: new() { [@"graphs\DATA\MISSIONS\OL_TEST\ol_test.ini"] = Text.Replace("ol_wave", "ol_gone") });
        var graph = m.Missions.Graph(Script);

        var notes = graph.Notes(Script, name => m.Missions.Model(Script).Index.Trigger(name) != null);
        Assert.Equal($"The graph file of {Script} has a node of trigger 'ol_gone', which the script does not have. The " +
                     "editor removes the node when it writes the file again.", notes[0]);
        Assert.Equal(2, notes.Count);

        m.Apply(new MoveNodes(TestScript, [new NodePlace("ol_end", 5, 5)]));
        m.Session.Save();

        var saved = File.ReadAllText(Path.Combine(m.Ws.ModFolder("act"), "graphs", "DATA", "MISSIONS", "OL_TEST", "ol_test.ini"));
        Assert.DoesNotContain("trigger = ol_gone", saved);
        Assert.DoesNotContain("member = ol_gone", saved);
        Assert.Contains("trigger = ol_end", saved);
        Assert.Contains("prefab = mod:ol_gate_ambush", saved);
    }

    [Fact]
    public void ADeleteRemovesTheNodeAndTheActPlaceAndOneUndoBringsThemBack()
    {
        using var m = Create(modFiles: new() { [@"graphs\DATA\MISSIONS\OL_TEST\ol_test.ini"] = Text });
        var before = m.State();
        var graph = m.Missions.Graph(Script);
        var lines = m.Lines("ol_wave");

        var result = m.Apply(new RemoveTrigger(T("ol_wave")));

        Assert.DoesNotContain("trigger = ol_wave", GraphText(m));
        Assert.DoesNotContain("member = ol_wave", GraphText(m));
        m.Apply(result.Inverse!);
        // The editor writes the file in its own form, so the inverse gives the same records, not the same line ends.
        Assert.Equal(graph, m.Missions.Graph(Script));
        Assert.Equal(lines, m.Lines("ol_wave"));
        m.Session.Undo();
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void ARenameRenamesTheNodeAndTheActMemberAlsoWithoutAChangeOfTheLines()
    {
        using var m = Create(modFiles: new() { [@"graphs\DATA\MISSIONS\OL_TEST\ol_test.ini"] = Text });

        m.Apply(new RenameTrigger(T("ol_wave"), "ol_wave_a") { ChangeReferences = false });

        var graph = m.Missions.Graph(Script);
        Assert.Equal("ol_wave_a", Assert.Single(graph.Nodes).Trigger);
        Assert.Equal(["ol_start", "ol_wave_a"], graph.Acts[0].Members);
    }

    [Fact]
    public void TheFirstActEditWritesTheProposedActs()
    {
        using var m = Create();
        var proposed = m.Missions.Model(Script).Acts!;

        m.Apply(new SetAct(new ActRef(Script, MissionActs.NicknameOf(proposed.Acts[0]))) { Name = "The launch" });

        var act = Assert.Single(m.Missions.Graph(Script).Acts);
        Assert.Equal(("act_1", "The launch"), (act.Nickname, act.Name));
        Assert.Equal(proposed.Acts[0].Triggers.Select(t => t.Nickname), act.Members);
        Assert.Equal("The launch", m.Missions.Model(Script).Acts!.Acts[0].Name);
    }

    [Fact]
    public void ATriggerJoinsOneActAndATriggerThatNoActNamesTakesItsProposedAct()
    {
        using var m = Create();
        m.Apply(new AddAct(TestScript) { Name = "The end", Members = ["ol_end"] });

        var acts = m.Missions.Model(Script).Acts!;

        Assert.Equal(["Act 1", "The end"], acts.Acts.Select(a => a.Name ?? ""));
        Assert.DoesNotContain(acts.Acts[0].Triggers, t => t.Nickname == "ol_end");
        Assert.Equal(["ol_end"], acts.Acts[1].Triggers.Select(t => t.Nickname));
        m.Apply(new AddTrigger(TestScript) { Nickname = "ol_later", Condition = LineSpec.Raw("Cnd_True", "no_params") });
        m.Apply(new LinkTriggers(T("ol_end"), "ol_later", LinkKind.Start));
        Assert.Contains(m.Missions.Model(Script).Acts!.Acts.SelectMany(a => a.Triggers), t => t.Nickname == "ol_later");
    }

    [Fact]
    public void AnActNameMustBeValidAndFree()
    {
        using var m = Create();
        m.Apply(new AddAct(TestScript) { Nickname = "act_x" });

        m.Refuse(new AddAct(TestScript) { Nickname = "ACT_X" }, MissionErrors.NameTaken);
        m.Refuse(new AddAct(TestScript) { Nickname = "a b" }, MissionErrors.NameInvalid);
        m.Refuse(new SetActMembers(new ActRef(Script, "act_x"), ["ol_nowhere"]), MissionErrors.TriggerNotFound);
        m.Refuse(new RemoveAct(new ActRef(Script, "act_none")), MissionErrors.ItemNotFound);
    }

    [Fact]
    public void RemoveActAndItsInverseGiveTheActsBack()
    {
        using var m = Create();
        m.Apply(new AddAct(TestScript) { Name = "The end", Members = ["ol_end"] });
        var before = m.State();

        var result = m.Apply(new RemoveAct(new ActRef(Script, "act_2")));
        m.Apply(result.Inverse!);

        Assert.Equal(before, m.State());
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The save gate of the missions on the retail scripts (sections 6.10 and 11.2 of the M6b design). The tests read the
/// vanilla scripts at run time and hold no text of them.
/// </summary>
public sealed class MissionGateRetailTests(ITestOutputHelper output)
{
    /// <summary>The problems of the retail scripts are base-game problems, so an edit of one trigger in each script
    /// gives no error in the save gate.</summary>
    [Fact]
    public void EachRetailScriptGivesNoMissionErrorInTheSaveGate()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bgate");
        var session = Open(workspace);
        Assert.DoesNotContain(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);

        var scripts = Scripts(session).ToList();
        foreach (var script in scripts)
            Apply(session.Missions, new SetTriggerSystem(new TriggerRef(script, FirstTrigger(session, script))) { System = "ol_gate" });
        var watch = Stopwatch.StartNew();
        var problems = session.SaveProblems();
        watch.Stop();

        output.WriteLine($"The save gate of {scripts.Count} changed scripts: {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(14, scripts.Count);
        Assert.True(problems.All(p => p.Severity != DiagnosticSeverity.Error),
            string.Join("\n", problems.Where(p => p.Severity == DiagnosticSeverity.Error)));
        Assert.True(session.Save().Saved);
    }

    /// <summary>A batch whose commands give W back changes no state, adds no history entry and writes no file (section
    /// 4.5 of the M6b design, item 6).</summary>
    [Fact]
    public void ABatchWithoutANetChangeWritesNoFile()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bnet");
        var session = Open(workspace);
        var script = Scripts(session).First();
        var trigger = FirstTrigger(session, script);
        var item = ItemRef.Of(script, "Trigger", trigger);
        var rows = session.OpenDocument(script)!.Working.First(s => TriggerRules.Nickname(s) == trigger).Count;
        var revision = session.Revision;
        var version = session.Missions.Version(script);

        var result = session.Missions.Apply(new MissionBatch("Add and remove",
        [
            new AddAction(new TriggerRef(script, trigger), LineSpec.Of("Act_ActTrig", ("trigger", trigger))),
            new RemoveAction(new RowRef(item, rows)),
        ]));
        var saved = session.Save();

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.Null(result.Inverse);
        Assert.False(session.CanUndo);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(version, session.Missions.Version(script));
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Empty(saved.Written);
    }

    private static EditSession Open(RetailWorkspace workspace)
    {
        var session = workspace.Open();
        session.Missions.Coverage = EngineCoverage.Instance;
        return session;
    }

    private static void Apply(MissionEditor missions, MissionCommand command)
    {
        var result = missions.Apply(command);
        Assert.True(result.Status == EditStatus.Applied, $"{command.HistoryLabel}: " +
                                                         string.Join(" ", result.Errors.Select(e => e.Message)));
    }

    private static IEnumerable<string> Scripts(EditSession session) =>
        MissionSchema.Retail.StorySlots.Select(s => session.DataPath(s.Path)!)
            .Where(p => session.OpenDocument(p) is { HoldsScript: true });

    /// <summary>The first trigger of a script whose name no other trigger of the script has.</summary>
    private static string FirstTrigger(EditSession session, string script)
    {
        var names = session.OpenDocument(script)!.Working.Where(s => s.Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            .Select(TriggerRules.Nickname).OfType<string>().ToList();
        return names.First(n => names.Count(o => o.Equals(n, StringComparison.OrdinalIgnoreCase)) == 1);
    }

    /// <summary>
    /// The data steps of manual check 12.1 on the vanilla M01a (section 12.1 of the M6b design). A raw edit of the whole
    /// script changes the target of a start link and adds a delay trigger after the changed trigger. The save writes one
    /// section with ol.replace and one with ol.new. An undo with a save removes the patch file (steps 2, 3 and 5). A raw
    /// edit that removes a started trigger gives marks and errors while the user types and stops the save. An undo
    /// removes the errors (steps 7 to 9). The steps in the editor window stay a manual check.
    /// </summary>
    [Fact]
    public void TheDataStepsOfTheManualCheckOfRawEditsOnM01a()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6braw");
        var session = Open(workspace);
        var script = session.DataPath(MissionSchema.Retail.StorySlots.Single(s => s.Slot == "M01a").Path)!;
        var document = session.OpenDocument(script)!;
        var names = document.Working.Where(s => s.Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            .Select(TriggerRules.Nickname).OfType<string>().ToList();
        bool Once(string name) => names.Count(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) == 1;
        var start = RawText.Canonical(script, document.Working).Text;
        var blocks = start.Split("\n\n").ToList();
        // The first trigger that starts another trigger, both with a name that no other trigger has.
        var (at, line, target) = blocks.Select((b, i) => (Block: b, Index: i))
            .Select(b => (b.Index, Lines: b.Block.Split('\n')))
            .Where(b => b.Lines[0] == "[Trigger]" && b.Lines.FirstOrDefault(l => l.StartsWith("nickname = ")) is { } n &&
                        Once(n["nickname = ".Length..]))
            .SelectMany(b => b.Lines.Where(l => l.StartsWith("Act_ActTrig = ") && Once(l["Act_ActTrig = ".Length..]))
                .Take(1).Select(l => (b.Index, l, l["Act_ActTrig = ".Length..])))
            .First();
        var changed = blocks.ToList();
        changed[at] = changed[at].Replace(line + "\n", "Act_ActTrig = ol_launch_delay\n").Replace("\n" + line, "\nAct_ActTrig = ol_launch_delay");
        changed.Insert(at + 1, $"[Trigger]\nnickname = ol_launch_delay\nCnd_Timer = 3\n{line}");

        var plan = RawMissionEdit.PlanFile(session, script, start, string.Join("\n\n", changed));
        var applied = session.Missions.Apply(plan.Batch!);
        var saved = session.Save();

        Assert.Empty(plan.Problems);
        Assert.Equal(EditStatus.Applied, applied.Status);
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        var patch = File.ReadAllText(Assert.Single(saved.Written));
        Assert.Equal(1, patch.Split("ol.replace = true").Length - 1);
        Assert.Equal(1, patch.Split("ol.new = true").Length - 1);
        Assert.DoesNotContain("ol.delete", patch);
        session.Undo();
        var undone = session.Save();
        Assert.True(undone.Saved, string.Join("\n", undone.Problems));
        Assert.False(File.Exists(Assert.Single(saved.Written)));

        // Steps 7 to 9: the started trigger goes.
        var text = RawText.Canonical(script, session.OpenDocument(script)!.Working).Text;
        var withoutTarget = string.Join("\n\n", text.Split("\n\n").Where(b => !b.Split('\n').Contains($"nickname = {target}")));
        var marks = RawMissionEdit.Draft(session, script, null, withoutTarget).Checks!();
        Assert.Contains(marks, m => m.Problem.Rule == "M05" && m.Line > 0);
        Assert.Equal(EditStatus.Applied, session.Missions.Apply(RawMissionEdit.PlanFile(session, script, text, withoutTarget).Batch!).Status);
        var stopped = session.Save();
        Assert.False(stopped.Saved);
        Assert.Contains(stopped.Problems, p => p.Rule == "M05" && p.Severity == DiagnosticSeverity.Error);
        session.Undo();
        Assert.DoesNotContain(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
    }
}

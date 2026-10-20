using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
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

    /// <summary>A batch whose commands give W back writes no file.</summary>
    [Fact]
    public void ABatchWithoutANetChangeWritesNoFile()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bnet");
        var session = Open(workspace);
        var script = Scripts(session).First();
        var trigger = FirstTrigger(session, script);
        var item = ItemRef.Of(script, "Trigger", trigger);
        var rows = session.OpenDocument(script)!.Working.First(s => TriggerRules.Nickname(s) == trigger).Count;

        var result = session.Missions.Apply(new MissionBatch("Add and remove",
        [
            new AddAction(new TriggerRef(script, trigger), LineSpec.Of("Act_ActTrig", ("trigger", trigger))),
            new RemoveAction(new RowRef(item, rows)),
        ]));
        var saved = session.Save();

        Assert.Equal(EditStatus.Applied, result.Status);
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
}

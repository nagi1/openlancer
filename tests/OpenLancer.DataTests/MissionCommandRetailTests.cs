using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the mission commands (section 11.2 of the M6b design) and the performance gates of section 11.4.
/// The tests read the vanilla scripts at run time and hold no text of them.
/// </summary>
public sealed class MissionCommandRetailTests(ITestOutputHelper output)
{
    [Fact]
    public void TheCacheGivesTheResultsOfTheWorkerRunForTheRetailScripts()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bcache");
        var session = Open(workspace);
        var watch = Stopwatch.StartNew();
        var run = MissionRun.Execute(session.Missions.RunInputs());
        watch.Stop();
        output.WriteLine($"The worker run of {run.Analyses.Count} scripts: {watch.ElapsedMilliseconds} ms");

        foreach (var (path, analysis) in run.Analyses)
        {
            var model = session.Missions.Model(path);
            Assert.Equal(analysis.Problems.Select(Key), model.Problems.Select(Key));
            Assert.Equal(analysis.Index.Triggers.Count, model.Index.Triggers.Count);
        }
        Assert.Equal(14, run.Scripts.Count(s => s.Slot != null && s.State != MissionScriptState.Missing));
        // The gate of M6a: the full worker run of the 14 retail scripts takes at most 2 s.
        Assert.True(watch.ElapsedMilliseconds < 2000, $"The run took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void TheGatesOfOneCommandABatchAndAnUndoOnM10()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bgates");
        var session = Open(workspace);
        var missions = session.Missions;
        var m10 = Slot(session, "M10");
        missions.Model(m10);
        var triggers = Triggers(session, m10).Where(t => !t.Repeated).Select(t => t.Name).ToList();
        // The first edit runs the base-game analysis of M10 once (section 7 of the M6b design).
        Apply(missions, new SetTriggerSystem(new TriggerRef(m10, triggers[0])) { System = "ol_first" });

        // The gates take the median of five runs, so that one pause of a busy machine does not fail them.
        var ones = new List<long>();
        var undos = new List<long>();
        MissionEditResult? result = null;
        MissionModel? model = null;
        for (var i = 0; i < 5; i++)
        {
            var one = Stopwatch.StartNew();
            result = Apply(missions, new AddAction(new TriggerRef(m10, triggers[1 + i]),
                LineSpec.Of("Act_ActTrig", ("trigger", triggers[2 + i]))));
            one.Stop();
            ones.Add(one.ElapsedMilliseconds);
            var undo = Stopwatch.StartNew();
            session.Undo();
            model = missions.Model(m10);
            undo.Stop();
            undos.Add(undo.ElapsedMilliseconds);
        }
        var oneTime = Median(ones);
        var undoTime = Median(undos);

        var commands = Enumerable.Range(0, 500).Select(i => (MissionCommand)new AddAction(
            new TriggerRef(m10, triggers[i % triggers.Count]),
            LineSpec.Of("Act_ActTrig", ("trigger", triggers[(i * 7 + 3) % triggers.Count])))).ToList();
        var batch = Stopwatch.StartNew();
        var many = missions.Apply(new MissionBatch("500 actions", commands));
        batch.Stop();
        output.WriteLine($"One command: {string.Join(", ", ones)} ms. Undo: {string.Join(", ", undos)} ms. 500 commands: " +
                         $"{batch.ElapsedMilliseconds} ms.");

        Assert.NotNull(result!.Inverse);
        Assert.NotNull(model!.Index);
        Assert.Equal(EditStatus.Applied, many.Status);
        Assert.True(oneTime < 50, $"One command took {oneTime} ms (median of {string.Join(", ", ones)}).");
        Assert.True(undoTime < 50, $"The undo took {undoTime} ms (median of {string.Join(", ", undos)}).");
        Assert.True(batch.ElapsedMilliseconds < 2000, $"The batch took {batch.ElapsedMilliseconds} ms.");

        // A step of a drag runs no analysis on the thread of the session.
        var analyses = missions.Cache.Count;
        using var transaction = missions.Begin("Drag");
        var step = Stopwatch.StartNew();
        transaction.Apply(new MissionBatch("Drag", [new SetTriggerSystem(new TriggerRef(m10, triggers[3])) { System = "ol_drag" }]));
        step.Stop();
        Assert.Equal(analyses, missions.Cache.Count);
        transaction.Cancel();
        output.WriteLine($"One step of a drag: {step.ElapsedMilliseconds} ms.");
    }

    private static long Median(List<long> times) => times.Order().ElementAt(times.Count / 2);

    [Fact]
    public void ASameValueGivesNoChangeAndARealChangeWithItsInverseGivesNoPatchFile()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bsame");
        var session = Open(workspace);
        var missions = session.Missions;
        var schema = MissionSchema.Retail;
        var tested = 0;
        foreach (var script in Scripts(session))
        {
            foreach (var trigger in Triggers(session, script).Where(t => !t.Repeated))
            {
                var section = trigger.Section;
                var nickname = TriggerRules.NicknameLine(section);
                for (var row = nickname + 1; row < section.Count; row++)
                {
                    if (!TriggerRules.IsAction(section[row].Name) || schema.Name(section[row].Name) is not { } name ||
                        name.Values.FirstOrDefault() is not { Width: 1, Rest: false } spec || section[row].Count == 0)
                        continue;
                    var text = MissionValues.TextsOf(section[row])[0];
                    // A word in another letter case changes, so it is not the same value.
                    if (spec.Kind is MissionValueKind.Token or MissionValueKind.Bool || spec.Also != null ||
                        spec.Scope == MissionKindScope.File)
                        continue;
                    var result = missions.Apply(new SetActionValue(new RowRef(ItemRef.Of(script, "Trigger", trigger.Name), row),
                        spec.Name, MissionValue.Of(text)));
                    Assert.True(result.Status == EditStatus.NoChange,
                        $"{trigger.Name} line {row}: {result.Status} {string.Join(" ", result.Errors.Select(e => e.Message))}");
                    tested++;
                    break;
                }
            }
            var first = Triggers(session, script).First(t => !t.Repeated);
            var change = Apply(missions, new SetTriggerSystem(new TriggerRef(script, first.Name)) { System = "ol_changed" });
            Apply(missions, change.Inverse!);
        }
        output.WriteLine($"{tested} actions gave no change.");
        Assert.True(tested > 1000, $"Only {tested} actions.");

        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Empty(saved.Written);
        Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(workspace.ModFolder, Data.Mods.Mod.PatchesFolder)));
    }

    [Fact]
    public void ARenameOfEachTriggerOfM10AndItsInverseGiveTheSameW()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6brename");
        var session = Open(workspace);
        var missions = session.Missions;
        var m10 = Slot(session, "M10");
        var before = State(session, m10);
        var triggers = Triggers(session, m10).Where(t => !t.Repeated).Select(t => t.Name).ToList();
        var longNames = triggers.Count(t => t.Length > 47);

        var n = 0;
        foreach (var name in triggers)
        {
            var renamed = $"ol_m6b_{n++.ToString(CultureInfo.InvariantCulture)}";
            var result = Apply(missions, new RenameTrigger(new TriggerRef(m10, name), renamed));
            Apply(missions, result.Inverse!);
        }
        foreach (var name in triggers.Where(t => t.Length > 47))
        {
            Apply(missions, new RenameTrigger(new TriggerRef(m10, name), "ol_m6b_short"));
            Apply(missions, new RenameTrigger(new TriggerRef(m10, "ol_m6b_short"), name));
        }

        output.WriteLine($"{triggers.Count} triggers, {longNames} with more than 47 characters.");
        Assert.Equal(4, longNames);
        Assert.Equal(before, State(session, m10));
    }

    [Fact]
    public void ARemoveOfEachTriggerOfM06AndItsUndoGiveTheSameW()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bremove");
        var session = Open(workspace);
        var m06 = Slot(session, "M06");
        var before = State(session, m06);

        var triggers = Triggers(session, m06).Where(t => !t.Repeated).Select(t => t.Name).ToList();
        foreach (var name in triggers)
        {
            Apply(session.Missions, new RemoveTrigger(new TriggerRef(m06, name)));
            session.Undo();
        }

        Assert.True(triggers.Count > 50);
        Assert.Equal(before, State(session, m06));
        Assert.False(session.IsDirty);
    }

    private static EditSession Open(RetailWorkspace workspace)
    {
        var session = workspace.Open();
        session.Missions.Coverage = EngineCoverage.Instance;
        return session;
    }

    private static MissionEditResult Apply(MissionEditor missions, MissionCommand command)
    {
        var result = missions.Apply(command);
        Assert.True(result.Status == EditStatus.Applied, $"{command.HistoryLabel}: " +
                                                         string.Join(" ", result.Errors.Select(e => e.Message)));
        return result;
    }

    private static string Slot(EditSession session, string slot) =>
        session.DataPath(MissionSchema.Retail.StorySlots.Single(s => s.Slot == slot).Path)!;

    private static IEnumerable<string> Scripts(EditSession session) =>
        MissionSchema.Retail.StorySlots.Select(s => session.DataPath(s.Path)!)
            .Where(p => session.OpenDocument(p) is { HoldsScript: true });

    /// <summary>The triggers of a script with a nickname: the nickname, the section, and true for a nickname that an
    /// earlier trigger has too.</summary>
    private static List<(string Name, Section Section, bool Repeated)> Triggers(EditSession session, string script)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = session.OpenDocument(script)!.Working.Where(s => s.Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            .Select(TriggerRules.Nickname).OfType<string>().CountBy(n => n.ToLowerInvariant()).ToDictionary(c => c.Key, c => c.Value);
        return session.OpenDocument(script)!.Working.Where(s => s.Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
            .Select(s => (Name: TriggerRules.Nickname(s), Section: s)).Where(t => t.Name != null)
            .Select(t => (t.Name!, t.Section, counts[t.Name!.ToLowerInvariant()] > 1)).ToList();
    }

    /// <summary>The engine form of W of a script.</summary>
    private static string State(EditSession session, string script) =>
        string.Join("\n", session.OpenDocument(script)!.Working.Select(s => $"[{s.Name}]\n" + string.Join("\n",
            s.Select(e => e.Name.ToLowerInvariant() + " = " + string.Join(", ", MissionValues.TextsOf(e))))));

    private static string Key(Diagnostic d) => $"{d.Severity}|{d.Rule}|{d.Subject}|{d.Origin?.Kind}";
}

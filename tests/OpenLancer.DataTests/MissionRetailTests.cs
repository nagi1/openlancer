using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the mission studio (sections 18.2 and 18.4 of the M6a design) over the 14 retail story scripts of
/// <c>GameData\base</c>. The tests count lines, sections, problems and prefab instances. They hold no retail text.
/// </summary>
public sealed class MissionRetailTests(ITestOutputHelper output)
{
    /// <summary>The run of the retail scripts, made once for the tests of this class. The workspace closes after the
    /// run, because the result holds all that the tests read.</summary>
    private static readonly Lazy<MissionRunResult> Vanilla = new(() =>
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6aproof");
        return MissionRun.Execute(workspace.Open().MissionInputs(EngineCoverage.Instance));
    });

    /// <summary>The run, or a skip without the retail data.</summary>
    private static MissionRunResult Run()
    {
        GameDataPaths.RequireBase();
        return Vanilla.Value;
    }

    private static List<MissionAnalysis> Slots(MissionRunResult run) =>
        run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!).ToList();

    private static int Count(IEnumerable<MissionIndex> indexes, string key) =>
        indexes.Sum(i => i.Lines.Count(l => l.Trigger != null && l.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void TheIndexCountsOfTheRetailScripts()
    {
        var indexes = Slots(Run()).Select(a => a.Index).ToList();
        var triggers = indexes.SelectMany(i => i.Triggers).ToList();
        var watches = indexes.SelectMany(i => i.Links.Where(l => l.Kind == MissionLinkKind.Watch)).ToList();

        Assert.Equal((2984, 14203, 2985), (triggers.Count, triggers.Sum(t => t.Actions.Count), triggers.Sum(t => t.Conditions.Count)));
        Assert.Equal((3246, 1109), (Count(indexes, "Act_ActTrig"), Count(indexes, "Act_DeactTrig")));
        Assert.Equal(16, indexes.Sum(i => i.StartTriggers.Count));
        Assert.Equal([36, 18, 16, 14, 45, 19, 17, 12, 10, 7, 104, 17, 61, 26],
            indexes.Select(i => i.Triggers.Count(t => !i.Reached.Contains(t))));
        Assert.Equal((38, 0, 8), (indexes.Sum(i => i.Restarts.Count), indexes.Sum(i => i.Cancels.Count),
            indexes.Sum(i => i.SelfStarts.Count)));
        Assert.Equal(100, watches.Count);
        Assert.Equal(new Dictionary<string, int> { ["ON"] = 36, ["OFF"] = 34, ["ACTIVE"] = 21, ["COMPLETE"] = 9 },
            watches.GroupBy(w => w.State!.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Count()));
        Assert.Equal((70, 68, 36), (Count(indexes, "Act_CallThorn"), Count(indexes, "Act_AddRTC"), Count(indexes, "Act_Save")));
        Assert.Equal((116, 118), (triggers.Count(t => t.LinesBeforeNickname.Count > 0),
            triggers.Sum(t => t.LinesBeforeNickname.Count)));
        Assert.Equal(47, indexes.Sum(i => i.Lines.Count(l => l.Key.Equals("rel_pos", StringComparison.OrdinalIgnoreCase))));
        Assert.Equal(33, triggers.Count(t => t.Repeats));
        Assert.Equal([254, 183, 171, 207, 307, 189, 333, 215, 79, 129, 408, 185, 174, 150], indexes.Select(i => i.Triggers.Count));
    }

    [Fact]
    public void TheSectionKindCountsOfTheRetailScripts()
    {
        var documents = Slots(Run()).Select(a => a.Document).ToList();
        int Of(MissionSectionKind kind) => documents.Sum(d => d.OfKind(kind).Count());

        Assert.Equal([1010, 188, 116, 4, 672, 395, 336, 231, 7, 6, 1],
            new[]
            {
                MissionSectionKind.Ship, MissionSectionKind.Formation, MissionSectionKind.Solar, MissionSectionKind.Loot,
                MissionSectionKind.OrderList, MissionSectionKind.Objective, MissionSectionKind.Npc,
                MissionSectionKind.Conversation, MissionSectionKind.RandomEncounter, MissionSectionKind.MetaBehavior,
                MissionSectionKind.Other,
            }.Select(Of));
        // The type of an order list has two spellings. Section types compare without letter case.
        Assert.Equal(17, documents.Sum(d => d.Sections.Count(s => s.Section.Name == "Objlist")));
    }

    /// <summary>The baseline with a new mod whose targets hold OpenLancer. Since OL-0061, no retail line stops the load
    /// in OpenLancer, so rule M11 finds nothing.</summary>
    [Fact]
    public void TheRetailBaselineShowsNoProblemAndHasNoError()
    {
        var run = Run();
        var problems = run.Problems.ToList();

        Assert.Equal(629, problems.Count);
        Assert.DoesNotContain(problems, p => p.Rule == "M11");
        Assert.All(problems, p => Assert.True(p.Origin!.IsBaseGame, p.Message));
        Assert.DoesNotContain(problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.All(MissionsBrowserModel.Rows(run, showBaseGame: false), r => Assert.Equal((0, 0), (r.Errors, r.Warnings)));
        Assert.Equal(629, MissionsBrowserModel.Rows(run, showBaseGame: false).Sum(r => r.Hidden));
    }

    /// <summary>The checks on the retail lines as lines of the active mod, without a base-game run, prove that the rules
    /// fire. Rule M06 counts the labels and the names of objects too, as section 3.12 of the design study asks.</summary>
    [Fact]
    public void TheRulesFireOnTheRetailLines()
    {
        var problems = Slots(Run()).SelectMany(a => MissionChecks.Run(a.Index)).ToList();

        Assert.Equal(new Dictionary<string, int>
        {
            ["G05"] = 355, ["G06"] = 11, ["M03"] = 1, ["M04"] = 118, ["M05"] = 36, ["M06"] = 89, ["M07"] = 3, ["M08"] = 2,
            ["M12"] = 10, ["M23"] = 2, ["M24"] = 1, ["M25"] = 1,
        }, problems.GroupBy(p => p.Rule!).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>The prefab instances of the retail scripts (section 8.2 of the M6a design). The claim rule gives a trigger
    /// to the first prefab that matches it, so Mission success and Watched state count fewer than the patterns.</summary>
    [Fact]
    public void ThePrefabCountsOfTheRetailScripts()
    {
        var instances = Slots(Run()).SelectMany(a => a.Prefabs!.Instances).ToList();

        Assert.Equal(new Dictionary<string, int>
        {
            ["ambush"] = 7, ["branch"] = 19, ["checkpoint"] = 36, ["conversation"] = 78, ["destroyTargets"] = 99,
            ["escort"] = 105, ["fail"] = 57, ["keepApplying"] = 10, ["lockGate"] = 7, ["objective"] = 148, ["offer"] = 13,
            ["patrol"] = 43, ["setReputation"] = 47, ["spaceCutscene"] = 70, ["spawnWave"] = 270, ["success"] = 13,
            ["watchedState"] = 34,
        }, instances.GroupBy(i => i.Definition.Id).ToDictionary(g => g.Key, g => g.Count()));
        // A trigger is in one instance at most.
        var claimed = instances.SelectMany(i => i.Claimed).ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
    }

    [Fact]
    public void EachRetailMissionIsCompleteAndItsBaseGameStepsAreHidden()
    {
        var slots = Slots(Run());
        var steps = slots.SelectMany(a => a.Prefabs!.Instances.Select(i => MissionCompleteness.OfInstance(a, i))).ToList();

        Assert.All(slots, a => Assert.Equal("All 7 steps complete.", MissionCompleteness.OfMission(a).Summary));
        Assert.Equal((1516, 0, 374), (steps.Sum(s => s.Total), steps.Sum(s => s.Missing.Count()), steps.Sum(s => s.HiddenCount)));
    }

    [Fact]
    public void TheBrowserHasTheFourteenSlotRowsAndNoOtherScript()
    {
        var rows = MissionsBrowserModel.Rows(Run(), showBaseGame: false);

        Assert.Equal(14, rows.Count);
        Assert.All(rows, r => Assert.Equal("Base game", r.State));
        Assert.Equal(MissionSchema.Retail.StorySlots.Select(s => (s.Slot, s.StartStage.ToString(), s.AcceptStage.ToString())),
            rows.Select(r => (r.Slot, r.StartStage, r.AcceptStage)));
        Assert.Equal(["At once", "Worth grows by 3,500", "The story ends"],
            new[] { rows[0], rows[1], rows[13] }.Select(r => r.NextMission));
        // Since OL-0061, the engine skips the lines that it cannot build, so each retail mission loads in OpenLancer.
        Assert.DoesNotContain(rows, r => r.OpenLancer == "Does not load");
    }

    /// <summary>The round trip contract of section 14.7: each section of each retail script gives the same entries
    /// through its canonical text, with one case for each retail form.</summary>
    [Fact]
    public void TheRawTextOfEachRetailScriptRoundTrips()
    {
        var raws = Slots(Run()).Select(a => (a, Raw: a.RawText())).ToList();
        var lines = raws.SelectMany(r => r.Raw.Lines).ToList();

        Assert.All(raws, r =>
        {
            Assert.False(r.Raw.IsFileText, r.a.Script.Path);
            Assert.Empty(r.Raw.ReadOnlySections);
            Assert.All(r.a.Document.Sections, s => Assert.True(RawText.RoundTrips(r.a.Script.Path, s.Section)));
        });
        // The bare keys, the values that start with '=', the empty last values and the doubled backslashes.
        Assert.Equal(6, lines.Count(l => l == "BreakFormation"));
        Assert.Equal(4, lines.Count(l => l.Contains(" = = ", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(l => l.EndsWith(", ", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.Contains(@"\\", StringComparison.Ordinal));
        // The map points each entry at its line.
        var first = raws[0];
        var trigger = first.a.Index.Triggers[0].Section;
        Assert.Equal($"[{trigger.Section.Name}]", first.Raw.Lines[first.Raw.LineOf(trigger.Ref.Id, -1)!.Value]);
    }

    /// <summary>The performance gate of section 18.4: the run of the 14 retail scripts stays under 2 s. Other builds can
    /// run on the machine at the same time, so run the test again alone before you judge a failure.</summary>
    [Fact]
    public void TheRunOfTheRetailScriptsStaysUnderTheGate()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6agate");
        var inputs = workspace.Open().MissionInputs(EngineCoverage.Instance);

        var watch = Stopwatch.StartNew();
        var run = MissionRun.Execute(inputs);
        watch.Stop();

        output.WriteLine($"The run of {run.Analyses.Count} scripts took {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(14, run.Analyses.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"The run took {watch.ElapsedMilliseconds} ms.");
    }

    /// <summary>The open of a mission (section 17.1 step 1.7 of the M6a design): a run, the raw text and the marks of
    /// M04 leave the session clean and change no file.</summary>
    [Fact]
    public void AnOpenOfAMissionWritesNothing()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var script = Path.Combine(baseFolder, "DATA", "MISSIONS", "M04", "m04.ini");
        var time = File.GetLastWriteTimeUtc(script);
        using var workspace = new RetailWorkspace(baseFolder, "m6aopen");
        var session = workspace.Open();
        var before = Directory.GetFiles(workspace.ModFolder, "*", SearchOption.AllDirectories).Order().ToList();

        var run = MissionRun.Execute(session.MissionInputs(EngineCoverage.Instance));
        Assert.True(session.TakeMissionRun(run));
        var analysis = run.Of(@"DATA\MISSIONS\M04\m04.ini")!;
        var raw = analysis.RawText();
        var marks = IniLint.Marks(raw, analysis.Problems, showBaseGame: true);

        Assert.NotEmpty(marks);
        Assert.False(session.IsDirty);
        Assert.Equal(time, File.GetLastWriteTimeUtc(script));
        Assert.Equal(before, Directory.GetFiles(workspace.ModFolder, "*", SearchOption.AllDirectories).Order());
    }
}

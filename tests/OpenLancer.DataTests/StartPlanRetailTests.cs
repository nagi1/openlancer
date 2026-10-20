using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the start plan (sections 5.3 and 12.3 of the M6c design) over the 14 story scripts of the base game.
/// The tests count and compare triggers. They hold no retail text.
/// </summary>
public sealed class StartPlanRetailTests(ITestOutputHelper output)
{
    private static IReadOnlyList<MissionAnalysis> Scripts()
    {
        var run = RetailMissions.Run();
        return run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!).ToList();
    }

    private static StartPlan Plan(MissionAnalysis analysis, MissionTrigger trigger) =>
        StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.At(trigger.Nickname));

    [Fact]
    public void EachRestoreTriggerGivesAnExactCheckpointStart()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var restore in DryRun.Checkpoints(analysis.Index))
            {
                var plan = Plan(analysis, restore);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {restore.Nickname}: {plan.Reason}");
                Assert.Equal(StartKind.Checkpoint, plan.Kind);
                // No retail restore trigger has a condition that saves a state, so the save names it alone.
                Assert.Equal([restore.Nickname], plan.SavedTriggers);
                Assert.Empty(plan.Setup);
                count++;
            }
        }

        Assert.Equal(34, count);
    }

    [Fact]
    public void EachStartTriggerGivesAMissionStart()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var start in analysis.Index.StartTriggers)
            {
                var plan = Plan(analysis, start);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {start.Nickname}: {plan.Reason}");
                Assert.Equal(StartKind.MissionStart, plan.Kind);
                Assert.Empty(plan.SavedTriggers);
                Assert.False(plan.Accepted);
                count++;
            }
        }

        Assert.Equal(16, count);
    }

    /// <summary>Each of the 14 restore triggers with Cnd_PlayerLaunch has a place line, and the plan starts in space at
    /// its point.</summary>
    [Fact]
    public void EachPlayerLaunchRestoreTriggerGivesASpaceStartAtThePointOfItsPlaceLine()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var restore in DryRun.Checkpoints(analysis.Index).Where(t => t.ConditionIs("Cnd_PlayerLaunch")))
            {
                var line = restore.Actions.First(a =>
                    a.Key.Equals("Act_MovePlayer", StringComparison.OrdinalIgnoreCase) ||
                    a.Key.Equals("Act_SetInitialPlayerPos", StringComparison.OrdinalIgnoreCase));
                var point = new Vector3(Number(line, 0), Number(line, 1), Number(line, 2));

                var place = Plan(analysis, restore).Place;

                Assert.Equal(StartPlaceKind.Space, place.Kind);
                Assert.Equal(point, place.Position);
                Assert.NotNull(place.System);
                count++;
            }
        }

        Assert.Equal(14, count);
    }

    /// <summary>For every tenth trigger that a start trigger reaches, in file order, the approximate plan has no error
    /// of the mission checks in its setup triggers. A plan can refuse for another reason, for example a stop on another
    /// branch.</summary>
    [Fact]
    public void EveryTenthReachedTriggerHasAnApproximatePlanWithoutACheckError()
    {
        var kinds = new Dictionary<string, int>();
        foreach (var analysis in Scripts())
        {
            var index = analysis.Index;
            var exact = index.StartTriggers.Concat(DryRun.Checkpoints(index)).ToHashSet();
            var reached = index.Triggers.Where(t => index.Reached.Contains(t) && !t.IsDuplicate).ToList();
            for (var i = 0; i < reached.Count; i += 10)
            {
                if (exact.Contains(reached[i]))
                    continue;
                var plan = Plan(analysis, reached[i]);
                Assert.True(plan.Refusal != StartRefusal.SetupProblem,
                    $"{analysis.Script.Slot!.Slot} {reached[i].Nickname}: {plan.Reason}");
                if (plan.CanStart)
                    Assert.Equal(StartKind.Approximate, plan.Kind);
                var key = plan.CanStart ? "ready" : plan.Refusal.ToString();
                kinds[key] = kinds.GetValueOrDefault(key) + 1;
            }
        }

        output.WriteLine(string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
        Assert.True(kinds.GetValueOrDefault("ready") > 200, "Too few approximate plans can start.");
    }

    /// <summary>A trigger that no start trigger and no restore trigger reaches over Start links gives a plan with only
    /// that trigger and the warning.</summary>
    [Fact]
    public void EachTriggerWithoutAPathGivesAPlanWithOnlyThatTrigger()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            var index = analysis.Index;
            var roots = index.StartTriggers.Concat(DryRun.Checkpoints(index)).ToList();
            var linked = Reach(index, roots);
            foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate && !linked.Contains(t)))
            {
                var plan = Plan(analysis, trigger);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {trigger.Nickname}: {plan.Reason}");
                Assert.Equal([trigger.Nickname], plan.ActiveSet);
                Assert.Contains(StartPlan.NoPathWarning(trigger.Nickname), plan.Warnings);
                count++;
            }
        }

        output.WriteLine($"{count} triggers have no path.");
        Assert.True(count > 0);
    }

    /// <summary>The triggers that the roots reach over Start links, the roots included.</summary>
    private static HashSet<MissionTrigger> Reach(MissionIndex index, IReadOnlyList<MissionTrigger> roots)
    {
        var next = index.Links.Where(l => l.Kind == MissionLinkKind.Start && l.Target is { IsDuplicate: false })
            .ToLookup(l => l.Source, l => l.Target!);
        var seen = roots.ToHashSet();
        var queue = new Queue<MissionTrigger>(roots);
        while (queue.Count > 0)
        {
            foreach (var child in next[queue.Dequeue()])
            {
                if (seen.Add(child))
                    queue.Enqueue(child);
            }
        }
        return seen;
    }

    private static float Number(MissionLine line, int index) =>
        float.Parse(line.Value(index)!, NumberStyles.Float, CultureInfo.InvariantCulture);
}

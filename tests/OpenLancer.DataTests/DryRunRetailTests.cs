using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the trigger rule machine and the dry run (sections 4.4 and 12.3 of the M6c design) over the 14
/// story scripts of the base game, and over the 14 HD Edition scripts with <c>OPENLANCER_HDE</c>. The tests count and
/// compare triggers. They hold no retail text.
/// </summary>
public sealed class DryRunRetailTests(ITestOutputHelper output)
{
    private static readonly Lazy<MissionRunResult> Hde = new(() =>
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireHde(), "m6chde");
        return MissionRun.Execute(workspace.Open().MissionInputs(EngineCoverage.Instance));
    });

    private static IReadOnlyList<MissionAnalysis> Slots(MissionRunResult run) =>
        run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!).ToList();

    private static IReadOnlyList<MissionAnalysis> Vanilla() => Slots(RetailMissions.Run());

    private static IReadOnlyList<MissionAnalysis> HdScripts()
    {
        GameDataPaths.RequireHde();
        return Slots(Hde.Value);
    }

    /// <summary>The invariants of a pass: a trigger is in the active list once, and a complete trigger is not in
    /// it.</summary>
    private static void Check(TriggerMachine machine, string slot)
    {
        var active = machine.Active;
        Assert.True(active.Count == active.Distinct().Count(), $"{slot}: a trigger is in the active list twice.");
        Assert.DoesNotContain(active, t => machine.StateOf(t).Status != TriggerStatus.Active);
    }

    /// <summary>Start mission, then 300 steps. Each step fires the first waiting event in key order and runs one
    /// heartbeat. Returns the runs, the steps and the end.</summary>
    private static (int Runs, int Steps, string End) ThreeHundredSteps(MissionAnalysis analysis)
    {
        var slot = analysis.Script.Slot!.Slot;
        var index = analysis.Index;
        var world = new DryRunWorld(index);
        var machine = new TriggerMachine(index, world);
        var runs = machine.StartMission().Changes.Count(c => c.Kind == MachineChangeKind.Ran);
        Check(machine, slot);
        var steps = 0;
        for (; steps < 300 && !machine.IsOver; steps++)
        {
            world.Step = steps + 2;
            var next = WorldEvents.Waiting(machine, world)
                .Where(w => w.Kind is WorldLineKind.Event or WorldLineKind.EventAndFact)
                .OrderBy(w => w.Key, StringComparer.Ordinal).FirstOrDefault();
            if (next != null)
            {
                runs += machine.Dispatch(next.Event).Changes.Count(c => c.Kind == MachineChangeKind.Ran);
                Check(machine, slot);
            }
            runs += machine.Heartbeat().Changes.Count(c => c.Kind == MachineChangeKind.Ran);
            Check(machine, slot);
        }
        var end = machine.End is { } e ? e.Kind.ToString() : machine.Halt is { } h ? h.Kind.ToString() : "none";
        return (runs, steps, end);
    }

    [Fact]
    public void ThreeHundredStepsKeepTheInvariantsInEachVanillaScript()
    {
        var scripts = Vanilla();

        foreach (var analysis in scripts)
        {
            var (runs, steps, end) = ThreeHundredSteps(analysis);
            output.WriteLine($"{analysis.Script.Slot!.Slot}: {runs} runs in {steps} steps, end {end}");
            Assert.True(runs > 0, $"{analysis.Script.Slot.Slot}: no trigger ran.");
        }
        Assert.Equal(14, scripts.Count);
    }

    /// <summary>
    /// The 38 restart pairs of the base game (TR9). The source and the target start in one load, and the time advances
    /// while both wait. Then the source runs. A target that waited stops and starts again in that pass, with a new start
    /// heartbeat. The test counts the pairs whose target waited, and the pairs whose start heartbeat changed.
    /// </summary>
    [Fact]
    public void EachRestartPairStartsItsTargetAgain()
    {
        var (pairs, restarts, later) = (0, 0, 0);
        foreach (var analysis in Vanilla())
        {
            var index = analysis.Index;
            foreach (var link in index.Restarts.Where(l => l.Target != null))
            {
                var world = new DryRunWorld(index);
                var machine = new TriggerMachine(index, world);
                var source = link.Source;
                var target = link.Target!;
                MachinePass pass;
                bool waited;
                int since;
                if (source.ConditionIs("Cnd_True"))
                {
                    // A Cnd_True source runs at its start, in the load, so no time goes by before it runs. Its starts
                    // wait for the next pass. The test checks the start of the target only.
                    machine.Load([source]);
                    (waited, since) = (false, -1);
                    pass = machine.Heartbeat();
                }
                else
                {
                    machine.Load([source, target]);
                    machine = Advance(machine, index, world, source, target);
                    Assert.Equal(TriggerStatus.Active, machine.StateOf(source).Status);
                    waited = machine.StateOf(target).Status == TriggerStatus.Active;
                    since = machine.StateOf(target).ActiveSince;
                    pass = machine.Force(source);
                }

                var stopped = pass.Changes.ToList().FindIndex(c => c.Kind == MachineChangeKind.Stopped && c.Trigger == target);
                var started = pass.Changes.ToList().FindLastIndex(c => c.Kind == MachineChangeKind.Started && c.Trigger == target);
                Assert.True(started >= 0, $"{source.Nickname}: the target {target.Nickname} did not start.");
                Assert.Equal(machine.Heartbeats, machine.StateOf(target).ActiveSince);
                if (waited)
                {
                    // A target that waited stops first, then starts again (TR9).
                    Assert.True(stopped >= 0 && stopped < started, $"{source.Nickname}: {target.Nickname} did not restart.");
                    restarts++;
                    if (machine.StateOf(target).ActiveSince > since)
                        later++;
                }
                pairs++;
            }
        }
        output.WriteLine($"{pairs} restart pairs, {restarts} with a target that waited, {later} with a new start heartbeat");
        // The 2 other pairs have a Cnd_True source, which runs at its start, so their target cannot wait before it.
        Assert.Equal((38, 36, 36), (pairs, restarts, later));
    }

    /// <summary>Advances the time by up to 10 heartbeats while the source and the target of a restart pair wait. A
    /// heartbeat that makes one of them run or stop is undone, so the pair stays as it was.</summary>
    private static TriggerMachine Advance(TriggerMachine machine, MissionIndex index, DryRunWorld world,
        MissionTrigger source, MissionTrigger target)
    {
        var targetWaits = machine.StateOf(target).Status == TriggerStatus.Active;
        for (var i = 0; i < 10; i++)
        {
            var before = machine.Snapshot();
            machine.Heartbeat();
            if (machine.IsOver || machine.StateOf(source).Status != TriggerStatus.Active ||
                targetWaits && machine.StateOf(target).Status != TriggerStatus.Active)
                return TriggerMachine.From(before, index, world);
        }
        return machine;
    }

    /// <summary>A trigger that starts two or more Cnd_True triggers: their runs come in id order (TR7).</summary>
    [Fact]
    public void TheCndTrueTriggersOfOneTriggerRunInIdOrder()
    {
        var sources = 0;
        foreach (var analysis in Vanilla())
        {
            var index = analysis.Index;
            foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate))
            {
                var children = index.Links.Where(l => l.Source == trigger && l.Kind == MissionLinkKind.Start &&
                                                      l.Target is { IsDuplicate: false } target && target.ConditionIs("Cnd_True"))
                    .Select(l => l.Target!).Distinct().ToList();
                if (children.Count < 2)
                    continue;
                var machine = new TriggerMachine(index, new DryRunWorld(index));
                machine.Load([trigger]);
                if (machine.Halt != null)
                    continue;
                var runs = machine.StateOf(trigger).Status == TriggerStatus.Active
                    ? machine.Force(trigger).Changes
                    : machine.Heartbeat().Changes;
                var order = runs.Where(c => c.Kind == MachineChangeKind.Ran && children.Contains(c.Trigger))
                    .Select(c => c.Trigger).Distinct().ToList();

                sources++;
                Assert.True(order.Count >= 2, $"{trigger.Nickname}: fewer than two of its Cnd_True triggers ran.");
                Assert.Equal(order.OrderBy(t => FLHash.CreateID(t.Nickname)), order);
            }
        }
        output.WriteLine($"{sources} triggers start two or more Cnd_True triggers");
        Assert.True(sources > 100, $"Only {sources} triggers start two or more Cnd_True triggers.");
    }

    /// <summary>Each of the 34 restore triggers of the base game loads as a checkpoint of the dry run: it starts, and the
    /// run does not stop.</summary>
    [Fact]
    public void EachVanillaCheckpointLoads()
    {
        var (restores, saves) = LoadEachCheckpoint(Vanilla());

        Assert.Equal((34, 36), (restores, saves));
    }

    [Fact]
    public void TheDryRunAndTheCheckpointsOfTheHdScripts()
    {
        var scripts = HdScripts();
        foreach (var analysis in scripts)
        {
            var (runs, steps, end) = ThreeHundredSteps(analysis);
            output.WriteLine($"HD {analysis.Script.Slot!.Slot}: {runs} runs in {steps} steps, end {end}");
        }

        var (restores, saves) = LoadEachCheckpoint(scripts);

        Assert.Equal(14, scripts.Count);
        Assert.Equal((33, 35), (restores, saves));
    }

    private (int Restores, int Saves) LoadEachCheckpoint(IReadOnlyList<MissionAnalysis> scripts)
    {
        var restores = 0;
        var saves = 0;
        foreach (var analysis in scripts)
        {
            var index = analysis.Index;
            saves += index.Links.Count(l => l.Kind == MissionLinkKind.Load);
            foreach (var restore in DryRun.Checkpoints(index))
            {
                var state = DryRun.Fold(index, [DryRunInput.LoadCheckpoint(restore.Nickname)], strict: true);
                var step = Assert.Single(state.Trace.Steps);
                Assert.Contains(step.Changes, c => c.Kind == TraceChangeKind.Started && c.Trigger == restore.Nickname);
                Assert.Null(state.Machine.Halt);
                restores++;
            }
        }
        output.WriteLine($"{restores} restore triggers, {saves} Act_Save lines");
        return (restores, saves);
    }

    /// <summary>
    /// The two performance gates of section 4.4 of the M6c design over M10, the script with the most triggers: a fold of
    /// 300 inputs with 18,000 heartbeats in all takes at most 16 ms, and Back after 1,000 inputs takes at most 5 ms.
    /// Other builds can run on the machine at the same time, so the test measures 10 runs of each and takes the best.
    /// Run the test again alone before you judge a failure.
    /// </summary>
    [Fact]
    public void TheFoldGatesOverM10()
    {
        const int Runs = 10;
        var index = RetailMissions.Slot("M10").Index;
        var fold = Inputs(index, 300, 120);
        Assert.Equal(300, fold.Count);
        Assert.Equal(18_000, fold.Sum(i => i.MostHeartbeats));
        DryRun.Fold(index, fold);
        var foldTimes = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            var state = DryRun.Fold(index, fold);
            watch.Stop();
            foldTimes.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(300, state.Trace.Steps.Count);
        }

        var run = new DryRun(index);
        foreach (var input in Inputs(index, 1000, 5))
            run.Add(input);
        var last = run.Inputs[^1];
        var backTimes = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            run.Back();
            watch.Stop();
            backTimes.Add(watch.Elapsed.TotalMilliseconds);
            run.Add(last);
        }

        // The best time of the runs is the cost of the fold. A slower run waited for other work of the machine.
        var foldBest = foldTimes.Min();
        var backBest = backTimes.Min();
        output.WriteLine($"Fold of 300 inputs, 18,000 heartbeats: {string.Join(", ", foldTimes.Select(t => t.ToString("F2")))} ms");
        output.WriteLine($"Back after 1,000 inputs: {string.Join(", ", backTimes.Select(t => t.ToString("F2")))} ms");
        Assert.True(foldBest <= 16, $"The fold of M10 took {foldBest:F2} ms.");
        Assert.True(backBest <= 5, $"Back after 1,000 inputs took {backBest:F2} ms.");
    }

    /// <summary>Inputs of a run: Start mission, then in turn a fire of the first waiting event in key order and a number
    /// of heartbeats. A step without a waiting event sets the player in space, which changes nothing.</summary>
    private static IReadOnlyList<DryRunInput> Inputs(MissionIndex index, int count, int heartbeats)
    {
        var run = new DryRun(index);
        run.Add(DryRunInput.StartMission());
        for (var i = 1; i < count; i++)
        {
            if (i % 2 == 1)
            {
                run.Add(DryRunInput.Heartbeats(heartbeats));
                continue;
            }
            var next = run.State.Waiting.Where(w => w.Kind is WorldLineKind.Event or WorldLineKind.EventAndFact)
                .OrderBy(w => w.Key, StringComparer.Ordinal).FirstOrDefault();
            run.Add(next != null ? DryRunInput.Event(WorldEvents.PatternOf(next.Line)) : DryRunInput.PlayerInSpace(true));
        }
        return run.Inputs;
    }
}

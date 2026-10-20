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

    /// <summary>The 38 restart pairs of the base game: after a run of the source, the target is active again with a new
    /// start heartbeat (TR9).</summary>
    [Fact]
    public void EachRestartPairStartsItsTargetAgain()
    {
        var pairs = 0;
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
                if (source.ConditionIs("Cnd_True"))
                {
                    // A Cnd_True source runs at the load. Its starts wait for the next pass.
                    machine.Load([source]);
                    pass = machine.Heartbeat();
                }
                else
                {
                    machine.Load([source, target]);
                    // A source that waits for an event does not run in a heartbeat of an empty world, so the time can
                    // advance first. Then the restart gives the target a new start heartbeat.
                    if (WorldEvents.KindOf(source.Condition) == WorldLineKind.Event)
                    {
                        for (var i = 0; i < 10; i++)
                            machine.Heartbeat();
                    }
                    Assert.Equal(TriggerStatus.Active, machine.StateOf(source).Status);
                    pass = machine.Force(source);
                }

                Assert.Contains(pass.Changes, c => c.Kind == MachineChangeKind.Started && c.Trigger == target);
                Assert.Equal(machine.Heartbeats, machine.StateOf(target).ActiveSince);
                pairs++;
            }
        }
        output.WriteLine($"{pairs} restart pairs");
        Assert.Equal(38, pairs);
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
    /// Other builds can run on the machine at the same time, so run the test again alone before you judge a
    /// failure.
    /// </summary>
    [Fact]
    public void TheFoldGatesOverM10()
    {
        var index = RetailMissions.Slot("M10").Index;
        var fold = Inputs(index, 300, 120);
        Assert.Equal(300, fold.Count);
        Assert.Equal(18_000, fold.Sum(i => i.MostHeartbeats));
        DryRun.Fold(index, fold);
        var foldTimes = new List<double>();
        for (var i = 0; i < 5; i++)
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
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            run.Back();
            watch.Stop();
            backTimes.Add(watch.Elapsed.TotalMilliseconds);
            run.Add(last);
        }

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

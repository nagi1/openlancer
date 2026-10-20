using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>The events and facts of the dry run (section 4.3 of the M6c design, decision D755) on synthetic
/// scripts.</summary>
public sealed class WorldEventsTests
{
    private static (TriggerMachine Machine, DryRunWorld World) Run(string text)
    {
        var index = Index(text);
        var world = new DryRunWorld(index);
        var machine = new TriggerMachine(index, world);
        machine.StartMission();
        return (machine, world);
    }

    private static string Start(string name, string condition, params string[] actions) =>
        $"[Trigger]\nnickname = {name}\nInitState = ACTIVE\n{condition}\n{string.Join("\n", actions)}\n";

    private static string Trigger(string name, string condition, params string[] actions) =>
        $"[Trigger]\nnickname = {name}\n{condition}\n{string.Join("\n", actions)}\n";

    private static List<string> Ran(MachinePass pass) =>
        pass.Changes.Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger.Nickname).ToList();

    [Fact]
    public void EachDistinctWaitingLineHasOneKey()
    {
        var (machine, world) = Run(
            Start("t_a", "Cnd_CommComplete = ol_line_1") +
            Start("t_b", "Cnd_CommComplete = OL_LINE_1") +
            Start("t_c", "Cnd_CommComplete = ol_line_2") +
            Start("t_d", "Cnd_BaseEnter = ANY") +
            Start("t_e", "Cnd_BaseEnter = any") +
            Start("t_f", "Cnd_Timer = 5"));

        var rows = WorldEvents.Waiting(machine, world);

        // Names compare without letter case. ANY of Cnd_BaseEnter compares with letter case (TR16). The machine tests
        // a timer itself, so it has no row.
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Single(r => r.Line.Value(0) is "ol_line_1" or "OL_LINE_1").Triggers.Count);
        Assert.Equal(2, rows.Count(r => r.Line.Key == "Cnd_BaseEnter"));
        Assert.All(rows, r => Assert.Equal(WorldLineKind.Event, r.Kind));
    }

    [Fact]
    public void TwoTriggersWithOneLineReactToOneEventInListOrder()
    {
        var (machine, world) = Run(Start("t_first", "Cnd_LaunchComplete = ol_ship") + Start("t_second", "Cnd_LaunchComplete = ol_ship"));
        var row = Assert.Single(WorldEvents.Waiting(machine, world));
        var order = machine.Active.Select(t => t.Nickname).ToList();

        var pass = machine.Dispatch(row.Event);

        Assert.Equal(order, Ran(pass));
        Assert.Equal("Starts when ol_ship completes its launch.", row.Sentence);
    }

    [Fact]
    public void ACountingConditionNeedsItsCountAndItsRowShowsIt()
    {
        var (machine, world) = Run(Start("t_kills", "Cnd_Destroyed = ol_wing, 3"));
        var row = Assert.Single(WorldEvents.Waiting(machine, world));

        machine.Dispatch(row.Event);
        var after = Assert.Single(WorldEvents.Waiting(machine, world));
        machine.Dispatch(row.Event);
        var third = machine.Dispatch(row.Event);

        Assert.Equal("Starts when 3 ships of ol_wing are destroyed. (1 of 3)", after.Sentence);
        Assert.Equal(["t_kills"], Ran(third));
    }

    [Fact]
    public void AFactFiresAtTheNextHeartbeat()
    {
        var (machine, world) = Run(Start("t_lane", "Cnd_InTradelane = true, Player"));
        var row = Assert.Single(WorldEvents.Waiting(machine, world));
        Assert.Equal(WorldLineKind.Fact, row.Kind);

        world.SetFact(row.Key, true);
        var pass = machine.Heartbeat();

        Assert.Equal(["t_lane"], Ran(pass));
    }

    [Fact]
    public void RunInSpaceOnlyDoesNotCountWhileThePlayerIsNotInSpace()
    {
        var index = Index(Start("t_space", "Cnd_Timer = 1, 1, RUN_IN_SPACE_ONLY"));
        var world = new DryRunWorld(index) { InSpace = false };
        var machine = new TriggerMachine(index, world);
        machine.StartMission();

        for (var i = 0; i < 40; i++)
            machine.Heartbeat();

        Assert.Equal(TriggerStatus.Active, machine.StateOf(index.Trigger("t_space")!).Status);
        world.InSpace = true;
        for (var i = 0; i < 30; i++)
            machine.Heartbeat();
        Assert.Equal(TriggerStatus.Complete, machine.StateOf(index.Trigger("t_space")!).Status);
    }

    [Fact]
    public void CndSystemEnterIsTrueAtTheStartInThePlayerSystem()
    {
        var index = Index(Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_here") +
                          Trigger("t_here", "Cnd_SystemEnter = ol_system"));
        var world = new DryRunWorld(index) { PlayerSystem = "OL_SYSTEM" };
        var machine = new TriggerMachine(index, world);

        var start = machine.StartMission();
        var beat = machine.Heartbeat();

        Assert.Equal(["t_go"], Ran(start));
        Assert.Equal(["t_here"], Ran(beat));
    }

    [Fact]
    public void TheRowsThatTheRunMakesLikelyComeFirst()
    {
        var index = Index("""
            [MsnShip]
            nickname = ol_pirate
            label = ol_wave

            [MsnFormation]
            nickname = ol_form
            ship = ol_pirate

            [Trigger]
            nickname = t_wait
            InitState = ACTIVE
            Cnd_BaseEnter = ol_base

            [Trigger]
            nickname = t_go
            InitState = ACTIVE
            Cnd_True = no_params
            Act_SendComm = ol_sender, Player, ol_line_1
            Act_SpawnFormation = ol_form
            Act_ActTrig = t_kill
            Act_ActTrig = t_comm

            [Trigger]
            nickname = t_kill
            Cnd_Destroyed = ol_wave

            [Trigger]
            nickname = t_comm
            Cnd_CommComplete = ol_line_1
            """);
        var world = new DryRunWorld(index) { Step = 1 };
        var machine = new TriggerMachine(index, world);
        machine.StartMission();
        world.Step = 2;
        machine.Heartbeat();

        var rows = WorldEvents.Waiting(machine, world);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Cnd_BaseEnter", rows[^1].Line.Key);
        Assert.Equal(["Sent at step 1", "Spawned at step 1"],
            rows.Take(2).Select(r => r.Cause!.Value.Text).Order());
    }

    [Fact]
    public void ALineWithAPartOfTheValuesMatchesTheLineWithAllValues()
    {
        var index = Index(Start("t_a", "Cnd_Destroyed = ol_pirate, -1") + Start("t_b", "Cnd_Destroyed = ol_other, -1"));
        var lines = index.Triggers.Select(t => t.Condition!).ToList();

        var match = WorldEvents.Match(LineSpec.Of("cnd_destroyed", ("target", "OL_PIRATE")), lines);
        var both = WorldEvents.Match(LineSpec.Of("Cnd_Destroyed", ("count", -1)), lines);
        var none = WorldEvents.Match(LineSpec.Of("Cnd_Destroyed", ("target", "ol_pirate"), ("count", 2)), lines);

        Assert.Equal("t_a", Assert.Single(match).Trigger!.Nickname);
        Assert.Equal(2, both.Count);
        Assert.Empty(none);
    }

    [Fact]
    public void AFullLineWinsOverALineWithMoreValues()
    {
        var index = Index(Start("t_a", "Cnd_Destroyed = ol_pirate") + Start("t_b", "Cnd_Destroyed = ol_pirate, 2"));
        var lines = index.Triggers.Select(t => t.Condition!).ToList();

        var match = WorldEvents.Match(WorldEvents.PatternOf(lines[0]), lines);

        Assert.Same(lines[0], Assert.Single(match));
    }

    [Fact]
    public void TheFactLinesOfAScriptAreItsPollConditionsOfTheWorld()
    {
        var index = Index(Start("t_a", "Cnd_InSpace = true") + Start("t_b", "Cnd_InSpace = true") +
                          Start("t_c", "Cnd_Timer = 1") + Start("t_d", "Cnd_NPCSystemEnter = ol_system, ol_ship"));

        var lines = WorldEvents.FactLines(index);

        Assert.Equal(["Cnd_InSpace", "Cnd_NPCSystemEnter"], lines.Select(l => l.Key));
        Assert.Equal(WorldLineKind.EventAndFact, WorldEvents.KindOf(lines[1]));
        Assert.Null(WorldEvents.KindOf(index.Trigger("t_c")!.Condition));
    }
}

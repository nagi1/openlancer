using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using LibreLancer.Data;

namespace OpenLancer.Data.Missions.Run;

/// <summary>How the machine tests the condition of a trigger (section 3.5 of the M6c design).</summary>
internal enum ConditionKind
{
    /// <summary>No condition line, or only a condition name that the schema does not know. The game stops when such a
    /// trigger starts (TR6).</summary>
    None,

    /// <summary><c>Cnd_True</c>: true at the start and at each heartbeat.</summary>
    True,

    /// <summary><c>Cnd_Timer</c>: counts heartbeats since the start of the trigger.</summary>
    Timer,

    /// <summary><c>Cnd_WatchTrigger</c>: polls the state of another trigger.</summary>
    Watch,

    /// <summary><c>Cnd_SystemEnter</c>: one test at the start, then it waits for its event.</summary>
    StartTest,

    /// <summary>A condition that waits for an event of the world.</summary>
    Event,

    /// <summary>A condition of a fact of the world that the game tests at each heartbeat.</summary>
    Poll,

    /// <summary>A fact of the world that also listens to events: <c>Cnd_NPCSystemEnter</c>.</summary>
    PollAndEvent,
}

/// <summary>The state that a <c>Cnd_WatchTrigger</c> line watches (CN-15).</summary>
internal enum WatchState
{
    /// <summary>A word that the game does not know. The condition is never true.</summary>
    Unknown,

    /// <summary><c>ON</c> or <c>ACTIVE</c>: the trigger is active.</summary>
    Active,

    /// <summary><c>OFF</c>: the trigger is not active.</summary>
    NotActive,

    /// <summary><c>INACTIVE</c>: the trigger is inactive.</summary>
    Inactive,

    /// <summary><c>COMPLETE</c>: the trigger ran.</summary>
    Complete,
}

/// <summary>The flow of an action line.</summary>
internal enum ActionKind
{
    Start,
    Stop,
    End,
    Checkpoint,
    Effect,
}

/// <summary>One action line of a trigger, with its target trigger resolved by game id.</summary>
/// <param name="Target">The slot of the target trigger of a start, a stop or a checkpoint, or -1.</param>
/// <param name="Name">The trigger name of a start, a stop or a checkpoint, as the line writes it.</param>
internal readonly record struct MachineAction(ActionKind Kind, MissionLine Line, int Target, string Name);

/// <summary>
/// The triggers of a mission index in the form that the machine runs (section 3.2 of the M6c design): one slot for each
/// trigger that the game keeps, the parts of each condition, and the actions with their targets. The game keys the
/// triggers by game id (<c>FLHash.CreateID</c>), so a second trigger with the id of an earlier one has no slot (TR4). The
/// program of an index is made once and shared by all machines of the index.
/// </summary>
internal sealed class MachineProgram
{
    /// <summary>The heartbeats of one second (TR11).</summary>
    public const int HeartbeatsPerSecond = 30;

    private static readonly ConditionalWeakTable<MissionIndex, MachineProgram> Programs = new();

    private readonly Dictionary<MissionTrigger, int> slotOf = [];
    private readonly Dictionary<uint, int> slotById = [];

    private MachineProgram(MissionIndex index)
    {
        Index = index;
        var triggers = new List<MissionTrigger>();
        foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate))
        {
            var id = FLHash.CreateID(trigger.Nickname);
            if (!slotById.TryAdd(id, triggers.Count))
                continue;
            slotOf[trigger] = triggers.Count;
            triggers.Add(trigger);
        }
        Triggers = triggers;
        Ids = triggers.Select(t => FLHash.CreateID(t.Nickname)).ToArray();
        Kinds = new ConditionKind[triggers.Count];
        TimerShortest = new int[triggers.Count];
        TimerLongest = new int[triggers.Count];
        InSpaceOnly = new bool[triggers.Count];
        WatchTarget = new int[triggers.Count];
        WatchStates = new WatchState[triggers.Count];
        CountNeeded = new int[triggers.Count];
        Actions = new MachineAction[triggers.Count][];
        for (var slot = 0; slot < triggers.Count; slot++)
            Compile(slot, triggers[slot]);
        StartSet = triggers.Select((t, slot) => (t, slot)).Where(p => p.t.StartsActive).OrderBy(p => Ids[p.slot])
            .Select(p => p.slot).ToArray();
    }

    /// <summary>The program of an index. The machines of one index share it.</summary>
    public static MachineProgram Of(MissionIndex index) => Programs.GetValue(index, i => new MachineProgram(i));

    public MissionIndex Index { get; }

    /// <summary>The triggers that the game keeps, in file order, by slot.</summary>
    public IReadOnlyList<MissionTrigger> Triggers { get; }

    /// <summary>The game id of each slot.</summary>
    public uint[] Ids { get; }

    public ConditionKind[] Kinds { get; }

    /// <summary>The heartbeats of a timer with the lower bound of its range.</summary>
    public int[] TimerShortest { get; }

    /// <summary>The heartbeats of a timer with the upper bound of its range, or the lower bound without one.</summary>
    public int[] TimerLongest { get; }

    /// <summary>True for a timer with the token <c>RUN_IN_SPACE_ONLY</c>.</summary>
    public bool[] InSpaceOnly { get; }

    /// <summary>The slot that a <c>Cnd_WatchTrigger</c> line watches, or -1.</summary>
    public int[] WatchTarget { get; }

    public WatchState[] WatchStates { get; }

    /// <summary>The matches that a counting condition needs. 1 for each other condition.</summary>
    public int[] CountNeeded { get; }

    /// <summary>The actions of each slot, in file order.</summary>
    public MachineAction[][] Actions { get; }

    /// <summary>The slots of the start triggers in id order (TR7).</summary>
    public int[] StartSet { get; }

    /// <summary>The slot of a trigger, or -1 when the game does not keep it (TR4).</summary>
    public int SlotOf(MissionTrigger trigger) => slotOf.GetValueOrDefault(trigger, -1);

    /// <summary>The slot of the trigger that a name names, by game id, or -1. The game finds a trigger by the id of its
    /// name, so the letter case does not matter.</summary>
    public int SlotOf(string name) =>
        name.Trim() is { Length: > 0 } trimmed ? slotById.GetValueOrDefault(FLHash.CreateID(trimmed), -1) : -1;

    /// <summary>The slot of a game id.</summary>
    public int SlotById(uint id) => slotById[id];

    /// <summary>True for a condition that the machine tests at each heartbeat.</summary>
    public bool PollsAt(int slot) =>
        Kinds[slot] is ConditionKind.True or ConditionKind.Timer or ConditionKind.Watch or ConditionKind.Poll or
            ConditionKind.PollAndEvent;

    private void Compile(int slot, MissionTrigger trigger)
    {
        var condition = trigger.Condition;
        Kinds[slot] = KindOf(condition);
        WatchTarget[slot] = -1;
        CountNeeded[slot] = 1;
        switch (Kinds[slot])
        {
            case ConditionKind.Timer:
                var lower = Heartbeats(condition!.Value(0));
                TimerShortest[slot] = lower;
                TimerLongest[slot] = condition.Value(1) is { } upper ? Math.Max(lower, Heartbeats(upper)) : lower;
                InSpaceOnly[slot] = MissionIndex.Is(condition.Value(2), "RUN_IN_SPACE_ONLY");
                break;
            case ConditionKind.Watch:
                // A name that does not resolve does nothing (TR13), so the watch is never true.
                WatchTarget[slot] = condition!.Value(0) is { } watched ? SlotOf(watched) : -1;
                WatchStates[slot] = (condition.Value(1)?.ToUpperInvariant()) switch
                {
                    "ON" or "ACTIVE" => WatchState.Active,
                    "OFF" => WatchState.NotActive,
                    "INACTIVE" => WatchState.Inactive,
                    "COMPLETE" => WatchState.Complete,
                    _ => WatchState.Unknown,
                };
                break;
            case ConditionKind.Event:
                CountNeeded[slot] = CountOf(condition!);
                break;
        }
        Actions[slot] = trigger.Actions.Select(Action).ToArray();
    }

    private MachineAction Action(MissionLine line)
    {
        var kind = line.Key.ToLowerInvariant() switch
        {
            "act_acttrig" => ActionKind.Start,
            "act_deacttrig" => ActionKind.Stop,
            "act_changestate" => ActionKind.End,
            "act_save" => ActionKind.Checkpoint,
            _ => ActionKind.Effect,
        };
        if (kind is ActionKind.End or ActionKind.Effect)
            return new MachineAction(kind, line, -1, "");
        var name = line.Value(0) ?? "";
        return new MachineAction(kind, line, SlotOf(name), name);
    }

    /// <summary>The kind of a condition line of the schema (section 3.5 of the M6c design).</summary>
    internal static ConditionKind KindOf(MissionLine? condition)
    {
        if (condition?.Name is not { Kind: MissionNameKind.Condition } name)
            return ConditionKind.None;
        return name.Name.ToLowerInvariant() switch
        {
            "cnd_true" => ConditionKind.True,
            "cnd_timer" => ConditionKind.Timer,
            "cnd_watchtrigger" => ConditionKind.Watch,
            "cnd_systementer" => ConditionKind.StartTest,
            _ when name.Polls && name.Wake.Count > 0 => ConditionKind.PollAndEvent,
            _ when name.Polls => ConditionKind.Poll,
            _ => ConditionKind.Event,
        };
    }

    /// <summary>
    /// The matches that a counting condition needs (section 3.5 of the M6c design): the value <c>count</c> of
    /// <c>Cnd_Destroyed</c>, <c>Cnd_ProjHit</c>, <c>Cnd_ProjHitShipToLbl</c> and <c>Cnd_LootAcquired</c>. A count of 1 or
    /// less, or no count, needs one match. <c>Cnd_Destroyed</c> with -1 waits for the last ship, which is one event of
    /// the dry run.
    /// </summary>
    internal static int CountOf(MissionLine condition) =>
        condition.Name?.Value("count") is { Kind: MissionValueKind.WholeNumber } spec &&
        int.TryParse(condition.ValueOf(spec), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 1
            ? count
            : 1;

    /// <summary>The heartbeats of a time in seconds: the first heartbeat at or after the time (TR11). A text that is not
    /// a number reads as 0, as the game reads it.</summary>
    internal static int Heartbeats(string? seconds)
    {
        if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value <= 0)
            return 0;
        return (int)Math.Min(int.MaxValue / 2, Math.Ceiling(value * HeartbeatsPerSecond - 1e-6));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Run;

/// <summary>The kind of an input of a dry run (section 4.2 of the M6c design).</summary>
public enum DryRunInputKind
{
    /// <summary>Starts a new run: the start triggers start (Start mission).</summary>
    StartMission,

    /// <summary>Starts a new run: the restore trigger of a checkpoint starts, as a load of a saved game does.</summary>
    LoadCheckpoint,

    /// <summary>Starts a new run at a trigger, with the triggers that wait there. It comes with the start plan of
    /// M6c-2.</summary>
    StartAt,

    /// <summary>An active trigger runs as if its condition is met now.</summary>
    RunTrigger,

    /// <summary>A world event happens: one waiting condition line.</summary>
    Event,

    /// <summary>A fact of a poll condition becomes true or not true.</summary>
    Fact,

    /// <summary>The time advances by heartbeats of 1/30 s, or until the next timer runs ("Next timer").</summary>
    Heartbeats,

    /// <summary>The system of the player changes.</summary>
    PlayerSystem,

    /// <summary>The player comes into space or leaves it.</summary>
    PlayerInSpace,
}

/// <summary>One input of a dry run (section 4.2 of the M6c design). Inputs name triggers by nickname, without letter
/// case.</summary>
public sealed record DryRunInput(DryRunInputKind Kind)
{
    /// <summary>The trigger of a Load checkpoint, a Start at trigger or a Run trigger input, else null.</summary>
    public string? Trigger { get; init; }

    /// <summary>The line of an Event or a Fact input: the condition and the values that it gives (section 7.3 of the
    /// M6c design), else null.</summary>
    public LineSpec? Line { get; init; }

    /// <summary>True for a fact that becomes true, and for a player that comes into space.</summary>
    public bool Value { get; init; }

    /// <summary>The heartbeats of a Heartbeats input, from 1 to 18,000.</summary>
    public int Count { get; init; }

    /// <summary>True for "Next timer": heartbeats until the next timer runs, at most 18,000.</summary>
    public bool UntilTimer { get; init; }

    /// <summary>The system of a Player system input, or null for no system.</summary>
    public string? System { get; init; }

    /// <summary>True for an input that starts a new run. It removes the inputs before it.</summary>
    public bool BeginsRun => Kind is DryRunInputKind.StartMission or DryRunInputKind.LoadCheckpoint or DryRunInputKind.StartAt;

    /// <summary>True for an input that changes only the world: a fact or a fact about the player.</summary>
    public bool IsWorld => Kind is DryRunInputKind.Fact or DryRunInputKind.PlayerSystem or DryRunInputKind.PlayerInSpace;

    /// <summary>The heartbeats that the input can run at most.</summary>
    public int MostHeartbeats => Kind != DryRunInputKind.Heartbeats ? 0 : UntilTimer ? DryRun.MaxHeartbeats : Count;

    public static DryRunInput StartMission() => new(DryRunInputKind.StartMission);

    public static DryRunInput LoadCheckpoint(string trigger) => new(DryRunInputKind.LoadCheckpoint) { Trigger = trigger };

    public static DryRunInput StartAt(string trigger) => new(DryRunInputKind.StartAt) { Trigger = trigger };

    public static DryRunInput RunTrigger(string trigger) => new(DryRunInputKind.RunTrigger) { Trigger = trigger };

    public static DryRunInput Event(LineSpec line) => new(DryRunInputKind.Event) { Line = line };

    public static DryRunInput Fact(LineSpec line, bool on) => new(DryRunInputKind.Fact) { Line = line, Value = on };

    public static DryRunInput Heartbeats(int count) => new(DryRunInputKind.Heartbeats) { Count = count };

    public static DryRunInput NextTimer() => new(DryRunInputKind.Heartbeats) { UntilTimer = true };

    public static DryRunInput PlayerSystem(string? system) => new(DryRunInputKind.PlayerSystem) { System = system };

    public static DryRunInput PlayerInSpace(bool value) => new(DryRunInputKind.PlayerInSpace) { Value = value };

    /// <summary>The input with a renamed trigger, or the same input when it does not name the old name.</summary>
    public DryRunInput Renamed(string from, string to) =>
        Trigger != null && MissionIndex.Is(Trigger, from.Trim()) ? this with { Trigger = to } : this;
}

/// <summary>The state of a dry run after its inputs: the trace, the machine of the last run and the world.</summary>
public sealed class DryRunState
{
    internal DryRunState(MissionTrace trace, TriggerMachine machine, DryRunWorld world)
    {
        Trace = trace;
        Machine = machine;
        World = world;
    }

    /// <summary>One step for each input.</summary>
    public MissionTrace Trace { get; }

    /// <summary>The machine of the last run. Before the first Start mission or Load checkpoint, a machine that did not
    /// start.</summary>
    public TriggerMachine Machine { get; internal set; }

    public DryRunWorld World { get; internal set; }

    /// <summary>The lines that the active triggers wait for, one row for each line.</summary>
    public IReadOnlyList<WaitingLine> Waiting => WorldEvents.Waiting(Machine, World);
}

/// <summary>
/// The dry run of the Mission tab (section 4 of the M6c design): a list of inputs and their fold over a new trigger rule
/// machine. The state of the run is the fold of the inputs, so the same inputs give the same trace. A snapshot of the
/// fold after each 50 inputs keeps "Back" fast in a long run. A new model of the script gives a new fold, and a rename
/// moves the inputs to the new name.
/// </summary>
/// <remarks>The editor UI keeps one instance for the open mission. The dry run query of the shared command layer folds
/// the inputs of a request with <see cref="Fold"/> and keeps no state (section 7.2 of the M6c design).</remarks>
public sealed class DryRun
{
    /// <summary>The most heartbeats of one Heartbeats input, and of "Next timer" (10 minutes).</summary>
    public const int MaxHeartbeats = 18_000;

    /// <summary>The inputs between two snapshots of the fold.</summary>
    public const int SnapshotEvery = 50;

    private readonly List<DryRunInput> inputs = [];
    private readonly List<Snapshot> snapshots = [];
    private Folder folder;

    /// <summary>A dry run of a model of a script without inputs.</summary>
    public DryRun(MissionIndex index, TimerBound timers = TimerBound.Shortest)
    {
        Timers = timers;
        folder = new Folder(index, Options(timers), strict: false);
    }

    /// <summary>The index of the model that the run folds over.</summary>
    public MissionIndex Index => folder.Index;

    /// <summary>The bound of a timer range (decision D756).</summary>
    public TimerBound Timers { get; private set; }

    /// <summary>The inputs in order: one step of the trace for each.</summary>
    public IReadOnlyList<DryRunInput> Inputs => inputs;

    /// <summary>The state of the run after the inputs.</summary>
    public DryRunState State => folder.State;

    /// <summary>True after a Start mission or a Load checkpoint input. Before it, the panel shows how to start.</summary>
    public bool Started => inputs.Any(i => i.BeginsRun);

    /// <summary>The restore triggers of the script: each trigger that an <c>Act_Save</c> line names, in file
    /// order.</summary>
    public static IReadOnlyList<MissionTrigger> Checkpoints(MissionIndex index) =>
        index.Links.Where(l => l.Kind == MissionLinkKind.Load && l.Target is { IsDuplicate: false })
            .Select(l => l.Target!).Distinct().ToList();

    /// <summary>
    /// Adds an input and folds it. An input that starts a new run removes the inputs before it (section 4.2 of the M6c
    /// design), except the facts and the facts about the player: the world stays, as it stays when a game loads. They
    /// stay before the new input in their net form.
    /// </summary>
    public void Add(DryRunInput input)
    {
        if (input.BeginsRun)
        {
            var kept = WorldInputs(inputs);
            inputs.Clear();
            inputs.AddRange(kept);
            inputs.Add(input);
            Refold();
            return;
        }
        inputs.Add(input);
        folder.Apply(input);
        TakeSnapshot();
    }

    /// <summary>Removes the last input ("Back"): the fold starts at the last snapshot before it and folds the rest.
    /// Returns false when the run has no input.</summary>
    public bool Back()
    {
        if (inputs.Count == 0)
            return false;
        inputs.RemoveAt(inputs.Count - 1);
        snapshots.RemoveAll(s => s.Inputs > inputs.Count);
        if (snapshots.Count == 0)
        {
            Refold();
            return true;
        }
        var snapshot = snapshots[^1];
        folder.Restore(snapshot);
        for (var i = snapshot.Inputs; i < inputs.Count; i++)
            folder.Apply(inputs[i]);
        return true;
    }

    /// <summary>Removes all inputs ("Reset").</summary>
    public void Reset()
    {
        inputs.Clear();
        Refold();
    }

    /// <summary>Uses another bound of the timer ranges, and folds the inputs again.</summary>
    public void SetTimers(TimerBound timers)
    {
        if (timers == Timers)
            return;
        Timers = timers;
        folder = new Folder(folder.Index, Options(timers), strict: false);
        Refold();
    }

    /// <summary>
    /// Folds the inputs again over a new model of the script (section 4.2 of the M6c design). The renames of the change
    /// move the inputs to the new names first. An undo gives the pairs in the other direction, so it moves them back.
    /// </summary>
    /// <param name="renames">The old and the new nickname of each renamed trigger, in order.</param>
    public void Rebuild(MissionIndex index, IReadOnlyList<(string From, string To)> renames)
    {
        for (var i = 0; i < inputs.Count; i++)
        {
            foreach (var (from, to) in renames)
                inputs[i] = inputs[i].Renamed(from, to);
        }
        folder = new Folder(index, Options(Timers), strict: false);
        Refold();
    }

    /// <summary>
    /// Folds a list of inputs over a new machine (section 4.2 of the M6c design). The same inputs give the same trace.
    /// An input that starts a new run makes a new machine, and the world stays.
    /// </summary>
    /// <param name="strict">True for a request of the command layer. An input that does not resolve then throws an
    /// <see cref="EditException"/>. Without it, the step shows "Missing: {name}" and does nothing, so the dry run
    /// follows the edits of the modder.</param>
    /// <exception cref="EditException">With <paramref name="strict"/>: <see cref="EditErrorKind.NotFound"/> for a trigger
    /// or a system that does not resolve, <see cref="EditErrorKind.InvalidArgument"/> for a line that matches two
    /// waiting lines or no condition of the script, and <see cref="EditErrorKind.NotAvailable"/> for Start at
    /// trigger.</exception>
    public static DryRunState Fold(MissionIndex index, IReadOnlyList<DryRunInput> inputs,
        TimerBound timers = TimerBound.Shortest, bool strict = false)
    {
        var folder = new Folder(index, Options(timers), strict);
        foreach (var input in inputs)
            folder.Apply(input);
        return folder.State;
    }

    private static MachineOptions Options(TimerBound timers) => new() { Timers = timers };

    private void Refold()
    {
        snapshots.Clear();
        folder.Clear();
        for (var i = 0; i < inputs.Count; i++)
        {
            folder.Apply(inputs[i]);
            if ((i + 1) % SnapshotEvery == 0)
                snapshots.Add(folder.Snapshot(i + 1));
        }
    }

    private void TakeSnapshot()
    {
        if (inputs.Count % SnapshotEvery == 0)
            snapshots.Add(folder.Snapshot(inputs.Count));
    }

    /// <summary>The net form of the world inputs: the last system and the last place of the player, and each fact that
    /// is true at the end, in the order of its first input.</summary>
    internal static IReadOnlyList<DryRunInput> WorldInputs(IEnumerable<DryRunInput> list)
    {
        DryRunInput? system = null;
        DryRunInput? space = null;
        var facts = new List<DryRunInput>();
        foreach (var input in list)
        {
            switch (input.Kind)
            {
                case DryRunInputKind.PlayerSystem:
                    system = input;
                    break;
                case DryRunInputKind.PlayerInSpace:
                    space = input;
                    break;
                case DryRunInputKind.Fact:
                    var at = facts.FindIndex(f => Equals(f.Line, input.Line));
                    if (at >= 0)
                        facts[at] = input;
                    else
                        facts.Add(input);
                    break;
            }
        }
        return [.. new[] { system, space }.OfType<DryRunInput>(), .. facts.Where(f => f.Value)];
    }

    /// <summary>The state of the fold after a number of inputs.</summary>
    private sealed record Snapshot(int Inputs, MachineState Machine, DryRunWorld World, int Steps);

    /// <summary>The fold of inputs, one step at a time.</summary>
    private sealed class Folder
    {
        private readonly MachineOptions options;
        private readonly bool strict;

        public Folder(MissionIndex index, MachineOptions options, bool strict)
        {
            Index = index;
            this.options = options;
            this.strict = strict;
            var world = new DryRunWorld(index);
            State = new DryRunState(new MissionTrace(), new TriggerMachine(index, world, options), world);
        }

        public MissionIndex Index { get; }

        public DryRunState State { get; }

        private TriggerMachine Machine => State.Machine;

        private DryRunWorld World => State.World;

        public void Clear()
        {
            var world = new DryRunWorld(Index);
            State.World = world;
            State.Machine = new TriggerMachine(Index, world, options);
            State.Trace.Reset();
        }

        public Snapshot Snapshot(int inputs) =>
            new(inputs, Machine.Snapshot(), World.Copy(), State.Trace.Steps.Count);

        public void Restore(Snapshot snapshot)
        {
            var world = snapshot.World.Copy();
            State.World = world;
            State.Machine = TriggerMachine.From(snapshot.Machine, Index, world);
            State.Trace.Truncate(snapshot.Steps);
        }

        /// <summary>Folds one input: one step of the trace.</summary>
        public void Apply(DryRunInput input)
        {
            var number = State.Trace.Steps.Count + 1;
            World.Step = number;
            var changes = new StepChanges(input.MostHeartbeats > 1);
            var cause = input.Kind switch
            {
                DryRunInputKind.StartMission => StartMission(changes),
                DryRunInputKind.LoadCheckpoint => LoadCheckpoint(input.Trigger!, changes),
                DryRunInputKind.StartAt => StartAt(input.Trigger!, changes),
                DryRunInputKind.RunTrigger => RunTrigger(input.Trigger!, changes),
                DryRunInputKind.Event => Event(input.Line!, changes),
                DryRunInputKind.Fact => Fact(input.Line!, input.Value, changes),
                DryRunInputKind.Heartbeats => Heartbeats(input, changes),
                DryRunInputKind.PlayerSystem => PlayerSystem(input.System),
                _ => PlayerInSpace(input.Value),
            };
            State.Trace.Append(new TraceStep(number, cause, changes.Changes(Machine))
            {
                Pending = Machine.Pending.Select(t => t.Nickname).ToList(),
                Heartbeats = Machine.Heartbeats,
            });
        }

        private string StartMission(StepChanges changes)
        {
            NewRun();
            changes.Add(Machine, Machine.StartMission());
            return "Start mission";
        }

        private string LoadCheckpoint(string name, StepChanges changes)
        {
            var cause = $"Load checkpoint {name}";
            if (Find(name, changes) is not { } trigger)
                return cause;
            if (strict && !Checkpoints(Index).Contains(trigger))
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, "NotACheckpoint",
                    $"Trigger '{trigger.Nickname}' is not a checkpoint. No Act_Save line names it. Use a trigger that an " +
                    "Act_Save line names.") { Field = "trigger" });
            NewRun();
            changes.Add(Machine, Machine.Load([trigger]));
            return $"Load checkpoint {trigger.Nickname}";
        }

        private string StartAt(string name, StepChanges changes)
        {
            const string message = "Start at trigger is not available in this version. Use Start mission or Load checkpoint.";
            if (strict)
                throw new EditException(new EditError(EditErrorKind.NotAvailable, "StartAtNotAvailable", message)
                {
                    Field = "kind",
                });
            changes.Note(message);
            return $"Start at {name}";
        }

        private string RunTrigger(string name, StepChanges changes)
        {
            if (Find(name, changes) is not { } trigger)
                return $"Run trigger {name}";
            if (Machine.StateOf(trigger).Status != TriggerStatus.Active || !Machine.Active.Contains(trigger))
            {
                changes.Note($"Not active: {trigger.Nickname}", trigger.Nickname);
                return $"Run trigger {trigger.Nickname}";
            }
            changes.Add(Machine, Machine.Force(trigger));
            return $"Run trigger {trigger.Nickname}";
        }

        private string Event(LineSpec pattern, StepChanges changes)
        {
            var lines = WorldEvents.Match(pattern, WorldEvents.EventLines(Machine));
            if (lines.Count == 0)
            {
                changes.Note("No trigger waits for this event.");
                return PatternCause(pattern);
            }
            if (lines.Count > 1 && strict)
                throw Ambiguous(pattern, lines);
            changes.Add(Machine, Machine.Dispatch(WorldEvents.Of(lines[0])));
            return CauseOf(lines[0]);
        }

        private string Fact(LineSpec pattern, bool on, StepChanges changes)
        {
            var lines = WorldEvents.Match(pattern, WorldEvents.FactLines(Index));
            if (lines.Count == 0)
            {
                if (strict)
                    throw new EditException(new EditError(EditErrorKind.InvalidArgument, "NoSuchFact",
                        $"No trigger of the script tests the fact {pattern}. Use a condition that the game tests at " +
                        "each heartbeat, for example Cnd_InSpace.") { Field = "line" });
                changes.Note("No trigger waits for this event.");
                return (on ? "Is true: " : "Is not true: ") + PatternCause(pattern);
            }
            if (lines.Count > 1 && strict)
                throw Ambiguous(pattern, lines);
            World.SetFact(WorldEvents.KeyOf(lines[0]), on);
            return (on ? "Is true: " : "Is not true: ") + Lower(CauseOf(lines[0]));
        }

        private string Heartbeats(DryRunInput input, StepChanges changes)
        {
            var target = input.MostHeartbeats;
            var done = 0;
            while (done < target && !Machine.IsOver)
            {
                var skipped = Machine.SkipIdle(target - done);
                if (skipped > 0)
                    break;
                var pass = Machine.Heartbeat();
                done++;
                changes.Add(Machine, pass);
                if (input.UntilTimer && RanTimer(pass))
                    break;
            }
            return input.UntilTimer ? "Next timer"
                : input.Count == 1 ? "Heartbeat"
                : $"{input.Count.ToString(CultureInfo.InvariantCulture)} heartbeats";
        }

        /// <summary>True when a timer ran in a pass.</summary>
        private static bool RanTimer(MachinePass pass)
        {
            foreach (var change in pass.Changes)
            {
                if (change.Kind == MachineChangeKind.Ran &&
                    MachineProgram.KindOf(change.Trigger.Condition) == ConditionKind.Timer)
                    return true;
            }
            return false;
        }

        private string PlayerSystem(string? system)
        {
            if (strict && system != null && Index.World.Knows(MissionValueKind.System) &&
                !Index.World.Has(MissionValueKind.System, system))
                throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                    $"The game data has no system '{system}'. Pick a system of the game data.") { Field = "system" });
            World.PlayerSystem = system;
            return system == null ? "Player system: none" : $"Player system: {system}";
        }

        private string PlayerInSpace(bool value)
        {
            World.InSpace = value;
            return value ? "Player is in space" : "Player is not in space";
        }

        /// <summary>A new run: a new machine, and the world without the records of the run before.</summary>
        private void NewRun()
        {
            var world = World.Next(Index);
            State.World = world;
            State.Machine = new TriggerMachine(Index, world, options);
        }

        /// <summary>The trigger of a name, or null with the note "Missing: {name}".</summary>
        private MissionTrigger? Find(string name, StepChanges changes)
        {
            if (Index.Trigger(name) is { IsDuplicate: false } trigger)
                return trigger;
            if (strict)
                throw new EditException(MissionErrors.TriggerNotFoundError(Index.Document.Path, name) with { Field = "trigger" });
            changes.Note($"Missing: {name}");
            return null;
        }

        private static EditException Ambiguous(LineSpec pattern, IReadOnlyList<MissionLine> lines) =>
            new(new EditError(EditErrorKind.InvalidArgument, "AmbiguousLine",
                $"The line {pattern} matches {lines.Count.ToString(CultureInfo.InvariantCulture)} waiting lines: " +
                string.Join(" and ", lines.Select(l => $"'{MissionEditor.LineText(l.Line)}'")) +
                ". Give more values, so that one line matches.")
            {
                Field = "line",
            });
    }

    /// <summary>The cause of a step of a line: its sentence without "Starts when", for example "Comm line X
    /// ends".</summary>
    internal static string CauseOf(MissionLine line)
    {
        var sentence = line.Sentence().TrimEnd('.');
        const string prefix = "Starts when ";
        if (sentence.StartsWith(prefix, StringComparison.Ordinal))
            sentence = sentence[prefix.Length..];
        return sentence.Length == 0 ? line.Key : char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    private static string PatternCause(LineSpec pattern) => $"Event {pattern.Key}";

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>The changes of one step. A step of many heartbeats folds the same change into one row with a count
    /// (section 4.4 of the M6c design).</summary>
    private sealed class StepChanges(bool fold)
    {
        // A row is a change of the machine, or a note. The step makes the rows of the log at its end, so a change that
        // repeats in many heartbeats makes its text once.
        private readonly List<(MachineChange Change, TraceChange? Note)> rows = [];
        private readonly List<int> counts = [];
        private readonly Dictionary<MachineChange, int> places = [];

        public void Note(string text, string trigger = "")
        {
            rows.Add((default, new TraceChange(TraceChangeKind.Note, trigger) { Text = text }));
            counts.Add(1);
        }

        public void Add(TriggerMachine machine, MachinePass pass)
        {
            if (pass.Changes.Count == 0)
                return;
            foreach (var change in pass.Changes)
            {
                if (fold && places.TryGetValue(change, out var at))
                {
                    counts[at]++;
                    continue;
                }
                if (fold)
                    places[change] = rows.Count;
                rows.Add((change, null));
                counts.Add(1);
            }
        }

        /// <summary>The rows of the step, with the end and the stop of the machine after the step.</summary>
        public IReadOnlyList<TraceChange> Changes(TriggerMachine machine)
        {
            var result = new List<TraceChange>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var change = rows[i].Note ?? Of(machine, rows[i].Change);
                result.Add(counts[i] > 1 ? change with { Count = counts[i] } : change);
            }
            return result;
        }

        private static TraceChange Of(TriggerMachine machine, MachineChange change)
        {
            var trigger = change.Trigger.Nickname;
            var row = change.Line?.Entry ?? -1;
            return change.Kind switch
            {
                MachineChangeKind.Started => new TraceChange(TraceChangeKind.Started, trigger),
                MachineChangeKind.Ran => new TraceChange(TraceChangeKind.Ran, trigger),
                MachineChangeKind.Rearmed => new TraceChange(TraceChangeKind.Rearmed, trigger),
                MachineChangeKind.Stopped => new TraceChange(TraceChangeKind.Stopped, trigger),
                MachineChangeKind.Cancelled => new TraceChange(TraceChangeKind.Cancelled, trigger),
                MachineChangeKind.Ignored => new TraceChange(TraceChangeKind.Ignored, trigger) { Name = change.Name, Row = row },
                MachineChangeKind.Checkpoint => new TraceChange(TraceChangeKind.Checkpoint, trigger)
                {
                    Name = change.Target?.Nickname ?? change.Name, Row = row,
                },
                MachineChangeKind.Effect => new TraceChange(TraceChangeKind.Effect, trigger)
                {
                    Text = MissionGraph.LineText(change.Line!), Row = row,
                },
                MachineChangeKind.End => new TraceChange(TraceChangeKind.End, trigger) { Text = EndText(machine.End!) },
                _ => new TraceChange(TraceChangeKind.Stop, trigger) { Text = StopText(machine.Halt!) },
            };
        }
    }

    /// <summary>The end row of the Run log (section 8.2 of the M6c design).</summary>
    public static string EndText(MissionEnd end) => end.Kind == MissionEndKind.Success
        ? $"The mission ends with success at trigger '{end.Trigger.Nickname}'."
        : end.TextId is { } id
            ? $"The mission ends with fail at trigger '{end.Trigger.Nickname}'. The player sees text {id}."
            : $"The mission ends with fail at trigger '{end.Trigger.Nickname}'.";

    /// <summary>The stop row of the Run log (section 8.2 of the M6c design).</summary>
    public static string StopText(MachineHalt halt) => halt.Kind switch
    {
        MachineHaltKind.NoCondition => $"The game stops here. Trigger '{halt.Trigger.Nickname}' has no condition.",
        _ => halt.Message,
    };
}

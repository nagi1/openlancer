using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

/// <summary>The kind of a change of a step of a trace (section 4.5 of the M6c design).</summary>
public enum TraceChangeKind
{
    Started,
    Ran,
    Rearmed,
    Stopped,
    Cancelled,
    Ignored,
    Checkpoint,
    Effect,
    End,

    /// <summary>The run stops before the mission ends.</summary>
    Stop,

    /// <summary>A row of the Run log without a change, for example "Missing: {name}".</summary>
    Note,
}

/// <summary>One change of a step: one row of the Run log under its step.</summary>
/// <param name="Trigger">The nickname of the trigger that changed, or of the trigger of the line of an Ignored, a
/// Checkpoint, an Effect or an End change. Empty for a note.</param>
public sealed record TraceChange(TraceChangeKind Kind, string Trigger)
{
    /// <summary>The trigger name of an Ignored or a Checkpoint change, else null.</summary>
    public string? Name { get; init; }

    /// <summary>The text of an Effect ("{plain action name}: {values}"), an End, a Stop or a Note change, else
    /// null.</summary>
    public string? Text { get; init; }

    /// <summary>The index of the action line in its section, or -1.</summary>
    public int Row { get; init; } = -1;

    /// <summary>How many times the change happened in the step. A step of many heartbeats folds the same change into one
    /// row (section 4.4 of the M6c design).</summary>
    public int Count { get; init; } = 1;

    /// <summary>The text of the row of the Run log (section 8.2 of the M6c design), or null for a change without a
    /// row.</summary>
    public string? LogText => Kind switch
    {
        TraceChangeKind.Started => Times($"Started {Trigger}"),
        TraceChangeKind.Ran => Times($"Ran {Trigger}"),
        TraceChangeKind.Stopped => Times($"Stopped {Trigger}"),
        // The log shows no row for a re-arm or a cancelled start. The state of the trigger shows them.
        TraceChangeKind.Rearmed or TraceChangeKind.Cancelled => null,
        TraceChangeKind.Ignored => Times($"Ignored: no trigger '{Name}'"),
        TraceChangeKind.Checkpoint => Times($"Checkpoint: {Name}"),
        _ => Times(Text ?? ""),
    };

    /// <summary>The note of a live view whose game dropped changes because its queue was full (section 6.2 of the M6c
    /// design).</summary>
    public static TraceChange Dropped(int count) => new(TraceChangeKind.Note, "")
    {
        Text = $"The game dropped {count.ToString(CultureInfo.InvariantCulture)} changes.",
    };

    private string Times(string text) =>
        Count > 1 ? $"{text}, {Count.ToString(CultureInfo.InvariantCulture)} times" : text;
}

/// <summary>One step of a trace: its cause and the ordered changes of its passes.</summary>
/// <param name="Number">The number of the step, from 1.</param>
/// <param name="Cause">The cause, for example "Start mission" or "Comm line X ends".</param>
public sealed record TraceStep(int Number, string Cause, IReadOnlyList<TraceChange> Changes)
{
    /// <summary>The pending starts at the end of the step, in the order of the next drain, or null when the trace does
    /// not know them (the live view).</summary>
    public IReadOnlyList<string>? Pending { get; init; }

    /// <summary>The heartbeats since the start of the run at the end of the step, for the dry run.</summary>
    public int Heartbeats { get; init; }

    /// <summary>The time of the step in seconds since the start of the play, for the live view, or null.</summary>
    public double? Time { get; init; }

    /// <summary>The title of the step in the Run log: "Step {n}: {cause}" in the dry run, "{m}:{ss}.{t}" in the live
    /// view.</summary>
    public string Title => Time is { } time
        ? Clock(time)
        : $"Step {Number.ToString(CultureInfo.InvariantCulture)}: {Cause}";

    /// <summary>A time as "{m}:{ss}.{t}".</summary>
    public static string Clock(double seconds)
    {
        var tenths = (long)Math.Floor(Math.Max(0, seconds) * 10);
        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 600}:{tenths / 10 % 60:00}.{tenths % 10}");
    }
}

/// <summary>The run state of one trigger in a trace (section 4.5 of the M6c design).</summary>
public sealed record TriggerTrace(string Trigger)
{
    /// <summary>True while the trigger is active: the state "Waits".</summary>
    public bool Waits { get; init; }

    /// <summary>The runs in the last step of the dry run, or in the last second of the live view: the state "Ran" or
    /// "Ran {n} times".</summary>
    public int RanLast { get; init; }

    /// <summary>All runs of the trigger in the trace.</summary>
    public int Runs { get; init; }

    /// <summary>True when a stop removed the trigger from the active list and it did not start again.</summary>
    public bool Stopped { get; init; }

    /// <summary>The place of the trigger in the pending order, from 1, or 0: the state "Starts next ({k})".</summary>
    public int StartsNext { get; init; }

    /// <summary>The step of the last start, or 0.</summary>
    public int ActiveSinceStep { get; init; }

    /// <summary>The time of the last start in the live view, or null.</summary>
    public double? ActiveSinceTime { get; init; }

    /// <summary>The last time of a run in the live view, or null.</summary>
    public double? LastRanTime { get; init; }

    /// <summary>The last start, run, stop or cancel of the trigger, or null.</summary>
    public TraceChange? LastChange { get; init; }

    /// <summary>The step of <see cref="LastChange"/>, or 0.</summary>
    public int LastStep { get; init; }

    /// <summary>True when the trigger ran before and is not active now: the state "Has run".</summary>
    public bool HasRun => Runs > 0 && !Waits;
}

/// <summary>
/// The steps of a run (section 4.5 of the M6c design): one trace for the dry run and the live view. A step has a cause
/// and the ordered changes of its passes. The trace gives each trigger its run state: Waits, Ran, Has run, Stopped and
/// Starts next. The dry run makes the steps from the passes of the machine. The live view of M6c-2 makes them from the
/// events of the game. Triggers compare by nickname without letter case.
/// </summary>
public sealed class MissionTrace
{
    /// <summary>The time that a trigger waits before the stall chip shows, in seconds (section 6.3 of the M6c
    /// design).</summary>
    public const double StallSeconds = 600;

    /// <summary>The time that "Ran" shows in the live view, in seconds.</summary>
    public const double RanSeconds = 1;

    private readonly List<TraceStep> steps = [];
    private readonly Dictionary<string, TriggerTrace> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ranLast = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="live">True for the trace of the live view: the steps have a time, and "Ran" lasts one second.</param>
    public MissionTrace(bool live = false) => Live = live;

    /// <summary>True for the trace of the live view.</summary>
    public bool Live { get; }

    /// <summary>The steps in order.</summary>
    public IReadOnlyList<TraceStep> Steps => steps;

    /// <summary>The state of each trigger that has a change in the trace.</summary>
    public IReadOnlyDictionary<string, TriggerTrace> States => states;

    /// <summary>The state of a trigger, or null when the trace has no change of it.</summary>
    public TriggerTrace? StateOf(string trigger) => states.GetValueOrDefault(trigger);

    /// <summary>The trigger of the last change of the last step, or null. The toggle "Follow" frames it.</summary>
    public string? LastTrigger => steps.Count > 0 && steps[^1].Changes.LastOrDefault(c => c.Trigger.Length > 0) is { } change
        ? change.Trigger
        : null;

    /// <summary>Adds a step.</summary>
    public void Append(TraceStep step)
    {
        steps.Add(step);
        Apply(step);
    }

    /// <summary>Keeps only the first steps, for "Back".</summary>
    public void Truncate(int count)
    {
        if (count >= steps.Count)
            return;
        steps.RemoveRange(count, steps.Count - count);
        states.Clear();
        ranLast.Clear();
        foreach (var step in steps)
            Apply(step);
    }

    /// <summary>Removes all steps, for example when the game loads the mission again.</summary>
    public void Reset() => Truncate(0);

    /// <summary>The runs of a trigger that show as "Ran" now: in the last step of the dry run, or in the last second of
    /// the live view.</summary>
    /// <param name="now">The time now in the live view, in seconds since the start of the play.</param>
    public int RanNow(TriggerTrace state, double now = 0) =>
        !Live ? state.RanLast
        : state.LastRanTime is { } at && now - at < RanSeconds ? Math.Max(1, state.RanLast)
        : 0;

    /// <summary>The whole minutes that an active trigger waits in the live view, when it waits for 10 minutes or more:
    /// the chip "Waits {m} min". Null for another trigger.</summary>
    public int? StallMinutes(TriggerTrace state, double now) =>
        Live && state.Waits && state.ActiveSinceTime is { } since && now - since >= StallSeconds
            ? (int)((now - since) / 60)
            : null;

    private void Apply(TraceStep step)
    {
        foreach (var name in ranLast)
        {
            if (states.TryGetValue(name, out var state))
                states[name] = state with { RanLast = 0 };
        }
        ranLast.Clear();
        foreach (var change in step.Changes)
        {
            if (change.Kind is not (TraceChangeKind.Started or TraceChangeKind.Ran or TraceChangeKind.Rearmed or
                TraceChangeKind.Stopped or TraceChangeKind.Cancelled))
                continue;
            var state = states.GetValueOrDefault(change.Trigger) ?? new TriggerTrace(change.Trigger);
            state = change.Kind switch
            {
                TraceChangeKind.Started => state with
                {
                    Waits = true, Stopped = false, ActiveSinceStep = step.Number, ActiveSinceTime = step.Time,
                },
                TraceChangeKind.Ran => state with
                {
                    Waits = false, Stopped = false, Runs = state.Runs + change.Count, RanLast = state.RanLast + change.Count,
                    LastRanTime = step.Time ?? state.LastRanTime,
                },
                TraceChangeKind.Rearmed => state with { Waits = true },
                TraceChangeKind.Stopped => state with { Waits = false, Stopped = true },
                _ => state,
            };
            if (change.Kind == TraceChangeKind.Ran)
                ranLast.Add(change.Trigger);
            states[change.Trigger] = state with { LastChange = change, LastStep = step.Number };
        }
        if (step.Pending is not { } pending)
            return;
        foreach (var (name, state) in states.Where(p => p.Value.StartsNext > 0).ToList())
            states[name] = state with { StartsNext = 0 };
        for (var i = 0; i < pending.Count; i++)
        {
            var state = states.GetValueOrDefault(pending[i]) ?? new TriggerTrace(pending[i]);
            states[pending[i]] = state with { StartsNext = i + 1 };
        }
    }
}

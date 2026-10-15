using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>What a drag on the Timeline changes.</summary>
internal enum TimelineDragKind
{
    /// <summary>The time of the events: a drag of a bar or a diamond.</summary>
    Move,

    /// <summary>The start of one bar: its time changes, and its end stays.</summary>
    Start,

    /// <summary>The end of one bar: its length changes.</summary>
    End,

    /// <summary>The <c>duration</c> of the script: a drag of the end handle of the ruler.</summary>
    Duration,
}

/// <summary>
/// One drag on the Timeline as one entry of the edit history (section 7.3 of the M8 design, the pattern of the M2
/// <c>GizmoDrag</c>): a move of events, a change of the start or the end of a bar, or a change of the duration. Each
/// frame gives the time under the mouse. The drag applies its command in one transaction, and the release of the mouse
/// commits it. It has no ImGui call, so a test can script it.
/// </summary>
/// <remarks>
/// <para>Each change of the drag is a whole number of milliseconds, the grid of all retail times. So a start drag keeps
/// the end of the bar on its time, and the rounding of <see cref="ThnEdits.MoveEvents"/> changes nothing.</para>
/// <para>With Ctrl, a moved edge snaps to the playhead or to an edge of another event when it is within the snap
/// distance. Of the edges that can snap, the nearest one wins.</para>
/// <para>A drag back to the start puts back the edit view as it was, so it adds no entry.</para>
/// </remarks>
internal sealed class TimelineDrag : IDisposable
{
    /// <summary>The steps of the time grid in one second.</summary>
    private const double StepsPerSecond = 1000;

    private readonly EditTransaction transaction;
    private readonly IReadOnlyList<TimelineEvent> events;
    private readonly IReadOnlyList<ThnRef> refs;
    private readonly string path;
    private readonly float duration;
    private readonly double grab;
    private readonly IReadOnlyList<double> snaps;
    private bool ended;

    private TimelineDrag(EditTransaction transaction, IReadOnlyList<TimelineEvent> events, string path, float duration,
        TimelineDragKind kind, double grab, IReadOnlyList<double> snaps)
    {
        this.transaction = transaction;
        this.events = events;
        refs = events.Select(e => e.Ref).ToList();
        this.path = path;
        this.duration = duration;
        Kind = kind;
        this.grab = grab;
        this.snaps = snaps;
    }

    public TimelineDragKind Kind { get; }

    /// <summary>The change of the time of the events, or of the duration, that the edit view holds now, in
    /// seconds.</summary>
    public double TimeDelta { get; private set; }

    /// <summary>The change of the length that the edit view holds now, in seconds.</summary>
    public double LengthDelta { get; private set; }

    /// <summary>The time that the last frame snapped to, or null.</summary>
    public double? SnappedTo { get; private set; }

    /// <summary>
    /// Starts a drag of events. A start drag or an end drag changes only the first event, which must be a bar.
    /// </summary>
    /// <param name="events">The events to drag, as the Timeline shows them at the start.</param>
    /// <param name="grab">The time under the mouse at the start of the drag.</param>
    /// <param name="snapPoints">The times that a moved edge can snap to: the playhead and the edges of the other
    /// events (see <see cref="SnapPoints"/>).</param>
    /// <exception cref="InvalidOperationException">The session takes no edits, a transaction is open, or the cutscene
    /// is read-only. Nothing changes.</exception>
    /// <exception cref="ArgumentException">There is no event, the kind is <see cref="TimelineDragKind.Duration"/>, or a
    /// start or an end drag has no bar.</exception>
    public static TimelineDrag Begin(EditSession session, IReadOnlyList<TimelineEvent> events, TimelineDragKind kind,
        double grab, IReadOnlyList<double> snapPoints)
    {
        if (events.Count == 0)
            throw new ArgumentException("There is no event to drag.", nameof(events));
        if (kind == TimelineDragKind.Duration)
            throw new ArgumentException("A drag of the duration has no events. Use BeginDuration.", nameof(kind));
        if (kind != TimelineDragKind.Move && !events[0].IsBar)
            throw new ArgumentException("Only a bar has a start and an end to drag.", nameof(kind));
        var path = events[0].Ref.Path;
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            throw new InvalidOperationException(reason);
        var dragged = kind == TimelineDragKind.Move ? events : [events[0]];
        var label = kind switch
        {
            TimelineDragKind.Start => "Change the start of event",
            TimelineDragKind.End => "Change the length of event",
            _ => dragged.Count == 1 ? "Move event" : $"Move {dragged.Count} events",
        };
        return new TimelineDrag(session.Begin(label), dragged, path, 0, kind, grab, snapPoints);
    }

    /// <summary>Starts a drag of the end handle: a change of the duration of a script.</summary>
    /// <exception cref="InvalidOperationException">The session takes no edits, a transaction is open, or the cutscene
    /// is read-only. Nothing changes.</exception>
    public static TimelineDrag BeginDuration(EditSession session, string path, float duration, double grab,
        IReadOnlyList<double> snapPoints)
    {
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            throw new InvalidOperationException(reason);
        return new TimelineDrag(session.Begin("Set duration"), [], path, duration, TimelineDragKind.Duration, grab,
            snapPoints);
    }

    /// <summary>
    /// What a press on an event drags: the start or the end of a bar when the time is within the edge distance of it,
    /// else the event. A short bar gives its end only the outer half of the distance, so its middle still moves it.
    /// </summary>
    /// <param name="edge">The edge distance, in seconds.</param>
    public static TimelineDragKind KindAt(TimelineEvent ev, double time, double edge)
    {
        if (!ev.IsBar)
            return TimelineDragKind.Move;
        var inner = Math.Min(edge, ev.Length / 3);
        if (time >= ev.End - inner && time <= ev.End + edge)
            return TimelineDragKind.End;
        return time >= ev.Time - edge && time <= ev.Time + inner ? TimelineDragKind.Start : TimelineDragKind.Move;
    }

    /// <summary>The snap points of a drag: the playhead, and the start and the end of each event that the drag does not
    /// move.</summary>
    public static IReadOnlyList<double> SnapPoints(TimelineModel timeline, IEnumerable<ThnRef> dragged, double playhead)
    {
        var moved = dragged.Select(e => e.Id).ToHashSet();
        return timeline.Events.Where(e => !moved.Contains(e.Ref.Id))
            .SelectMany(e => e.IsBar ? new double[] { e.Time, e.End } : [e.Time])
            .Append(playhead).Distinct().Order().ToList();
    }

    /// <summary>Takes the time under the mouse in this frame, and applies the change when it differs from the change
    /// that the edit view holds.</summary>
    /// <param name="snap">True while Ctrl is down.</param>
    /// <param name="tolerance">The snap distance, in seconds.</param>
    /// <exception cref="InvalidOperationException">The drag has ended, or the change does not apply. Then the edit
    /// view holds the state before the drag.</exception>
    public void Drag(double mouse, bool snap, double tolerance)
    {
        if (ended)
            throw new InvalidOperationException("The drag has ended.");
        var delta = mouse - grab;
        SnappedTo = null;
        if (snap && Snap(delta, tolerance) is { } snapped)
            (delta, SnappedTo) = snapped;
        // The bounds of the clamp are times of the file, so the result rounds to the grid again.
        delta = Round(Clamp(Round(delta)));
        var (time, length) = Kind switch
        {
            TimelineDragKind.Start => (delta, -delta),
            TimelineDragKind.End => (0.0, delta),
            _ => (delta, 0.0),
        };
        if (time == TimeDelta && length == LengthDelta)
            return;
        try
        {
            transaction.Apply(time == 0 && length == 0 ? EditCommand.Group("", []) : Command(time, length));
        }
        catch (InvalidOperationException)
        {
            (TimeDelta, LengthDelta) = (0, 0);
            throw;
        }

        (TimeDelta, LengthDelta) = (time, length);
    }

    /// <summary>Ends the drag with the change that the edit view holds: one entry, or none for no change.</summary>
    public void End()
    {
        if (ended)
            throw new InvalidOperationException("The drag has ended.");
        ended = true;
        transaction.Commit();
    }

    /// <summary>Ends the drag and puts back the edit view as it was before it. Does nothing after the end.</summary>
    public void Cancel()
    {
        if (ended)
            return;
        ended = true;
        transaction.Cancel();
    }

    public void Dispose() => Cancel();

    /// <summary>The command of a change: a move of the events, or a new duration.</summary>
    private EditCommand Command(double time, double length) => Kind == TimelineDragKind.Duration
        ? ThnEdits.SetDuration(path, ThnEdits.OnGrid(duration + time))
        : ThnEdits.MoveEvents(refs, (float)time, (float)length);

    /// <summary>A change rounded to the 1 ms grid.</summary>
    private static double Round(double seconds) =>
        Math.Round(seconds * StepsPerSecond, MidpointRounding.AwayFromZero) / StepsPerSecond;

    /// <summary>
    /// The change that puts the nearest moved edge on a snap point, and that point, or null when no edge is within the
    /// snap distance.
    /// </summary>
    private (double Delta, double Point)? Snap(double delta, double tolerance)
    {
        (double Delta, double Point)? best = null;
        foreach (var edge in MovedEdges())
        {
            foreach (var point in snaps)
            {
                var distance = Math.Abs(edge + delta - point);
                if (distance <= tolerance && (best == null || distance < Math.Abs(best.Value.Delta - delta)))
                    best = (point - edge, point);
            }
        }
        return best;
    }

    /// <summary>The edges that the drag moves, at the start of the drag.</summary>
    private IEnumerable<double> MovedEdges() => Kind switch
    {
        TimelineDragKind.Start => [events[0].Time],
        TimelineDragKind.End => [events[0].End],
        TimelineDragKind.Duration => [duration],
        _ => events.SelectMany(e => e.IsBar ? new double[] { e.Time, e.End } : [e.Time]),
    };

    /// <summary>
    /// A change that keeps each event at 0 or later and each length at 0 or more: a move stops when the first event is
    /// at 0, a start drag stops at 0 and at the end of the bar, an end drag stops at the start of the bar, and the
    /// duration stops at 0.
    /// </summary>
    private double Clamp(double delta) => Kind switch
    {
        TimelineDragKind.Start => Math.Clamp(delta, -events[0].Time, events[0].Length),
        TimelineDragKind.End => Math.Max(delta, -events[0].Length),
        TimelineDragKind.Duration => Math.Max(delta, -duration),
        _ => Math.Max(delta, -events.Min(e => e.Time)),
    };
}

using System;
using System.Collections.Generic;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// One drag of a point of the curve of an event in the curve editor, as one entry of the edit history (section 7.5 of
/// the M8 design). Each frame gives the time and the value of the point. The drag sets <c>param_curve</c> in one
/// transaction, and the release of the mouse commits it. It has no ImGui call, so a test can script it.
/// </summary>
/// <remarks>The time of a point stays between the times of the points before and after it, and from 0 to 1, because
/// the engine finds the segment of a time in the order of the points (ParameterCurve.cs:120-129).</remarks>
internal sealed class CurvePointDrag : IDisposable
{
    private readonly EditTransaction transaction;
    private readonly ThnRef ev;
    private readonly ThnTable curve;
    private readonly IReadOnlyList<ThnCurvePoint> points;
    private readonly int index;
    private ThnCurvePoint applied;
    private bool ended;

    private CurvePointDrag(EditTransaction transaction, ThnRef ev, ThnTable curve, IReadOnlyList<ThnCurvePoint> points,
        int index)
    {
        this.transaction = transaction;
        this.ev = ev;
        this.curve = curve;
        this.points = points;
        this.index = index;
        applied = points[index];
    }

    /// <summary>The point that the edit view holds now.</summary>
    public ThnCurvePoint Applied => applied;

    /// <summary>Starts a drag of a point of the curve of an event, from the curve that W holds now.</summary>
    /// <exception cref="InvalidOperationException">The session takes no edits, a transaction is open, the cutscene is
    /// read-only, or the event has no such point. Nothing changes.</exception>
    public static CurvePointDrag Begin(EditSession session, ThnRef ev, int index)
    {
        if (session.CutsceneReadOnlyReason(ev) is { } reason)
            throw new InvalidOperationException(reason);
        var table = session.Cutscene(ev.Path)?.Element(ThnElementKind.Event, ev.Id)?.Table ??
                    throw new InvalidOperationException("The cutscene has no such event.");
        if (ThnCurve.Path.ValueIn(ThnElementKind.Event, table) is not ThnTable { IsRecord: true } curve ||
            ThnCurve.Points(curve) is not { } points || index < 0 || index >= points.Count)
            throw new InvalidOperationException($"The curve of the event has no point {index + 1}.");
        return new CurvePointDrag(session.Begin($"Move point {index + 1} of the curve"), ev, curve, points, index);
    }

    /// <summary>Takes the time and the value of the point in this frame, and applies them when they changed.</summary>
    /// <param name="time">The time as a part of the period.</param>
    /// <exception cref="InvalidOperationException">The drag has ended, or the edit does not apply.</exception>
    public void Move(float time, float value)
    {
        if (ended)
            throw new InvalidOperationException("The drag has ended.");
        if (!float.IsFinite(time) || !float.IsFinite(value))
            return;
        var low = index > 0 ? points[index - 1].Time : 0;
        var high = index < points.Count - 1 ? points[index + 1].Time : 1;
        var point = points[index] with { Time = Math.Clamp(time, Math.Min(low, high), Math.Max(low, high)), Value = value };
        if (point == applied)
            return;
        transaction.Apply(point == points[index]
            ? EditCommand.Group("", [])
            : ThnEdits.Set(ev, ThnCurve.Path, ThnCurve.WithPoint(curve, index, point)));
        applied = point;
    }

    /// <summary>Ends the drag with the point that the edit view holds: one entry, or none for no change.</summary>
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
}

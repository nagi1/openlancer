using System;

namespace OpenLancer.Editor.Editing;

/// <summary>What one drag label of the Inspector tells in one frame (section 6.3 of the M20 design).</summary>
/// <param name="Id">The ImGui id of the label button.</param>
/// <param name="Frame">The number of the frame.</param>
/// <param name="Activated">The button became active in this frame: the left mouse button went down on it.</param>
/// <param name="Held">The button is active.</param>
/// <param name="Released">The button stopped being active in this frame.</param>
/// <param name="MouseX">The X position of the mouse in pixels, or NaN when ImGui has no valid position.</param>
/// <param name="Escape">The user pressed Escape in this frame.</param>
/// <param name="Right">The user clicked the right mouse button in this frame.</param>
/// <param name="Shift">Shift is down: fine steps.</param>
/// <param name="Ctrl">Ctrl is down: large steps. Shift wins when both are down.</param>
/// <param name="Scale">The UI scale: the pixels of one point.</param>
/// <param name="Threshold">The pixels that the mouse must move before the drag starts.</param>
/// <param name="Owned">The open field edit belongs to this button, before the routing of this frame.</param>
internal readonly record struct LabelDragInput(uint Id, int Frame, bool Activated, bool Held, bool Released,
    float MouseX, bool Escape, bool Right, bool Shift, bool Ctrl, float Scale, float Threshold, bool Owned);

/// <summary>
/// The state of the one label drag of the Inspector: a press on a label, the drag after the threshold, and its end or
/// its cancel. It gives the events of the field edit (see <see cref="FieldEditRouter"/>) and the units of the drag. One
/// unit is one step of the value (see <see cref="FieldDrag.BaseStep"/>).
/// </summary>
/// <remarks>
/// <para>The drag counts whole points of mouse motion from the start of the drag. It divides the pixel total, not the
/// motion of each frame, so no rounding error adds up. Each new point adds 1 unit, 0.1 with Shift or 10 with Ctrl, by
/// the keys of its frame. So a press of Shift during a drag changes the speed from that point on.</para>
/// <para>The editor reads Escape and the right mouse button itself. ImGui does not clear the active button on
/// Escape, so after a cancel the drag ignores the button until its release. Escape wins over a release in the same
/// frame, as for a text field.</para>
/// <para>Every label of the grid calls <see cref="Frame"/> in each frame. A call of another label changes nothing,
/// unless that label became active. A label that did not draw in the last frame lost its drag.</para>
/// </remarks>
internal sealed class LabelDrag
{
    private static readonly FieldEvents None = default;

    private State state;
    private uint id;
    private int lastFrame;
    private int dragFrame;
    private float pressX;
    private float lastX;
    private float pixels;
    private long emitted;
    private decimal units;

    private enum State
    {
        Idle,
        Pressed,
        Dragging,
        Ended,
    }

    /// <summary>Takes the input of one label in one frame. Returns the events for the field edits and the units of
    /// the drag since its start.</summary>
    public (FieldEvents Events, decimal Units) Frame(LabelDragInput input)
    {
        if (state != State.Idle && input.Id != id)
        {
            // Another label draws in the same frame. Only a new press takes the drag.
            if (!input.Activated)
                return (None, 0);
            state = State.Idle;
        }

        if (state != State.Idle && input.Frame > lastFrame + 1)
            state = State.Idle;

        id = input.Id;
        lastFrame = input.Frame;
        var events = state switch
        {
            State.Idle => Idle(input),
            State.Pressed => Pressed(input),
            State.Dragging => Dragging(input),
            _ => Ended(input),
        };
        return (events, state == State.Dragging || events.Deactivated ? units : 0);
    }

    private FieldEvents Idle(LabelDragInput input)
    {
        if (input.Activated)
        {
            state = State.Pressed;
            pressX = input.MouseX;
        }

        return None;
    }

    private FieldEvents Pressed(LabelDragInput input)
    {
        if (input.Released || !input.Held)
        {
            // A click is no edit.
            state = State.Idle;
            return None;
        }

        if (input.Escape || input.Right)
        {
            state = State.Ended;
            return None;
        }

        if (float.IsNaN(pressX))
            pressX = input.MouseX;
        if (!(MathF.Abs(input.MouseX - pressX) >= input.Threshold))
            return None;

        state = State.Dragging;
        dragFrame = input.Frame;
        lastX = input.MouseX;
        pixels = 0;
        emitted = 0;
        units = 0;
        return new FieldEvents(Activated: true, Active: true, Changed: false, Deactivated: false, Cancelled: false);
    }

    private FieldEvents Dragging(LabelDragInput input)
    {
        if (input.Frame > dragFrame && !input.Owned)
        {
            // The edit ended from outside, for example at the close prompt, or it did not start.
            state = input.Held ? State.Ended : State.Idle;
            return None;
        }

        if (input.Escape || input.Right)
        {
            state = input.Held ? State.Ended : State.Idle;
            return new FieldEvents(Activated: false, Active: false, Changed: false, Deactivated: true, Cancelled: true);
        }

        if (!input.Held && !input.Released)
        {
            // The button lost its active state without an end. The field edits end the edit and keep it.
            state = State.Idle;
            return None;
        }

        var changed = Move(input);
        if (input.Released)
        {
            state = State.Idle;
            return new FieldEvents(Activated: false, Active: false, Changed: changed, Deactivated: true, Cancelled: false);
        }

        return new FieldEvents(Activated: false, Active: true, Changed: changed, Deactivated: false, Cancelled: false);
    }

    private FieldEvents Ended(LabelDragInput input)
    {
        if (input.Released || !input.Held)
            state = State.Idle;
        return None;
    }

    /// <summary>Adds the motion of the frame. Returns true when the units changed.</summary>
    private bool Move(LabelDragInput input)
    {
        if (float.IsNaN(input.MouseX))
            return false;
        pixels += input.MouseX - lastX;
        lastX = input.MouseX;
        var scale = input.Scale > 0 ? input.Scale : 1;
        var whole = (long)MathF.Truncate(pixels / scale);
        var points = whole - emitted;
        emitted = whole;
        units += points * (input.Shift ? 0.1m : input.Ctrl ? 10m : 1m);
        return points != 0;
    }
}

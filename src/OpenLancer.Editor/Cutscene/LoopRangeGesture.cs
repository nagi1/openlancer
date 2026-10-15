namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// A drag of the loop range on the ruler of the Timeline (section 7.7 of the M8 design). A drag that starts with Shift
/// sets the range from the time of the press to the time of the mouse, and turns the loop on. A Shift+click without a
/// drag clears the range, so that the loop plays the whole script again. A press without Shift is a scrub.
/// </summary>
internal sealed class LoopRangeGesture
{
    // The time of the press of a range drag, or null when no range drag runs.
    private double? anchor;
    private bool dragged;

    /// <summary>True from the press of a range drag to its release.</summary>
    public bool Active => anchor != null;

    /// <summary>The press of the mouse button on the ruler.</summary>
    /// <param name="time">The time under the mouse.</param>
    /// <param name="range">True when the press starts a range drag (Shift is down).</param>
    public void Press(double time, bool range) => (anchor, dragged) = (range ? time : null, false);

    /// <summary>A step of a range drag to a time.</summary>
    public void Drag(PreviewTransport transport, double time)
    {
        if (anchor is not { } start)
        {
            return;
        }

        dragged = true;
        transport.LoopRange = (start, time);
        transport.Loop = true;
    }

    /// <summary>The release. A range drag without a drag step clears the range.</summary>
    public void Release(PreviewTransport transport)
    {
        if (anchor != null && !dragged)
        {
            transport.LoopRange = null;
        }

        anchor = null;
    }
}

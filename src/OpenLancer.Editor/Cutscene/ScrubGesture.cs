namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// A drag of the playhead, on the scrub bar of the Cutscene view or on the ruler of the Timeline. A drag forward seeks
/// at once, because the visible player runs on. A drag back waits for the release, because each seek back builds the
/// scene again (section 8.4 of the M8 design). Until then, the playhead shows the held time.
/// </summary>
internal sealed class ScrubGesture
{
    /// <summary>The time of a drag back that waits for the release, or null.</summary>
    public double? Held { get; private set; }

    /// <summary>The time that the playhead shows: the held time, or the time of the transport.</summary>
    public double Shown(PreviewTransport transport) => Held ?? transport.Time;

    /// <summary>A step of the drag to a time.</summary>
    public void Drag(PreviewTransport transport, double time)
    {
        transport.NoteScrub();
        if (time < transport.Time)
        {
            Held = time;
            return;
        }

        Held = null;
        transport.Seek(time);
    }

    /// <summary>The end of the drag: a held time seeks now.</summary>
    public void Release(PreviewTransport transport)
    {
        if (Held is { } held)
        {
            Held = null;
            transport.Seek(held);
        }
    }
}

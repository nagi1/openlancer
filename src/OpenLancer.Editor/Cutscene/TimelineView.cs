using System;
using System.Collections.Generic;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The view of the Timeline of one cutscene: the time at the left edge, the zoom, the collapsed groups and the event
/// filter. The view keeps between 0 and the end of the time range, with a margin of <see cref="Margin"/> pixels at each
/// end, so that a diamond at time 0 or at the end shows whole. It shows at most 600 s, so a long ambient script opens
/// on its first 600 s (section 7.7 of the M8 design).
/// </summary>
internal sealed class TimelineView
{
    /// <summary>The longest time range that the view shows.</summary>
    public const double MaxSpan = 600;

    /// <summary>The highest zoom: 1 ms is 2 pixels.</summary>
    public const double MaxPixelsPerSecond = 2000;

    /// <summary>The space before time 0 and after the end, in pixels.</summary>
    public const float Margin = 10;

    /// <summary>The time at the left edge of the tracks, in seconds. It is below 0 when the view shows the start.</summary>
    public double Start { get; private set; }

    /// <summary>The zoom, in pixels for each second. It is 0 until the first <see cref="Fit"/>.</summary>
    public double PixelsPerSecond { get; private set; }

    /// <summary>The groups that show only their header.</summary>
    public HashSet<TimelineGroupKind> Collapsed { get; } = [];

    /// <summary>True to show only the rows with events. It is on at the start, because many markers have no
    /// events.</summary>
    public bool OnlyWithEvents { get; set; } = true;

    /// <summary>True after the first <see cref="Fit"/>.</summary>
    public bool IsFitted => PixelsPerSecond > 0;

    /// <summary>The time at an x position of the tracks.</summary>
    /// <param name="left">The x position of the left edge of the tracks.</param>
    public double TimeAt(float x, float left) => Start + (x - left) / PixelsPerSecond;

    /// <summary>The x position of a time.</summary>
    public float XOf(double time, float left) => left + (float)((time - Start) * PixelsPerSecond);

    /// <summary>Shows the time range from 0, at most 600 s of it.</summary>
    /// <param name="end">The end of the time range.</param>
    /// <param name="width">The width of the tracks, in pixels.</param>
    public void Fit(double end, float width) => Show(0, Math.Min(Math.Max(end, 0.001), MaxSpan), end, width);

    /// <summary>Shows a time span with a margin of a tenth on each side, for example the events of the
    /// selection.</summary>
    public void Frame(double from, double to, double end, float width)
    {
        var margin = Math.Max((to - from) / 10, 0.05);
        Show(from - margin, to + margin, end, width);
    }

    /// <summary>Zooms in or out by a factor. The time under <paramref name="x"/> stays under it.</summary>
    public void ZoomAt(float x, float left, double factor, double end, float width)
    {
        var time = TimeAt(x, left);
        PixelsPerSecond = ClampZoom(PixelsPerSecond * factor, width);
        Start = time - (x - left) / PixelsPerSecond;
        ClampStart(end, width);
    }

    /// <summary>Moves the view by a number of pixels. A positive number shows later times.</summary>
    public void Pan(float pixels, double end, float width)
    {
        Start += pixels / PixelsPerSecond;
        ClampStart(end, width);
    }

    /// <summary>Keeps the view valid after the width of the tracks or the end of the time range changed.</summary>
    public void Keep(double end, float width)
    {
        PixelsPerSecond = ClampZoom(PixelsPerSecond, width);
        ClampStart(end, width);
    }

    /// <summary>Shows a span in the width between the margins.</summary>
    private void Show(double from, double to, double end, float width)
    {
        PixelsPerSecond = ClampZoom(Inner(width) / Math.Max(to - from, 1e-3), width);
        Start = from - Margin / PixelsPerSecond;
        ClampStart(end, width);
    }

    private static double ClampZoom(double pixelsPerSecond, float width) =>
        Math.Clamp(pixelsPerSecond, Inner(width) / MaxSpan, MaxPixelsPerSecond);

    /// <summary>
    /// Keeps the margin before time 0 at the left edge or to the right of it, and the margin after the end at the right
    /// edge or to the left of it. A range that is shorter than the view starts at the margin.
    /// </summary>
    private void ClampStart(double end, float width)
    {
        var first = -Margin / PixelsPerSecond;
        Start = Math.Clamp(Start, first, Math.Max(first, end + Margin / PixelsPerSecond - Math.Max(width, 1) / PixelsPerSecond));
    }

    /// <summary>The width between the margins, at least 1 pixel.</summary>
    private static double Inner(float width) => Math.Max(width - 2 * Margin, 1);
}

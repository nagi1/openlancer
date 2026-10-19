using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LibreLancer;

namespace OpenLancer.Data;

/// <summary>
/// Collects the warning and error lines that Librelancer writes through <see cref="FLLog"/> while it is active.
/// Lines still go to the listener that was set before.
/// </summary>
/// <remarks>
/// <para>FLLog is global, so captures must not overlap. Lines that other threads write during a capture are
/// collected too.</para>
/// <para>FLLog writes from one background thread, in order, and gives lines to <see cref="FLLog.AppendLine"/> only
/// through <see cref="FLLog.UIThread"/>. Lines that were logged before the capture can still be in its queue.
/// So <see cref="Start"/> logs a start sentinel and the capture ignores all lines before it, and
/// <see cref="Stop"/> logs an end sentinel and waits for it. (<see cref="FLLog.Flush"/> can return before the
/// last line arrives.)</para>
/// <para>The capture does not collect the lines that <see cref="EngineLineRules"/> drops, because they describe the
/// state of the engine process, not the data. An example is the line of a second data load in one process, which
/// replaces the global material map.</para>
/// </remarks>
public sealed class EngineLogCapture : IUIThread, IDisposable
{
    private static readonly Lock StartLock = new();

    private readonly ConcurrentQueue<EngineLine> lines = new();
    private readonly ManualResetEventSlim endSeen = new();
    private readonly string startSentinel;
    private readonly string endSentinel;
    private readonly IUIThread previousThread;
    private readonly Action<string, LogSeverity> previousAppend;
    private volatile bool collecting;
    private bool stopped;

    private EngineLogCapture()
    {
        var id = Guid.NewGuid().ToString("N");
        startSentinel = "OpenLancer.EngineLogCapture.Start." + id;
        endSentinel = "OpenLancer.EngineLogCapture.End." + id;
        // FLLog's thread reads both fields without a lock, so they are never set back to null.
        previousThread = FLLog.UIThread ?? ImmediateThread.Instance;
        previousAppend = FLLog.AppendLine ?? ((_, _) => { });
    }

    public static EngineLogCapture Start()
    {
        lock (StartLock)
        {
            var capture = new EngineLogCapture();
            FLLog.UIThread = capture;
            FLLog.AppendLine = capture.Append;
            FLLog.Error("OpenLancer", capture.startSentinel);
            return capture;
        }
    }

    /// <summary>
    /// Converts captured lines to diagnostics. Lines that start with "Error " are errors, all others warnings.
    /// </summary>
    public static IEnumerable<Diagnostic> ToDiagnostics(IEnumerable<string> captured, string source) =>
        captured.Select(line => line.StartsWith("Error ", StringComparison.Ordinal)
            ? Diagnostic.Error(source, line)
            : Diagnostic.Warning(source, line));

    void IUIThread.QueueUIThread(Action work) => work();

    /// <summary>
    /// Stops the capture. Returns the warning and error lines in arrival order, as "Severity Component: message", without
    /// the time and the line number (see <see cref="EngineLine.Normalized"/>).
    /// </summary>
    public IReadOnlyList<string> Stop() => StopLines().Select(l => l.Normalized).ToList();

    /// <summary>Stops the capture. Returns the warning and error lines in arrival order.</summary>
    public IReadOnlyList<EngineLine> StopLines()
    {
        lock (StartLock)
        {
            if (!stopped)
            {
                FLLog.Error("OpenLancer", endSentinel);
                if (!endSeen.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("FLLog did not deliver the end sentinel line in 30 s.");
                stopped = true;
                if (FLLog.UIThread == this)
                {
                    FLLog.UIThread = previousThread;
                    FLLog.AppendLine = previousAppend;
                }
            }
        }
        return lines.ToList();
    }

    /// <summary>Stops the capture. All lines that were logged before are delivered first.</summary>
    public void Dispose() => StopLines();

    private void Append(string line, LogSeverity severity)
    {
        if (line.EndsWith(startSentinel, StringComparison.Ordinal))
        {
            collecting = true;
            return;
        }
        if (line.EndsWith(endSentinel, StringComparison.Ordinal))
        {
            collecting = false;
            endSeen.Set();
            return;
        }
        if (collecting && severity >= LogSeverity.Warning)
        {
            var parsed = EngineLine.Parse(line, severity);
            if (EngineLineRules.Match(parsed)?.Disposition != EngineLineDisposition.Drop)
                lines.Enqueue(parsed);
        }
        previousThread.QueueUIThread(() => previousAppend(line, severity));
    }

    private sealed class ImmediateThread : IUIThread
    {
        public static readonly ImmediateThread Instance = new();

        public void QueueUIThread(Action work) => work();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
/// </remarks>
public sealed partial class EngineLogCapture : IUIThread, IDisposable
{
    private static readonly Lock StartLock = new();

    /// <summary>
    /// Lines that describe the engine's process state, not the data. They are not collected.
    /// </summary>
    private static readonly string[] ProcessStateLines =
    [
        // Logged when a second data load in one process replaces the global material map.
        "Warning MaterialMap: Switching global material map. TODO make instance",
    ];

    private readonly ConcurrentQueue<string> lines = new();
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
    /// Stops the capture. Returns the warning and error lines in arrival order, as "Severity Component: message".
    /// </summary>
    public IReadOnlyList<string> Stop()
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
    public void Dispose() => Stop();

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
            var text = Normalize(line);
            if (!ProcessStateLines.Contains(text))
                lines.Enqueue(text);
        }
        previousThread.QueueUIThread(() => previousAppend(line, severity));
    }

    /// <summary>
    /// Removes what differs between two loads of the same data:
    /// "[Warning: 12:34:56] Ini: Unknown entry x at section Ship: a.ini, line 12" and
    /// "[Warning: 12:34:56] Ini: Unknown entry x in section Ship: a.ini (line not available)" both become
    /// "Warning Ini: Unknown entry x in section Ship: a.ini". The time separator depends on the culture. The line
    /// is known only for a text INI file, and it changes when a patch rewrites the file.
    /// </summary>
    private static string Normalize(string line) =>
        Location().Replace(Timestamp().Replace(line, "$1 "), " in${section} ${file}");

    [GeneratedRegex(@"^\[(\w+): [^\]]*\] ")]
    private static partial Regex Timestamp();

    // The two forms of upstream IniDiagnostic.FormatLine, with or without the section part.
    [GeneratedRegex(@" (?:at|in)(?<section> section [^:]+:)? (?<file>\S.*?)(?:, line \d+| \(line not available\))$")]
    private static partial Regex Location();

    private sealed class ImmediateThread : IUIThread
    {
        public static readonly ImmediateThread Instance = new();

        public void QueueUIThread(Action work) => work();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using OpenLancer.Data.Missions;
using static System.FormattableString;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The frame time bench of the editor option <c>--bench-mission</c> (section 18.4 of the M6a design). After the game data
/// loads and the mission checks run, it opens each script in the Mission tab with the Scene part on. For each script, it
/// runs two cases: the Beats lens at the summary level, and the Logic lens with "Show lines". Each case draws 300 frames
/// with a pan and a zoom of the graph, after 30 frames to warm up. The bench writes the median and the 95th percentile of
/// the frame times to the log. The gates are a median under 16 ms in the Beats lens and under 33 ms in the Logic lens.
/// Then the editor closes. It needs no input.
/// </summary>
/// <remarks>A frame time is the time of the work of one frame of the editor, without the wait for the screen.</remarks>
internal sealed class MissionBench(EditorWindow window, MissionView view, IReadOnlyList<string> paths)
{
    /// <summary>The log component of the lines of the bench.</summary>
    public const string Component = "Bench";

    /// <summary>The frames of one case that the bench measures.</summary>
    public const int Frames = 300;

    private const int WarmFrames = 30;

    /// <summary>The bench fails when the data does not load or a case does not end in this wall time.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>The cases of each script: the lens, "Show lines" and the gate of the median, in milliseconds.</summary>
    internal static readonly (GraphLens Lens, bool Lines, double Gate)[] Cases =
    [
        (GraphLens.Beats, false, 16),
        (GraphLens.Logic, true, 33),
    ];

    private readonly List<double> times = [];
    private readonly DateTime started = DateTime.UtcNow;
    private int index = -1;
    private int run;
    private int frame;

    /// <summary>The scripts and cases that failed.</summary>
    public int Failures { get; private set; }

    public bool Done { get; private set; }

    /// <summary>Runs the bench for one frame, after the frame drew.</summary>
    /// <param name="drawTime">The time of the work of the frame that just drew.</param>
    public void Update(TimeSpan drawTime)
    {
        if (Done)
        {
            return;
        }

        var session = window.Session;
        if (session is { IsLoading: true } || session?.GameData != null && window.Missions.Result == null)
        {
            if (DateTime.UtcNow - started > Timeout)
            {
                Fail("The game data or the mission checks did not end in time.");
                Finish();
            }

            return;
        }

        if (session?.GameData == null)
        {
            Fail("The profile did not open, or its game data did not load. The Console and the log show why.");
            Finish();
            return;
        }

        if (index < 0)
        {
            Next();
            return;
        }

        frame++;
        if (frame <= WarmFrames)
        {
            view.Graph.BenchStep(frame);
            return;
        }

        times.Add(drawTime.TotalMilliseconds);
        view.Graph.BenchStep(frame);
        if (times.Count >= Frames)
        {
            Report();
            run++;
            if (run < Cases.Length)
            {
                Start();
            }
            else
            {
                Next();
            }
        }
    }

    /// <summary>Opens the next script, or ends the bench.</summary>
    private void Next()
    {
        while (++index < paths.Count)
        {
            var path = paths[index];
            if (window.Missions.Analysis(path) == null)
            {
                Fail($"{path}: the mission checks have no such script.");
                continue;
            }

            window.Missions.OpenMission(path, show: true);
            window.Missions.ShowRaw = false;
            window.Settings.MissionShowScene = true;
            run = 0;
            Start();
            return;
        }

        Finish();
    }

    /// <summary>Starts a case of the open script.</summary>
    private void Start()
    {
        var (lens, lines, _) = Cases[run];
        window.Settings.MissionLens = lens;
        window.Settings.MissionShowLines = lines;
        view.State.Lens = lens;
        times.Clear();
        frame = 0;
    }

    /// <summary>Writes the numbers of a case and checks its gate.</summary>
    private void Report()
    {
        var (lens, lines, gate) = Cases[run];
        var (median, high) = Stats(times);
        var graph = view.Graph.Graph;
        var name = $"{paths[index]}, {lens} lens{(lines ? " with lines" : "")}";
        FLLog.Info(Component, Invariant($"{name}: {graph?.Nodes.Count ?? 0} nodes, {graph?.Links.Count ?? 0} links. ") +
                              Invariant($"Frame time over {times.Count} frames: median {median:F2} ms, 95th ") +
                              Invariant($"percentile {high:F2} ms. Gate: median under {gate:0} ms."));
        if (median >= gate)
        {
            Fail(Invariant($"{name}: the median frame time {median:F2} ms is not under the gate of {gate:0} ms."));
        }
    }

    /// <summary>The median and the 95th percentile of the frame times, in milliseconds.</summary>
    internal static (double Median, double High) Stats(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (0, 0);
        }

        var sorted = values.Order().ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        var high = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(0.95 * sorted.Count) - 1)];
        return (median, high);
    }

    private void Fail(string message)
    {
        Failures++;
        FLLog.Error(Component, message);
    }

    private void Finish()
    {
        Done = true;
        FLLog.Info(Component, $"The bench ended: {paths.Count} scripts, {Failures} failed.");
        FLLog.Flush();
        window.ExitCode = Failures == 0 ? 0 : 1;
        window.Exit();
    }
}

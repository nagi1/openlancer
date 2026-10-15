using System;
using System.Collections.Generic;
using System.Diagnostics;
using LibreLancer;
using OpenLancer.Runtime.Thn;
using static System.FormattableString;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The preview bench of the editor option <c>--bench-cutscene</c> (section 8.5 of the M8 design). After the game data
/// loads, it opens each script in its suggested context, seeks to the end, and lets the Cutscene tab run the clock with
/// the budget of a normal frame. Then it seeks back to the middle of that time, as a back scrub does. The build of the
/// back scrub finds the models of the first build in the resources, so it is the build that each back scrub and each
/// edit waits for (section 7.7). For each of the two runs, it writes the build time, the step time, the catch-up time
/// and the THN camera after the build to the log. Then the editor closes. It needs no input.
/// </summary>
internal sealed class CutsceneBench(EditorWindow window, CutsceneWorkspace workspace, IReadOnlyList<string> paths)
{
    /// <summary>The log component of the lines of the bench.</summary>
    public const string Component = "Bench";

    /// <summary>A long ambient script catches up only to this time, the end of the scrub bar.</summary>
    private const double MaxTarget = 600;

    /// <summary>A run that does not reach its target in this wall time fails.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly Stopwatch watch = new();
    private int index = -1;
    private double target;

    // The run is the back scrub, after the first build of the script.
    private bool backScrub;
    private int frames;
    private long stepsBefore;
    private TimeSpan stepTimeBefore;

    /// <summary>The scripts that failed.</summary>
    public int Failures { get; private set; }

    public bool Done { get; private set; }

    /// <summary>Runs the bench for one frame. Call it after the layout drew the Cutscene tab.</summary>
    public void Update()
    {
        if (Done)
        {
            return;
        }

        var session = window.Session;
        if (session is { IsLoading: true })
        {
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

        frames++;
        var clock = workspace.Clock;
        if (clock?.Problem is { } problem)
        {
            Fail($"{paths[index]}: the preview stopped: {problem.Message}");
            Next();
        }
        else if (clock is { CatchingUp: false, Visible: { } visible } && Math.Abs(visible.Time - target) < 1e-6)
        {
            Report();
            var middle = LastStep(target / 2);
            if (!backScrub && middle < target)
            {
                backScrub = true;
                Run(middle);
            }
            else
            {
                Next();
            }
        }
        else if (watch.Elapsed > Timeout)
        {
            Fail(Invariant($"{paths[index]}: the catch-up did not reach {target:0.###} s in ") +
                 Invariant($"{Timeout.TotalSeconds:0} s."));
            Next();
        }
    }

    /// <summary>Opens the next script and seeks to its end, or ends the bench.</summary>
    private void Next()
    {
        while (++index < paths.Count)
        {
            var path = paths[index];
            if (!workspace.Open(path) || workspace.Transport is not { } transport)
            {
                Fail($"{path}: {workspace.Problem}");
                continue;
            }

            window.ShowPanel(CutsceneView.PanelId);
            backScrub = false;
            Run(LastStep(Math.Min(workspace.Duration, MaxTarget)));
            FLLog.Info(Component,
                Invariant($"{path}: {workspace.Duration:0.###} s, {workspace.Context!.Label}, catch-up to ") +
                Invariant($"{target:0.###} s. References scan {Ms(workspace.LastScan)} ms."));
            return;
        }

        Finish();
    }

    /// <summary>Seeks to the target of a run and starts its measure.</summary>
    private void Run(double time)
    {
        target = time;
        workspace.Transport!.Seek(time);
        (frames, stepsBefore, stepTimeBefore) = (0, workspace.Meter.Steps, workspace.Meter.StepTime);
        watch.Restart();
    }

    /// <summary>The clock runs whole steps, so a target is the last step at or before a time.</summary>
    private static double LastStep(double time) =>
        Math.Floor(time / CutsceneClock.StepSeconds + 1e-6) * CutsceneClock.StepSeconds;

    /// <summary>Writes the numbers of the run that reached its target.</summary>
    private void Report()
    {
        var wall = watch.Elapsed;
        var steps = workspace.Meter.Steps - stepsBefore;
        var stepTime = workspace.Meter.StepTime - stepTimeBefore;
        var perStep = steps > 0 ? stepTime.TotalMilliseconds / steps : 0;
        var perSecond = target > 0 ? stepTime.TotalMilliseconds / target : 0;
        var run = backScrub ? "back scrub" : "first build";
        FLLog.Info(Component,
            Invariant($"{paths[index]}: {run}: build {Ms(workspace.LastBuild)} ms ({workspace.Builds} builds), ") +
            Invariant($"{steps} steps in {Ms(stepTime)} ms ({perStep:F3} ms for each step, {perSecond:F1} ms ") +
            Invariant($"of CPU for each script second), catch-up to {target:0.###} s in {wall.TotalSeconds:F2} s ") +
            Invariant($"of wall time over {frames} frames. Camera after the build: ") +
            Invariant($"{workspace.CameraAfterBuild ?? "none"}. Camera at the target: ") +
            Invariant($"{workspace.Visible?.CameraName ?? "none"}."));
    }

    private void Fail(string message)
    {
        Failures++;
        FLLog.Error(Component, message);
    }

    private void Finish()
    {
        Done = true;
        workspace.Close();
        FLLog.Info(Component, $"The bench ended: {paths.Count} scripts, {Failures} failed.");
        FLLog.Flush();
        window.ExitCode = Failures == 0 ? 0 : 1;
        window.Exit();
    }

    private static string Ms(TimeSpan time) => Invariant($"{time.TotalMilliseconds:F1}");
}

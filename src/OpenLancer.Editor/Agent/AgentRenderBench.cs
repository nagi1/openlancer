using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Studio.Agent;
using static System.FormattableString;

namespace OpenLancer.Editor.Agent;

/// <summary>
/// The render bench of the editor option <c>--bench-agent-render</c> (section 13.4 of the MCP1 design). After the game
/// data loads, it opens a system in the Scene and renders images of 1024 by 640 through the views of the agent tools, as
/// <c>render_scene</c> does: one image to warm up, then one image from each of 10 directions around the system. For each
/// image, it writes two times to the log: the session part of the call on the UI thread, and the time until the PNG is
/// ready. The session part holds the whole GPU time of the image, because the read of the pixels waits for the GPU. The
/// gate is a median session part under 100 ms. Then the editor closes. It needs no input.
/// </summary>
internal sealed class AgentRenderBench(EditorWindow window, IAgentViews views, string system)
{
    /// <summary>The log component of the lines of the bench.</summary>
    public const string Component = "Bench";

    /// <summary>The images that the bench measures, after the image that warms up.</summary>
    public const int Images = 10;

    /// <summary>The gate of the median session part of an image, in milliseconds.</summary>
    public const double GateMs = 100;

    private const int Width = ViewTools.DefaultWidth;
    private const int Height = ViewTools.DefaultHeight;

    /// <summary>The bench fails when the data does not load or an image does not end in this wall time.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly List<double> renders = [];
    private readonly Stopwatch watch = Stopwatch.StartNew();

    // The image that the bench waits for: its number (0 warms up), the time of its session part and the start of its
    // wait.
    private (int Index, Task<(SceneShotView View, ToolImage Image)> Result, double RenderMs, long Started)? pending;
    private int next = -1;

    /// <summary>The images and steps that failed.</summary>
    public int Failures { get; private set; }

    public bool Done { get; private set; }

    /// <summary>The camera of an image: the frame of the whole system, turned about the vertical axis by a step for each
    /// image. Image 0 and image 10 have the camera of the Scene frame.</summary>
    internal static ShotCamera Camera(ShotCamera framed, int index)
    {
        var turn = Matrix4x4.CreateRotationY(index * 2 * MathF.PI / Images);
        return framed with { Position = framed.LookAt + Vector3.Transform(framed.Position - framed.LookAt, turn) };
    }

    /// <summary>The median of a list of times.</summary>
    internal static double Median(IReadOnlyList<double> times)
    {
        var sorted = times.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Runs the bench for one frame, before the panels draw.</summary>
    public void Update()
    {
        if (Done)
            return;
        if (watch.Elapsed > Timeout)
        {
            Fail($"The bench did not end in {Timeout.TotalMinutes:0} minutes.");
            Finish();
            return;
        }

        var session = window.Session;
        if (session is { IsLoading: true })
            return;
        if (session?.GameData is not { } data)
        {
            Fail("The profile did not open, or its game data did not load. The Console and the log show why.");
            Finish();
            return;
        }

        if (next < 0)
        {
            // The open of the system takes its own frame, so it does not count for an image.
            if (data.Items.Systems.Get(system) is not { } open)
            {
                Fail($"The game data has no system {system}.");
                Finish();
                return;
            }

            window.OpenSystem(open);
            next = 0;
            return;
        }

        if (pending is { } wait)
        {
            if (!wait.Result.IsCompleted)
                return;
            Report(wait.Index, wait.Result, wait.RenderMs, Stopwatch.GetElapsedTime(wait.Started).TotalMilliseconds);
            pending = null;
            if (next > Images)
                Finish();
            return;
        }

        Start(next++);
    }

    /// <summary>Starts an image: the session part of <c>render_scene</c> runs now, with the render and the read of the
    /// pixels. The PNG encode ends on a worker.</summary>
    private void Start(int index)
    {
        if (window.Scene.Stage is not { } stage)
        {
            Fail($"The Scene did not open the system {system}.");
            Finish();
            return;
        }

        var framed = ShotCamera.Frame(stage.ShotItems(false).Select(i => (i.Center, i.Radius)), Width / (float)Height);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var shot = views.RenderScene(new SceneShotRequest(system, Camera(framed, index), [], Width, Height, true,
                new AgentLimits().MaxListRows));
            var renderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            pending = (index, shot(CancellationToken.None), renderMs, Stopwatch.GetTimestamp());
        }
        catch (Exception e)
        {
            Fail($"Image {index}: {e.Message}");
            if (next > Images)
                Finish();
        }
    }

    /// <summary>Writes the times of an image. The image that warms up does not count for the gate.</summary>
    private void Report(int index, Task<(SceneShotView View, ToolImage Image)> result, double renderMs, double readyMs)
    {
        if (!result.IsCompletedSuccessfully)
        {
            Fail($"Image {index}: {result.Exception?.GetBaseException().Message ?? "the image was cancelled."}");
            return;
        }

        var (view, image) = result.Result;
        var kind = index == 0 ? "warm-up image" : $"image {index}";
        FLLog.Info(Component,
            Invariant($"{system}: {kind}: render {renderMs:F1} ms on the UI thread, PNG ready after {readyMs:F1} ms, ") +
            Invariant($"{image.Png.Length / 1024} KB, {view.VisibleTotal} items in view."));
        if (index > 0)
            renders.Add(renderMs);
    }

    private void Fail(string message)
    {
        Failures++;
        FLLog.Error(Component, message);
    }

    private void Finish()
    {
        Done = true;
        if (renders.Count > 0)
        {
            var median = Median(renders);
            var passed = median < GateMs;
            if (!passed)
                Failures++;
            FLLog.Info(Component,
                Invariant($"{system}: {renders.Count} images of {Width} by {Height}: median {median:F1} ms, ") +
                Invariant($"max {renders.Max():F1} ms on the UI thread. Gate {GateMs:0} ms: ") +
                (passed ? "passed." : "failed."));
        }

        FLLog.Info(Component, $"The bench ended: {Failures} failures.");
        FLLog.Flush();
        window.ExitCode = Failures == 0 ? 0 : 1;
        window.Exit();
    }
}

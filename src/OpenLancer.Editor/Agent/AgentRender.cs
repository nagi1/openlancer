using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.Thn;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Agent;

/// <summary>An image of a cutscene of the agent and the state that it shows.</summary>
/// <param name="Time">The script time of the image, in seconds: the last step of the preview clock at or before the time
/// of the call.</param>
/// <param name="Camera">The scene object that the image looks through.</param>
internal sealed record CutsceneShotDone(double Time, string Camera, ToolImage Image);

/// <summary>An image of a cutscene that steps over the next frames.</summary>
/// <param name="Result">The image. A refusal ends it with an <see cref="EditException"/>, and <see cref="Cancel"/> ends
/// it as cancelled in the next frame.</param>
/// <param name="Cancel">Stops the image. It can run on any thread, for example when the client cancels the
/// call.</param>
internal sealed record CutsceneShotJob(Task<CutsceneShotDone> Result, Action Cancel);

/// <summary>
/// The offscreen render service of the agent server (section 12.3 of the MCP1 design, decision D721): the images of the
/// Scene and of the cutscene preview from a camera of the agent, and the capture of a panel as the user sees it. It
/// runs on the UI thread. An image never moves the camera of the user and never changes the preview of the user.
/// </summary>
/// <remarks>
/// <para>The read-back of the pixels ends in a later frame (<c>Texture2D.GetDataAsync</c>), and the PNG encode runs on a
/// worker. So a caller awaits the image in the tail of its call (decision D765).</para>
/// <para>A cutscene image builds a player of its own and steps it to the time of the call over several frames, with a
/// time budget in each frame, as the preview of the user catches up (section 8.4 of the M8 design).</para>
/// <para>A capture asks the window for its back buffer. The window reads the buffer at the end of the frame, after the
/// panels drew, so the capture shows the panel of that frame with its overlays and marks.</para>
/// </remarks>
internal sealed class AgentRender : IDisposable
{
    /// <summary>The time that the cutscene images can use in one frame. The preview of the user uses this budget while
    /// the user drags.</summary>
    internal static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(12);

    /// <summary>The file name of a screenshot of the window that a capture asks for. The window gives each screenshot to
    /// each handler, so the service takes only its own.</summary>
    private const string CaptureTag = "OpenLancer.AgentCapture";

    private readonly EditorWindow window;
    private readonly List<CutsceneJob> jobs = [];
    private readonly List<(string PanelId, TaskCompletionSource<(byte[] Bgra, int Width, int Height)> Done)> captures = [];

    public AgentRender(EditorWindow window)
    {
        this.window = window;
        window.ScreenshotSave += Captured;
    }

    /// <summary>
    /// Renders the open system of the Scene from a camera and starts the read-back. The image has the view toggles of
    /// the user, except the Zones toggle (see <see cref="Scene.SceneView.RenderShot"/>).
    /// </summary>
    /// <returns>The image. It ends in a later frame.</returns>
    /// <exception cref="InvalidOperationException">No system is open.</exception>
    public Task<ToolImage> RenderScene(ShotCamera camera, int width, int height, bool zones) =>
        ReadBack(window.Scene.RenderShot(camera, width, height, zones), width, height);

    /// <summary>
    /// Starts an image of a cutscene at a time. The image steps a player of its own to the time over the next frames,
    /// then renders it. A space script plays without its system, because the stage of the system belongs to the preview
    /// of the user.
    /// </summary>
    /// <param name="camera">The name of a CAMERA entity, or null for the active camera at the time.</param>
    /// <exception cref="InvalidOperationException">No profile is open.</exception>
    public CutsceneShotJob RenderCutscene(CutsceneShotPlan plan, double time, string? camera, int width, int height)
    {
        var session = window.Session ?? throw new InvalidOperationException("No profile is open.");
        var job = new CutsceneJob(window, session, plan, time, camera, width, height);
        jobs.Add(job);
        return new CutsceneShotJob(job.Result, job.Cancel);
    }

    /// <summary>
    /// Asks for an image of a panel as the user sees it at the end of this frame. Call it before the panels draw.
    /// </summary>
    /// <param name="panelId">The id of the panel in the layout, for example <c>scene</c>.</param>
    /// <returns>The image. It ends with an <see cref="EditException"/> when the panel does not show at the end of the
    /// frame.</returns>
    public Task<ToolImage> Capture(string panelId)
    {
        var done = new TaskCompletionSource<(byte[] Bgra, int Width, int Height)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        captures.Add((panelId, done));
        window.Screenshot(CaptureTag);
        return Encode(done.Task);
    }

    /// <summary>Steps the cutscene images within the budget of the frame, and renders each one that reached its time.
    /// Call it once in each frame, before the panels draw.</summary>
    public void Frame()
    {
        var start = TimeProvider.System.GetTimestamp();
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var left = FrameBudget - TimeProvider.System.GetElapsedTime(start);
            bool ended;
            try
            {
                // Each image steps at least a little, so a busy frame does not stop the others.
                ended = job.Frame(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A fault of an image ends its call, and the window draws on.
                job.Fail(EditErrorKind.StepFailed, "ToolFailed",
                    AgentTexts.Error("tool_failed", ("tool", "render_cutscene"), ("reason", e.Message)));
                ended = true;
            }

            if (!ended)
                continue;
            job.Dispose();
            jobs.RemoveAt(i--);
        }
    }

    /// <summary>Ends each image that waits, for example when the profile closes. Their calls give an error.</summary>
    public void Reset()
    {
        foreach (var job in jobs)
        {
            job.Fail(EditErrorKind.NotAvailable, "ProfileClosed", AgentTexts.Error("render_closed"));
            job.Dispose();
        }
        jobs.Clear();
        foreach (var (_, done) in captures)
        {
            done.TrySetException(new EditException(new EditError(EditErrorKind.NotAvailable, "ProfileClosed",
                AgentTexts.Error("render_closed"))));
        }
        captures.Clear();
    }

    public void Dispose()
    {
        Reset();
        window.ScreenshotSave -= Captured;
    }

    /// <summary>The script time of the last step of the preview clock at or before a time. The clock steps whole steps,
    /// so a cutscene image shows this time.</summary>
    internal static double LastStep(double time) =>
        Math.Floor(time / CutsceneClock.StepSeconds + 1e-6) * CutsceneClock.StepSeconds;

    /// <summary>
    /// The PNG image of a texture of a render: the read-back starts now, on the UI thread, and the texture goes at once.
    /// The read copies the pixels into a buffer of the GPU first, so the texture is not necessary after the call. The
    /// encode runs on a worker.
    /// </summary>
    internal static async Task<ToolImage> ReadBack(Texture2D texture, int width, int height)
    {
        Task<byte[]> pixels;
        try
        {
            pixels = texture.GetDataAsync();
        }
        finally
        {
            texture.Dispose();
        }
        // The fence of the read ends in a later frame, on the UI thread. The encode then moves to a worker.
        var data = await pixels.ConfigureAwait(false);
        return await Task.Run(() => AgentImages.Png(width, height, data)).ConfigureAwait(false);
    }

    /// <summary>The PNG image of the pixels of a capture, encoded on a worker.</summary>
    private static async Task<ToolImage> Encode(Task<(byte[] Bgra, int Width, int Height)> pixels)
    {
        var (bgra, width, height) = await pixels.ConfigureAwait(false);
        return AgentImages.Png(width, height, bgra);
    }

    /// <summary>The window read its back buffer at the end of a frame. Each capture of that frame gets the part of its
    /// panel, or an error when its panel did not show.</summary>
    private void Captured(string? name, int width, int height, Bgra8[] data)
    {
        if (name != CaptureTag || captures.Count == 0)
            return;
        foreach (var (panelId, done) in captures)
        {
            var place = window.PanelPlace(panelId);
            var crop = place is var (min, size)
                ? AgentImages.Crop(data, width, height, new Rectangle((int)min.X, (int)min.Y, (int)size.X, (int)size.Y))
                : null;
            if (crop is { } pixels)
            {
                done.TrySetResult(pixels);
            }
            else
            {
                done.TrySetException(new EditException(new EditError(EditErrorKind.Refused, "PanelHidden",
                    AgentTexts.Error("capture_hidden", ("panel", panelId)))));
            }
        }
        captures.Clear();
    }

    /// <summary>
    /// One image of a cutscene: a clock of its own with a player of its own, at the time of the call. The plan holds the
    /// scripts of the call, so an edit of the user after the call does not change the image. When the game data loads
    /// again, the image stops, because the player uses the resources of the session.
    /// </summary>
    private sealed class CutsceneJob : IDisposable
    {
        private readonly EditorWindow window;
        private readonly EditorSession session;
        private readonly CutsceneShotPlan plan;
        private readonly CutsceneClock clock;
        private readonly double target;
        private readonly string? camera;
        private readonly int width;
        private readonly int height;
        private readonly TaskCompletionSource<CutsceneShotDone> done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool cancelled;

        public CutsceneJob(EditorWindow window, EditorSession session, CutsceneShotPlan plan, double time, string? camera,
            int width, int height)
        {
            this.window = window;
            this.session = session;
            this.plan = plan;
            this.camera = camera;
            this.width = width;
            this.height = height;
            var items = plan.Data.Items;
            var viewport = new Rectangle(0, 0, width, height);
            clock = new CutsceneClock(() => CutscenePlayer.Build(plan.Plan,
                path => new ThnScript(plan.Scripts.TryGetValue(path, out var bytes) ? bytes : items.VFS.ReadAllBytes(path),
                    items.ThornReadCallback, path),
                plan.Data, session.Resources, window, viewport));
            target = LastStep(time);
            clock.Seek(target);
        }

        public Task<CutsceneShotDone> Result => done.Task;

        /// <summary>Notes a cancel of the call. It can run on any thread.</summary>
        public void Cancel() => cancelled = true;

        /// <summary>Steps the player within a budget. Gives true when the image ended: rendered, refused or
        /// cancelled.</summary>
        public bool Frame(TimeSpan budget)
        {
            if (cancelled)
            {
                done.TrySetCanceled();
                return true;
            }

            if (!ReferenceEquals(window.Session, session) || !ReferenceEquals(session.GameData, plan.Data))
            {
                Fail(EditErrorKind.Refused, "DataReloaded", AgentTexts.Error("render_data_changed"));
                return true;
            }

            // A build and a step load models with the resources of the session, so they wait for a reload.
            if (session.IsBusy)
                return false;
            clock.Frame(0, budget);
            if (clock.Problem is { } problem)
            {
                Fail(EditErrorKind.Refused, "PreviewFailed", AgentTexts.Error("render_preview_failed",
                    ("time", Seconds(clock.ProblemTime)), ("reason", ErrorText.Of(problem))));
                return true;
            }

            if (clock is not { CatchingUp: false, Visible: CutscenePlayer player } || Math.Abs(player.Time - target) > 1e-6)
                return false;
            Render(player);
            return true;
        }

        public void Fail(EditErrorKind kind, string detail, string message) =>
            done.TrySetException(new EditException(new EditError(kind, detail, message)));

        public void Dispose() => clock.Dispose();

        /// <summary>Renders the player through the camera of the call into a texture and starts the read-back.</summary>
        private void Render(CutscenePlayer player)
        {
            if (camera != null)
            {
                if (player.Find(camera) is not { } entity)
                {
                    Fail(EditErrorKind.NotFound, "CameraNotFound", AgentTexts.Error("render_camera_none", ("camera", camera)));
                    return;
                }

                if (entity.Type != EntityTypes.Camera || !player.UseCamera(camera))
                {
                    Fail(EditErrorKind.InvalidValue, "NotACamera", AgentTexts.Error("render_camera_type", ("camera", camera)));
                    return;
                }
            }
            else if (player.CameraName == null)
            {
                Fail(EditErrorKind.Refused, "NoCamera", AgentTexts.Error("render_no_camera", ("time", Seconds(player.Time))));
                return;
            }

            var context = window.RenderContext;
            var restoreTarget = context.RenderTarget;
            var clearColor = context.ClearColor;
            var viewport = new Rectangle(0, 0, width, height);
            var texture = new RenderTarget2D(context, width, height);
            var drawn = false;
            context.RenderTarget = texture;
            context.PushViewport(0, 0, width, height);
            context.PushScissor(viewport, false);
            try
            {
                context.Cull = true;
                context.DepthEnabled = true;
                // The image is the screen of the game, so the camera takes the ratio of the image.
                player.Draw(CutsceneClock.StepSeconds, viewport, width / (float)height, null);
                drawn = true;
            }
            catch (Exception e)
            {
                Fail(EditErrorKind.Refused, "DrawFailed", AgentTexts.Error("render_draw_failed",
                    ("time", Seconds(player.Time)), ("reason", ErrorText.Of(e))));
            }
            finally
            {
                context.PopViewport();
                context.PopScissor();
                context.ClearColor = clearColor;
                context.DepthEnabled = false;
                context.BlendMode = BlendMode.Normal;
                context.Cull = false;
                context.RenderTarget = restoreTarget;
                texture.Dispose(keepTexture: drawn);
            }

            if (drawn)
                _ = Finish(ReadBack(texture.Texture, width, height), player.Time, player.CameraName!);
        }

        private async Task Finish(Task<ToolImage> image, double time, string name)
        {
            try
            {
                done.TrySetResult(new CutsceneShotDone(time, name, await image.ConfigureAwait(false)));
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }

        private static string Seconds(double time) => time.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

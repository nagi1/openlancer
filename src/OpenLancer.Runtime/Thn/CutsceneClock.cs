using System;
using System.Diagnostics;

namespace OpenLancer.Runtime.Thn;

/// <summary>
/// The fixed clock of the cutscene preview (sections 8.3, 8.4 and 8.6 of the M8 design). It steps a player at exactly
/// <see cref="StepSeconds"/>, so a play to a time and a seek to that time give the same state, bit for bit.
/// </summary>
/// <remarks>
/// <para>The engine state depends on the step pattern. An event runs in the first step at or after its time
/// (ThnScriptInstance.cs:325), and the processors integrate with the step. So the clock never steps with the wall
/// time.</para>
/// <para>The clock holds up to two players. The view draws the visible player. Play and a seek forward step the visible
/// player. A seek back, an edit and a data reload build a new player, which catches up to the target without a draw. The
/// visible player stays on screen until the new player reaches the target. Then the clock swaps them and disposes the
/// old one. A player cannot take an edit, because its event queue is filled once (ThnScriptInstance.cs:26,
/// :47-50).</para>
/// <para>A step that fails stops its player. The steps are deterministic, so each player of the same cutscene fails in
/// that step. So until the next <see cref="Invalidate"/>, no player runs that step again: the clock builds only for a
/// target before it, stops a catch-up to a target at or after it, and steps the visible player up to the step before
/// it. A build that fails stops all builds and steps until the next <see cref="Invalidate"/>. <see cref="Problem"/> and
/// <see cref="ProblemTime"/> keep the failure until then.</para>
/// <para>All work runs in <see cref="Frame"/>, on the UI thread, within the time budget of the frame. A player shares
/// the resource manager and the game data with the editor, and neither is safe for threads.</para>
/// </remarks>
public sealed class CutsceneClock : IDisposable
{
    private const int StepsPerSecond = 60;

    public const double StepSeconds = 1.0 / StepsPerSecond;

    /// <summary>A time less than this part of a step below a step boundary gets that step. A sum of frame times can be
    /// a little below the exact boundary.</summary>
    private const double StepTolerance = 1e-6;

    /// <summary>A gesture builds when no change came for this time, also before it ends.</summary>
    public static readonly TimeSpan GestureQuietTime = TimeSpan.FromMilliseconds(150);

    private readonly Func<ICutscenePlayer> build;
    private readonly Func<TimeSpan> now;

    private ICutscenePlayer? visible;
    private long visibleSteps;

    // An edit, a context change or a data reload came after the build of the visible player, or a step of it failed.
    private bool visibleStale;

    // The player that catches up to the target. An invalidate disposes it, so it is never stale.
    private ICutscenePlayer? next;
    private long nextSteps;

    // The time of the last change of a gesture, or null. The builds wait until the quiet time passes after it.
    private TimeSpan? gestureChange;

    // The steps that a player of the current cutscene runs without a failure: the steps before the step that failed.
    // It is -1 after a failed build, and long.MaxValue without a failure.
    private long stepLimit = long.MaxValue;

    private double? speed;
    private bool disposed;

    /// <param name="build">Builds a player at script time 0 from the current cutscene, context and game data.</param>
    public CutsceneClock(Func<ICutscenePlayer> build) : this(build, Elapsed())
    {
    }

    /// <param name="now">Gives the time that the budget of a frame and the quiet time of a gesture measure. The tests
    /// give a fake time.</param>
    internal CutsceneClock(Func<ICutscenePlayer> build, Func<TimeSpan> now)
    {
        this.build = build;
        this.now = now;
    }

    /// <summary>The player that the view draws, or null before the first build.</summary>
    public ICutscenePlayer? Visible => visible;

    /// <summary>The playhead in seconds. The view shows the last step at or before it.</summary>
    public double Target { get; private set; }

    /// <summary>The playhead moves with the wall time.</summary>
    public bool Playing => speed != null;

    /// <summary>A new player catches up to the target, or the next frame builds one. The view shows the visible player
    /// until the new player arrives.</summary>
    public bool CatchingUp => next != null || BuildDue(StepOf(Target), now());

    /// <summary>The time of the player that catches up, for the progress bar on the ruler. Without a catch-up, it is
    /// the time of the visible player.</summary>
    public double CatchUpTime =>
        Seconds(next != null ? nextSteps : BuildDue(StepOf(Target), now()) ? 0 : visibleSteps);

    /// <summary>
    /// The script time that the visible player advanced in the last <see cref="Frame"/>: its steps times
    /// <see cref="StepSeconds"/>. The view passes it to <c>Cutscene.Draw</c>, which advances the particle effects with it
    /// (Cutscene.cs:443-446). It is 0 for a paused frame and for each frame of a catch-up.
    /// </summary>
    public double DrawSeconds { get; private set; }

    /// <summary>The exception of the last build or step that failed, or null. The view keeps the last frame. The next
    /// <see cref="Invalidate"/> clears it. The clock keeps the same exception object until then, so the view can report
    /// each new problem one time.</summary>
    public Exception? Problem { get; private set; }

    /// <summary>The script time of the work that failed, in seconds: 0 for a build, and the end time of the step for a
    /// step. It is 0 without a problem.</summary>
    public double ProblemTime { get; private set; }

    /// <summary>Moves the playhead. A time below 0 moves it to 0. The next frames do the work.</summary>
    public void Seek(double time)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(time))
            throw new ArgumentOutOfRangeException(nameof(time), time, "The time must be a finite number.");
        Target = Math.Max(0, time);
    }

    /// <summary>
    /// Tells the clock that the cutscene changed: after an edit, an undo or a redo, the cancel of a gesture, a context
    /// change or a data reload. The view keeps the last frame until a new player reaches the target.
    /// </summary>
    /// <param name="gesture">The change is one step of a gesture, for example a drag. Then the clock builds when
    /// <see cref="GestureQuietTime"/> passes without a change, or at <see cref="EndGesture"/>. A build for each step
    /// would never finish, because each new build starts the catch-up again.</param>
    public void Invalidate(bool gesture = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Discard(ref next);
        visibleStale = visible != null;
        Problem = null;
        ProblemTime = 0;
        stepLimit = long.MaxValue;
        gestureChange = gesture ? now() : null;
    }

    /// <summary>
    /// Tells the clock that a gesture ended and kept its last change, for example at the commit of a drag. The clock
    /// builds in the next frame, unless the quiet time already built that change. A cancel changes the cutscene back,
    /// so it calls <see cref="Invalidate"/>.
    /// </summary>
    public void EndGesture()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        gestureChange = null;
    }

    /// <summary>Moves the playhead with the wall time times the speed.</summary>
    public void Play(double speed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(speed) || speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "The speed must be a finite number more than 0.");
        this.speed = speed;
    }

    public void Pause() => speed = null;

    /// <summary>
    /// Does the work of one editor frame: it moves the playhead when the clock plays, builds a player when one is due,
    /// steps a player toward the target, and swaps the players at the target.
    /// </summary>
    /// <param name="wallSeconds">The wall time since the last frame.</param>
    /// <param name="budget">The time that the builds and the steps of this frame can use. A frame always runs one step
    /// when it builds nothing, so a step that costs more than the budget still advances.</param>
    public void Frame(double wallSeconds, TimeSpan budget)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(wallSeconds) || wallSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(wallSeconds), wallSeconds,
                "The wall time must be a finite number that is not negative.");
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "The budget must be more than 0.");

        DrawSeconds = 0;
        if (speed is { } s)
            Target += wallSeconds * s;
        var start = now();
        var target = StepOf(Target);

        // A catch-up past the target starts again, and a catch-up to a target at or after the failed step stops. When
        // the visible player reaches the target, the catch-up is behind it, because the visible player does not step
        // during a catch-up. Then the catch-up stops too.
        if (next != null && (nextSteps > target || target > stepLimit || VisibleReaches(target)))
            Discard(ref next);

        if (BuildDue(target, start))
        {
            try
            {
                next = build();
                nextSteps = 0;
            }
            catch (Exception e)
            {
                // The engine message goes to the view. The last frame stays.
                Problem = e;
                ProblemTime = 0;
                stepLimit = -1;
                return;
            }
        }

        if (next != null)
        {
            if (!Run(next, ref nextSteps, target, start, budget))
                Discard(ref next);
            else if (nextSteps == target)
                Swap();
            return;
        }

        // A gesture, a failure or a stale visible player keeps the last frame.
        if (!VisibleReaches(target))
            return;
        var before = visibleSteps;
        if (!Run(visible!, ref visibleSteps, Math.Min(target, stepLimit), start, budget))
            visibleStale = true;
        DrawSeconds = Seconds(visibleSteps - before);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Discard(ref next);
        Discard(ref visible);
    }

    /// <summary>The last step at or before a time.</summary>
    private static long StepOf(double time) => (long)Math.Floor(time * StepsPerSecond + StepTolerance);

    private static double Seconds(long steps) => steps / (double)StepsPerSecond;

    private static Func<TimeSpan> Elapsed()
    {
        var watch = Stopwatch.StartNew();
        return () => watch.Elapsed;
    }

    private static void Discard(ref ICutscenePlayer? player)
    {
        player?.Dispose();
        player = null;
    }

    /// <summary>The visible player is up to date and not past the target, so steps can take it there.</summary>
    private bool VisibleReaches(long target) => visible != null && !visibleStale && visibleSteps <= target;

    /// <summary>No player catches up, the visible player cannot reach the target, no failed step blocks the target, and
    /// no gesture changed the cutscene in the quiet time before <paramref name="time"/>.</summary>
    private bool BuildDue(long target, TimeSpan time) =>
        next == null && !VisibleReaches(target) && target <= stepLimit &&
        !(gestureChange is { } change && time - change < GestureQuietTime);

    /// <summary>Steps a player toward the target until the budget is used. Gives false when a step fails.</summary>
    private bool Run(ICutscenePlayer player, ref long steps, long target, TimeSpan start, TimeSpan budget)
    {
        try
        {
            while (steps < target && now() - start < budget)
            {
                player.Step(StepSeconds);
                steps++;
            }
            return true;
        }
        catch (Exception e)
        {
            Problem = e;
            ProblemTime = Seconds(steps + 1);
            stepLimit = steps;
            return false;
        }
    }

    private void Swap()
    {
        visible?.Dispose();
        visible = next;
        visibleSteps = nextSteps;
        visibleStale = false;
        next = null;
    }
}

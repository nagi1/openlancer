using System;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The Preview controls over the fixed clock (section 7.7 of the M8 design): play with a speed, pause, stop, a step of
/// one frame or one second, a scrub, a loop of the whole script or of a range, and the pre-roll. The playhead stays
/// between 0 and the end of the script, so a play never passes the end and then seeks back, which would build again.
/// </summary>
/// <remarks>
/// <para>A play from a scrubbed time starts <see cref="PreRollSeconds"/> before the playhead and plays that part at
/// <see cref="PreRollSpeed"/>. The particle effects advance only in drawn frames (section 8.6), so the pre-roll shows
/// the effects that started before the playhead as the game shows them. The pre-roll ends exactly at the playhead, and
/// the play goes on at the speed of the controls. A jump to the start of a loop range is a scrub too.</para>
/// <para>A play holds the playhead while a new player builds or catches up. Else the playhead moves on during that
/// work, and the old player goes on before the new player arrives. Then a pre-roll draws nothing, and a loop skips the
/// start of the script.</para>
/// </remarks>
internal sealed class PreviewTransport(CutsceneClock clock, Func<double> end)
{
    public const double PreRollSeconds = 3;
    public const double PreRollSpeed = 4;

    /// <summary>The speeds of the speed menu.</summary>
    public static readonly double[] Speeds = [0.25, 0.5, 1, 2];

    // The playhead that the pre-roll plays up to, or null when no pre-roll plays.
    private double? preRollTarget;

    // The playhead moved since the last play, so the next play starts with a pre-roll.
    private bool scrubbed;

    // The last frame started while a new player built or caught up. The wall time of this frame is the time of that
    // work.
    private bool waited;

    private double speed = 1;

    private (double Start, double End)? loopRange;

    /// <summary>The speed of a play, from 0.25 to 2.</summary>
    public double Speed
    {
        get => speed;
        set
        {
            speed = Math.Clamp(value, Speeds[0], Speeds[^1]);
            if (clock.Playing && preRollTarget == null)
            {
                clock.Play(speed);
            }
        }
    }

    /// <summary>A play that reaches the end of the script, or the end of <see cref="LoopRange"/>, goes on from the
    /// start of the range.</summary>
    public bool Loop { get; set; }

    /// <summary>
    /// The range that <see cref="Loop"/> plays, or null for the whole script. A new range takes its two times in any
    /// order, between 0 and the end of the script. A range shorter than one step of the clock clears the range.
    /// </summary>
    public (double Start, double End)? LoopRange
    {
        get => loopRange;
        set
        {
            loopRange = null;
            if (value is { } range)
            {
                var start = Math.Clamp(Math.Min(range.Start, range.End), 0, End);
                var stop = Math.Clamp(Math.Max(range.Start, range.End), 0, End);
                loopRange = stop - start >= CutsceneClock.StepSeconds ? (start, stop) : null;
            }
        }
    }

    /// <summary>A play from a scrubbed time starts with a pre-roll.</summary>
    public bool PreRoll { get; set; } = true;

    public bool Playing => clock.Playing;

    /// <summary>True while a pre-roll plays.</summary>
    public bool InPreRoll => preRollTarget != null;

    /// <summary>The playhead.</summary>
    public double Time => clock.Target;

    /// <summary>The end of the script: its duration, at least 0.</summary>
    public double End => Math.Max(0, end());

    /// <summary>
    /// The part of the script that a play goes through again and again: the loop range while <see cref="Loop"/> is on,
    /// else the whole script. The end of the script can move after the range was set, so the range stays inside it.
    /// </summary>
    private (double Start, double End) Looped =>
        Loop && loopRange is { } range && Math.Min(range.End, End) - range.Start >= CutsceneClock.StepSeconds
            ? (range.Start, Math.Min(range.End, End))
            : (0, End);

    /// <summary>
    /// Plays from the playhead. At the end, the play starts at 0. With <see cref="Loop"/> and a loop range, a playhead
    /// outside the range goes to the start of the range. After a scrub, the play starts with a pre-roll when
    /// <see cref="PreRoll"/> is on.
    /// </summary>
    public void Play()
    {
        if (clock.Playing)
        {
            return;
        }

        var (start, stop) = Looped;
        if (clock.Target < start || clock.Target >= stop)
        {
            clock.Seek(start);
            scrubbed = true;
        }

        PlayFromPlayhead();
    }

    /// <summary>Plays from the playhead, after a scrub with a pre-roll when <see cref="PreRoll"/> is on and the
    /// playhead is after 0.</summary>
    private void PlayFromPlayhead()
    {
        if (PreRoll && scrubbed && clock.Target > 0)
        {
            preRollTarget = clock.Target;
            clock.Seek(Math.Max(0, clock.Target - PreRollSeconds));
            clock.Play(PreRollSpeed);
        }
        else
        {
            clock.Play(speed);
        }

        scrubbed = false;
    }

    /// <summary>Pauses. A pause in the pre-roll goes to the playhead of the pre-roll.</summary>
    public void Pause()
    {
        clock.Pause();
        if (preRollTarget is { } target)
        {
            preRollTarget = null;
            clock.Seek(target);
            scrubbed = true;
        }
    }

    public void TogglePlay()
    {
        if (clock.Playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Pauses and goes to 0.</summary>
    public void Stop()
    {
        Pause();
        Seek(0);
    }

    /// <summary>
    /// Moves the playhead, between 0 and the end. A play goes on from the new time without a pre-roll, and a pre-roll
    /// stops.
    /// </summary>
    public void Seek(double time)
    {
        if (preRollTarget != null)
        {
            preRollTarget = null;
            clock.Play(speed);
        }

        clock.Seek(Math.Clamp(time, 0, End));
        scrubbed = !clock.Playing;
    }

    /// <summary>Pauses and moves the playhead by whole frames of the clock.</summary>
    public void StepFrames(int frames)
    {
        Pause();
        var step = Math.Floor(clock.Target / CutsceneClock.StepSeconds + 1e-6);
        Seek((step + frames) * CutsceneClock.StepSeconds);
    }

    /// <summary>Pauses and moves the playhead by a time.</summary>
    public void StepSeconds(double seconds)
    {
        Pause();
        Seek(clock.Target + seconds);
    }

    /// <summary>
    /// Runs the clock for one editor frame. A play stops at the end. With <see cref="Loop"/>, a play that reaches the
    /// end of the loop range, or the end of the script, goes on from the start of the range or from 0. A pre-roll ends
    /// exactly at its playhead. While a new player builds or catches up, and in the next frame, a play holds the
    /// playhead. So the play goes on from the frame that the new player shows.
    /// </summary>
    public void Frame(double wallSeconds, TimeSpan budget)
    {
        var waiting = clock.CatchingUp;
        if (waiting || waited)
        {
            wallSeconds = 0;
        }

        waited = waiting;
        var (start, loopEnd) = Looped;
        // A playhead after the loop range, after a seek in the play, plays to the end of the script first.
        var stop = preRollTarget ?? (clock.Target < loopEnd ? loopEnd : End);
        var rate = preRollTarget != null ? PreRollSpeed : speed;
        if (clock.Playing)
        {
            wallSeconds = Math.Clamp((stop - clock.Target) / rate, 0, wallSeconds);
        }

        clock.Frame(wallSeconds, budget);
        if (!clock.Playing || clock.Target < stop - 1e-9)
        {
            return;
        }

        if (preRollTarget is { } target)
        {
            // The sum of the frame times can differ from the playhead in the last bits. Both give the same step.
            preRollTarget = null;
            clock.Seek(target);
            clock.Play(speed);
        }
        else if (Loop && End > 0)
        {
            clock.Seek(start);
            scrubbed = true;
            PlayFromPlayhead();
        }
        else
        {
            clock.Pause();
            clock.Seek(End);
        }
    }
}

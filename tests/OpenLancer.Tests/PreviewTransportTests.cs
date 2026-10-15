using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The Preview controls over the fixed clock (section 7.7 of the M8 design) with the fake players of the clock tests:
/// the end of a play, the loop, the pre-roll, the steps and the scrub limits.
/// </summary>
public sealed class PreviewTransportTests
{
    private const double Frame60 = 1.0 / 60;

    /// <summary>The time that an editor frame uses after the clock, for example to draw.</summary>
    private const double DrawTime = 0.005;

    private static readonly TimeSpan NoLimit = TimeSpan.FromHours(1);

    private readonly CutsceneFakes fakes = new();
    private double end = 1;

    // The wall time of the next editor frame of RunFrames.
    private double wall = Frame60;

    /// <summary>A play stops exactly at the end, without a step past it. Frames of 30 ms pass 1 s at 1.02 s, one step
    /// after the end, so a play without the limit would seek back and build again.</summary>
    [Fact]
    public void APlayStopsExactlyAtTheEnd()
    {
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end);

        transport.Play();
        for (var i = 0; i < 100; i++)
            transport.Frame(0.030, NoLimit);

        Assert.False(transport.Playing);
        Assert.Equal(1, transport.Time);
        var player = Assert.Single(fakes.Built);
        Assert.Equal(60, player.Steps.Count);
        Assert.Equal(1, player.Time, 9);
    }

    /// <summary>With the loop, a play that reaches the end goes on from 0 with a new player. The frame of the build and
    /// the frame after it hold the playhead. So the 70 frames give 60 steps, then two held frames, then 8
    /// steps.</summary>
    [Fact]
    public void ALoopPlaysAgainFromTheStart()
    {
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end) { Loop = true };
        Settle(clock);

        transport.Play();
        for (var i = 0; i < 70; i++)
            transport.Frame(Frame60, NoLimit);

        Assert.True(transport.Playing);
        Assert.Equal(2, fakes.Built.Count);
        Assert.Equal(60, fakes.Built[0].Steps.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.Equal(8, fakes.Built[1].Steps.Count);
        Assert.Equal(8 * Frame60, transport.Time, 9);
    }

    /// <summary>
    /// A loop with the build cost of s002xe (590 ms) starts again at the first step and draws each step: the frame
    /// after the slow build holds the playhead, so the play does not jump by the time of the build. A play from the end
    /// starts at 0 in the same way.
    /// </summary>
    [Fact]
    public void ALoopWithASlowBuildDrawsEachStepFromTheStart()
    {
        fakes.BuildCost = TimeSpan.FromMilliseconds(590);
        fakes.StepCost = TimeSpan.FromMilliseconds(0.63);
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end) { Loop = true };

        transport.Play();
        var draws = new List<double>();
        RunFrames(transport, TimeSpan.FromMilliseconds(40),
            () => fakes.Built.Count < 2 || fakes.Built[1].Steps.Count < 30,
            () => draws.Add(Math.Round(clock.DrawSeconds / Frame60)));

        Assert.Equal(60, fakes.Built[0].Steps.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.All(draws, d => Assert.InRange(d, 0, 1));
        Assert.Equal(90, draws.Sum());
    }

    /// <summary>
    /// A loop range takes its two times in any order and stays inside the script. A range shorter than one step of the
    /// clock clears the range. When the end of the script moves before the range, the loop plays the whole script.
    /// </summary>
    [Fact]
    public void TheLoopRangeStaysInsideTheScript()
    {
        end = 10;
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end) { Loop = true, PreRoll = false };

        transport.LoopRange = (8, 2);
        Assert.Equal((2.0, 8.0), transport.LoopRange);
        transport.LoopRange = (-1, 20);
        Assert.Equal((0.0, 10.0), transport.LoopRange);
        transport.LoopRange = (5, 5 + Frame60 / 2);
        Assert.Null(transport.LoopRange);

        transport.LoopRange = (8, 9);
        end = 5;
        transport.Seek(5);
        transport.Play();
        Assert.Equal(0, transport.Time);
    }

    /// <summary>
    /// With the loop and a loop range, a play from a playhead before the range starts at the start of the range, and a
    /// play that reaches the end of the range goes on from its start with a new player. The playhead never leaves the
    /// range. Without the loop, the play goes through the range to the end of the script.
    /// </summary>
    [Fact]
    public void ALoopWithARangePlaysOnlyTheRange()
    {
        end = 4;
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end) { Loop = true, PreRoll = false, LoopRange = (2, 3) };

        transport.Play();
        Assert.Equal(2, transport.Time);
        for (var frames = 0; fakes.Built.Count < 3; frames++)
        {
            Assert.True(frames < 10_000, "The loop did not start again.");
            transport.Frame(Frame60, NoLimit);
            Assert.InRange(transport.Time, 2, 3);
        }

        // The first player caught up to 2 s and played to 3 s.
        Assert.Equal(180, fakes.Built[0].Steps.Count);
        Assert.True(transport.Playing);

        transport.Loop = false;
        var afterRange = false;
        for (var i = 0; i < 200 && transport.Playing; i++)
        {
            transport.Frame(Frame60, NoLimit);
            afterRange |= transport.Playing && transport.Time > 3.5;
        }

        Assert.True(afterRange);
        Assert.False(transport.Playing);
        Assert.Equal(4, transport.Time);
    }

    /// <summary>A playhead inside the loop range plays from where it is. Each jump to the start of the range, at a
    /// play from outside the range and at each loop, starts with a pre-roll when the pre-roll is on.</summary>
    [Fact]
    public void AJumpToTheStartOfTheLoopRangeStartsWithAPreRoll()
    {
        end = 10;
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end) { Loop = true, PreRoll = false, LoopRange = (5, 6) };
        transport.Play();
        transport.Pause();
        Assert.Equal(5, transport.Time);
        transport.PreRoll = true;

        // Inside the range, without a scrub: no pre-roll.
        transport.Play();
        Assert.False(transport.InPreRoll);
        transport.Pause();
        transport.Seek(1);

        transport.Play();
        Assert.True(transport.InPreRoll);
        Assert.Equal(2, transport.Time);
        RunFrames(transport, NoLimit, () => transport.InPreRoll);
        Assert.Equal(5, transport.Time);

        RunFrames(transport, NoLimit, () => !transport.InPreRoll);
        Assert.Equal(2, transport.Time);
        RunFrames(transport, NoLimit, () => transport.InPreRoll);
        Assert.Equal(5, transport.Time);
        Assert.True(transport.Playing);
    }

    /// <summary>
    /// A play from a scrubbed time starts 3 s before the playhead at 4 times the speed. The frame of the build and the
    /// frame after it hold the playhead. Then each frame of 1/60 s draws 4 steps. The pre-roll ends exactly at the
    /// playhead, and the play goes on at the speed of the controls.
    /// </summary>
    [Fact]
    public void APlayAfterAScrubStartsWithAPreRollThatEndsAtThePlayhead()
    {
        end = 10;
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end) { Speed = 0.5 };
        transport.Seek(5);
        Settle(clock);

        transport.Play();

        Assert.True(transport.InPreRoll);
        Assert.Equal(2, transport.Time);
        var draws = new List<double>();
        while (transport.InPreRoll)
        {
            transport.Frame(Frame60, NoLimit);
            draws.Add(Math.Round(clock.DrawSeconds / Frame60, 9));
        }

        Assert.Equal(5, transport.Time);
        Assert.Equal(2, fakes.Built.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.Equal(300, fakes.Built[1].Steps.Count);
        Assert.Equal([0, 0, .. Enumerable.Repeat(4.0, 45)], draws);
        Assert.True(transport.Playing);
        transport.Frame(2 * Frame60, NoLimit);
        Assert.Equal(1, Math.Round(clock.DrawSeconds / Frame60));
    }

    /// <summary>
    /// With the costs of the bench (a build of 590 ms and steps of 0.63 ms for s002xe), and frames that last as long as
    /// their work, the pre-roll draws its 180 steps on the new player and ends exactly at the playhead. The play holds
    /// the playhead while the new player builds and catches up, so the old player does not go on.
    /// </summary>
    [Theory]
    [InlineData(40, 5.0)]
    [InlineData(40, 13.0)]
    [InlineData(12, 13.0)]
    [InlineData(12, 60.0)]
    public void APreRollWithTheCostsOfTheBenchDrawsItsThreeSeconds(int budgetMs, double playhead)
    {
        end = 92.75;
        fakes.BuildCost = TimeSpan.FromMilliseconds(590);
        fakes.StepCost = TimeSpan.FromMilliseconds(0.63);
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end);
        var budget = TimeSpan.FromMilliseconds(budgetMs);
        transport.Seek(playhead);
        RunFrames(transport, budget, () => clock.Visible == null || clock.CatchingUp);
        var old = Assert.Single(fakes.Built);

        transport.Play();
        var drawn = 0.0;
        RunFrames(transport, budget, () => transport.InPreRoll, () => drawn += clock.DrawSeconds);

        Assert.Equal(playhead, transport.Time);
        Assert.True(transport.Playing);
        Assert.Equal(2, fakes.Built.Count);
        Assert.True(old.Disposed);
        Assert.Equal(playhead * 60, old.Steps.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.Equal(playhead * 60, fakes.Built[1].Steps.Count);
        Assert.Equal(PreviewTransport.PreRollSeconds, drawn, 9);
    }

    /// <summary>A pause in the pre-roll goes to the playhead of the pre-roll, and the next play starts a pre-roll
    /// again.</summary>
    [Fact]
    public void APauseInThePreRollGoesToItsPlayhead()
    {
        end = 10;
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end);
        transport.Seek(5);
        transport.Play();
        transport.Frame(Frame60, NoLimit);

        transport.Pause();

        Assert.False(transport.Playing);
        Assert.False(transport.InPreRoll);
        Assert.Equal(5, transport.Time);
        transport.Play();
        Assert.True(transport.InPreRoll);
    }

    /// <summary>A play from the start, a play after a pause, and a play with the pre-roll off start at the
    /// playhead.</summary>
    [Fact]
    public void APlayWithoutAScrubHasNoPreRoll()
    {
        end = 10;
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end);

        transport.Play();
        Assert.False(transport.InPreRoll);
        for (var i = 0; i < 30; i++)
            transport.Frame(Frame60, NoLimit);
        transport.Pause();
        transport.Play();
        Assert.False(transport.InPreRoll);

        transport.Pause();
        transport.PreRoll = false;
        transport.Seek(6);
        transport.Play();
        Assert.False(transport.InPreRoll);
        Assert.Equal(6, transport.Time);
    }

    /// <summary>A step moves the playhead by whole frames or by seconds and pauses. A seek stays between 0 and the end,
    /// and Stop goes to 0. Play at the end starts from 0.</summary>
    [Fact]
    public void StepsAndSeeksStayInTheScript()
    {
        end = 2;
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => end);

        transport.StepFrames(1);
        Assert.Equal(Frame60, transport.Time, 12);
        transport.StepFrames(-2);
        Assert.Equal(0, transport.Time);
        transport.StepSeconds(1.5);
        Assert.Equal(1.5, transport.Time);
        transport.Seek(99);
        Assert.Equal(2, transport.Time);
        transport.Seek(-1);
        Assert.Equal(0, transport.Time);

        transport.Seek(2);
        transport.Play();
        Assert.Equal(0, transport.Time);
        Assert.True(transport.Playing);
        transport.Stop();
        Assert.False(transport.Playing);
        Assert.Equal(0, transport.Time);
    }

    /// <summary>The speed stays between 0.25 and 2, and a new speed applies to a play at once.</summary>
    [Fact]
    public void TheSpeedStaysInItsRange()
    {
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => 100);
        Settle(clock);

        transport.Speed = 8;
        Assert.Equal(2, transport.Speed);
        transport.Speed = 0.01;
        Assert.Equal(0.25, transport.Speed);

        transport.Play();
        transport.Speed = 2;
        transport.Frame(Frame60, NoLimit);
        Assert.Equal(2 * Frame60, transport.Time, 12);
    }

    /// <summary>Runs frames without wall time until the clock shows the target.</summary>
    private static void Settle(CutsceneClock clock)
    {
        do
            clock.Frame(0, NoLimit);
        while (clock.CatchingUp || clock.DrawSeconds > 0);
    }

    /// <summary>
    /// Runs editor frames while a condition holds, as the editor runs them: a frame lasts as long as the work of the
    /// clock plus <see cref="DrawTime"/>, and at least 1/60 s. The next frame gets that time as its wall time.
    /// </summary>
    /// <param name="afterFrame">Runs after the transport did the work of each frame.</param>
    private void RunFrames(PreviewTransport transport, TimeSpan budget, Func<bool> condition, Action? afterFrame = null)
    {
        for (var frames = 0; condition(); frames++)
        {
            Assert.True(frames < 100_000, "The frames did not end.");
            var start = fakes.Now;
            transport.Frame(wall, budget);
            afterFrame?.Invoke();
            var work = (fakes.Now - start).TotalSeconds;
            var frame = Math.Max(Frame60, work + DrawTime);
            fakes.Spend(TimeSpan.FromSeconds(frame - work));
            wall = frame;
        }
    }
}

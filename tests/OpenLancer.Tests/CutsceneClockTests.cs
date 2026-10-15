using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The fixed clock of the cutscene preview (section 8.4 of the M8 design, test 13.1 item 11) with a fake player that
/// records its calls. A fake time moves 1 ms for each step, so a budget of 12 ms gives 12 steps in a frame. The fake time
/// moves only with the steps, the builds and <see cref="CutsceneFakes.Spend"/>. So a frame takes no time when a test does not
/// spend it.
/// </summary>
public sealed class CutsceneClockTests
{
    private const double Frame60 = 1.0 / 60;

    private static readonly TimeSpan Budget12 = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan Budget40 = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan NoLimit = TimeSpan.FromHours(1);

    private readonly CutsceneFakes fakes = new();

    /// <summary>Play adds the wall time to the playhead and runs whole steps of 1/60 s. Frames of 10 ms give 0.6 steps
    /// each, so the steps come in the pattern 0, 1, 0, 1, 1. A speed of 0.5 gives half the steps.</summary>
    [Theory]
    [InlineData(1.0, 60)]
    [InlineData(0.5, 30)]
    public void PlayRunsFixedStepsFromTheWallTime(double speed, int steps)
    {
        using var clock = fakes.Clock();
        clock.Play(speed);

        var draws = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            clock.Frame(0.010, Budget40);
            draws.Add(Math.Round(clock.DrawSeconds * 60));
        }

        var player = Assert.Single(fakes.Built);
        Assert.Same(player, clock.Visible);
        Assert.Equal(steps, player.Steps.Count);
        Assert.All(player.Steps, s => Assert.Equal(CutsceneClock.StepSeconds, s));
        Assert.Equal(steps, draws.Sum(), 9);
        if (speed == 1)
            Assert.Equal(new double[] { 0, 1, 0, 1, 1, 0, 1, 0, 1, 1 }, draws.Take(10));
    }

    /// <summary>A seek forward steps the visible player and draws its steps. It builds nothing.</summary>
    [Fact]
    public void ASeekForwardStepsTheVisiblePlayerWithoutABuild()
    {
        using var clock = fakes.Clock();
        Settle(clock);
        var player = Assert.Single(fakes.Built);

        clock.Seek(2);
        var draws = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            clock.Frame(Frame60, Budget40);
            draws.Add(clock.DrawSeconds);
            Assert.False(clock.CatchingUp);
        }

        Assert.Same(player, Assert.Single(fakes.Built));
        Assert.Same(player, clock.Visible);
        Assert.Equal(120, player.Steps.Count);
        Assert.Equal(new[] { 40.0 / 60, 40.0 / 60, 40.0 / 60, 0 }, draws);
    }

    /// <summary>A seek back builds a new player. It catches up 12 steps in each frame of 12 ms and draws nothing. The
    /// view shows the old player until the new one reaches the target. Then the clock swaps them and disposes the old
    /// one. The target is 61 steps, so the fifth frame ends one step before it and must not swap.</summary>
    [Fact]
    public void ASeekBackCatchesUpWithinTheBudgetAndSwapsOnlyAtTheTarget()
    {
        using var clock = fakes.Clock();
        clock.Seek(2);
        Settle(clock);
        var old = Assert.Single(fakes.Built);

        clock.Seek(61 / 60.0);
        for (var frame = 1; frame <= 5; frame++)
        {
            clock.Frame(Frame60, Budget12);
            Assert.Same(old, clock.Visible);
            Assert.False(old.Disposed);
            Assert.True(clock.CatchingUp);
            Assert.Equal(12 * frame / 60.0, clock.CatchUpTime);
            Assert.Equal(0, clock.DrawSeconds);
        }
        Assert.Equal(60, fakes.Built[1].Steps.Count);

        clock.Frame(Frame60, Budget12);

        Assert.Equal(2, fakes.Built.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.Equal(61, fakes.Built[1].Steps.Count);
        Assert.True(old.Disposed);
        Assert.Equal(120, old.Steps.Count);
        Assert.False(clock.CatchingUp);
        Assert.Equal(0, clock.DrawSeconds);
    }

    /// <summary>The build and the steps of a frame share the budget. A build that uses the whole budget leaves the
    /// steps to the next frame.</summary>
    [Theory]
    [InlineData(12, 0, 12, 12)]
    [InlineData(40, 0, 40, 40)]
    [InlineData(40, 5, 35, 40)]
    [InlineData(12, 20, 0, 12)]
    public void TheBuildAndTheStepsOfAFrameShareTheBudget(int budgetMs, int buildMs, int firstFrame, int secondFrame)
    {
        fakes.BuildCost = TimeSpan.FromMilliseconds(buildMs);
        using var clock = fakes.Clock();
        clock.Seek(10);

        clock.Frame(Frame60, TimeSpan.FromMilliseconds(budgetMs));
        var player = Assert.Single(fakes.Built);
        Assert.Equal(firstFrame, player.Steps.Count);

        clock.Frame(Frame60, TimeSpan.FromMilliseconds(budgetMs));
        Assert.Equal(firstFrame + secondFrame, player.Steps.Count);
        Assert.Single(fakes.Built);
    }

    /// <summary>A step that costs more than the budget still advances one step in each frame.</summary>
    [Fact]
    public void AStepThatCostsMoreThanTheBudgetStillAdvances()
    {
        fakes.StepCost = TimeSpan.FromMilliseconds(30);
        using var clock = fakes.Clock();
        clock.Seek(1);

        clock.Frame(Frame60, Budget12);
        clock.Frame(Frame60, Budget12);

        Assert.Equal(2, Assert.Single(fakes.Built).Steps.Count);
    }

    /// <summary>A new target ahead of the catch-up continues it. A new target behind the catch-up starts it again.</summary>
    [Fact]
    public void ANewTargetAheadOfTheCatchUpContinuesItAndOneBehindStartsItAgain()
    {
        using var clock = fakes.Clock();
        clock.Seek(4);
        Settle(clock);

        clock.Seek(1);
        clock.Frame(Frame60, Budget12);
        Assert.Equal(12, fakes.Built[1].Steps.Count);

        clock.Seek(1.5);
        clock.Frame(Frame60, Budget12);
        Assert.Equal(2, fakes.Built.Count);
        Assert.Equal(24, fakes.Built[1].Steps.Count);

        clock.Seek(0.1);
        clock.Frame(Frame60, Budget12);
        Assert.Equal(3, fakes.Built.Count);
        Assert.True(fakes.Built[1].Disposed);
        Assert.Equal(24, fakes.Built[1].Steps.Count);
        Assert.Same(fakes.Built[2], clock.Visible);
        Assert.Equal(6, fakes.Built[2].Steps.Count);
        Assert.True(fakes.Built[0].Disposed);
    }

    /// <summary>A seek forward past the visible player during a catch-up stops the catch-up. The visible player is
    /// nearer to the target, so it takes the seek without a build.</summary>
    [Fact]
    public void ASeekPastTheVisiblePlayerDuringACatchUpStepsTheVisiblePlayer()
    {
        using var clock = fakes.Clock();
        clock.Seek(2);
        Settle(clock);
        clock.Seek(1);
        clock.Frame(Frame60, Budget12);

        clock.Seek(3);
        clock.Frame(Frame60, Budget12);

        Assert.Equal(2, fakes.Built.Count);
        Assert.True(fakes.Built[1].Disposed);
        Assert.Same(fakes.Built[0], clock.Visible);
        Assert.Equal(132, fakes.Built[0].Steps.Count);
        Assert.Equal(12.0 / 60, clock.DrawSeconds);
        Assert.False(clock.CatchingUp);
    }

    /// <summary>An edit rebuilds and catches up to the playhead, also while the clock plays. The out-of-date player
    /// stays on screen without a step until the new player reaches the moving target.</summary>
    [Fact]
    public void AnInvalidateRebuildsAndSwapsAtTheMovingTarget()
    {
        using var clock = fakes.Clock();
        clock.Seek(1);
        Settle(clock);
        var old = fakes.Built[0];
        clock.Play(1);

        clock.Invalidate();
        Assert.True(clock.CatchingUp);
        Assert.Equal(0, clock.CatchUpTime);
        var frames = 0;
        do
        {
            clock.Frame(Frame60, Budget12);
            frames++;
            Assert.Equal(0, clock.DrawSeconds);
        } while (clock.CatchingUp);

        // Frame n moves the target to 60 + n steps, and the catch-up reaches 12 n steps. They meet at n = 6.
        Assert.Equal(6, frames);
        Assert.Equal(60, old.Steps.Count);
        Assert.True(old.Disposed);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.Equal(66, fakes.Built[1].Steps.Count);

        clock.Frame(Frame60, Budget12);
        Assert.Equal(Frame60, clock.DrawSeconds);
        Assert.Equal(2, fakes.Built.Count);
    }

    /// <summary>An edit during a catch-up disposes the player that catches up at once, because it holds the cutscene
    /// before the edit. That player never shows. A new build catches up and swaps in at the target.</summary>
    [Fact]
    public void AnInvalidateDuringACatchUpDisposesItAndBuildsAgain()
    {
        using var clock = fakes.Clock();
        clock.Seek(2);
        Settle(clock);
        clock.Seek(1);
        clock.Frame(Frame60, Budget12);
        var before = fakes.Built[1];
        Assert.Equal(12, before.Steps.Count);

        clock.Invalidate();

        Assert.True(before.Disposed);
        Assert.True(clock.CatchingUp);
        Assert.Equal(0, clock.CatchUpTime);
        do
        {
            clock.Frame(Frame60, Budget12);
            Assert.NotSame(before, clock.Visible);
            Assert.Equal(0, clock.DrawSeconds);
        } while (clock.CatchingUp);

        Assert.Equal(3, fakes.Built.Count);
        Assert.Same(fakes.Built[2], clock.Visible);
        Assert.Equal(60, fakes.Built[2].Steps.Count);
        Assert.True(fakes.Built[0].Disposed);
        Assert.Equal(12, before.Steps.Count);
    }

    /// <summary>A gesture during a catch-up disposes the player that catches up at once. The clock then builds nothing
    /// and swaps nothing until the gesture ends.</summary>
    [Fact]
    public void AGestureDuringACatchUpDisposesItAndHoldsTheBuilds()
    {
        using var clock = fakes.Clock();
        clock.Seek(2);
        Settle(clock);
        clock.Seek(1);
        clock.Frame(Frame60, Budget12);
        var before = fakes.Built[1];

        clock.Invalidate(gesture: true);

        Assert.True(before.Disposed);
        for (var i = 0; i < 5; i++)
        {
            fakes.Spend(TimeSpan.FromSeconds(Frame60));
            clock.Frame(Frame60, Budget12);
            Assert.Same(fakes.Built[0], clock.Visible);
            Assert.False(clock.CatchingUp);
            Assert.Equal(0, clock.DrawSeconds);
        }
        Assert.Equal(2, fakes.Built.Count);

        clock.EndGesture();
        Settle(clock);

        Assert.Equal(3, fakes.Built.Count);
        Assert.Same(fakes.Built[2], clock.Visible);
        Assert.Equal(60, fakes.Built[2].Steps.Count);
        Assert.Equal(12, before.Steps.Count);
    }

    /// <summary>A drag sends an invalidate for each step. 100 steps give no build, and the view keeps its frame. The end
    /// of the gesture gives one build. A slow editor frame does not count as a time without a change, because the change
    /// comes at its end.</summary>
    [Theory]
    [InlineData(1000.0 / 60)]
    [InlineData(200.0)]
    public void OneHundredGestureInvalidatesGiveNoBuildAndTheEndGivesOne(double frameMs)
    {
        using var clock = fakes.Clock();
        clock.Seek(1);
        Settle(clock);
        var old = fakes.Built[0];

        for (var i = 0; i < 100; i++)
        {
            fakes.Spend(TimeSpan.FromMilliseconds(frameMs));
            clock.Invalidate(gesture: true);
            clock.Frame(frameMs / 1000, Budget12);
            Assert.Same(old, clock.Visible);
            Assert.False(clock.CatchingUp);
            Assert.Equal(0, clock.DrawSeconds);
        }
        Assert.Single(fakes.Built);
        Assert.Equal(60, old.Steps.Count);

        clock.EndGesture();
        Settle(clock);

        Assert.Equal(2, fakes.Built.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.Equal(60, fakes.Built[1].Steps.Count);
    }

    /// <summary>A gesture builds once when no change came for 150 ms of the clock time. A slow frame that ends with the
    /// change does not count, and 149 ms are not enough.</summary>
    [Fact]
    public void AGestureBuildsOnceWhenNoChangeCameFor150Ms()
    {
        using var clock = fakes.Clock();
        clock.Seek(1);
        Settle(clock);

        fakes.Spend(TimeSpan.FromMilliseconds(200));
        clock.Invalidate(gesture: true);
        clock.Frame(0.2, Budget12);
        Assert.Single(fakes.Built);
        Assert.False(clock.CatchingUp);

        fakes.Spend(TimeSpan.FromMilliseconds(149));
        clock.Frame(0.149, Budget12);
        Assert.Single(fakes.Built);
        Assert.False(clock.CatchingUp);

        fakes.Spend(TimeSpan.FromMilliseconds(1));
        Assert.True(clock.CatchingUp);
        clock.Frame(0.001, Budget12);
        Assert.Equal(2, fakes.Built.Count);
        for (var i = 0; i < 20; i++)
        {
            fakes.Spend(TimeSpan.FromMilliseconds(50));
            clock.Frame(0.05, Budget12);
        }

        Assert.Equal(2, fakes.Built.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.False(clock.CatchingUp);
    }

    /// <summary>A drag that rests for the quiet time before its end builds once. The end of the gesture keeps the build
    /// of the quiet time, because the cutscene did not change after it.</summary>
    [Fact]
    public void ARestBeforeTheEndOfAGestureBuildsOnce()
    {
        using var clock = fakes.Clock();
        clock.Seek(1);
        Settle(clock);

        clock.Invalidate(gesture: true);
        fakes.Spend(CutsceneClock.GestureQuietTime);
        clock.Frame(0.15, Budget12);
        Assert.Equal(2, fakes.Built.Count);
        var quiet = fakes.Built[1];
        Assert.Equal(12, quiet.Steps.Count);

        clock.EndGesture();
        Assert.False(quiet.Disposed);
        Settle(clock);

        Assert.Equal(2, fakes.Built.Count);
        Assert.Same(quiet, clock.Visible);
        Assert.Equal(60, quiet.Steps.Count);
    }

    /// <summary>A paused frame draws 0 s, so the particle effects stop with the preview. A play frame draws one step.</summary>
    [Fact]
    public void APausedFrameDrawsNothing()
    {
        using var clock = fakes.Clock();
        clock.Seek(1);
        Settle(clock);

        clock.Frame(Frame60, Budget40);
        Assert.Equal(0, clock.DrawSeconds);
        Assert.False(clock.Playing);

        clock.Play(1);
        clock.Frame(Frame60, Budget40);
        Assert.Equal(1.0 / 60, clock.DrawSeconds);

        clock.Pause();
        clock.Frame(Frame60, Budget40);
        Assert.Equal(0, clock.DrawSeconds);
        Assert.Equal(61, fakes.Built[0].Steps.Count);
    }

    /// <summary>Play with pre-roll from a playhead at 5 s: a seek back to 2 s catches up without a draw, then a play at
    /// speed 4 runs four steps in each frame and draws them.</summary>
    [Fact]
    public void APreRollFrameWithFourStepsDrawsFourSteps()
    {
        using var clock = fakes.Clock();
        clock.Seek(5);
        Settle(clock);

        clock.Seek(2);
        do
        {
            clock.Frame(Frame60, Budget40);
            Assert.Equal(0, clock.DrawSeconds);
        } while (clock.CatchingUp);

        clock.Play(4);
        var frames = 0;
        while (clock.Target < 5 - 1e-9)
        {
            clock.Frame(Frame60, Budget40);
            Assert.Equal(4.0 / 60, clock.DrawSeconds);
            frames++;
        }
        Assert.Equal(45, frames);
        Assert.Equal(300, fakes.Built[1].Steps.Count);
    }

    /// <summary>A build that fails keeps the last frame and names the problem. The clock does not try again in each frame,
    /// also not for another target. The next invalidate builds again.</summary>
    [Fact]
    public void AFailedBuildKeepsTheLastFrameUntilTheNextInvalidate()
    {
        using var clock = fakes.Clock();
        clock.Seek(1);
        Settle(clock);

        var calls = fakes.BuildCalls;
        fakes.BuildFailure = new InvalidOperationException("No camera");
        clock.Invalidate();
        for (var i = 0; i < 5; i++)
            clock.Frame(Frame60, Budget12);
        clock.Seek(0.5);
        clock.Frame(Frame60, Budget12);

        Assert.Equal(calls + 1, fakes.BuildCalls);
        Assert.Same(fakes.BuildFailure, clock.Problem);
        Assert.Same(fakes.Built[0], clock.Visible);
        Assert.False(clock.CatchingUp);

        fakes.BuildFailure = null;
        clock.Invalidate();
        Assert.Null(clock.Problem);
        Settle(clock);
        Assert.Equal(2, fakes.Built.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
    }

    /// <summary>A step that fails during a catch-up disposes the new player and keeps the last frame.</summary>
    [Fact]
    public void AFailedStepDuringACatchUpKeepsTheLastFrame()
    {
        using var clock = fakes.Clock();
        clock.Seek(2);
        Settle(clock);

        fakes.FailAtStep = 5;
        clock.Seek(1);
        clock.Frame(Frame60, Budget12);
        clock.Frame(Frame60, Budget12);

        Assert.Equal(2, fakes.Built.Count);
        Assert.True(fakes.Built[1].Disposed);
        Assert.Same(fakes.Built[0], clock.Visible);
        Assert.IsType<InvalidOperationException>(clock.Problem);
        Assert.False(clock.CatchingUp);
    }

    /// <summary>A step of the visible player that fails stops the player at its last frame, and the clock never steps it
    /// again. A target at or after the failed step builds nothing. A target before it builds a new player, also the step
    /// just before it, because the failed player stopped in the middle of a step. The next invalidate clears the
    /// failure.</summary>
    [Fact]
    public void AFailedStepOfTheVisiblePlayerBuildsOnlyForATargetBeforeIt()
    {
        fakes.FailAtStep = 10;
        using var clock = fakes.Clock();
        Settle(clock);
        var failed = fakes.Built[0];

        clock.Seek(1);
        clock.Frame(Frame60, Budget40);
        Assert.Equal(9.0 / 60, clock.DrawSeconds);
        var problem = Assert.IsType<InvalidOperationException>(clock.Problem);
        Assert.Equal(10, failed.Calls);

        foreach (var time in new[] { 1, 10 / 60.0 })
        {
            clock.Seek(time);
            for (var i = 0; i < 3; i++)
            {
                clock.Frame(Frame60, Budget40);
                Assert.Equal(0, clock.DrawSeconds);
                Assert.False(clock.CatchingUp);
            }
        }
        Assert.Same(failed, Assert.Single(fakes.Built));
        Assert.Same(failed, clock.Visible);
        Assert.Equal(9, failed.Steps.Count);
        Assert.Equal(10, failed.Calls);

        clock.Seek(9 / 60.0);
        Settle(clock);
        Assert.Equal(2, fakes.Built.Count);
        Assert.Same(fakes.Built[1], clock.Visible);
        Assert.Equal(9, fakes.Built[1].Calls);
        Assert.True(failed.Disposed);
        Assert.Equal(10, failed.Calls);
        Assert.Same(problem, clock.Problem);

        fakes.FailAtStep = null;
        clock.Invalidate();
        Assert.Null(clock.Problem);
        clock.Seek(1);
        Settle(clock);
        Assert.Equal(3, fakes.Built.Count);
        Assert.Same(fakes.Built[2], clock.Visible);
        Assert.Equal(60, fakes.Built[2].Steps.Count);
    }

    /// <summary>After a failed step, no player runs that step again. A seek past it stops a catch-up at once. A player
    /// that plays toward it stops at the step before it and keeps the problem.</summary>
    [Fact]
    public void NoPlayerRunsTheFailedStepAgain()
    {
        fakes.FailAtStep = 10;
        using var clock = fakes.Clock();
        Settle(clock);
        clock.Seek(1);
        clock.Frame(Frame60, Budget40);
        var problem = Assert.IsType<InvalidOperationException>(clock.Problem);

        clock.Seek(0.1);
        clock.Frame(Frame60, TimeSpan.FromMilliseconds(4));
        var stopped = fakes.Built[1];
        Assert.Equal(4, stopped.Calls);
        clock.Seek(1);
        clock.Frame(Frame60, Budget40);
        Assert.True(stopped.Disposed);
        Assert.Equal(4, stopped.Calls);
        Assert.Same(fakes.Built[0], clock.Visible);
        Assert.False(clock.CatchingUp);

        clock.Seek(0.1);
        Settle(clock);
        var player = fakes.Built[2];
        Assert.Same(player, clock.Visible);
        Assert.Equal(6, player.Steps.Count);
        clock.Play(1);
        var draws = 0.0;
        for (var i = 0; i < 10; i++)
        {
            clock.Frame(Frame60, Budget40);
            draws += clock.DrawSeconds;
        }

        Assert.Equal(9, player.Calls);
        Assert.Equal(9, player.Steps.Count);
        Assert.Equal(3.0 / 60, draws, 9);
        Assert.Equal(0, clock.DrawSeconds);
        Assert.Same(problem, clock.Problem);
        Assert.Equal(3, fakes.Built.Count);
    }

    /// <summary>Dispose disposes the visible player and the player that catches up.</summary>
    [Fact]
    public void DisposeDisposesBothPlayers()
    {
        var clock = fakes.Clock();
        clock.Seek(2);
        Settle(clock);
        clock.Seek(1);
        clock.Frame(Frame60, Budget12);

        clock.Dispose();

        Assert.All(fakes.Built, p => Assert.True(p.Disposed));
        Assert.Throws<ObjectDisposedException>(() => clock.Frame(Frame60, Budget12));
    }

    [Fact]
    public void TheClockRefusesTimesAndSpeedsThatAreNotValid()
    {
        using var clock = fakes.Clock();

        clock.Seek(-1);
        Assert.Equal(0, clock.Target);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Seek(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Play(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Frame(-0.01, Budget12));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Frame(Frame60, TimeSpan.Zero));
    }

    /// <summary>Runs frames without wall time until the clock shows the target.</summary>
    private static void Settle(CutsceneClock clock)
    {
        do
            clock.Frame(0, NoLimit);
        while (clock.CatchingUp || clock.DrawSeconds > 0);
    }
}

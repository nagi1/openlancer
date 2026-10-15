using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The fixed clock on two retail story scenes with the headless player (test 13.2 item 5 of the M8 design). Each player
/// builds as the preview builds: the writer writes W, and the engine compiles the text into a <see cref="ThnScript"/>.
/// </summary>
public sealed class CutsceneClockRetailTests(ITestOutputHelper output)
{
    internal const string S003x = @"DATA\SCRIPTS\STORY\s003x_nrml_manhtn_bar_li_01_Bar_01.thn";

    /// <summary>The budget of a frame when nothing is dragged (section 8.4).</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(40);

    private const double End = 60;

    static CutsceneClockRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// A seek to 60 s gives the state of a play to 60 s, bit for bit. The play has frames of 4 to 50 ms, so it runs 0 to 3
    /// steps in a frame. A seek to 30 s and then forward to 60 s gives the same state too, without a second build.
    /// </summary>
    [Theory]
    [InlineData(ThnPatchRetailTests.S002xe)]
    [InlineData(S003x)]
    public void ASeekGivesTheStateOfAPlayAndOfASeekForward(string path)
    {
        var baseFolder = GameDataPaths.RequireBase();
        var game = RetailGame.Instance;
        var retail = ThornEvaluator.Instance.Evaluate(File.ReadAllBytes(Path.Combine(baseFolder, path)), path);
        var working = ThnScriptModel.From(retail, ThnElementId.Counter());
        var text = Encoding.ASCII.GetBytes(ThnWriter.Write(working.ToGlobals()));
        var builds = 0;
        ICutscenePlayer Build()
        {
            builds++;
            var script = new ThnScript(text, include => throw new FileNotFoundException(include), path);
            return new HeadlessCutscenePlayer(script, game.Data, game.Resources);
        }

        // A seek from 0: a new player catches up to 60 s, and the clock swaps it in.
        var watch = Stopwatch.StartNew();
        using var seek = new CutsceneClock(Build);
        seek.Seek(End);
        var seekFrames = Settle(seek);
        var seekTime = watch.Elapsed;
        var expected = Shown(seek);

        // A play from 0 with frame times from 4 to 50 ms. A frame runs 0 to 3 steps. A stall of the machine can use up
        // the budget of a frame, and the next frame then runs more steps. The steps stay the same.
        using var play = new CutsceneClock(Build);
        play.Play(1);
        var random = new Random(8);
        var playFrames = 0;
        var steps = new SortedDictionary<int, int>();
        while (End - play.Target > 1e-9)
        {
            play.Frame(Math.Min(random.Next(4, 51) / 1000.0, End - play.Target), Budget);
            Assert.Null(play.Problem);
            var drawn = (int)Math.Round(play.DrawSeconds * 60);
            steps[drawn] = steps.GetValueOrDefault(drawn) + 1;
            playFrames++;
        }
        play.Pause();
        Settle(play);

        // A seek to 30 s, then a seek forward to 60 s.
        using var forward = new CutsceneClock(Build);
        forward.Seek(30);
        Settle(forward);
        var buildsBefore = builds;
        forward.Seek(End);
        var forwardFrames = Settle(forward);
        Assert.Equal(buildsBefore, builds);

        Assert.Equal(3, builds);
        var shown = expected.Player;
        Assert.Equal(End, shown.Time, 9);
        Assert.Null(FirstDifference(expected.Pose, Shown(play).Pose));
        Assert.Null(FirstDifference(expected.Pose, Shown(forward).Pose));
        output.WriteLine($"{Path.GetFileName(path)}: {expected.Pose.Count} values ({expected.Pose.Values.Sum(v => v.Length)} " +
                         $"bytes) of {shown.Objects} objects and {shown.Characters} characters are equal, bit for bit. " +
                         $"{shown.Moved} objects moved from their start pose. The player left out {shown.LeftOut} events.");
        output.WriteLine($"Seek from 0: {seekFrames} frames of 40 ms, {seekTime.TotalSeconds:F2} s with the build. " +
                         $"Play: {playFrames} frames, as frames with a count of steps: " +
                         $"{string.Join(", ", steps.Select(p => $"{p.Value} with {p.Key}"))}. " +
                         $"Seek forward from 30 s: {forwardFrames} frames, no build.");
    }

    /// <summary>Runs frames without wall time until the clock shows the target. Gives the count of frames.</summary>
    private static int Settle(CutsceneClock clock)
    {
        var frames = 0;
        do
        {
            clock.Frame(0, Budget);
            Assert.Null(clock.Problem);
            frames++;
        } while (clock.CatchingUp || clock.DrawSeconds > 0);
        return frames;
    }

    private static (HeadlessCutscenePlayer Player, SortedDictionary<string, byte[]> Pose) Shown(CutsceneClock clock)
    {
        var player = Assert.IsType<HeadlessCutscenePlayer>(clock.Visible);
        return (player, player.Pose());
    }

    /// <summary>The name of the first value that differs, or null.</summary>
    private static string? FirstDifference(SortedDictionary<string, byte[]> a, SortedDictionary<string, byte[]> b)
    {
        foreach (var (name, bytes) in a)
        {
            if (!b.TryGetValue(name, out var other))
                return $"{name} is missing";
            if (!bytes.AsSpan().SequenceEqual(other))
                return $"{name} differs";
        }
        return b.Keys.FirstOrDefault(k => !a.ContainsKey(k)) is { } extra ? $"{extra} is new" : null;
    }
}

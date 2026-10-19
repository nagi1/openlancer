using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Thn;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The camera rigs of the GCS conversations with the headless player (test 5.3 item 1 of the cutscene fixes plan). The
/// sit rigs and the stand tester script make a cycle of attachments in their first update. Before OL-0054, the cycle
/// overflowed the stack and ended the process, so a failure of this test ends the test host.
/// </summary>
/// <remarks>The test does not check the height of the sit camera. That height depends on the order in which retail
/// walks the entities, and a view of the game must confirm it first.</remarks>
public sealed class AttachCycleRetailTests(ITestOutputHelper output)
{
    private const string GcsFolder = @"DATA\SCRIPTS\GCS";
    private const string Tester = @"DATA\MISSIONS\gcs_tester_stand_male.thn";

    static AttachCycleRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>Each GCS script and the stand tester script play to their end at 60 Hz without an exception. After each
    /// step, each scene object has a finite pose.</summary>
    [Fact]
    public void EachGcsScriptPlaysToItsEndWithFinitePoses()
    {
        GameDataPaths.RequireBase();
        var game = RetailGame.Instance;
        var view = game.Data.Items.VFS;
        var paths = VfsFiles.Find(view, ".thn", GcsFolder).ToList();
        Assert.NotEmpty(paths);
        Assert.True(view.FileExists(Tester), $"The data has no {Tester}.");
        paths.Add(Tester);

        var watch = Stopwatch.StartNew();
        var steps = 0L;
        foreach (var path in paths)
        {
            var player = Build(path, view.ReadAllBytes(path), game);
            while (player.Time + CutsceneClock.StepSeconds <= player.Duration)
            {
                player.Step(CutsceneClock.StepSeconds);
                steps++;
                Assert.True(player.NotFinite() == null,
                    $"{path} at {player.Time:F3} s: {player.NotFinite()} has a pose that is not finite.");
            }
        }

        output.WriteLine($"{paths.Count} scripts played to their end in {steps} steps ({watch.Elapsed.TotalSeconds:F1} s).");
    }

    /// <summary>A player of the script as the preview builds it: the writer writes W, and the engine compiles the
    /// text.</summary>
    private static HeadlessCutscenePlayer Build(string path, byte[] bytes, RetailGame game)
    {
        var working = ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(bytes, path), ThnElementId.Counter());
        var text = Encoding.ASCII.GetBytes(ThnWriter.Write(working.ToGlobals()));
        var script = new ThnScript(text, include => throw new FileNotFoundException(include), path);
        return new HeadlessCutscenePlayer(script, game.Data, game.Resources);
    }
}

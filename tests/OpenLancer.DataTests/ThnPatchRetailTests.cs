using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LibreLancer;
using LibreLancer.Thn;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The hand-written patch of the design (section 6.2) on s002xe, in a temporary mod over the retail data. The merged
/// file is text that the engine reads into a <see cref="ThnScript"/>.
/// </summary>
public sealed class ThnPatchRetailTests
{
    internal const string S002xe = @"DATA\SCRIPTS\STORY\s002xe_nrml_sinclair_li_01_Cityscape_01.thn";

    /// <summary>A wider Trent shot, and a later cut to the medics. This is the patch of the manual check A0.</summary>
    internal const string DesignPatch = """
        -- My mod: a wider Trent shot, and a later cut to the medics.
        ol_thn_patch = 1
        entities = {
        	{ ol_match = "Cam_02_Intro_Trent_A", ol_set = { ["cameraprops.fovh"] = 20 } }
        }
        events = {
        	{ ol_match = { 25.75, SET_CAMERA, { "Cam_Monitor_s002xe", "Cam_03_ACOH_Trent_Medics_A" } }, ol_set = { time = 27 } }
        }
        """;

    static ThnPatchRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>Writes the design patch into the patches folder of a mod.</summary>
    internal static void WriteDesignPatch(string modFolder)
    {
        var file = Path.Combine(modFolder, Mod.PatchesFolder, S002xe);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, DesignPatch);
    }

    [Fact]
    public void AMergedS002xeBuildsAThnScriptWithTheTwoChangesAndNothingElse()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var baseFile = Path.Combine(baseFolder, S002xe);
        var hashBefore = SHA256.HashData(File.ReadAllBytes(baseFile));
        using var ws = new RetailWorkspace(baseFolder, "m8proof");
        WriteDesignPatch(ws.ModFolder);

        var overlay = ModOverlay.Build(baseFolder, ModProfile.Load(ws.ProfilePath).Mods, DllStringResourceWriter.Instance,
            ThornEvaluator.Instance);

        Assert.True(overlay.Diagnostics.Count == 0, string.Join("\n", overlay.Diagnostics));
        var merged = overlay.CreateFileSystem().ReadAllBytes(S002xe);
        var script = new ThnScript(merged, include => throw new FileNotFoundException(include), S002xe);
        Assert.Equal(20, script.Entities["Cam_02_Intro_Trent_A"].FovH);
        var cut = Assert.Single(script.Events, e =>
            e.Type == EventTypes.SetCamera && e.Targets is ["Cam_Monitor_s002xe", "Cam_03_ACOH_Trent_Medics_A"]);
        Assert.Equal(27, cut.Time);

        // Every other value is the retail value, with the same float bits and key order.
        var retail = ThornEvaluator.Instance.Evaluate(File.ReadAllBytes(baseFile), S002xe);
        var entities = (ThnTable)retail["entities"]!;
        var events = (ThnTable)retail["events"]!;
        var camera = entities.List.Cast<ThnTable>().ToList().FindIndex(e => e["entity_name"] == new ThnText("Cam_02_Intro_Trent_A"));
        var medics = events.List.Cast<ThnTable>().ToList().FindIndex(e =>
            e.List[0] == new ThnNumber(25.75f) && e.List[2] == ThnTable.FromList(
                new ThnText("Cam_Monitor_s002xe"), new ThnText("Cam_03_ACOH_Trent_Medics_A")));
        var expected = Replace(retail, ThnScriptModel.EntitiesGlobal, camera, ThnElementKind.Entity, "cameraprops.fovh", 20);
        expected = Replace(expected, ThnScriptModel.EventsGlobal, medics, ThnElementKind.Event, "time", 27);
        Assert.Null(ThnValues.FirstDifference(expected, ThornEvaluator.Instance.Evaluate(merged, S002xe)));
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(baseFile)));
    }

    /// <summary>The globals with one value of one element replaced.</summary>
    private static ThnTable Replace(ThnTable globals, string list, int index, ThnElementKind kind, string path, float value)
    {
        Assert.True(index >= 0, $"No element of {list} matches.");
        var items = (ThnTable)globals[list]!;
        var element = ThnElementPaths.Set(kind, (ThnTable)items.List[index], new ThnPath(path), new ThnNumber(value));
        var changed = new ThnTable(items.List.SetItem(index, element), items.Record);
        return new ThnTable([], [.. globals.Record.Select(f => f.Key == list ? new ThnField(list, changed) : f)]);
    }
}

using System.IO;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The test layer of a play from a trigger (section 5.4 of the M6c design, build step 2.2).</summary>
public sealed class TestLayerTests
{
    private static StartPlan Plan(string trigger) =>
        StartPlan.Make(MissionTestData.Index(StartPlanTests.Script), StartPlanTests.Slot, MissionStartPoint.At(trigger));

    [Fact]
    public void TheLayerLoadsAsATierOneMod()
    {
        using var ws = new TestWorkspace();
        var folder = Path.Combine(ws.Root, "test.play", "start", "layer");

        TestLayer.Write(folder, Plan("t_chosen"));
        var mod = Mod.Load(folder);

        Assert.Equal(TestLayer.ModId, mod.Id);
        Assert.Equal(ModManifest.DataTier, mod.Manifest.Tier);
        Assert.True(File.Exists(Path.Combine(folder, "patches", "DATA", "MISSIONS", "TEST", "test.ini")));
    }

    [Fact]
    public void TheMergeAddsTheTwoTriggersAtTheEndOfTheScript()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(MissionTestData.Path, StartPlanTests.Script);
        var folder = Path.Combine(ws.Root, "test.play", "start", "layer");
        TestLayer.Write(folder, Plan("t_chosen"));

        var overlay = ws.Build(Mod.Load(folder));
        var sections = TestWorkspace.Sections(overlay, MissionTestData.Path);

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal(["ol_play_setup", "ol_play_world"], sections.TakeLast(2).Select(s => s["nickname"]![0].ToString()));
        Assert.Equal(MissionTestData.Index(StartPlanTests.Script).Document.Sections.Count + 2, sections.Count);
        // The merge removes its directives, so the game reads plain triggers.
        Assert.All(sections.TakeLast(2), s => Assert.Null(s["ol.new"]));
    }

    [Fact]
    public void ASecondWriteReplacesTheFolderAndLeavesNoOtherFolder()
    {
        using var ws = new TestWorkspace();
        var start = Path.Combine(ws.Root, "test.play", "start");
        var folder = Path.Combine(start, "layer");
        TestLayer.Write(folder, Plan("t_chosen"));

        // An exact start has no setup trigger, so its layer has no patch.
        TestLayer.Write(folder, Plan("t_restore"));

        Assert.False(Directory.Exists(Path.Combine(folder, "patches")));
        Assert.Equal(TestLayer.ModId, Mod.Load(folder).Id);
        Assert.Equal(["layer"], Directory.GetDirectories(start).Select(d => Path.GetFileName(d)));
    }

    [Fact]
    public void TheLayerIsNotAModOfTheProfile()
    {
        using var ws = new TestWorkspace();
        ws.WriteProfile();
        var folder = Path.Combine(ws.Root, "test.play", "start", "layer");
        TestLayer.Write(folder, Plan("t_chosen"));

        var profile = ModProfile.Load(ws.ProfilePath);

        Assert.DoesNotContain(profile.Mods, m => m.Id == TestLayer.ModId);
    }
}

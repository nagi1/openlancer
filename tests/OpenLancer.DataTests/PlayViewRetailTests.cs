using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Play;
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the play view (decision D38): after a save, Play loads exactly the edit view of the editor, the
/// retail data with the mods through the active mod. A later mod is not in it.
/// </summary>
public sealed class PlayViewRetailTests
{
    private const string Depot = "Li01_14_depot_water";

    static PlayViewRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void ThePlayViewIsTheEditViewAfterASave()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m4proof");
        // A later mod moves the depot to another place.
        var later = ModWorkspace.CreateMod(Path.Combine(workspace.Root, "mods"), "later", "Later");
        var laterPatch = Path.Combine(later, Mod.PatchesFolder, @"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(laterPatch)!);
        File.WriteAllText(laterPatch, $"[Object]\r\nnickname = {Depot}\r\npos = -30000, 0, -27705\r\n");
        ModWorkspace.AddToProfile(workspace.ProfilePath, later);
        var session = workspace.Open();
        Assert.Equal(["later"], session.LaterMods.Select(m => m.Id));

        // The move of the M2 proof: +1,000 on X.
        var depot = new SectionRef(session.SystemPath("Li01")!, new SectionIdentity("Object", Depot));
        var start = session.Get(depot)!.Lines("pos").Single().Values;
        var moved = SceneBuild.Position(start) + new Vector3(1000, 0, 0);
        session.Execute(EditCommand.SetLines(depot, "pos", [TransformValues.Line(start, moved)]));
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));

        var profile = ModProfile.Load(workspace.ProfilePath);
        var view = PlayView.Build(profile, "m4proof");

        Assert.Equal(["m4proof"], view.Mods.Select(m => m.Id));
        Assert.False(view.HasErrors);
        var files = Files(view);
        Assert.Equal(Files(session.EditView), files);
        // The later mod changes the system file of the profile, so the view of all mods differs.
        var all = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        var systemFile = files.Keys.Single(p => p.EndsWith(@"\li01.ini", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(files[systemFile], Files(all)[systemFile]);

        var items = EngineDataLoader.Load(view.CreateFileSystem()).RequireComplete();
        var loaded = items.Systems.Get("li01")!.Objects
            .Single(o => o.Nickname.Equals(Depot, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new Vector3(-37675, 0, -27705), loaded.Position);
        Assert.Equal(moved, loaded.Position);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>The files of a view: the install path to the SHA-256 of the bytes.</summary>
    private static SortedDictionary<string, string> Files(ModOverlay overlay) =>
        new(overlay.Files.ToDictionary(f => f.Path, f => Convert.ToHexString(SHA256.HashData(f.ReadAllBytes()))),
            StringComparer.OrdinalIgnoreCase);
}

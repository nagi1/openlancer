using System;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The rows and the chips of the Mods window (section 10.2 of the M11c design).</summary>
public sealed class ModCatalogTests
{
    private static readonly DateTime Monday = new(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void ProfileModsComeFirstInLoadOrder()
    {
        using var workspace = new PublishWorkspace();
        var b = workspace.Mod("b-mod", more: ", \"loadAfter\": [ \"a-mod\" ]");
        var a = workspace.Mod("a-mod");
        workspace.Mod("c-mod");
        var profile = workspace.Profile("p", b, a);

        var rows = ModCatalog.Scan(new CatalogInput(workspace.Root, profile));

        Assert.Equal(["a-mod", "b-mod", "c-mod"], rows.Select(r => r.Id));
        Assert.Equal([1, 2], rows.Take(2).Select(r => r.Place!.Value));
        Assert.All(rows.Take(2), r => Assert.Equal(ModGroup.Profile, r.Group));
        Assert.True(rows[0].Has(ChipKind.InProfile));
        Assert.Equal(ModCatalog.WorkspaceOf(profile), Path.GetFullPath(workspace.Root));
    }

    [Fact]
    public void WorkspaceModsOutsideTheProfileFollow()
    {
        using var workspace = new PublishWorkspace();
        var a = workspace.Mod("a-mod");
        workspace.Mod("z-mod");
        workspace.Mod("m-mod");
        Directory.CreateDirectory(Path.Combine(workspace.Mods, "not-a-mod"));

        var rows = ModCatalog.Scan(new CatalogInput(workspace.Root, workspace.Profile("p", a)));
        var noProfile = ModCatalog.Scan(new CatalogInput(workspace.Root, null));

        Assert.Equal(["a-mod", "m-mod", "z-mod"], rows.Select(r => r.Id));
        Assert.Equal([ModGroup.Profile, ModGroup.Workspace, ModGroup.Workspace], rows.Select(r => r.Group));
        Assert.True(rows[1].Has(ChipKind.NotInProfile));
        Assert.Null(rows[1].Place);
        Assert.Equal(3, noProfile.Count);
        Assert.All(noProfile, r => Assert.Equal(ModGroup.Workspace, r.Group));
    }

    [Fact]
    public void AModWithABadManifestShowsItsError()
    {
        using var workspace = new PublishWorkspace();
        var bad = Path.Combine(workspace.Mods, "bad");
        PublishWorkspace.Write(Path.Combine(bad, "mod.json"), """{ "id": "Bad Id", "formatVersion": 2 }""");

        var row = Assert.Single(ModCatalog.Scan(new CatalogInput(workspace.Root, null)));

        Assert.Null(row.Manifest);
        var chip = Assert.Single(row.Chips);
        Assert.Equal(ChipKind.DoesNotLoad, chip.Kind);
        Assert.Equal(ChipColor.Red, chip.Color);
        Assert.Equal(row.LoadError, chip.Tooltip);
        Assert.StartsWith($"{bad}: id must be lowercase letters", row.LoadError);
        Assert.EndsWith(" OpenLancer cannot show this mod until mod.json loads. Correct the file, then click Refresh.", row.LoadError);
    }

    [Fact]
    public void AProfileModOutsideTheWorkspaceIsListed()
    {
        using var workspace = new PublishWorkspace();
        using var other = new PublishWorkspace();
        var outside = other.Mod("ol-sample");
        var profile = workspace.Profile("p", outside);

        var row = Assert.Single(ModCatalog.Scan(new CatalogInput(workspace.Root, profile)));

        Assert.Equal(outside, row.Folder);
        Assert.Equal(ModGroup.Profile, row.Group);
    }

    [Fact]
    public void TheChipsFollowTheHistoryAndTheFingerprints()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m", more: ", \"targets\": [ \"openlancer\" ]");
        var fingerprints = new ModFingerprint();
        var manifest = ModManifest.Load(mod);
        var content = fingerprints.Content(manifest, mod);
        var page = fingerprints.Page(manifest, mod, PublishSettings.Load(mod).Settings);
        var location = new HistoryLocation(workspace.Exports("m"), "m", mod, []);
        PublishHistory.Update(location, "export", h =>
        {
            h.RecordExport(new HistoryExport("1.0.0", Monday, "test", content, page, workspace.Exports("m"),
                [new HistoryPackage("openlancer", PackageForms.ModFolder, "m.zip", "s", "g")]));
            h.SetCheck(new HistoryCheck("openlancer", Monday, content, "", 0, 2));
            h.Mark("1.0.0", Monday, "https://example.org/mods/1");
        });
        var input = new CatalogInput(workspace.Root, null) { ActiveModFolder = mod, UnsavedEdits = true, DraftFolders = [mod] };

        var exported = Assert.Single(ModCatalog.Scan(input));
        PublishSettings.Write(mod, PublishSettings.Empty with { Summary = "New text." });
        var pageChanged = Assert.Single(ModCatalog.Scan(input));
        PublishWorkspace.Write(Path.Combine(mod, "patches", "DATA", "EQUIPMENT", "goods.ini"), "[Good]\nnickname = x\n");
        var contentChanged = Assert.Single(ModCatalog.Scan(input));

        Assert.Equal(
            [ChipKind.Active, ChipKind.UnsavedEdits, ChipKind.UnappliedChanges, ChipKind.Version, ChipKind.Tier,
             ChipKind.Target, ChipKind.Exported, ChipKind.Checks, ChipKind.Published, ChipKind.NotInProfile],
            exported.Chips.Select(c => c.Kind));
        var date = Monday.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        Assert.Equal("Exported 1.0.0 on " + date, exported.Chips.Single(c => c.Kind == ChipKind.Exported).Text);
        Assert.Equal(("Checks: 2 warnings", ChipColor.Yellow),
            exported.Chips.Where(c => c.Kind == ChipKind.Checks).Select(c => (c.Text, c.Color)).Single());
        Assert.Equal("https://example.org/mods/1", exported.Chips.Single(c => c.Kind == ChipKind.Published).Tooltip);
        Assert.True(pageChanged.Has(ChipKind.PageChanged));
        Assert.False(pageChanged.Has(ChipKind.ContentChanged));
        Assert.True(contentChanged.Has(ChipKind.ContentChanged));
        Assert.True(contentChanged.Has(ChipKind.NotChecked));
        Assert.False(contentChanged.Has(ChipKind.PageChanged));
    }

    [Fact]
    public void FlatM11PackagesGiveTheChipExportedBeforeTheHistory()
    {
        using var workspace = new PublishWorkspace();
        workspace.Mod("my-mod-2");
        workspace.Mod("other");
        foreach (var file in new[] { "my-mod-2-1.0.0-hde-0.7.1-flmm.zip", "my-mod-2-1.0.0-hde-0.7.1.zip", "my-mod-2-0.9.0-openlancer.zip" })
            PublishWorkspace.Write(Path.Combine(workspace.Exports("my-mod-2"), file), "zip");

        var rows = ModCatalog.Scan(new CatalogInput(workspace.Root, null));

        var chip = rows[0].Chips.Single(c => c.Kind == ChipKind.ExportedBeforeHistory);
        Assert.Equal($"OpenLancer found packages of version 0.9.0, 1.0.0 in {workspace.Exports("my-mod-2")}. The history " +
                     "starts with the next export.", chip.Tooltip);
        Assert.True(rows[1].Has(ChipKind.NeverExported));
        Assert.Equal(["0.9.0", "1.0.0"], ModCatalog.M11Versions(workspace.Exports("my-mod-2"), "my-mod-2"));
    }
}

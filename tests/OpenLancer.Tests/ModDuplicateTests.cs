using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>Duplicate of a mod (decision D573 and section 12.2 of the M11c design).</summary>
public sealed class ModDuplicateTests
{
    [Fact]
    public void CopiesWithoutOlsaveWithTheNewIdAndName()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("ship", more: ", \"later\": true");
        PublishWorkspace.Write(Path.Combine(mod, SaveTransaction.FolderName, "recovery.json"), "{}");
        PublishWorkspace.Write(Path.Combine(mod, "files", "DATA", "a.txt.tmp"), "temp");
        PublishWorkspace.Screenshot(mod, "a.png");
        var id = ModDuplicate.FreeId(workspace.Mods, "ship");

        var copy = ModDuplicate.Run(mod, workspace.Mods, id, ModDuplicate.DefaultName("Starrunner"), []);

        Assert.Equal("ship-copy", id);
        Assert.Equal(Path.Combine(workspace.Mods, "ship-copy"), copy);
        var manifest = ModManifest.Load(copy);
        Assert.Equal(("ship-copy", "Starrunner (copy)"), (manifest.Id, manifest.Name));
        Assert.True(JsonNode.Parse(File.ReadAllText(Path.Combine(copy, ModManifest.FileName)))!["later"]!.GetValue<bool>());
        Assert.False(Directory.Exists(Path.Combine(copy, SaveTransaction.FolderName)));
        Assert.False(File.Exists(Path.Combine(copy, "files", "DATA", "a.txt.tmp")));
        Assert.True(File.Exists(Path.Combine(copy, "patches", "DATA", "EQUIPMENT", "goods.ini")));
        Assert.True(File.Exists(Path.Combine(copy, "publish", "screenshots", "a.png")));
        Assert.Equal("ship-copy-2", ModDuplicate.FreeId(workspace.Mods, "ship"));
        Assert.Equal("ship", ModManifest.Load(mod).Id);
    }

    [Fact]
    public void ClearsThePageLink()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("ship");
        PublishSettings.Write(mod, PublishSettings.Empty with { Summary = "Kept.", PageUrl = "https://example.org/mods/1" });

        var copy = ModDuplicate.Run(mod, workspace.Mods, "ship-copy", "Copy", []);

        var settings = PublishSettings.Load(copy).Settings;
        Assert.Equal("", settings.PageUrl);
        Assert.Equal("Kept.", settings.Summary);
        Assert.Equal("https://example.org/mods/1", PublishSettings.Load(mod).Settings.PageUrl);
    }

    [Fact]
    public void TheCopyIsNotInAProfile()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("ship");
        var profile = workspace.Profile("p", mod);
        var before = File.ReadAllText(profile);

        var copy = ModDuplicate.Run(mod, workspace.Mods, "ship-copy", "Copy", []);
        var rows = ModCatalog.Scan(new CatalogInput(workspace.Root, profile));

        Assert.Equal(before, File.ReadAllText(profile));
        Assert.Equal(ModGroup.Workspace, rows.Single(r => r.Folder == copy).Group);
    }

    [Fact]
    public void AFailureLeavesNoFolder()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("ship");
        PublishWorkspace.Write(Path.Combine(mod, "files", "DATA", "locked.txt"), "x");
        using var held = new FileStream(Path.Combine(mod, "files", "DATA", "locked.txt"), FileMode.Open, FileAccess.ReadWrite,
            FileShare.None);

        Assert.ThrowsAny<IOException>(() => ModDuplicate.Run(mod, workspace.Mods, "ship-copy", "Copy", []));
        Assert.False(Directory.Exists(Path.Combine(workspace.Mods, "ship-copy")));
        Assert.Throws<IOException>(() => ModDuplicate.Run(mod, workspace.Mods, "ship", "Copy", []));
        Assert.Throws<ArgumentException>(() => ModDuplicate.Run(mod, workspace.Mods, "Bad Id", "Copy", []));
    }
}

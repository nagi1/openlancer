using System;
using System.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The content and page fingerprints of a mod (decision D583 and section 8.5 of the M11c design).</summary>
public sealed class ModFingerprintTests
{
    private static string Content(string folder) => new ModFingerprint().Content(ModManifest.Load(folder), folder);

    private static string Page(string folder) =>
        new ModFingerprint().Page(ModManifest.Load(folder), folder, PublishSettings.Load(folder).Settings);

    [Fact]
    public void TheContentIgnoresTheNameTheVersionAndThePublishFiles()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m", "Old name", "1.0.0");
        var before = Content(mod);

        workspace.Mod("m", "New name", "1.0.1");
        PublishSettings.Write(mod, PublishSettings.Empty with { Summary = "A summary." });
        PublishWorkspace.Screenshot(mod, "a.png");

        Assert.Equal(before, Content(mod));
    }

    [Fact]
    public void TheContentChangesWithAPatch()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var before = Content(mod);

        PublishWorkspace.Write(Path.Combine(mod, "patches", "DATA", "EQUIPMENT", "goods.ini"), "[Good]\nnickname = m_good\nprice = 6\n");
        var patched = Content(mod);
        PublishWorkspace.Write(Path.Combine(mod, "strings.json"), """{ "strings": { "a": "A" } }""");
        var withText = Content(mod);
        workspace.Mod("m", more: ", \"targets\": [ \"openlancer\" ]");

        Assert.NotEqual(before, patched);
        Assert.NotEqual(patched, withText);
        Assert.NotEqual(withText, Content(mod));
    }

    [Fact]
    public void TheContentIgnoresHiddenAndTemporaryFiles()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var before = Content(mod);

        PublishWorkspace.Write(Path.Combine(mod, "files", "DATA", "x.txt.tmp"), "temp");
        PublishWorkspace.Write(Path.Combine(mod, "files", "DATA", ".notes"), "notes");
        PublishWorkspace.Write(Path.Combine(mod, "patches", "Thumbs.db"), "x");
        PublishWorkspace.Write(Path.Combine(mod, ".olsave", "lock.json"), "{}");

        Assert.Equal(before, Content(mod));
    }

    [Fact]
    public void ThePageChangesWithTheDescriptionAndAScreenshot()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var empty = Page(mod);
        PublishSettings.Write(mod, PublishSettings.Empty with { Description = "One." });
        var described = Page(mod);
        var shot = PublishWorkspace.Screenshot(mod, "a.png", mark: 1);
        PublishSettings.Write(mod, PublishSettings.Empty with
        {
            Description = "One.",
            Screenshots = [new PublishScreenshot(shot, "")],
        });
        var listed = Page(mod);
        PublishWorkspace.Screenshot(mod, "a.png", mark: 2);
        var otherImage = Page(mod);
        workspace.Mod("m", "Other name");

        Assert.NotEqual(empty, described);
        Assert.NotEqual(described, listed);
        Assert.NotEqual(listed, otherImage);
        Assert.NotEqual(otherImage, Page(mod));
    }

    [Fact]
    public void TheCacheReadsOnlyChangedFiles()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(Path.Combine(mod, "files", "DATA", "a.txt"), "a");
        var fingerprint = new ModFingerprint();
        var manifest = ModManifest.Load(mod);

        var first = fingerprint.Content(manifest, mod);
        var afterFirst = fingerprint.FilesHashed;
        var second = fingerprint.Content(manifest, mod);
        var afterSecond = fingerprint.FilesHashed;
        var changed = Path.Combine(mod, "files", "DATA", "a.txt");
        File.WriteAllText(changed, "changed");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        var third = fingerprint.Content(manifest, mod);

        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(first, second);
        Assert.Equal(afterSecond + 1, fingerprint.FilesHashed);
        Assert.NotEqual(first, third);
    }
}

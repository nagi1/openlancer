using System;
using System.IO;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The publish history, the same-version guard and the repeat exports (decisions D565, D568, D589 and D590
/// of the M11c design).</summary>
public sealed class PublishHistoryTests
{
    private static readonly DateTime Monday = new(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc);

    private const string Patch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    private static HistoryExport Entry(string version, DateTime time, string content, params HistoryPackage[] packages) =>
        new(version, time, "test", content, "page", @"C:\out", packages);

    private static HistoryPackage Package(string target, string form, string sha, string gameFiles) =>
        new(target, form, $"m-{target}-{form}.zip", sha, gameFiles);

    private static HistoryLocation Location(PublishWorkspace workspace, string mod) =>
        new(workspace.Exports("m"), "m", mod, []);

    [Fact]
    public void AnExportAddsAnEntry()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch, "ol-test");
        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer));
        var folder = Path.Combine(game.Game.Folder, "exports", "ol-test");
        var location = new HistoryLocation(folder, "ol-test", mod.Folder, [game.Root]);

        var write = PublishHistory.Update(location, "export", h => h.RecordExport(PublishHistory.EntryOf(result, Monday)));
        var (history, problem) = PublishHistory.Load(folder, "ol-test");

        Assert.True(write.Written, string.Join("\n", write.Messages));
        Assert.Null(problem);
        var entry = Assert.Single(history.Exports);
        Assert.Equal(("1.2.0", Monday, result.Content, result.Page), (entry.Version, entry.Time, entry.Content, entry.Page));
        Assert.Equal(["dropin", "flmm", "modfolder"], entry.Packages.Select(p => p.Form));
        Assert.All(entry.Packages, p => Assert.Equal(DataDigest.HashFile(Path.Combine(result.OutputFolder, p.File)), p.Sha256));
        Assert.All(entry.Packages, p => Assert.Equal(64, p.GameFiles.Length));
        Assert.Equal(mod.Folder, history.ModFolder);
        Assert.False(File.Exists(Path.Combine(folder, PublishHistory.LockName)));
        Assert.DoesNotContain("newest", File.ReadAllText(Path.Combine(folder, PublishHistory.FileName)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFileIsInTheExportFolderAndNeverInTheModFolder()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");

        var inMod = PublishHistory.Update(new HistoryLocation(Path.Combine(mod, "exports"), "m", mod, []), "export", _ => { });
        var inExports = PublishHistory.Update(Location(workspace, mod), "export", _ => { });

        Assert.False(inMod.Written);
        Assert.Contains("overlaps the mod folder", Assert.Single(inMod.Messages));
        Assert.False(Directory.Exists(Path.Combine(mod, "exports")));
        Assert.True(inExports.Written);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "exports", "m", PublishHistory.FileName)));
    }

    [Fact]
    public void TheSameVersionWithOtherGameFilesIsAConflict()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0", Monday, "c", Package("vanilla-1.1", "dropin", "s1", "g1")));

        var guard = history.Conflict("1.0.0", "c2", [("vanilla-1.1", "dropin", "g2")]);

        Assert.Equal(GuardKind.Exported, guard.Kind);
        Assert.True(guard.CanReplace);
        Assert.Equal("Version 1.0.0 exists", guard.Title);
        Assert.Equal(PublishMessages.GuardExported("1.0.0", Monday), guard.Text);
        Assert.Equal(GuardKind.None, history.Conflict("1.0.1", "c2", [("vanilla-1.1", "dropin", "g2")]).Kind);
    }

    [Fact]
    public void ATextsOnlyDifferenceIsNoConflict()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0", Monday, "c", Package("vanilla-1.1", "dropin", "s1", "g1")));

        var guard = history.Conflict("1.0.0", "c", [("vanilla-1.1", "dropin", "g1")]);
        var record = history.RecordExport(Entry("1.0.0", Monday.AddHours(1), "c", Package("vanilla-1.1", "dropin", "s2", "g1")));

        Assert.Equal(GuardKind.None, guard.Kind);
        Assert.Equal(RecordKind.TextsOnly, record.Kind);
        Assert.Equal(2, history.Exports.Count);
        Assert.Equal(Monday.AddHours(1), history.Exports[0].ReplacedBy);
        Assert.Equal([PublishMessages.TextsOnly("1.0.0", Monday)], record.Messages("1.0.0"));
    }

    [Fact]
    public void ANewDescriptionDoesNotChangeTheFlmmDigest()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var request = game.Request(mod);

        var first = ModExport.Prepare(request with { Publish = PublishSettings.Empty with { Description = "One." } });
        var second = ModExport.Prepare(request with
        {
            Publish = PublishSettings.Empty with { Description = "Two.", Summary = "Other.", Authors = ["Ann"] },
        });
        var written = ModExport.Write(second, []);
        var again = ModExport.Write(first, []);

        Assert.Equal(first.Digests, second.Digests);
        Assert.Equal(2, first.Digests.Count);
        Assert.NotEqual(written.Packages.Single(p => p.Form == PackageForm.Flmm).Sha256,
            again.Packages.Single(p => p.Form == PackageForm.Flmm).Sha256);
    }

    [Fact]
    public void ANewDisplayNameDoesNotChangeTheOpenLancerDigest()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var before = ModExport.Prepare(game.Request(mod, ExportTarget.OpenLancer));
        var manifest = Path.Combine(mod.Folder, ModManifest.FileName);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"Mod m\"", "\"Another name\""));

        var after = ModExport.Prepare(game.Request(Mod.Load(mod.Folder), ExportTarget.OpenLancer));

        Assert.Equal("Another name", Mod.Load(mod.Folder).Manifest.Name);
        Assert.Equal(before.Content, Assert.Single(before.Digests).GameFiles);
        Assert.Equal(before.Digests, after.Digests);
        Assert.NotEqual(before.Page, after.Page);
    }

    [Fact]
    public void OtherContentForAnotherTargetIsAConflict()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0", Monday, "c", Package("hde-0.7.1", "dropin", "s1", "g1")));

        var sameContent = history.Conflict("1.0.0", "c", [("vanilla-1.1", "dropin", "g2")]);
        var otherContent = history.Conflict("1.0.0", "c2", [("vanilla-1.1", "dropin", "g2")]);

        Assert.Equal(GuardKind.None, sameContent.Kind);
        Assert.Equal(GuardKind.Exported, otherContent.Kind);
        Assert.False(otherContent.CanReplace);
        Assert.Equal(["HD Edition 0.7.1 (drop-in zip)"], otherContent.Missing);
    }

    [Fact]
    public void APublishedVersionCannotBeReplaced()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0", Monday, "c", Package("vanilla-1.1", "dropin", "s1", "g1")));
        history.Mark("1.0.0", Monday, "https://example.org/mods/1");

        var guard = history.Conflict("1.0.0", "c2", [("vanilla-1.1", "dropin", "g2")]);

        Assert.Equal(GuardKind.Published, guard.Kind);
        Assert.False(guard.CanReplace);
        Assert.Equal("Version 1.0.0 is on Nexus", guard.Title);
        Assert.Equal(PublishMessages.GuardPublished("1.0.0"), guard.Text);
    }

    [Fact]
    public void AReplaceNeedsEachTargetAndForm()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0", Monday, "c",
            Package("vanilla-1.1", "dropin", "s1", "g1"), Package("vanilla-1.1", "flmm", "s2", "g2")));

        var partial = history.Conflict("1.0.0", "c2", [("vanilla-1.1", "dropin", "g3")]);
        var full = history.Conflict("1.0.0", "c2", [("vanilla-1.1", "dropin", "g3"), ("vanilla-1.1", "flmm", "g4")]);

        Assert.False(partial.CanReplace);
        Assert.Equal(PublishMessages.GuardPartialReplace("1.0.0", Monday, "Freelancer 1.1 (FLMM package)"), partial.PartialText);
        Assert.True(full.CanReplace);
        Assert.Null(full.PartialText);
    }

    [Fact]
    public void AReplaceKeepsTheOldEntry()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0", Monday, "c", Package("vanilla-1.1", "dropin", "s1", "g1")));

        var record = history.RecordExport(Entry("1.0.0", Monday.AddDays(1), "c2", Package("vanilla-1.1", "dropin", "s2", "g2")),
            replace: true);

        Assert.Equal(RecordKind.Replaced, record.Kind);
        Assert.Equal(2, history.Exports.Count);
        Assert.Equal(Monday.AddDays(1), history.Exports[0].ReplacedBy);
        Assert.Null(history.Exports[1].ReplacedBy);
        Assert.Equal(GuardKind.None, history.Conflict("1.0.0", "c2", [("vanilla-1.1", "dropin", "g2")]).Kind);
    }

    [Fact]
    public void TheSameBytesAddNoEntry()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0", Monday, "c", Package("vanilla-1.1", "dropin", "s1", "g1")));

        var record = history.RecordExport(Entry("1.0.0", Monday.AddHours(2), "c", Package("vanilla-1.1", "dropin", "s1", "g1")));

        Assert.Equal(RecordKind.Same, record.Kind);
        Assert.Single(history.Exports);
        Assert.Equal([PublishMessages.SamePackages("1.0.0", Monday)], record.Messages("1.0.0"));
    }

    [Fact]
    public void VersionsCompareWithoutLetterCase()
    {
        var history = new PublishHistory();
        history.RecordExport(Entry("1.0.0-beta", Monday, "c", Package("vanilla-1.1", "dropin", "s1", "g1")));
        history.Mark("1.0.0-beta", Monday, "");

        Assert.True(history.IsPublished("1.0.0-BETA"));
        Assert.Equal(GuardKind.Published, history.Conflict("1.0.0-Beta", "c", [("vanilla-1.1", "dropin", "g2")]).Kind);
        Assert.Single(history.ValidExports("1.0.0-BETA"));
        history.Unmark("1.0.0-BETA");
        Assert.Empty(history.Published);
    }

    [Fact]
    public void ABadFileMovesAsideAndANewHistoryStarts()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var folder = workspace.Exports("m");
        PublishWorkspace.Write(Path.Combine(folder, PublishHistory.FileName), "{ not json");

        var (_, problem) = PublishHistory.Load(folder, "m");
        var write = PublishHistory.Update(Location(workspace, mod), "export", h => h.Mark("1.0.0", Monday, ""));

        Assert.Equal(PublishMessages.HistoryUnreadable(Path.Combine(folder, PublishHistory.FileName)), problem);
        Assert.True(write.Written);
        Assert.Equal(PublishMessages.HistoryBad(Path.Combine(folder, PublishHistory.FileName),
            Path.Combine(folder, PublishHistory.BadName)), Assert.Single(write.Messages));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(folder, PublishHistory.BadName)));
        Assert.Single(PublishHistory.Load(folder, "m").History.Published);
    }

    [Fact]
    public void AHistoryOfAnotherFolderGivesAWarning()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var other = Path.Combine(workspace.Root, "elsewhere", "m");
        PublishHistory.Update(new HistoryLocation(workspace.Exports("m"), "m", other, []), "export", _ => { });

        var (history, _) = PublishHistory.Load(workspace.Exports("m"), "m");

        Assert.Equal(PublishMessages.HistoryOfAnotherFolder(other), history.FolderWarning(mod));
        Assert.Equal($"This history belongs to the mod in {other}.", history.FolderWarning(mod));
        Assert.Null(history.FolderWarning(other));
    }

    [Fact]
    public void AHeldLockGivesNoRecord()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        Directory.CreateDirectory(workspace.Exports("m"));
        using (new FileStream(Path.Combine(workspace.Exports("m"), PublishHistory.LockName), FileMode.Create,
                   FileAccess.ReadWrite, FileShare.None))
        {
            var write = PublishHistory.Update(Location(workspace, mod), "play", h => h.Mark("1.0.0", Monday, ""),
                TimeSpan.FromMilliseconds(200));

            Assert.False(write.Written);
            Assert.Equal("Another program writes the publish history now. OpenLancer did not record this play.",
                Assert.Single(write.Messages));
        }
        Assert.False(File.Exists(Path.Combine(workspace.Exports("m"), PublishHistory.FileName)));
    }

    [Fact]
    public void PlaysKeepTheLastFifty()
    {
        var history = new PublishHistory();

        for (var i = 0; i < 60; i++)
            history.AddPlay(new HistoryPlay(Monday.AddMinutes(i), "hde-0.7.1", "c", true, i, false));

        Assert.Equal(PublishHistory.MaxPlays, history.Plays.Count);
        Assert.Equal(10, history.Plays[0].Seconds);
        Assert.Equal(59, history.Plays[^1].Seconds);
    }

    [Fact]
    public void ChecksKeepTheLastResultOfEachTarget()
    {
        var history = new PublishHistory();

        history.SetCheck(new HistoryCheck("hde-0.7.1", Monday, "c1", "b", 1, 0));
        history.SetCheck(new HistoryCheck("vanilla-1.1", Monday, "c1", "b", 0, 0));
        history.SetCheck(new HistoryCheck("hde-0.7.1", Monday.AddHours(1), "c2", "b", 0, 2));

        Assert.Equal(2, history.Checks.Count);
        Assert.Equal(("c2", 2), (history.CheckOf("hde-0.7.1")!.Content, history.CheckOf("hde-0.7.1")!.Warnings));
    }
}

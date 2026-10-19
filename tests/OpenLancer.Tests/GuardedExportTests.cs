using System;
using System.IO;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The export with the publish history that the Export dialog and <c>oltool export</c> share (decisions D565,
/// D568 and D590 of the M11c design).</summary>
public sealed class GuardedExportTests
{
    private const string Patch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    private static HistoryLocation Location(ExportGame game, Mod mod) =>
        new(PublishHistory.FolderOf(game.Game.Folder, mod.Id), mod.Id, mod.Folder, [game.Root]);

    private static ExportRequest Request(ExportGame game, Mod mod) =>
        game.Request(Mod.Load(mod.Folder)) with
        {
            OutputFolder = Path.Combine(PublishHistory.FolderOf(game.Game.Folder, mod.Id), "1.2.0"),
            Forms = ExportForms.DropIn,
        };

    [Fact]
    public void TheRecordAddsTheEntryAndTheChecks()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var time = new DateTime(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc);

        var export = GuardedExport.Prepare(Request(game, mod), Location(game, mod), time);
        var result = export.Write();
        var (record, warnings) = export.RecordExport(result, replace: false);

        Assert.Null(export.Refusal);
        Assert.Equal(GuardKind.None, export.Guard.Kind);
        Assert.True(result.Succeeded, string.Join(" | ", result.Problems));
        Assert.Equal(RecordKind.Added, record!.Kind);
        Assert.Empty(warnings);
        var history = PublishHistory.Load(Location(game, mod).Folder, mod.Id).History;
        var entry = Assert.Single(history.Exports);
        Assert.Equal(time, entry.Time);
        Assert.Equal(result.Packages[0].Sha256, Assert.Single(entry.Packages).Sha256);
        Assert.Equal(time, history.CheckOf(ExportTarget.Vanilla.Id)!.Time);
    }

    /// <summary>A conflict stops before the write. The checks of the prepare still go into the history, and the
    /// packages of the version do not change.</summary>
    [Fact]
    public void AConflictRecordsTheChecksAndWritesNothing()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var first = GuardedExport.Prepare(Request(game, mod), Location(game, mod), DateTime.UtcNow);
        var written = first.Write();
        var package = written.Packages[0].Path!;
        first.RecordExport(written, replace: false);
        var before = File.ReadAllBytes(package);
        File.WriteAllText(Path.Combine(mod.Folder, "patches", GoodsPath), "[Good]\nnickname = lf_hull\nprice = 4400\n");

        var second = GuardedExport.Prepare(Request(game, mod), Location(game, mod), DateTime.UtcNow);
        var warnings = second.RecordChecks();

        Assert.Equal(GuardKind.Exported, second.Guard.Kind);
        Assert.True(second.Guard.CanReplace);
        Assert.Empty(warnings);
        Assert.Equal(before, File.ReadAllBytes(package));
        var history = PublishHistory.Load(Location(game, mod).Folder, mod.Id).History;
        Assert.Single(history.Exports);
        Assert.Equal(second.Prepared!.Content, history.CheckOf(ExportTarget.Vanilla.Id)!.Content);
    }

    /// <summary>Without a readable history, the guard cannot protect the packages, so the export does not
    /// prepare.</summary>
    [Fact]
    public void AHistoryThatDoesNotLoadStopsBeforeThePrepare()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var location = Location(game, mod);
        Directory.CreateDirectory(location.Folder);
        File.WriteAllText(Path.Combine(location.Folder, PublishHistory.FileName), "{ not json");

        var export = GuardedExport.Prepare(Request(game, mod), location, DateTime.UtcNow);

        Assert.Equal(PublishMessages.GuardUnreadable(Path.Combine(location.Folder, PublishHistory.FileName)), export.Refusal);
        Assert.Null(export.Prepared);
        Assert.Empty(export.RecordChecks());
        Assert.Throws<InvalidOperationException>(() => export.Write());
        Assert.Equal(["publish-history.json"], Directory.EnumerateFileSystemEntries(location.Folder).Select(Path.GetFileName));
    }
}

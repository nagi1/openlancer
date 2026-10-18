using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Games;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The game link of milestone M11a on the retail data (section 14.2 of the design): the check of the retail base, the
/// root search from the developer layout, a copy with a changed file, the first-run profile, and <c>oltool game</c>.
/// The tests read the base folder, and copy files only to a temporary folder.
/// </summary>
public sealed class GameLinkRetailTests(ITestOutputHelper output)
{
    [Fact]
    public void TheRetailBaseIsFreelancer10WithUnchangedFiles()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var clock = Stopwatch.StartNew();

        var report = Detect(baseFolder);

        output.WriteLine($"The check took {clock.ElapsedMilliseconds} ms.");
        foreach (var line in report.Evidence)
            output.WriteLine(line);
        Assert.Equal(GameKind.Vanilla, report.Kind);
        Assert.Equal("1.0", report.Version);
        Assert.Equal(VersionProof.Hash, report.Proof);
        Assert.Equal("vanilla-1.0", report.Build);
        Assert.Equal(ModTargets.Vanilla, report.Target);
        Assert.Equal(DataState.NotChanged, report.Data);
        Assert.Equal(1255, report.ComparedFiles);
        Assert.Equal(0, report.ChangedFiles);
        Assert.Null(report.ExePath);
        Assert.False(report.Jflp);
        Assert.Equal("Freelancer 1.0", report.Name);
        Assert.Equal(GameSeverity.Ok, report.Severity);
        Assert.Contains(new GameFinding(GameSeverity.Info, GameTexts.GameDataOnly), report.Findings);
        Assert.Contains(new GameFinding(GameSeverity.Info, GameTexts.OnePointZeroTarget), report.Findings);
        Assert.Contains(@"EXE\Common.dll: file version 1.0.1223.11.", report.Evidence);
        Assert.Contains(report.Evidence, e => e.StartsWith(@"EXE\Common.dll: 1769984 bytes") && e.EndsWith("the hash of vanilla-1.0."));
        Assert.Contains(report.Evidence, e => e.StartsWith("EXE\\dacom.ini [Libraries]: 14 active lines"));
    }

    [Fact]
    public void TheRetailReportShowsEachNoteOnceAndADetailRowWithACheckForEachLine()
    {
        var report = Detect(GameDataPaths.RequireBase());

        var (shown, more) = GameDetails.Split(GameDetails.Notes(report));
        var rows = GameDetails.Rows(report);

        foreach (var row in rows)
            output.WriteLine($"{row.Check} | {row.Result}");
        // The two notes of the base showed above Details and again in Details. Now each one shows once.
        Assert.Contains(shown, f => f.Text == GameTexts.GameDataOnly);
        Assert.Contains(shown, f => f.Text == GameTexts.OnePointZeroTarget);
        Assert.Equal(report.Findings.Distinct().Count(), shown.Count);
        Assert.Empty(more);
        Assert.Equal(report.Evidence.Count, rows.Count);
        Assert.All(rows, r => Assert.NotEqual("", r.Check));
        Assert.Equal(GameDetector.CodeFiles.Length, rows.Count(r => r.Hash != null));
        Assert.Contains(rows, r => r.Check == @"EXE\Common.dll" && r.Result.StartsWith("1769984 bytes, SHA-256 ") &&
                                   r.Result.EndsWith("..." + r.Hash![^4..] + ", the hash of vanilla-1.0."));
    }

    [Fact]
    public void TheGameDataFolderGivesTheSameRoot()
    {
        var baseFolder = GameDataPaths.RequireBase();

        Assert.Equal(baseFolder, Detect(Path.GetDirectoryName(baseFolder)!).Root);
        Assert.Equal(baseFolder, Detect(Path.Combine(baseFolder, "DATA")).Root);
        Assert.Equal(baseFolder, Detect(Path.Combine(baseFolder, "DATA", "EQUIPMENT", "goods.ini")).Root);
    }

    [Fact]
    public void TheCheckDoesNotChangeTheRetailBase()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var before = Stamps(baseFolder);

        Detect(baseFolder);
        GameDetector.HashLines(baseFolder);

        Assert.Equal(DataDigest.RetailV1FileCount, before.Count);
        Assert.Equal(before, Stamps(baseFolder));
    }

    [Fact]
    public void ACopyWithOneChangedIniIsChanged()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var copy = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"), "Freelancer");
        try
        {
            CopyGameFiles(baseFolder, copy);
            var same = Detect(copy);
            Assert.Equal(DataState.NotChanged, same.Data);
            Assert.Equal("vanilla-1.0", same.Build);
            Assert.Equal(VersionProof.Hash, same.Proof);

            var goods = PathRules.WithDiskCase(copy, @"DATA\EQUIPMENT\goods.ini");
            var bytes = File.ReadAllBytes(Path.Combine(copy, goods));
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(Path.Combine(copy, goods), bytes);
            var changed = Detect(copy);

            Assert.Equal(DataState.Changed, changed.Data);
            Assert.Equal(["data/equipment/goods.ini (changed)"], changed.DataFiles);
            Assert.Contains(new GameFinding(GameSeverity.Warning, GameTexts.ChangedData(1, "Freelancer 1.0")), changed.Findings);
        }
        finally
        {
            Delete(Path.GetDirectoryName(copy)!);
        }
    }

    [Fact]
    public void TheFirstRunProfileOnTheRetailBaseOpensAndSavesOutsideTheBase()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var workspace = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var report = Detect(baseFolder);
            var (path, modId) = ModWorkspace.CreateProfile(workspace, report.ProfileName, report.Root!, [report.Root!]);
            Assert.Equal(Path.Combine(workspace, "profiles", "freelancer-1-0.json"), path);

            using (var session = EditSession.Open(ModProfile.Load(path), modId, DllStringResourceWriter.Instance,
                       ThornEvaluator.Instance))
            {
                Assert.True(session.CanEdit, session.ReadOnlyReason);
                Assert.False(session.EditView.HasErrors);
                var good = session.Find(ItemFileKind.Goods, "lf_hull")!;
                session.Execute(EditCommand.SetLines(good, "price", [[new Int32Value(1234)]]));
                var result = session.Save();
                Assert.True(result.Saved, string.Join("\n", result.Problems));
                Assert.All(result.Written, f => Assert.StartsWith(Path.Combine(workspace, "mods", modId), f));
            }

            RetailChecks.AssertBaseIsUnchanged(baseFolder);
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public void OltoolGamePrintsTheRetailReport()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var empty = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var (exit, json, errors) = Oltool.Run("game", baseFolder, "--json");
            Assert.True(exit == 0, errors);
            using var document = JsonDocument.Parse(json);
            Assert.Equal("vanilla-1.0", document.RootElement.GetProperty("Build").GetString());
            Assert.Equal("vanilla-1.1", document.RootElement.GetProperty("Target").GetString());
            Assert.Equal("Vanilla", document.RootElement.GetProperty("Kind").GetString());

            var (textExit, text, _) = Oltool.Run("game", baseFolder);
            Assert.Equal(0, textExit);
            Assert.Equal(
            [
                "Game: Freelancer 1.0",
                $"Root: {baseFolder}",
                "Build: vanilla-1.0 (proof: hash)",
                "Export target: vanilla-1.1",
                "Game program: not found (game data only)",
                "INI files: not changed (1255 INI files compared with vanilla-1.0)",
                $"Info: {GameTexts.GameDataOnly}",
                $"Info: {GameTexts.OnePointZeroTarget}",
            ], text.ReplaceLineEndings("\n").TrimEnd().Split('\n'));

            var (hashesExit, hashes, _) = Oltool.Run("game", baseFolder, "--hashes");
            Assert.Equal(0, hashesExit);
            Assert.Contains("exe/common.dll\t1769984\te6c08a7cfdfc5cf8c5d00047cc0b5e361bcaf31cdd91fe89f384d5920dd8c483", hashes);

            Assert.Equal(1, Oltool.Run("game", empty).Exit);
            Assert.Equal(2, Oltool.Run("game", Path.Combine(empty, "missing")).Exit);
            Assert.Equal(2, Oltool.Run("game", baseFolder, "--wrong").Exit);
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    /// <summary>The check with the embedded build manifests.</summary>
    private static GameReport Detect(string folder) =>
        GameDetector.Detect(folder, cancel: TestContext.Current.CancellationToken);

    /// <summary>The size, the write time and the attributes of each file.</summary>
    private static Dictionary<string, (long, DateTime, FileAttributes)> Stamps(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => f, f =>
        {
            var info = new FileInfo(f);
            return (info.Length, info.LastWriteTimeUtc, info.Attributes);
        });

    /// <summary>
    /// Copies the files that the check reads: the EXE folder, the code file of DLLS\BIN and the INI files of DATA with
    /// the universe file (about 23 MB). The copy clears the read-only attribute of the retail files.
    /// </summary>
    private static void CopyGameFiles(string baseFolder, string copy)
    {
        var files = Directory.EnumerateFiles(Path.Combine(baseFolder, "EXE"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(baseFolder, "DATA"), "*.ini", SearchOption.AllDirectories))
            .Append(Path.Combine(baseFolder, PathRules.WithDiskCase(baseFolder, @"DLLS\BIN\Content.dll")));
        foreach (var file in files)
        {
            var target = Path.Combine(copy, Path.GetRelativePath(baseFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
    }

    private static void Delete(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(folder, true);
    }
}

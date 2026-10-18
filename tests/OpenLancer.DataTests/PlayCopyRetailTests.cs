using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The play copy of the retail data with the sample mod (section 8.11 of the M11 design). The data folder is only read.
/// A snapshot through handles of each linked file before and after proves that the play copy changes no file of the
/// game. The retail files are read-only, so the remove of the play copy proves the flags that ignore the read-only
/// attribute too. The play copy goes into the output folder next to the data folder, on its volume. Each test removes it
/// at the end.
/// </summary>
public sealed class PlayCopyRetailTests(ITestOutputHelper output)
{
    static PlayCopyRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void ARetailCopyMakesAndInstallsTheSample()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var report = GameDetector.CheckRoot(baseFolder, cancel: TestContext.Current.CancellationToken);
        var folder = PlayCopyFolder(baseFolder);
        var before = LinkStamps(baseFolder);
        try
        {
            var clock = Stopwatch.StartNew();
            using var copy = PlayCopy.Make(report.Base!, folder, PlayCopyMode.Links, report.Build);
            output.WriteLine($"Make: {clock.Elapsed.TotalSeconds:0.00} s, {copy.Record.LinkCount} links, " +
                             $"{copy.Record.CopyCount} copies, {PlayCopyTexts.Size(copy.SizeOnDisk)} on disk.");
            var build = Build(report);

            clock.Restart();
            var first = copy.Install(build.Files, null);
            output.WriteLine($"First install: {clock.Elapsed.TotalSeconds:0.00} s, {first.Written} files written.");
            clock.Restart();
            var second = copy.Install(build.Files, null);
            output.WriteLine($"Second install: {clock.Elapsed.TotalSeconds:0.00} s, {second.Written} files written.");

            Assert.Empty(first.Problems);
            Assert.Empty(second.Problems);
            Assert.Equal(0, second.Written);
            Assert.Equal(build.Files.Files.Count, copy.Record.Installed.Count);
            Assert.True(copy.Record.LinkCount > 4000, $"{copy.Record.LinkCount} links");
            Assert.Contains("DLL = openlancer_strings.dll", File.ReadAllText(Path.Combine(folder, @"EXE\freelancer.ini")));
            Assert.Empty(copy.Check().Problems);
            var uninstall = copy.Uninstall();
            Assert.Equal(build.Files.Files.Count, uninstall.Restored);
            Assert.Empty(copy.Check().Problems);
            copy.Remove();
        }
        finally
        {
            Remove(folder);
        }

        Assert.False(Directory.Exists(folder));
        Assert.Equal(before, LinkStamps(baseFolder));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void TheInstalledFilesEqualTheOverlayFiles()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var report = GameDetector.CheckRoot(baseFolder, cancel: TestContext.Current.CancellationToken);
        var folder = PlayCopyFolder(baseFolder);
        try
        {
            using var copy = PlayCopy.Make(report.Base!, folder, PlayCopyMode.Links, report.Build);
            var build = Build(report);

            copy.Install(build.Files, null);

            foreach (var file in build.Files.Files)
            {
                var path = Path.Combine(folder, PathRules.WithDiskCase(folder, file.Path));
                Assert.True(file.Bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)), file.Path);
                Assert.False(new FileInfo(path).IsReadOnly, file.Path);
            }

            // The merged text cutscene of D321 is in the play copy as Lua text.
            Assert.Contains("pos = { 59.658787, 86.31464, 97.57275 }",
                File.ReadAllText(Path.Combine(folder, @"DATA\Scripts\Bases\li_01_cityscape_hardpoint_01.thn")));
            Assert.Equal(GameKind.NotAGame, GameDetector.CheckRoot(folder, cancel: TestContext.Current.CancellationToken).Kind);
            copy.Remove();
        }
        finally
        {
            Remove(folder);
        }
    }

    /// <summary>The merge of the sample mod on the game, as Play in the game makes it.</summary>
    internal static PlayBuildResult Build(GameReport report)
    {
        var sample = Mod.Load(GameDataPaths.SampleMod("ol-sample"));
        Assert.Null(PlayBuild.Refusal(report, [sample]));
        var build = PlayBuild.Run(report, [sample], DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        Assert.DoesNotContain(build.Problems, p => p.Severity == DiagnosticSeverity.Error);
        return build;
    }

    /// <summary>A new play copy folder in the output folder next to the data folder, on the volume of the data.</summary>
    private static string PlayCopyFolder(string baseFolder) =>
        Path.Combine(GameDataPaths.OutFolder(baseFolder), "playcopy-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Removes a play copy that a failed test left. A folder without a record stays.</summary>
    internal static void Remove(string folder)
    {
        if (PlayCopyRecord.Exists(folder))
            PlayCopy.Remove(folder);
    }

    /// <summary>The size, the attributes, the creation time and the write time of each file of the game that a play copy
    /// links, read through handles. The text files, which a play copy copies, also give their SHA-256.</summary>
    internal static SortedDictionary<string, string> LinkStamps(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, _) in PlayCopyPlan.Files(root))
        {
            var path = Path.Combine(root, relative);
            var stamp = HardLinks.Instance.Stamp(path);
            var text = PlayCopyPlan.KindOf(relative, GameLayout.Standard, PlayCopyMode.Links) == PlayCopyKind.Copy;
            result[relative] = $"{stamp.Size} {stamp.Attributes} {stamp.CreationTime.Ticks} {stamp.WriteTime.Ticks}" +
                               (text ? " " + DataDigest.HashFile(path) : "");
        }
        return result;
    }
}

/// <summary>
/// The play copy of HD Edition 0.7.1 with the sample mod (section 8.11 of the M11 design). The tests need
/// <c>OPENLANCER_HDE</c>, a copy of an HD Edition install, and only read it. They skip without it. The play copy goes
/// next to that copy, with hard links when its volume allows them, else as a full copy.
/// </summary>
public sealed class HdePlayCopyRetailTests(ITestOutputHelper output)
{
    static HdePlayCopyRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void AnHdCopyInstallsTheSample()
    {
        var root = GameDataPaths.RequireHde();
        var report = GameDetector.CheckRoot(root, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(ModTargets.HdEdition, report.Target);
        var before = PlayCopyRetailTests.LinkStamps(root);
        var folder = Folder(root);
        try
        {
            using var copy = Make(report, folder);
            var build = PlayCopyRetailTests.Build(report);

            var clock = Stopwatch.StartNew();
            var result = copy.Install(build.Files, null);
            output.WriteLine($"Install: {clock.Elapsed.TotalSeconds:0.00} s, {result.Written} files written, " +
                             $"{result.Links} links, {result.Copies} real files.");

            Assert.Empty(result.Problems);
            var ini = File.ReadAllText(Path.Combine(folder, @"EXE\freelancer.ini"));
            var resources = ini[ini.IndexOf("[Resources]", StringComparison.OrdinalIgnoreCase)..];
            var dlls = resources.Split("\r\n").TakeWhile(l => !l.StartsWith('[') || l.StartsWith("[Resources]"))
                .Where(l => l.TrimStart().StartsWith("DLL", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal(8, dlls.Count);
            Assert.Contains("openlancer_strings.dll", dlls[^1]);
            Assert.Empty(copy.Check().Problems);
            copy.Remove();
        }
        finally
        {
            PlayCopyRetailTests.Remove(folder);
        }

        Assert.Equal(before, PlayCopyRetailTests.LinkStamps(root));
    }

    [Fact]
    public void TheHdCopyHasSaveInDirectory()
    {
        var root = GameDataPaths.RequireHde();
        var report = GameDetector.CheckRoot(root, cancel: TestContext.Current.CancellationToken);
        var source = File.ReadAllBytes(Path.Combine(root, @"EXE\flplusplus.ini"));
        var folder = Folder(root);
        try
        {
            using var copy = Make(report, folder);

            var text = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(folder, @"EXE\flplusplus.ini")));
            Assert.Contains("save_in_directory = true", text);
            Assert.DoesNotContain("save_in_directory = false", text);
            Assert.True(copy.SavesInCopy);
            Assert.True(Directory.Exists(Path.Combine(folder, "SAVE")));
            // The edit changes one line, so the file keeps its comments.
            Assert.Equal(Encoding.Latin1.GetString(source).Split("\r\n").Count(l => l.StartsWith(';')),
                text.Split("\r\n").Count(l => l.StartsWith(';')));
            copy.Remove();
        }
        finally
        {
            PlayCopyRetailTests.Remove(folder);
        }

        Assert.Equal(source, File.ReadAllBytes(Path.Combine(root, @"EXE\flplusplus.ini")));
    }

    /// <summary>A new play copy folder next to the copy of HD Edition.</summary>
    private static string Folder(string root) =>
        Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + " - play test " + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Makes the play copy with hard links when the volume allows them, else a full copy.</summary>
    private PlayCopy Make(GameReport report, string folder)
    {
        var mode = PlayCopy.Probe(report.Base!, folder) == null ? PlayCopyMode.Links : PlayCopyMode.Full;
        output.WriteLine($"Play copy mode: {mode}.");
        return PlayCopy.Make(report.Base!, folder, mode, report.Build);
    }
}

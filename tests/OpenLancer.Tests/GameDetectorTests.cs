using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The game folder check (sections 6.2 to 6.4 of the M11a design) on fake game folders and fake build manifests. No test
/// uses a game file.
/// </summary>
public sealed class GameDetectorTests
{
    private static readonly Version RetailVersion = new(1, 0, 1223, 11);

    private static bool Has(GameReport report, GameSeverity severity, string text) =>
        report.Findings.Contains(new GameFinding(severity, text));

    // Section 6.2: the root must hold a complete game.

    [Fact]
    public void AMissingDataPathIsNotAGame()
    {
        using var game = new FakeGame();
        game.Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\nodata\n\n[Data]\nuniverse = universe\\universe.ini\n");

        var report = game.Detect();

        Assert.Equal(GameKind.NotAGame, report.Kind);
        Assert.Equal(GameTexts.DataPathMissing(@"..\nodata"), report.Error);
    }

    [Fact]
    public void AMissingUniverseFileIsNotAGame()
    {
        using var game = new FakeGame();
        game.Delete(@"DATA\universe\universe.ini");

        var report = game.Detect();

        Assert.Equal(GameKind.NotAGame, report.Kind);
        Assert.StartsWith("The game data is not complete. The file ", report.Error);
        Assert.Contains("universe.ini", report.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingResourcesDllIsNotAGame()
    {
        using var game = new FakeGame();
        game.Delete(@"EXE\resources.dll");

        Assert.Equal(GameTexts.NotComplete(@"EXE\resources.dll"), game.Detect().Error);
    }

    [Fact]
    public void AnHdeFileSetWithoutResourcesDllIsNotComplete()
    {
        // The P2 layout: the text fixes of HD Edition without the files of the retail game (decision D212).
        using var game = new FakeGame();
        game.Delete(@"EXE\resources.dll");
        game.WriteBytes(@"EXE\resources_tsr.dll", FakeGame.TextDll(new() { [1] = "fixed text" }));
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "HDE v0.7.1" }));
        game.Resources("Controls.dll");

        var report = game.Detect();

        Assert.Equal(GameKind.NotAGame, report.Kind);
        Assert.Equal(GameTexts.NotComplete(@"EXE\resources.dll"), report.Error);
    }

    // Rule G6: the code files.

    [Fact]
    public void CodeFilesOfTheFakeOnePointZeroBuildGiveFreelancer10ByHash()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));

        var report = game.Detect(builds);

        Assert.Equal(GameKind.Vanilla, report.Kind);
        Assert.Equal("1.0", report.Version);
        Assert.Equal(VersionProof.Hash, report.Proof);
        Assert.Equal("vanilla-1.0", report.Build);
        Assert.Equal(ModTargets.Vanilla, report.Target);
        Assert.Equal(DataState.NotChanged, report.Data);
        Assert.Equal("Freelancer 1.0", report.Name);
        Assert.True(Has(report, GameSeverity.Info, GameTexts.OnePointZeroTarget));
        Assert.Equal(GameSeverity.Ok, report.Severity);
        Assert.Contains(report.Evidence, e => e.StartsWith(@"EXE\Common.dll: ") && e.EndsWith("the hash of vanilla-1.0."));
    }

    [Fact]
    public void CodeFilesOfTheFakeOnePointOneBuildGiveFreelancer11ByHash()
    {
        using var game = new FakeGame();
        var oneZero = game.Manifest();
        game.CodeFiles("1.1");
        var builds = FakeGame.Builds(("vanilla-1.0", oneZero), ("vanilla-1.1", game.Manifest(codeFilesOnly: true)));

        var report = game.Detect(builds);

        Assert.Equal(GameKind.Vanilla, report.Kind);
        Assert.Equal("1.1", report.Version);
        Assert.Equal(VersionProof.Hash, report.Proof);
        Assert.Equal("vanilla-1.1", report.Build);
        Assert.Equal(ModTargets.Vanilla, report.Target);
        Assert.Equal("Freelancer 1.1", report.Name);
    }

    [Fact]
    public void OnePointOneDataIsComparedWithOnePointZero()
    {
        using var game = new FakeGame();
        var oneZero = game.Manifest();
        game.CodeFiles("1.1");
        var builds = FakeGame.Builds(("vanilla-1.0", oneZero), ("vanilla-1.1", game.Manifest(codeFilesOnly: true)));

        var report = game.Detect(builds);

        // The 1.1 manifest lists no INI file, so the INI files come from the manifest of 1.0 (open question Q2).
        Assert.Equal(DataState.NotChanged, report.Data);
        Assert.Equal(5, report.ComparedFiles);
        Assert.True(Has(report, GameSeverity.Info, GameTexts.OnePointOneData));
    }

    [Fact]
    public void CodeFilesOfAnHdeBuildGiveHdEditionByHash()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("hde-0.7.1", game.Manifest()));

        var report = game.Detect(builds);

        Assert.Equal(GameKind.HdEdition, report.Kind);
        Assert.Equal("0.7.1", report.Version);
        Assert.Equal(VersionProof.Hash, report.Proof);
        Assert.Equal(ModTargets.HdEdition, report.Target);
        Assert.Equal(DataState.NotChanged, report.Data);
    }

    [Fact]
    public void ACommonDllWithAnUnknownHashAndTheRetailVersionIsFreelancer10ByFileVersionWithAWarning()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(RetailVersion));

        var report = game.Detect(builds);

        Assert.Equal(GameKind.Vanilla, report.Kind);
        Assert.Equal("1.0", report.Version);
        Assert.Equal(VersionProof.FileVersion, report.Proof);
        Assert.Equal("vanilla-1.0", report.Build);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.OnePointZeroCodeChanged));
        Assert.Equal(GameSeverity.Warning, report.Severity);
        Assert.Contains(@"EXE\Common.dll: file version 1.0.1223.11.", report.Evidence);
    }

    [Fact]
    public void AHigherFileVersionGivesFreelancer11ByFileVersionWithAnInfo()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(new Version(1, 0, 1224, 0)));

        var report = game.Detect(builds);

        Assert.Equal(GameKind.Vanilla, report.Kind);
        Assert.Equal("1.1", report.Version);
        Assert.Equal(VersionProof.FileVersion, report.Proof);
        Assert.True(Has(report, GameSeverity.Info, GameTexts.OnePointOneByFileVersion("1.0.1224.0")));
        Assert.True(Has(report, GameSeverity.Info, GameTexts.OnePointOneData));
        Assert.Equal("vanilla-1.0", report.Reference);
        Assert.Equal(GameSeverity.Ok, report.Severity);
    }

    [Fact]
    public void NoHashAndNoVersionGiveAnUnknownGame()
    {
        using var game = new FakeGame();

        var report = game.Detect();

        Assert.Equal(GameKind.Unknown, report.Kind);
        Assert.Equal("unknown", report.Build);
        Assert.Null(report.Target);
        Assert.Equal("Unknown Freelancer version", report.Name);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.UnknownVersion));
    }

    [Fact]
    public void SomeMissingCodeFilesGiveAnUnknownGame()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        game.Delete(@"EXE\DALib.dll");

        var report = game.Detect(builds);

        Assert.Equal(GameKind.Unknown, report.Kind);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.CodeFilesMissing("DALib.dll")));
        Assert.Contains(@"Missing code files: EXE\DALib.dll.", report.Evidence);
    }

    [Fact]
    public void ADataCopyWithoutCodeFilesIsVanillaWithAnUnknownVersion()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        foreach (var file in GameDetector.CodeFiles)
            game.Delete(file);

        var report = game.Detect(builds);

        Assert.Equal(GameKind.Vanilla, report.Kind);
        Assert.Null(report.Version);
        Assert.Equal("Freelancer (version unknown)", report.Name);
        Assert.Equal("Freelancer", report.ShortName);
        Assert.True(Has(report, GameSeverity.Info, GameTexts.NoCodeFiles));
        // The INI files are compared with 1.0. The manifest lists the code files, but they are not INI files.
        Assert.Equal(DataState.NotChanged, report.Data);
    }

    // Rule G7: JFLP.

    [Fact]
    public void JflpLineAndFileGiveTheJflpFlag()
    {
        using var game = new FakeGame();
        game.Libraries("jflp.dll\t; Jason's Freelancer Patch");
        game.WriteBytes(@"EXE\jflp.dll", FakeGame.VersionDll(new Version(1, 27, 0, 0)));
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));

        var report = game.Detect(builds);

        Assert.True(report.Jflp);
        Assert.Equal("1.27.0.0", report.JflpVersion);
        Assert.Equal("Freelancer 1.0 with JFLP", report.Name);
        Assert.Equal(ModTargets.Vanilla, report.Target);
        Assert.DoesNotContain(report.Findings, f => f.Text.StartsWith("Plugins"));
    }

    [Fact]
    public void AJflpLineWithoutTheFileGivesAWarning()
    {
        using var game = new FakeGame();
        game.Libraries("jflp.dll");

        var report = game.Detect();

        Assert.False(report.Jflp);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.JflpDllMissing));
    }

    [Fact]
    public void AJflpFileWithoutTheLineGivesNoFlag()
    {
        using var game = new FakeGame();
        game.WriteBytes(@"EXE\jflp.dll", FakeGame.VersionDll(new Version(1, 27, 0, 0)));

        var report = game.Detect();

        Assert.False(report.Jflp);
        Assert.DoesNotContain(report.Findings, f => f.Text.Contains("jflp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangedDataWithJflpSaysThatJflpChangesFiles()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        game.Libraries("jflp.dll");
        game.WriteBytes(@"EXE\jflp.dll", FakeGame.VersionDll(new Version(1, 27, 0, 0)));

        var report = game.Detect(builds);

        Assert.True(Has(report, GameSeverity.Warning,
            GameTexts.ChangedData(1, "Freelancer 1.0") + " " + GameTexts.JflpChangesFiles));
    }

    [Fact]
    public void LibraryLinesWithArgumentsAndCommentsGiveTheirDllNames()
    {
        const string text = "[System]\nx.dll\n[Libraries]\n;;;dosfile.dll\nReadFile.dll\t; reads files\n.\\flmaterials.dll\n" +
                            "Zone.dll upper\nTurretZoom.dll inverse max=1000\n;HudStatus.dll\n@include FL_DevOnlyLibs.ini\n" +
                            "\n[Software]\ny.dll\n";

        var names = DacomLibraries.Read(new StringReader(text));

        Assert.Equal(["ReadFile.dll", "flmaterials.dll", "Zone.dll", "TurretZoom.dll"], names);
    }

    // Rule G5: HD Edition.

    [Fact]
    public void ControlsDllWithTheHdeTextGivesHdEdition071AndItsTarget()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll ; controls and the custom version number");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "Joystick", [2] = "Freelancer: HD Edition v0.7.1" }));

        var report = game.Detect();

        Assert.Equal(GameKind.HdEdition, report.Kind);
        Assert.Equal("0.7.1", report.Version);
        Assert.Equal(VersionProof.VersionText, report.Proof);
        Assert.Equal("hde-0.7.1", report.Build);
        Assert.Equal(ModTargets.HdEdition, report.Target);
        Assert.Equal("Freelancer: HD Edition 0.7.1", report.Name);
        Assert.Equal("HD Edition 0.7.1", report.ShortName);
        Assert.Contains(report.Evidence, e => e.StartsWith("Controls.dll: string 2 matches the HD Edition version"));
        // The code files of the fake are no known build, but HD Edition takes its kind from the marker.
        Assert.DoesNotContain(report.Findings, f => f.Text == GameTexts.UnknownVersion);
    }

    [Fact]
    public void HdeWithoutAManifestIsNotCompared()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "HDE v0.7.1" }));

        var report = game.Detect(FakeGame.Builds(("vanilla-1.0", game.Manifest())));

        Assert.Equal(DataState.NotCompared, report.Data);
        Assert.True(Has(report, GameSeverity.Info, GameTexts.HdeNotCompared));
        Assert.Equal("INI files: not compared", report.DataText);
    }

    [Fact]
    public void HdeResourcesDllWithTheHdeTextGivesHdEdition()
    {
        using var game = new FakeGame();
        game.Resources("HDEResources.dll");
        game.WriteBytes(@"EXE\HDEResources.dll", FakeGame.TextDll([], new() { [5] = "<RDL><TEXT>HD Edition 0.8.0</TEXT></RDL>" }));

        var report = game.Detect();

        Assert.Equal(GameKind.HdEdition, report.Kind);
        Assert.Equal("0.8.0", report.Version);
    }

    [Theory]
    [InlineData("Freelancer v1.1 HDE v0.7.1", "0.7.1")]
    [InlineData("HD Edition 0.7.1", "0.7.1")]
    [InlineData("hdedition v0.7", "0.7")]
    [InlineData("v0.7.1", null)]
    [InlineData("Freelancer v1.1", null)]
    public void TheGameVersionInTheTextIsNotTheHdeVersion(string text, string? version)
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.Libraries("flplusplus.dll");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = text }));

        var report = game.Detect();

        Assert.Equal(GameKind.HdEdition, report.Kind);
        Assert.Equal(version, report.Version);
    }

    [Fact]
    public void ControlsDllWithoutTheTextButWithFlplusplusIsHdeWithVersionUnknown()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.Libraries("flplusplus.dll");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "Joystick" }));

        var report = game.Detect();

        Assert.Equal(GameKind.HdEdition, report.Kind);
        Assert.Null(report.Version);
        Assert.Equal("hde", report.Build);
        Assert.Null(report.Target);
        Assert.Equal("Freelancer: HD Edition (version unknown)", report.Name);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.HdeVersionUnknown("Controls.dll")));
        Assert.Contains(report.Evidence, e => e.Contains("Joystick"));
    }

    [Fact]
    public void ControlsDllWithoutTheTextAndWithoutHdePluginsIsNotHde()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "Joystick" }));
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));

        var report = game.Detect(builds);

        Assert.Equal(GameKind.Vanilla, report.Kind);
        Assert.Contains(report.Evidence, e => e.Contains("is not the HD Edition marker"));
    }

    [Fact]
    public void AnotherHdeVersionGivesAWarningAndNoTarget()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "HDE v0.8" }));

        var report = game.Detect();

        Assert.Equal("0.8", report.Version);
        Assert.Null(report.Target);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.OtherHdeVersion("0.8")));
    }

    [Fact]
    public void AnHdeLineWithoutTheDllGivesAWarning()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.Libraries("FLSharp.dll");

        var report = game.Detect();

        Assert.Equal(GameKind.HdEdition, report.Kind);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.HdeDllMissing("Controls.dll")));
    }

    [Fact]
    public void HdePluginsGoToTheEvidenceOnly()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.Libraries("jflp.dll", "flplusplus.dll", "Zone.dll upper");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "HDE v0.7.1" }));

        var report = game.Detect();

        Assert.DoesNotContain(report.Findings, f => f.Text.StartsWith("Plugins in dacom.ini"));
        Assert.Contains(report.Evidence, e => e.StartsWith("Plugins in dacom.ini: flplusplus.dll, Zone.dll."));
        // HD Edition includes JFLP, so the name does not name it.
        Assert.False(report.Jflp);
        Assert.Equal("Freelancer: HD Edition 0.7.1", report.Name);
    }

    // Rule G4: total conversions.

    [Fact]
    public void DsLauncherGivesDiscoveryNotSupported()
    {
        using var game = new FakeGame();
        game.WriteBytes("DSLauncher.exe", []);

        var report = game.Detect();

        Assert.Equal(GameKind.NotSupported, report.Kind);
        Assert.Equal("Discovery", report.Product);
        Assert.Equal("Discovery (not supported)", report.Name);
        Assert.Null(report.Target);
        Assert.Equal(DataState.NotCompared, report.Data);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.NotSupported("Discovery")));
        Assert.Equal("discovery", report.ProfileName);
    }

    // Rule G9: the INI files.

    [Fact]
    public void OneChangedIniGivesChangedWithItsPath()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        game.Write(@"DATA\EQUIPMENT\goods.ini", "[Good]\nnickname = g1\nprice = 200\n");

        var report = game.Detect(builds);

        Assert.Equal(DataState.Changed, report.Data);
        Assert.Equal(1, report.ChangedFiles);
        Assert.Equal(["data/equipment/goods.ini (changed)"], report.DataFiles);
        Assert.True(Has(report, GameSeverity.Warning, GameTexts.ChangedData(1, "Freelancer 1.0")));
        Assert.Equal("INI files: 1 changed", report.DataText);
    }

    [Fact]
    public void AMissingIniAndAnExtraIniCountAsChanges()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        game.Delete(@"DATA\SHIPS\shiparch.ini");
        game.Write(@"DATA\EQUIPMENT\new_goods.ini", "[Good]\nnickname = g9\n");

        var report = game.Detect(builds);

        Assert.Equal(2, report.ChangedFiles);
        Assert.Equal(["data/equipment/new_goods.ini (extra)", "data/ships/shiparch.ini (missing)"], report.DataFiles);
    }

    // Rules G8 and G10.

    [Fact]
    public void UnknownLibrariesShowAsPlugins()
    {
        using var game = new FakeGame();
        game.Libraries("MyPlugin.dll");

        var report = game.Detect(FakeGame.Builds(("vanilla-1.0", game.Manifest())));

        Assert.True(Has(report, GameSeverity.Info, GameTexts.Plugins("MyPlugin.dll")));
    }

    [Fact]
    public void AMissingExeGivesGameDataOnly()
    {
        using var game = new FakeGame();

        var withoutExe = game.Detect();
        game.WriteBytes(@"EXE\Freelancer.exe", Encoding.ASCII.GetBytes("not a program"));
        var withExe = game.Detect();

        Assert.Null(withoutExe.ExePath);
        Assert.True(Has(withoutExe, GameSeverity.Info, GameTexts.GameDataOnly));
        Assert.Equal(game.PathOf(@"EXE\Freelancer.exe"), withExe.ExePath);
        Assert.False(Has(withExe, GameSeverity.Info, GameTexts.GameDataOnly));
    }

    // Section 6.7: what the check never does.

    [Fact]
    public void TheCheckChangesNoFile()
    {
        using var game = new FakeGame();
        game.Resources("Controls.dll");
        game.WriteBytes(@"EXE\Controls.dll", FakeGame.TextDll(new() { [1] = "HDE v0.7.1" }));
        game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(RetailVersion));
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        var before = FakeGame.Snapshot(game.Folder);

        game.Detect(builds);
        GameDetector.Detect(game.Folder, builds, TestContext.Current.CancellationToken);
        GameDetector.CheckRoot(game.Root, builds, TestContext.Current.CancellationToken);
        GameDetector.HashLines(game.Root);

        Assert.Equal(before, FakeGame.Snapshot(game.Folder));
    }

    [Fact]
    public void TheCheckWorksOnAReadOnlyFolder()
    {
        using var game = new FakeGame();
        var builds = FakeGame.Builds(("vanilla-1.0", game.Manifest()));
        var writable = game.Detect(builds);
        foreach (var file in Directory.EnumerateFiles(game.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.ReadOnly);

        var readOnly = game.Detect(builds);

        Assert.Equal(writable.Name, readOnly.Name);
        Assert.Equal(writable.Data, readOnly.Data);
        Assert.Equal(writable.Findings, readOnly.Findings);
        Assert.Equal(writable.Evidence, readOnly.Evidence);
    }

    [Fact]
    public void ACancelledCheckStops()
    {
        using var game = new FakeGame();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => GameDetector.Detect(game.Root, FakeGame.Builds(), cancel.Token));
    }

    [Fact]
    public void TheHashLinesNameTheCodeFilesAndTheMarkerDlls()
    {
        using var game = new FakeGame();
        game.WriteBytes(@"EXE\jflp.dll", [1, 2, 3]);

        var lines = GameDetector.HashLines(game.Root);

        Assert.Equal(["exe/common.dll", "exe/server.dll", "exe/dalib.dll", "exe/flserver.exe", "dlls/bin/content.dll",
            "exe/jflp.dll"], lines.Select(l => l.Split('\t')[0]));
        Assert.All(lines, l => Assert.Equal(3, l.Split('\t').Length));
    }

    [Fact]
    public void CheckRootDoesNotSearch()
    {
        using var game = new FakeGame();

        var cancel = TestContext.Current.CancellationToken;

        Assert.True(GameDetector.CheckRoot(game.Root, FakeGame.Builds(), cancel).IsGame);
        Assert.Equal(GameTexts.NotAGameFolder, GameDetector.CheckRoot(game.Folder, FakeGame.Builds(), cancel).Error);
        Assert.True(GameDetector.LooksLikeGame(game.Root));
        Assert.False(GameDetector.LooksLikeGame(game.Folder));
    }

    [Fact]
    public void TheProfileNameFollowsTheBuild()
    {
        using var game = new FakeGame();
        var unknown = game.Detect();
        var oneZero = game.Detect(FakeGame.Builds(("vanilla-1.0", game.Manifest())));
        game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(new Version(1, 0, 1300, 0)));
        var oneOne = game.Detect();

        Assert.Equal("freelancer", unknown.ProfileName);
        Assert.Equal("freelancer-1-0", oneZero.ProfileName);
        Assert.Equal("freelancer-1-1", oneOne.ProfileName);
    }
}

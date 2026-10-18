using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The export run (sections 7.13 to 7.15 of the M11 design): the packages, the texts and the folder rules.</summary>
public sealed class ModExportTests
{
    private const string Patch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    [Fact]
    public void TheExportWritesEachPackageAndTheTexts()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch, "ol-test");

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer));

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(["ol-test-1.2.0-vanilla-1.1.zip", "ol-test-1.2.0-vanilla-1.1-flmm.zip", "ol-test-1.2.0-openlancer.zip"],
            result.Packages.Select(p => p.FileName));
        Assert.All(result.Packages, p => Assert.True(File.Exists(p.Path)));
        var nexus = File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile));
        Assert.Contains("- ol-test-1.2.0-vanilla-1.1.zip (Freelancer 1.1)", nexus);
        Assert.Contains("Microsoft's \"Game Content Usage Rules\"", nexus);
        var checklist = File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.ChecklistFile));
        Assert.Contains("Target: Freelancer 1.1\r\nDATA\\EQUIPMENT\\goods.ini\r\n- Good 'lf_hull': price 6600 -> 5600.", checklist);
    }

    [Fact]
    public void TheOutputFolderMustNotOverlapTheGameOrTheMod()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var before = FakeGame.Snapshot(game.Root);

        var inGame = ModExport.Run(game.Request(mod) with { OutputFolder = Path.Combine(game.Root, "exports") });
        var inMod = ModExport.Run(game.Request(mod) with { OutputFolder = Path.Combine(mod.Folder, "exports") });

        Assert.StartsWith("The export folder", Assert.Single(inGame.Problems).Message);
        Assert.Contains("OpenLancer does not write into the game folder", inGame.Problems[0].Message);
        Assert.Contains("overlaps the mod folder", Assert.Single(inMod.Problems).Message);
        Assert.Equal(before, FakeGame.Snapshot(game.Root));
        Assert.False(Directory.Exists(Path.Combine(mod.Folder, "exports")));
    }

    /// <summary>A game that is not a base of the export, for example the base of the profile or another install, is a
    /// game folder too.</summary>
    [Fact]
    public void TheOutputFolderMustNotBeInAnotherGameFolder()
    {
        using var game = new ExportGame();
        using var other = new FakeGame();
        var mod = game.GoodsMod(Patch);
        var before = FakeGame.Snapshot(other.Root);

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with
        {
            OutputFolder = Path.Combine(other.Root, "DATA", "exports"),
        });

        Assert.Contains($"is in the game folder {other.Root}. OpenLancer does not write into a game folder.",
            Assert.Single(result.Problems).Message);
        Assert.Empty(result.Packages);
        Assert.Equal(before, FakeGame.Snapshot(other.Root));
    }

    [Fact]
    public void AnExportFolderInAGitWorkTreeGivesAWarning()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        Directory.CreateDirectory(Path.Combine(game.Game.Folder, "repo", ".git"));

        var result = ModExport.Run(game.Request(mod) with { OutputFolder = Path.Combine(game.Game.Folder, "repo", "exports") });

        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Warning &&
                                              p.Message == "The export folder is in a git repository. The packages hold " +
                                              "merged game files. Do not commit them.");
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void TheDefaultFolderIsNextToTheProfilesFolder()
    {
        var profile = Path.Combine(Path.GetTempPath(), "w", "profiles", "p.json");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "w", "exports", "m"), ModExport.DefaultFolder(profile, "m"));
    }

    [Fact]
    public void TheChecklistAloneWritesNoFile()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var result = ModExport.Run(game.Request(mod) with { ChecklistOnly = true });

        Assert.Contains("- Good 'lf_hull': price 6600 -> 5600.", result.Checklist);
        Assert.Empty(result.Packages);
        Assert.False(Directory.Exists(result.OutputFolder));
    }
}

/// <summary><c>oltool export</c> and <c>oltool game --write-manifest</c> on a fake game (section 7.14 of the M11
/// design). The tests run the oltool of the test folder.</summary>
public sealed class OltoolExportTests
{
    /// <summary>Runs the oltool of the test folder. The play copy tests use it too.</summary>
    internal static (int Exit, string Output, string Errors) Run(params string[] arguments)
    {
        var tool = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oltool.exe" : "oltool");
        Assert.True(File.Exists(tool), $"{tool} is not in the test folder.");
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "oltool did not end.");
        return (process.ExitCode, output.Result, errors.Result);
    }

    /// <summary>A fake Freelancer 1.0 (by the file version of Common.dll), a mod and a profile. Returns the profile
    /// file.</summary>
    private static string Profile(ExportGame game, string patch = "[Good]\nnickname = lf_hull\nprice = 5600\n")
    {
        game.Game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(new Version(1, 0, 1223, 11)));
        var mod = game.GoodsMod(patch, "ol-test");
        var profile = Path.Combine(game.Game.Folder, "profiles", "test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, JsonSerializer.Serialize(new { @base = game.Root, mods = new[] { mod.Folder } }));
        return profile;
    }

    [Fact]
    public void TheUsageNamesTheCommand()
    {
        var (exit, _, errors) = Run("export");

        Assert.Equal(2, exit);
        Assert.Contains("export <profile.json> --mod <id> --target <hde-0.7.1|vanilla-1.1|openlancer>", errors);
        Assert.Equal(2, Run("export", "x.json", "--mod", "m", "--target", "other").Exit);
    }

    [Fact]
    public void TheExitCodesTellTheResult()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var output = Path.Combine(game.Game.Folder, "out");

        var written = Run("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin", "--out", output);
        var noBase = Run("export", profile, "--mod", "ol-test", "--target", "hde-0.7.1", "--out", output);
        var noMod = Run("export", profile, "--mod", "none", "--target", "vanilla-1.1");

        Assert.True(written.Exit == 0, written.Errors);
        Assert.True(File.Exists(Path.Combine(output, "ol-test-1.2.0-vanilla-1.1.zip")));
        Assert.False(File.Exists(Path.Combine(output, "ol-test-1.2.0-vanilla-1.1-flmm.zip")));
        Assert.Equal(1, noBase.Exit);
        Assert.Contains("No linked game gives the target 'hde-0.7.1'.", noBase.Errors);
        Assert.Equal(2, noMod.Exit);
    }

    [Fact]
    public void TheChecklistIsPrintedAndNothingIsWritten()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var output = Path.Combine(game.Game.Folder, "out");

        var (exit, text, errors) = Run("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--checklist",
            "--out", output);

        Assert.True(exit == 0, errors);
        Assert.Contains("- Good 'lf_hull': price 6600 -> 5600.", text);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void TheProbeHasItsOwnName()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var output = Path.Combine(game.Game.Folder, "out");

        var (exit, _, errors) = Run("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "flmm",
            "--flmm-probe", "--out", output);

        Assert.True(exit == 0, errors);
        Assert.True(File.Exists(Path.Combine(output, "ol-test-1.2.0-vanilla-1.1-flmm-probe.zip")));
    }

    [Fact]
    public void WriteManifestWritesTheFileList()
    {
        using var game = new FakeGame();
        var file = Path.Combine(game.Folder, "list.tsv");

        var (exit, output, errors) = Run("game", game.Root, "--write-manifest", file);
        var inside = Run("game", game.Root, "--write-manifest", Path.Combine(game.Root, "list.tsv"));

        Assert.True(exit == 0, errors);
        Assert.StartsWith("Wrote ", output);
        Assert.Contains("exe/freelancer.ini\t", File.ReadAllText(file));
        Assert.Equal(2, inside.Exit);
        Assert.False(File.Exists(Path.Combine(game.Root, "list.tsv")));
    }
}

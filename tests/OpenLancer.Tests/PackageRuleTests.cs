using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>
/// What a package holds (section 7.8 of the M11 design): only the files of the mod and the merged files that the mod
/// changes, never a game file.
/// </summary>
public sealed class PackageRuleTests
{
    [Fact]
    public void HiddenAndTemporaryFilesAreSkipped()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            [@"files\DATA\MODS\m\a.txt"] = "kept",
            [@"files\DATA\MODS\m\Thumbs.db"] = "x",
            [@"files\DATA\MODS\m\desktop.ini"] = "x",
            [@"files\DATA\MODS\m\.notes"] = "x",
            [@"files\DATA\MODS\.cache\b.txt"] = "x",
            [@"files\DATA\MODS\m\save.oltmp"] = "x",
            [@"files\DATA\MODS\m\c.tmp"] = "x",
            [@"files\DATA\MODS\m\d.txt~"] = "x",
            [@"files\DATA\MODS\m\hidden.txt"] = "x",
        });
        var hidden = Path.Combine(mod.Folder, @"files\DATA\MODS\m\hidden.txt");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var files = game.Build(OpenLancer.Data.Mods.Mod.Load(mod.Folder));

        Assert.Equal([@"DATA\MODS\m\a.txt"], files.Files.Select(f => f.Path));
        Assert.Equal(8, files.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning &&
                                                     d.Message.Contains("hidden, system and temporary files are not part of a package")));
    }

    [Fact]
    public void AFormatOnlyChangeIsLeftOut()
    {
        using var game = new ExportGame();

        // The patch writes the same values in another form.
        var files = game.Build(game.GoodsMod("[Good]\nnickname = g2\nPRICE = 200.0\n"));

        Assert.Empty(files.Files);
        Assert.Empty(files.Diagnostics);
    }

    [Fact]
    public void AWholeIniWithTheBaseContentIsRefused()
    {
        using var game = new ExportGame();
        // The same sections without the comments.
        var copy = "[Good]\nnickname = lf_hull\nprice = 6600\nids_name = 100\n\n[Good]\nnickname = g2\nprice = 200\n";
        var mod = game.Mod("m", new() { [@"files\" + GoodsPath] = copy });

        var files = game.Build(mod);

        Assert.Empty(files.Files);
        var error = Assert.Single(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.EndsWith($"has the content of {GoodsPath} of the game in another form. A mod must not contain game files. " +
                        "Remove the file.", error.Message);
    }

    [Fact]
    public void AWholeIniThatChangesTheBaseGivesAWarning()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new() { [@"files\" + GoodsPath] = "[Good]\nnickname = lf_hull\nprice = 1\n" });

        var files = game.Build(mod);

        var file = Assert.Single(files.Files);
        Assert.True(file.IsModFile);
        Assert.Equal(File.ReadAllBytes(mod.Files[GoodsPath]), file.Bytes);
        Assert.Contains(files.Diagnostics, d => d.Message.StartsWith($"The file replaces {GoodsPath} of the game."));
    }

    [Fact]
    public void AFileWithAManifestHashIsRefusedInEachForm()
    {
        using var game = new ExportGame();
        var gameFile = Encoding.ASCII.GetBytes("bytes of a game file");
        var builds = FakeGame.Builds(("vanilla-1.0",
            $"data/models/ship.cmp\t{gameFile.Length}\t{Convert.ToHexStringLower(SHA256.HashData(gameFile))}\n"));
        var mod = game.Mod("m", new() { [@"files\DATA\MODS\m\ship.cmp"] = "bytes of a game file" });
        var files = game.Build(mod);

        var dropIn = DropInPackage.Write(files, mod.Manifest, game.Target, builds);
        var flmm = FlmmPackage.Write(game.Build(mod, keepPlaceholders: true), mod, game.Target, false, builds);
        var zip = ModZip.Write(mod, builds);

        foreach (var package in new[] { dropIn, flmm, zip })
        {
            Assert.Null(package.Bytes);
            Assert.Contains(package.Problems, p => p.Severity == DiagnosticSeverity.Error &&
                                                   p.Message.EndsWith("has the same bytes as data/models/ship.cmp of Freelancer 1.0. " +
                                                                      "A mod must not contain game files. Remove the file."));
        }
        // An empty file is no game content.
        Assert.Null(PackageRules.GameFile("empty", [], FakeGame.Builds(("vanilla-1.0",
            $"data/empty.ini\t0\t{Convert.ToHexStringLower(SHA256.HashData([]))}\n"))));
    }

    [Fact]
    public void AWholeConfigurationFileIsRefusedForARetailTarget()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            [@"files\EXE\freelancer.ini"] = "[Freelancer]\ndata path = ..\\data\n",
            [@"files\EXE\dacom.ini"] = "[System]\n",
        });

        var result = ModExport.Run(game.Request(mod));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Message == @"A whole EXE\freelancer.ini replaces the lines of the game. Use a " +
                                              "patch with ol.append, or remove the file.");
        Assert.Contains(result.Problems, p => p.Message.StartsWith(@"A whole EXE\dacom.ini replaces the lines of the game."));
        Assert.All(result.Packages, p => Assert.Null(p.Path));
    }
}

/// <summary>The package of the target OpenLancer: a zip of the mod folder (section 7.12 of the M11 design).</summary>
public sealed class ModZipTests
{
    [Fact]
    public void TheZipLeavesOutOlsavePropsAndHiddenFiles()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "strings": { "n": "Name" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nprice = 1\n",
            [@"files\DATA\MODS\m\a.txt"] = "a",
            [@"files\DATA\MODS\m\Thumbs.db"] = "x",
            [@".olsave\recovery.json"] = "{}",
            [@"props\group.ini"] = "[Prop]\n",
            ["notes.txt"] = "my notes",
        });

        var package = ModZip.Write(mod);

        Assert.Equal("m-1.2.0-openlancer.zip", package.FileName);
        using var zip = new ZipArchive(new MemoryStream(package.Bytes!));
        Assert.Equal(["m/files/DATA/MODS/m/a.txt", "m/m README.txt", "m/mod.json", "m/patches/DATA/EQUIPMENT/goods.ini",
            "m/strings.json"], zip.Entries.Select(e => e.FullName).Order(System.StringComparer.Ordinal));
        Assert.Single(package.Problems, p => p.Message.Contains("Thumbs.db"));
    }
}

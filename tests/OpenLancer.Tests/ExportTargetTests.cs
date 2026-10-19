using System;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor;
using OpenLancer.Editor.Export;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The targets of the export, their bases and their refusals (sections 7.1 and 7.9 of the M11 design).</summary>
public sealed class ExportTargetTests
{
    [Fact]
    public void ATier2ModCannotExportToHdEdition()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "tier": 2""");

        Assert.Equal("Mod 'm' is a tier-2 mod. A tier-2 mod runs only in OpenLancer. Pick the target OpenLancer.",
            ExportTarget.HdEdition.Refusal(mod.Manifest));
        Assert.Equal(ExportTarget.HdEdition.Refusal(mod.Manifest), ExportTarget.Vanilla.Refusal(mod.Manifest));
        Assert.Null(ExportTarget.OpenLancer.Refusal(mod.Manifest));
    }

    [Fact]
    public void AModWithoutTheTargetIsRefused()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "targets": [ "hde-0.7.1" ]""");

        Assert.Equal("Mod 'm' does not name the target 'vanilla-1.1'. Add the target to mod.json, or pick another target.",
            ExportTarget.Vanilla.Refusal(mod.Manifest));
        Assert.Null(ExportTarget.HdEdition.Refusal(mod.Manifest));
    }

    [Fact]
    public void ATargetWithoutALinkedBaseIsNotAvailable()
    {
        using var game = new ExportGame();
        var report = game.Report();

        Assert.Equal(ModTargets.Vanilla, report.Target);
        Assert.Null(ExportTarget.Vanilla.BaseRefusal(report));
        Assert.Equal("No linked game gives the target 'hde-0.7.1'. Link a game folder of Freelancer: HD Edition 0.7.1 in " +
                     "File > Game link.", ExportTarget.HdEdition.BaseRefusal(report));
        Assert.Equal("No linked game gives the target 'vanilla-1.1'. Link a game folder of Freelancer 1.0 or 1.1 in " +
                     "File > Game link.", ExportTarget.Vanilla.BaseRefusal(null));
        Assert.Null(ExportTarget.OpenLancer.BaseRefusal(null));
        // The dialog finds the base of each target among the linked games.
        Assert.Same(report, ExportDialog.BaseOf(ExportTarget.Vanilla, [report]));
        Assert.Null(ExportDialog.BaseOf(ExportTarget.HdEdition, [report]));
    }

    [Fact]
    public void TheDialogRequestCarriesThePublishSettings()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n");
        PublishSettings.Write(mod.Folder, PublishSettings.Empty with { Summary = "From publish.json." });

        var request = ExportDialog.RequestOf(mod, [ExportTarget.Vanilla], [game.Report()], game.Game.Folder, ExportForms.DropIn);

        Assert.Equal("From publish.json.", request.Publish.Summary);
        Assert.NotNull(request.BaseOf(ExportTarget.Vanilla));
        Assert.Equal(ExportForms.DropIn, request.Forms);
    }

    [Fact]
    public void AModWithDependenciesIsRefused()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "dependsOn": [ "other" ]""");

        Assert.Equal("Mod 'm' needs other mods. The export of a mod that needs other mods is not available yet.",
            ExportTarget.Vanilla.Refusal(mod.Manifest));
    }

    [Fact]
    public void AChangedBaseGivesAWarning()
    {
        using var game = new ExportGame();
        var builds = game.Builds;
        game.Game.Write(GoodsPath, "[Good]\nnickname = changed\n");

        var report = game.Game.Detect(builds);

        Assert.Equal(DataState.Changed, report.Data);
        Assert.Equal(["The INI files of Freelancer 1.0 differ from the retail files. Players with a clean install can see " +
                      "other results."], ExportTarget.Vanilla.BaseWarnings(report));
    }

    [Fact]
    public void AJflpBaseGivesAWarning()
    {
        using var game = new ExportGame();
        game.Game.Libraries("jflp.dll");
        game.Game.WriteBytes(@"EXE\jflp.dll", FakeGame.VersionDll(new Version(1, 2, 0, 0)));

        var report = game.Report();

        Assert.True(report.Jflp);
        Assert.Contains("The base has JFLP. The drop-in zip carries the JFLP version of each changed file to each player.",
            ExportTarget.Vanilla.BaseWarnings(report));
    }

    [Fact]
    public void TheExportOfARefusedTargetWritesNoFile()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "tier": 2""");

        var result = ModExport.Run(game.Request(mod, ExportTarget.HdEdition, ExportTarget.Vanilla));

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Problems.Count(p => p.Severity == DiagnosticSeverity.Error));
        Assert.All(result.Packages, p => Assert.Null(p.Path));
        Assert.False(Directory.Exists(result.OutputFolder));
    }
}

/// <summary>The choices of the Export dialog in the editor settings (section 7.15 of the M11 design).</summary>
public sealed class ExportSettingsTests
{
    [Fact]
    public void TheChoicesOfEachModStayAfterASaveAndARead()
    {
        var settings = new EditorSettings();
        Assert.Null(settings.ExportOf(@"C:\mods\m"));
        settings.SetExport(@"C:\mods\m", new ExportChoices(["hde-0.7.1"], true, false, @"C:\exports\m"));

        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        var choices = read.ExportOf(@"C:\mods\m")!;
        Assert.Equal(["hde-0.7.1"], choices.Targets);
        Assert.True(choices.DropIn);
        Assert.False(choices.Flmm);
        Assert.Equal(@"C:\exports\m", choices.Folder);
        if (OperatingSystem.IsWindows())
            Assert.NotNull(read.ExportOf(@"c:\MODS\m"));
    }
}

/// <summary>The files of a game root and its layout (section 7.1 of the M11 design).</summary>
public sealed class GameBaseTests
{
    [Fact]
    public void TheLayoutMapsExeToBin()
    {
        using var game = new FakeGame();
        game.Write(@"bin\freelancer.ini", "[Freelancer]\ndata path = ..\\DATA\n");

        var source = new GameBase(game.Root, GameLayout.SourceBuild);
        var standard = GameBase.Standard(game.Root);

        Assert.Contains("data path = ..\\DATA", source.OpenFileSystem().ReadAllText(@"EXE\freelancer.ini"));
        Assert.DoesNotContain("data path = ..\\DATA", standard.OpenFileSystem().ReadAllText(@"EXE\freelancer.ini"));
        Assert.True(source.OpenFileSystem().FileExists(@"DATA\EQUIPMENT\goods.ini"));
        Assert.Equal(@"bin\freelancer.ini", source.ToDisk(@"EXE\freelancer.ini"));
        Assert.Equal(@"DATA\x.ini", source.ToDisk(@"DATA\x.ini"));
        Assert.Equal(@"EXE\freelancer.ini", standard.ToDisk(@"EXE\freelancer.ini"));
    }
}

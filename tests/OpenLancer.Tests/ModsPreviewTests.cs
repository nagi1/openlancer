using System;
using System.IO;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Export;
using OpenLancer.Editor.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The models of the Preview tab, the History tab, the thumbnails and the guard question of the Export dialog
/// (sections 10.5, 10.6, 13.2 and decision D592 of the M11c design).</summary>
public sealed class ModsPreviewTests
{
    private static readonly ModManifest Manifest =
        new ModManifest("starrunner", "Starrunner", "1.0.1", [], []) with { Targets = [ModTargets.Vanilla, ModTargets.OpenLancer] };

    /// <summary>Copy of a part copies the exact text of the field in <c>nexus-page.txt</c>.</summary>
    [Fact]
    public void TheNexusPartsAreTheBlocksOfTheFile()
    {
        var settings = PublishSettings.Empty with
        {
            Summary = "A fast ship.",
            Description = "# What it changes\n- New ship",
            Tags = ["Ships", "Gameplay"],
        };
        var page = PackageTexts.NexusPage(new PublishTexts(Manifest, settings), PreviewTab.Packages(Manifest, null), []);

        var parts = PreviewTab.Parts(page);

        Assert.Equal(["Starrunner"], parts["Mod name"]);
        Assert.Equal(["1.0.1"], parts["Version"]);
        Assert.Equal(["A fast ship."], parts["Summary"]);
        Assert.Equal(["Ships", "Gameplay"], parts["Tags"]);
        Assert.Equal("[size=5][b]What it changes[/b][/size]", parts["Description (BBCode)"][0]);
        Assert.Contains("[*]New ship", parts["Description (BBCode)"]);
        // Each part is a block of the file, and the empty line before the next block is not part of it.
        foreach (var (name, lines) in parts)
            Assert.Contains($"== {name} ==\r\n" + string.Concat(lines.Select(l => l + "\r\n")), page);
        Assert.Empty(parts["Images"]);
    }

    /// <summary>The Files part of the preview lists the packages of the targets and the forms of the last export
    /// choices, as the next export writes them.</summary>
    [Fact]
    public void ThePackagesFollowTheTargetsAndTheSavedChoices()
    {
        var all = PreviewTab.Packages(Manifest, null);
        var dropInOnly = PreviewTab.Packages(Manifest,
            new ExportChoices([ModTargets.Vanilla, ModTargets.OpenLancer], DropIn: true, Flmm: false, ""));
        var vanillaOnly = PreviewTab.Packages(Manifest,
            new ExportChoices([ModTargets.Vanilla], DropIn: true, Flmm: true, ""));

        Assert.Equal(["starrunner-1.0.1-vanilla-1.1.zip", "starrunner-1.0.1-vanilla-1.1-flmm.zip", "starrunner-1.0.1-openlancer.zip"],
            all.Select(p => p.FileName));
        Assert.Equal([PackageForm.DropIn, PackageForm.ModFolder], dropInOnly.Select(p => p.Form));
        Assert.Equal(["starrunner-1.0.1-vanilla-1.1.zip", "starrunner-1.0.1-vanilla-1.1-flmm.zip"],
            vanillaOnly.Select(p => p.FileName));
        // A mod that needs other mods has no export.
        Assert.Empty(PreviewTab.Packages(Manifest with { DependsOn = ["base"] }, null));
    }

    /// <summary>The Images part lists only the entries that pass the path rule, with the names of the export (decision
    /// D567).</summary>
    [Fact]
    public void TheImagesHaveTheNamesOfTheExport()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ol-preview", "starrunner");
        var settings = PublishSettings.Empty with
        {
            Screenshots =
            [
                new PublishScreenshot("publish/screenshots/../../mod.json", ""),
                new PublishScreenshot("publish/screenshots/dock.png", "The dock"),
                new PublishScreenshot("//host/share/x.png", ""),
                new PublishScreenshot("publish/screenshots/fight.jpg", ""),
            ],
        };

        var shots = PreviewTab.Screenshots(folder, settings);

        Assert.Equal(["publish/screenshots/dock.png", "publish/screenshots/fight.jpg"], shots.Select(s => s.File));
        Assert.Equal(["01-dock.png", "02-fight.jpg"], shots.Select((s, i) => ModExport.ScreenshotName(i, s.File)));
    }

    [Fact]
    public void TheHistoryTabNamesTheStateAndThePlays()
    {
        var time = new DateTime(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc);
        var entry = new HistoryExport("1.0.0", time, "test", "c", "p", Path.GetTempPath(), []);

        Assert.Equal("Files in place", HistoryTab.StateText(entry, ExportFilesState.InPlace));
        Assert.Equal("Files not found", HistoryTab.StateText(entry, ExportFilesState.NotFound));
        Assert.Equal("Other files", HistoryTab.StateText(entry, ExportFilesState.OtherFiles));
        Assert.Equal($"Replaced on {PublishMessages.Time(time.AddHours(1))}",
            HistoryTab.StateText(entry with { ReplacedBy = time.AddHours(1) }, ExportFilesState.Replaced));
        Assert.Equal($"{PublishMessages.Time(time)}: HD Edition 0.7.1, alone, 6 min 52 s",
            HistoryTab.PlayText(new HistoryPlay(time, ModTargets.HdEdition, "c", true, 412, false)));
        Assert.Equal($"{PublishMessages.Time(time)}: Freelancer 1.1, with other mods, 59 s",
            HistoryTab.PlayText(new HistoryPlay(time, ModTargets.Vanilla, "c", false, 59, false)));
        Assert.Equal($"{PublishMessages.Time(time)}: manual", HistoryTab.PlayText(new HistoryPlay(time, null, "c", true, 0, true)));
    }

    /// <summary>A version with the mark "published" offers only "Export as next" and Cancel (decision D568).</summary>
    [Fact]
    public void APublishedVersionOffersOnlyExportAsNextAndCancel()
    {
        var guard = new GuardResult(GuardKind.Published, "1.0.0", Earlier(), []);

        var choices = ExportDialog.GuardChoices(guard, "1.0.1", () => { }, () => { }, () => { });

        Assert.Equal(["Export as 1.0.1", "Cancel"], choices.Select(c => c.Label));
        Assert.True(choices[0].Enabled);
    }

    /// <summary>Replace needs each target and form of the old export (finding R7). Else it is disabled with the
    /// reason.</summary>
    [Fact]
    public void ReplaceNeedsEachTargetAndFormOfTheOldExport()
    {
        var full = new GuardResult(GuardKind.Exported, "1.0.0", Earlier(), []);
        var partial = full with { Missing = ["OpenLancer (mod folder zip)"] };

        var replace = ExportDialog.GuardChoices(full, "1.0.1", () => { }, () => { }, () => { });
        var blocked = ExportDialog.GuardChoices(partial, null, () => { }, () => { }, () => { });

        Assert.Equal(["Export as 1.0.1", "Replace version 1.0.0", "Cancel"], replace.Select(c => c.Label));
        Assert.True(replace[1].Enabled);
        Assert.False(blocked[1].Enabled);
        Assert.Equal(partial.PartialText, blocked[1].Tooltip);
        // A version that does not follow the rule has no next version.
        Assert.False(blocked[0].Enabled);
        Assert.Equal(PublishMessages.VersionRule, blocked[0].Tooltip);
    }

    /// <summary>A thumbnail has at most 256 pixels on its long side. Each pixel is the mean of the pixels that it
    /// covers (decision D592).</summary>
    [Fact]
    public void ShrinkKeepsTheAspectAndTakesTheMean()
    {
        // Four by two pixels: a black and a white column pair in each row.
        byte[] image =
        [
            0, 0, 0, 255, 255, 255, 255, 255, 10, 20, 30, 255, 10, 20, 30, 255,
            0, 0, 0, 255, 255, 255, 255, 255, 10, 20, 30, 255, 10, 20, 30, 255,
        ];

        var (data, width, height) = ScreenshotThumbnails.Shrink(image, 4, 2, 2);
        var same = ScreenshotThumbnails.Shrink(image, 4, 2, 4);

        Assert.Equal((2, 1), (width, height));
        Assert.Equal([127, 127, 127, 255, 10, 20, 30, 255], data);
        Assert.Same(image, same.Data);
    }

    private static HistoryExport Earlier() =>
        new("1.0.0", new DateTime(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc), "test", "c", "p", Path.GetTempPath(), []);
}

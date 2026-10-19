using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor;
using OpenLancer.Editor.Mods;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The list of the Mods window (section 10.2 of the M11c design): the filter, the search, the state glyphs, and
/// the games of "Play this mod alone".</summary>
public sealed class ModsWindowTests
{
    private static ModRow Row(string id, string name, ModGroup group, IReadOnlyList<string>? tags = null,
        params ModChip[] chips) =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ol-mods", id), group, group == ModGroup.Profile ? 1 : null, new ModManifest(id, name, "1.0.0", [], []),
            null, chips)
        {
            Settings = PublishSettings.Empty with { Tags = tags ?? [] },
        };

    [Fact]
    public void TheSearchMatchesTheNameTheIdAndTheTags()
    {
        IReadOnlyList<ModRow> rows =
        [
            Row("starrunner", "Fast Ship", ModGroup.Profile),
            Row("cargo", "Big Hauler", ModGroup.Workspace, ["Trade"]),
            Row("other", "Other", ModGroup.Workspace),
        ];

        Assert.Equal(["starrunner"], Ids(ModsWindow.Visible(rows, ModsFilter.ProfileAndWorkspace, "fast")));
        Assert.Equal(["starrunner"], Ids(ModsWindow.Visible(rows, ModsFilter.ProfileAndWorkspace, "STARR")));
        Assert.Equal(["cargo"], Ids(ModsWindow.Visible(rows, ModsFilter.ProfileAndWorkspace, "trade")));
        Assert.Equal(["starrunner", "cargo", "other"], Ids(ModsWindow.Visible(rows, ModsFilter.ProfileAndWorkspace, "  ")));
    }

    [Fact]
    public void TheFilterShowsTheGroups()
    {
        IReadOnlyList<ModRow> rows = [Row("a", "A", ModGroup.Profile), Row("b", "B", ModGroup.Workspace)];

        Assert.Equal(["a"], Ids(ModsWindow.Visible(rows, ModsFilter.Profile, "")));
        Assert.Equal(["b"], Ids(ModsWindow.Visible(rows, ModsFilter.Workspace, "")));
        Assert.Equal(["a", "b"], Ids(ModsWindow.Visible(rows, ModsFilter.ProfileAndWorkspace, "")));
    }

    /// <summary>A row shows up to three glyphs: unsaved or unapplied changes, a change after the export, and the check
    /// state. A mod that does not load shows its error glyph only.</summary>
    [Fact]
    public void TheGlyphsFollowTheChips()
    {
        var changed = Row("a", "A", ModGroup.Profile, null,
            new ModChip(ChipKind.UnappliedChanges, "Unapplied changes", ChipColor.Blue),
            new ModChip(ChipKind.ContentChanged, "Content changed", ChipColor.Yellow),
            new ModChip(ChipKind.Checks, "Checks: 2 errors", ChipColor.Red));
        var clean = Row("b", "B", ModGroup.Profile, null, new ModChip(ChipKind.Checks, "Checks: OK", ChipColor.Green));
        var broken = Row("c", "C", ModGroup.Workspace, null, new ModChip(ChipKind.DoesNotLoad, "Does not load", ChipColor.Red),
            new ModChip(ChipKind.UnappliedChanges, "Unapplied changes", ChipColor.Blue));

        Assert.Equal([EditorIcons.Circle, EditorIcons.ExclamationTriangle, EditorIcons.TimesCircle],
            ModsWindow.Glyphs(changed).Select(g => g.Glyph));
        Assert.Equal([(EditorIcons.CheckCircle, EditorTheme.Success)], ModsWindow.Glyphs(clean).Select(g => (g.Glyph, g.Color)));
        Assert.Equal([EditorIcons.TimesCircle], ModsWindow.Glyphs(broken).Select(g => g.Glyph));
    }

    /// <summary>"Play this mod alone" offers each linked game with a game program that is the base of a retail target
    /// of the mod (decision D577).</summary>
    [Fact]
    public void PlayGamesAreTheLinkedGamesOfTheRetailTargetsOfTheMod()
    {
        using var game = new PlayGame();
        var targets = PlayTarget.Of([game.Export.Report()]);
        var all = new ModManifest("m", "M", "1.0.0", [], []);

        Assert.Single(ModsWindow.PlayGamesOf(all, targets));
        Assert.Single(ModsWindow.PlayGamesOf(all with { Targets = [ModTargets.Vanilla] }, targets));
        Assert.Empty(ModsWindow.PlayGamesOf(all with { Targets = [ModTargets.HdEdition, ModTargets.OpenLancer] }, targets));
        Assert.Empty(ModsWindow.PlayGamesOf(all with { Tier = ModManifest.RuntimeTier, Targets = [ModTargets.OpenLancer] },
            targets));
        // A mod that needs other mods has no export, so it has no play alone.
        Assert.Empty(ModsWindow.PlayGamesOf(all with { DependsOn = ["base"] }, targets));
    }

    private static IEnumerable<string> Ids(IEnumerable<ModRow> rows) => rows.Select(r => r.Id);
}

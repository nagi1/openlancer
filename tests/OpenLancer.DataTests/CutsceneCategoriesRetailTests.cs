using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The categories of the retail cutscene scripts (test 14 of section 9 of the cutscene categories design). The test
/// checks counts only. It holds no retail file name and no retail folder name. The numbers are the numbers of the
/// expected tree of section 12, which a separate read-only reader of the retail INI files found.
/// </summary>
public sealed class CutsceneCategoriesRetailTests(ITestOutputHelper output)
{
    [Fact]
    public void TheRetailScriptsHaveTheShapeOfTheDesign()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var references = ThnReferences.Scan(items.VFS);
        var paths = VfsFiles.Find(items.VFS, ".thn").ToList();
        var facts = CutsceneFacts.From(items, references, ids => items.Ini.Infocards.GetStringResource(ids), id => id, []);

        var categories = CutsceneCategories.Build(paths, facts);

        foreach (var category in categories.Categories)
        {
            output.WriteLine($"{CutsceneCategories.Label(category.Category)}: {category.Count}");
            foreach (var group in category.Groups)
                output.WriteLine($"  {group.Label} {group.Detail}: {group.Rows.Count}");
        }

        // Each script has one place, so the counts add up to the number of scripts.
        Assert.Equal(1506, paths.Count);
        Assert.Equal(paths.Count, categories.Count);
        Assert.Equal(paths.Count, categories.Categories.Sum(c => c.Count));
        Assert.All(paths, p => Assert.NotNull(categories.PlaceOf(p)));

        var story = Category(categories, CutsceneCategory.MainStory);
        Assert.Equal(205, story.Count);
        Assert.Equal(14, story.Groups.Count);
        Assert.Equal(13, story.Groups.Count(g => g.Label.StartsWith("Mission ", StringComparison.Ordinal)));
        Assert.Equal("Not used by a mission", story.Groups[^1].Label);
        var opening = story.Groups[0];
        Assert.Equal(categories.OpeningGroup, opening.Id);
        Assert.Equal(CutsceneCategories.OpeningDetail, opening.Detail);
        Assert.Equal(40, opening.Rows.Count);

        Assert.Equal(4, Category(categories, CutsceneCategory.Intro).Count);
        Assert.Equal(25, Category(categories, CutsceneCategory.Characters).Count);
        var bar = Category(categories, CutsceneCategory.BarConversations);
        Assert.Equal(635, bar.Count);
        Assert.Equal(5, bar.Groups.Count);
        Assert.Equal(1, Category(categories, CutsceneCategory.RandomMissions).Count);
        Assert.Equal(1, Category(categories, CutsceneCategory.Other).Count);

        // The tie rule puts the planet sets of the Hi systems in Hispania and the station sets in Border Worlds.
        var bases = Category(categories, CutsceneCategory.Bases);
        Assert.Equal(635, bases.Count);
        Assert.Equal(11, bases.Groups.Single(g => g.Label == "Hispania").Rows.Count);
        Assert.Equal(53, bases.Groups.Single(g => g.Label == "Border Worlds").Rows.Count);

        // The game opening: the first four rows are in the folder of the first storyline mission. An Act_CallThorn line
        // of its mission file names each of them.
        var first = ThnReferences.PathOf(items.Ini.Storyline.Missions[0].File)!;
        var folder = VfsPath.Parent(first);
        foreach (var row in opening.Rows.Take(4))
        {
            Assert.StartsWith(folder + "\\", row.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(references.UsedBy(row.Path), r =>
                VfsPath.Comparer.Equals(r.File, first) && r.Key.Equals("Act_CallThorn", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(CutsceneCategories.SpaceDetail, row.Detail);
        }
    }

    private static CutsceneCategoryNode Category(CutsceneCategories categories, CutsceneCategory category) =>
        categories.Categories.Single(c => c.Category == category);
}

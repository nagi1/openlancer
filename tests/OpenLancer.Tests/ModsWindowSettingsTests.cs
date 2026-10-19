using System;
using System.IO;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The settings of the Mods window and the keys of a renamed mod (sections 10.1 and 12.3 of the M11c
/// design).</summary>
public sealed class ModsWindowSettingsTests
{
    private static readonly string Workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ol-workspace"));

    private static string Mods(string id) => Path.Combine(Workspace, "mods", id);

    private static string Profile(string name) => Path.Combine(Workspace, "profiles", name + ".json");

    /// <summary>A folder outside the workspace that the user picked.</summary>
    private static readonly string Picked = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ol-releases"));

    [Fact]
    public void TheStateOfTheWindowSurvivesASave()
    {
        var settings = EditorSettings.Transient();
        settings.ModsWindow.Filter = ModsFilter.Workspace;
        settings.ModsWindow.Tab = ModsTab.History;
        settings.ModsWindow.SelectedFolder = Mods("a");
        settings.ModsWindow.ListWidth = 420;

        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        Assert.Equal(ModsFilter.Workspace, read.ModsWindow.Filter);
        Assert.Equal(ModsTab.History, read.ModsWindow.Tab);
        Assert.Equal(Mods("a"), read.ModsWindow.SelectedFolder);
        Assert.Equal(420, read.ModsWindow.ListWidth);
    }

    /// <summary>A name of a later editor gives the default of that setting only, and a file without the window gives
    /// the defaults.</summary>
    [Fact]
    public void UnknownNamesGiveTheDefaults()
    {
        var read = EditorSettings.Parse("""{ "ModsWindow": { "Filter": "Later", "Tab": "Later", "ListWidth": 250 } }""",
            DateTime.UtcNow);
        var old = EditorSettings.Parse("{}", DateTime.UtcNow);

        Assert.Equal(ModsFilter.ProfileAndWorkspace, read.ModsWindow.Filter);
        Assert.Equal(ModsTab.Settings, read.ModsWindow.Tab);
        Assert.Equal(250, read.ModsWindow.ListWidth);
        Assert.Equal(ModsWindowSettings.DefaultListWidth, old.ModsWindow.ListWidth);
        Assert.Equal("", old.ModsWindow.SelectedFolder);
    }

    /// <summary>A rename moves the choices of the Export dialog, and a saved output folder that is the old default
    /// folder becomes the new default folder (finding R10). The active mod of each profile of the rename follows.</summary>
    [Fact]
    public void RenameMovesTheKeysOfTheMod()
    {
        var settings = EditorSettings.Transient();
        settings.SetExport(Mods("old"), new ExportChoices(["vanilla-1.1"], true, false, PublishHistory.FolderOf(Workspace, "old")));
        settings.SetExport(Mods("other"), new ExportChoices(["openlancer"], true, true, Picked));
        settings.SetActiveMod(Profile("a"), "old");
        settings.SetActiveMod(Profile("b"), "old");
        settings.ModsWindow.SelectedFolder = Mods("old");
        var plan = new RenamePlan("old", "new", Mods("old"), Mods("new"), [Profile("a")], null, null, [], null);

        settings.RenameMod(plan, Workspace);

        Assert.Null(settings.ExportOf(Mods("old")));
        Assert.Equal(PublishHistory.FolderOf(Workspace, "new"), settings.ExportOf(Mods("new"))!.Folder);
        Assert.Equal(["vanilla-1.1"], settings.ExportOf(Mods("new"))!.Targets);
        Assert.Equal(Picked, settings.ExportOf(Mods("other"))!.Folder);
        Assert.Equal("new", settings.ActiveModOf(Profile("a"), ["new"]));
        // A profile outside the plan does not name the mod, so its setting stays.
        Assert.Equal("old", settings.ActiveMods[Profile("b")]);
        Assert.Equal(Mods("new"), settings.ModsWindow.SelectedFolder);
    }

    /// <summary>The plan of the Rename dialog lists the changes of the settings of the editor (section 12.3 of the M11c
    /// design), with the workspace of the rename.</summary>
    [Fact]
    public void ThePlanListsTheChangesOfTheSettings()
    {
        var settings = EditorSettings.Transient();
        settings.SetExport(Mods("old"), new ExportChoices([], true, true, PublishHistory.FolderOf(Workspace, "old")));
        settings.SetActiveMod(Profile("a"), "old");
        settings.SetActiveMod(Profile("b"), "other");
        var plan = new RenamePlan("old", "new", Mods("old"), Mods("new"), [Profile("a"), Profile("b")], null, null, [],
            null);
        var picked = EditorSettings.Transient();
        picked.SetExport(Mods("old"), new ExportChoices([], true, true, Picked));

        var lines = settings.RenameChanges(plan, Workspace);

        Assert.Equal(
        [
            "The editor keeps the choices of the Export dialog for the new mod folder.",
            $"The output folder of the Export dialog changes to {PublishHistory.FolderOf(Workspace, "new")}.",
            $"The active mod of the profile {Profile("a")} changes to new.",
        ], lines);
        Assert.Equal(["The editor keeps the choices of the Export dialog for the new mod folder."],
            picked.RenameChanges(plan, Workspace));
        Assert.Empty(EditorSettings.Transient().RenameChanges(plan, Workspace));
        // The list does not change the settings.
        Assert.NotNull(settings.ExportOf(Mods("old")));
        Assert.Equal("old", settings.ActiveMods[Profile("a")]);
    }

    /// <summary>An output folder that the user picked stays after a rename.</summary>
    [Fact]
    public void RenameKeepsAnOutputFolderThatTheUserPicked()
    {
        var settings = EditorSettings.Transient();
        settings.SetExport(Mods("old"), new ExportChoices([], true, true, Picked));

        settings.RenameMod(new RenamePlan("old", "new", Mods("old"), Mods("new"), [], null, null, [], null), Workspace);

        Assert.Equal(Picked, settings.ExportOf(Mods("new"))!.Folder);
    }
}

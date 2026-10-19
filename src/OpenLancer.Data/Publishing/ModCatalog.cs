using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>The group of a row of the Mods window.</summary>
public enum ModGroup
{
    /// <summary>"This profile: &lt;name&gt; (load order)".</summary>
    Profile,

    /// <summary>"Other mods in the workspace".</summary>
    Workspace,
}

/// <summary>The color of a chip (section 10.2 of the M11c design).</summary>
public enum ChipColor
{
    Grey,
    Blue,
    Red,
    Green,
    Yellow,
    WhiteOutline,

    /// <summary>The mark "published". Violet stays for prefab overrides.</summary>
    GreenOutline,
}

/// <summary>The kind of a chip.</summary>
public enum ChipKind
{
    Active,
    UnsavedEdits,
    UnappliedChanges,
    EarlierSession,
    SaveNotComplete,
    InGameFolder,
    OtherEditor,
    Version,
    Tier,
    Target,
    Exported,
    ExportedBeforeHistory,
    NeverExported,
    ContentChanged,
    PageChanged,
    Checks,
    NotChecked,
    Published,
    InProfile,
    NotInProfile,
    DoesNotLoad,
}

/// <summary>A state chip of a mod.</summary>
public sealed record ModChip(ChipKind Kind, string Text, ChipColor Color, string? Tooltip = null);

/// <summary>A row of the Mods window.</summary>
/// <param name="Place">The place in the load order of the profile from 1, or null for a mod outside the
/// profile.</param>
/// <param name="Manifest">The manifest, or null when <c>mod.json</c> does not load.</param>
/// <param name="LoadError">The text of a mod that does not load, else null.</param>
public sealed record ModRow(string Folder, ModGroup Group, int? Place, ModManifest? Manifest, string? LoadError,
    IReadOnlyList<ModChip> Chips)
{
    public string Id => Manifest?.Id ?? Path.GetFileName(Folder);

    public string Name => Manifest?.Name ?? Path.GetFileName(Folder);

    public PublishSettings Settings { get; init; } = PublishSettings.Empty;

    public PublishHistory History { get; init; } = new();

    /// <summary>The problem of the history file, or null.</summary>
    public string? HistoryProblem { get; init; }

    /// <summary>The content fingerprint of the saved files, or empty when the mod does not load.</summary>
    public string Content { get; init; } = "";

    /// <summary>The page fingerprint of the saved files, or empty when the mod does not load.</summary>
    public string Page { get; init; } = "";

    public bool Has(ChipKind kind) => Chips.Any(c => c.Kind == kind);
}

/// <summary>What the scan of the Mods window reads.</summary>
/// <param name="Workspace">The workspace of the window (finding R22): the folder above the folder of the open
/// profile, or the workspace setting of the editor without an open profile.</param>
/// <param name="ProfilePath">The open profile, or null.</param>
public sealed record CatalogInput(string Workspace, string? ProfilePath)
{
    /// <summary>The folder of the active mod of the session, or null.</summary>
    public string? ActiveModFolder { get; init; }

    /// <summary>True when the session of the active mod has unsaved edits.</summary>
    public bool UnsavedEdits { get; init; }

    /// <summary>The mod folders with a publish draft that has changes.</summary>
    public IReadOnlyCollection<string> DraftFolders { get; init; } = [];

    /// <summary>The base of the open profile and the root of each linked game.</summary>
    public IReadOnlyCollection<string> GameRoots { get; init; } = [];

    /// <summary>The hash cache. The window keeps one instance for its scans.</summary>
    public ModFingerprint Fingerprints { get; init; } = new();
}

/// <summary>
/// The rows of the Mods window (section 10.2 of the M11c design): first the mods of the open profile in load order,
/// then the other mods of <c>&lt;workspace&gt;\mods</c>. Each row has its state chips. A folder whose
/// <c>mod.json</c> does not load gives a row with the chip "Does not load".
/// </summary>
/// <remarks>The scan reads the saved files only and writes nothing. The window runs it on a worker.</remarks>
public static class ModCatalog
{
    /// <summary>The workspace of a profile: the folder above the folder of the profile file.</summary>
    public static string WorkspaceOf(string profilePath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath))!, ".."));

    public static IReadOnlyList<ModRow> Scan(CatalogInput input)
    {
        var rows = new List<ModRow>();
        var profileFolders = new List<string>();
        if (input.ProfilePath != null)
        {
            try
            {
                profileFolders = [.. ModProfile.Load(input.ProfilePath).Mods.Select(m => m.Folder)];
            }
            catch (ModLoadException)
            {
                // The rows of the profile still show, in the order of the file, and each row tells its own error.
                try
                {
                    profileFolders = [.. ModProfile.ListedFolders(input.ProfilePath)];
                }
                catch (ModLoadException)
                {
                    profileFolders = [];
                }
            }
        }
        for (var i = 0; i < profileFolders.Count; i++)
            rows.Add(Row(input, profileFolders[i], ModGroup.Profile, i + 1));

        var mods = Path.Combine(Path.GetFullPath(input.Workspace), ModWorkspace.ModsFolder);
        if (Directory.Exists(mods))
        {
            foreach (var folder in Directory.EnumerateDirectories(mods).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(folder, ModManifest.FileName)) &&
                    !profileFolders.Any(f => GameFolders.Same(f, folder)))
                    rows.Add(Row(input, folder, ModGroup.Workspace, null));
            }
        }
        return rows;
    }

    /// <summary>
    /// The versions of the flat packages of M11 exports in an exports folder, for the chip "Exported before the
    /// history" (decision D591): the files <c>&lt;id&gt;-&lt;version&gt;-&lt;target&gt;*.zip</c>.
    /// </summary>
    public static IReadOnlyList<string> M11Versions(string exportsFolder, string modId)
    {
        if (!Directory.Exists(exportsFolder))
            return [];
        var versions = new List<string>();
        foreach (var file in Directory.EnumerateFiles(exportsFolder, $"{modId}-*.zip").Select(Path.GetFileName).OfType<string>())
        {
            var rest = file[(modId.Length + 1)..^".zip".Length];
            foreach (var target in ModTargets.All)
            {
                var at = rest.IndexOf("-" + target, StringComparison.Ordinal);
                if (at > 0 && !versions.Contains(rest[..at], ModVersion.TextComparer))
                {
                    versions.Add(rest[..at]);
                    break;
                }
            }
        }
        return [.. versions.Order(Comparer<string>.Create(ModVersion.Compare))];
    }

    private static ModRow Row(CatalogInput input, string folder, ModGroup group, int? place)
    {
        folder = Path.GetFullPath(folder);
        ModManifest manifest;
        try
        {
            manifest = ModManifest.Load(folder);
        }
        catch (ModLoadException e)
        {
            var text = $"{folder}: {GameRoot.Sentence(Reason(e))} OpenLancer cannot show this mod until mod.json " +
                       "loads. Correct the file, then click Refresh.";
            return new ModRow(folder, group, place, null, text, [new ModChip(ChipKind.DoesNotLoad, "Does not load", ChipColor.Red, text)]);
        }

        var settings = PublishSettings.Load(folder).Settings;
        var historyFolder = PublishHistory.FolderOf(input.Workspace, manifest.Id);
        var (history, historyProblem) = PublishHistory.Load(historyFolder, manifest.Id);
        string content = "", page = "";
        try
        {
            content = input.Fingerprints.Content(manifest, folder);
            page = input.Fingerprints.Page(manifest, folder, settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that OpenLancer cannot read now. The chips that compare fingerprints do not show.
        }

        var chips = new List<ModChip>();
        var active = input.ActiveModFolder != null && GameFolders.Same(input.ActiveModFolder, folder);
        if (active)
            chips.Add(new ModChip(ChipKind.Active, "Active", ChipColor.WhiteOutline, "The editor saves your edits into this mod."));
        if (active && input.UnsavedEdits)
            chips.Add(new ModChip(ChipKind.UnsavedEdits, "Unsaved edits", ChipColor.Blue,
                "Save with Ctrl+S. The export uses the saved files."));
        if (input.DraftFolders.Any(f => GameFolders.Same(f, folder)))
            chips.Add(new ModChip(ChipKind.UnappliedChanges, "Unapplied changes", ChipColor.Blue,
                "Click Apply to write the publish settings."));
        if (!active && File.Exists(Path.Combine(folder, SaveTransaction.FolderName, RecoveryFile.FileName)))
            chips.Add(new ModChip(ChipKind.EarlierSession, "Edits of an earlier session", ChipColor.Blue,
                "Make this mod the active mod. Then restore or discard the edits."));
        if (SaveTransaction.HasJournal(folder))
            chips.Add(new ModChip(ChipKind.SaveNotComplete, "Save not complete", ChipColor.Red,
                "Open the profile once, so that the editor completes the save."));
        if (ModWrite.GameFolderOf(folder, input.GameRoots) is { } game)
            chips.Add(new ModChip(ChipKind.InGameFolder, "In the game folder", ChipColor.Red, PublishMessages.ModInGameFolder(game)));
        if (!active && ModLock.HeldBy(folder, manifest.Id) is { } held)
            chips.Add(new ModChip(ChipKind.OtherEditor, "Open in another editor", ChipColor.Grey, held));

        chips.Add(new ModChip(ChipKind.Version, manifest.Version, ChipColor.Grey, ModVersion.Problem(manifest.Version)?.Message));
        chips.Add(new ModChip(ChipKind.Tier, $"Tier {manifest.Tier}", ChipColor.Grey,
            "Tier 1: a data mod for the retail game and OpenLancer. Tier 2: a mod that only OpenLancer runs."));
        foreach (var target in ExportTarget.All.Where(t => manifest.Targets.Contains(t.Id)))
            chips.Add(new ModChip(ChipKind.Target, target.Name, ChipColor.Grey, target.Game));

        ExportChips(chips, input, manifest, history, content, page);
        CheckChips(chips, manifest, history, content);
        if (history.Published.OrderByDescending(m => m.Version, Comparer<string>.Create(ModVersion.Compare)).FirstOrDefault() is { } mark)
            chips.Add(new ModChip(ChipKind.Published, $"Published {mark.Version}", ChipColor.GreenOutline,
                mark.Page.Length > 0 ? mark.Page : null));
        chips.Add(group == ModGroup.Profile
            ? new ModChip(ChipKind.InProfile, "In this profile", ChipColor.Grey, input.ProfilePath)
            : new ModChip(ChipKind.NotInProfile, "Not in this profile", ChipColor.Grey, input.ProfilePath));

        return new ModRow(folder, group, place, manifest, null, chips)
        {
            Settings = settings,
            History = history,
            HistoryProblem = historyProblem ?? history.FolderWarning(folder),
            Content = content,
            Page = page,
        };
    }

    private static void ExportChips(List<ModChip> chips, CatalogInput input, ModManifest manifest, PublishHistory history,
        string content, string page)
    {
        if (history.Newest is { } last)
        {
            var date = last.Time.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            chips.Add(new ModChip(ChipKind.Exported, $"Exported {last.Version} on {date}", ChipColor.Green, last.Folder));
            if (content.Length > 0 && last.Content != content)
                chips.Add(new ModChip(ChipKind.ContentChanged, "Content changed", ChipColor.Yellow,
                    $"The patches or the files changed after the export of {last.Version}."));
            else if (page.Length > 0 && last.Page != page)
                chips.Add(new ModChip(ChipKind.PageChanged, "Page text changed", ChipColor.Yellow,
                    $"The publish texts changed after the export of {last.Version}."));
            return;
        }
        var exports = PublishHistory.FolderOf(input.Workspace, manifest.Id);
        var versions = M11Versions(exports, manifest.Id);
        chips.Add(versions.Count > 0
            ? new ModChip(ChipKind.ExportedBeforeHistory, "Exported before the history", ChipColor.Grey,
                $"OpenLancer found packages of version {string.Join(", ", versions)} in {exports}. The history starts " +
                "with the next export.")
            : new ModChip(ChipKind.NeverExported, "Never exported", ChipColor.Grey));
    }

    /// <summary>The chip of the checks: the last checks of each target must have the current content.</summary>
    private static void CheckChips(List<ModChip> chips, ModManifest manifest, PublishHistory history, string content)
    {
        var checks = manifest.Targets.Select(history.CheckOf).ToList();
        if (content.Length == 0 || checks.Count == 0 || checks.Any(c => c == null || c.Content != content))
        {
            chips.Add(new ModChip(ChipKind.NotChecked, "Not checked", ChipColor.Grey, "Click Check now."));
            return;
        }
        var errors = checks.Sum(c => c!.Errors);
        var warnings = checks.Sum(c => c!.Warnings);
        var tooltip = string.Join("\n", checks.Select(c =>
            $"{ExportTarget.Find(c!.Target)?.Name ?? c.Target}: {c.Errors} errors, {c.Warnings} warnings"));
        chips.Add(errors > 0 ? new ModChip(ChipKind.Checks, $"Checks: {errors} errors", ChipColor.Red, tooltip)
            : warnings > 0 ? new ModChip(ChipKind.Checks, $"Checks: {warnings} warnings", ChipColor.Yellow, tooltip)
            : new ModChip(ChipKind.Checks, "Checks: OK", ChipColor.Green, tooltip));
    }

    /// <summary>The message of a load error without the path that the exception puts first.</summary>
    private static string Reason(ModLoadException e)
    {
        var prefix = e.Path + ": ";
        return e.Message.StartsWith(prefix, StringComparison.Ordinal) ? e.Message[prefix.Length..] : e.Message;
    }
}

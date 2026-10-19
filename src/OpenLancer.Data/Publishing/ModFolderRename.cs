using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>What a rename of a mod gets.</summary>
/// <param name="ModFolder">The folder of the mod. Its name is the id.</param>
/// <param name="NewId">The new id, which is also the new folder name.</param>
/// <param name="Workspace">The workspace of the Mods window: it holds <c>profiles</c>, <c>mods</c> and
/// <c>exports</c>.</param>
public sealed record RenameRequest(string ModFolder, string NewId, string Workspace)
{
    /// <summary>The recent profiles of the editor. The plan also changes them when they name the folder.</summary>
    public IReadOnlyList<string> RecentProfiles { get; init; } = [];

    /// <summary>The base of the open profile and the root of each linked game.</summary>
    public IReadOnlyCollection<string> GameRoots { get; init; } = [];

    /// <summary>True while a play copy installs, a game of a play copy runs, or an export runs.</summary>
    public bool Busy { get; init; }

    /// <summary>True when the edit session holds the lock of the mod. The editor closes the session before the
    /// run.</summary>
    public bool SessionHolds { get; init; }
}

/// <summary>The plan of a rename (section 12.3 of the M11c design). The dialog shows it before a file changes.</summary>
/// <param name="Profiles">The profile files that name the folder.</param>
/// <param name="HistoryFrom">The publish history folder of the old id, or null when it does not move.</param>
/// <param name="HistoryTo">The publish history folder of the new id, or null.</param>
/// <param name="Refusal">The reason why the rename cannot run, or null.</param>
public sealed record RenamePlan(string OldId, string NewId, string OldFolder, string NewFolder,
    IReadOnlyList<string> Profiles, string? HistoryFrom, string? HistoryTo, IReadOnlyList<string> Warnings,
    string? Refusal);

/// <summary>The result of a rename.</summary>
/// <param name="Log">One line for each step and its result, for the Console.</param>
/// <param name="Error">The reason of a failure, or null.</param>
public sealed record RenameResult(bool Done, IReadOnlyList<string> Log, string? Error);

/// <summary>
/// Rename of a mod (decisions D562 and D588 of the M11c design): one name for the id and the folder, as New mod makes
/// them. Rename changes the folder, the id in <c>mod.json</c>, each profile that names the folder, and the folder of
/// the publish history. It never writes a file of another mod. A failed step restores each changed file.
/// </summary>
/// <remarks>The keys of <c>editor.json</c> belong to the editor. The editor changes them after a run.</remarks>
public static class ModFolderRename
{
    private const string FolderStep = "the move of the mod folder";
    private const string HistoryStep = "the move of the publish history";

    public static RenamePlan Plan(RenameRequest request)
    {
        var oldFolder = Path.GetFullPath(request.ModFolder);
        var newFolder = Path.Combine(Path.GetDirectoryName(oldFolder)!, request.NewId);
        ModManifest manifest;
        try
        {
            manifest = ModManifest.Load(oldFolder);
        }
        catch (ModLoadException e)
        {
            return new RenamePlan(Path.GetFileName(oldFolder), request.NewId, oldFolder, newFolder, [], null, null, [], e.Message);
        }

        var oldId = manifest.Id;
        var others = WorkspaceMods(request.Workspace).Where(m => !GameFolders.Same(m.Folder, oldFolder)).ToList();
        var warnings = new List<string>();
        var profiles = new List<string>();
        string? refusal = Refusal(request, manifest, oldFolder, newFolder, others);

        var candidates = new List<string>();
        var profilesFolder = Path.Combine(Path.GetFullPath(request.Workspace), ModWorkspace.ProfilesFolder);
        if (Directory.Exists(profilesFolder))
            candidates.AddRange(Directory.EnumerateFiles(profilesFolder, "*.json").Order(StringComparer.OrdinalIgnoreCase));
        foreach (var recent in request.RecentProfiles.Select(Path.GetFullPath))
        {
            if (candidates.Any(c => GameFolders.Same(c, recent)))
                continue;
            if (!File.Exists(recent))
                warnings.Add(PublishMessages.RenameProfileNotFound(recent));
            else
                candidates.Add(recent);
        }
        foreach (var profile in candidates)
        {
            try
            {
                if (!ModProfile.ListedFolders(profile).Any(f => GameFolders.Same(f, oldFolder)))
                    continue;
            }
            catch (ModLoadException)
            {
                // A file that does not load names no mod for OpenLancer.
                continue;
            }
            profiles.Add(profile);
            refusal ??= ModWrite.ProfileGuard(profile, request.GameRoots) ?? Writable(profile);
        }

        foreach (var other in others.Where(m => m.Manifest.LoadAfter.Contains(oldId, StringComparer.Ordinal)))
            warnings.Add(PublishMessages.RenameWarningLoadAfter(other.Manifest.Id, oldId));
        var historyFrom = PublishHistory.FolderOf(request.Workspace, oldId);
        var historyTo = PublishHistory.FolderOf(request.Workspace, request.NewId);
        var (history, _) = PublishHistory.Load(historyFrom, oldId);
        if (history.Published.OrderByDescending(m => m.Version, Comparer<string>.Create(ModVersion.Compare)).FirstOrDefault() is { } mark)
            warnings.Add(PublishMessages.RenameAfterRelease(mark.Version, oldId));
        var moveHistory = Directory.Exists(historyFrom) && !Path.Exists(historyTo) && historyFrom != historyTo;
        if (Directory.Exists(historyFrom) && !moveHistory && historyFrom != historyTo)
            warnings.Add(PublishMessages.RenameHistoryStays(historyTo));
        return new RenamePlan(oldId, request.NewId, oldFolder, newFolder, profiles,
            moveHistory ? historyFrom : null, moveHistory ? historyTo : null, warnings, refusal);
    }

    /// <summary>Runs a plan. A failure of a step after the move of the folder writes back each kept file in the reverse
    /// order and moves the folder back.</summary>
    public static RenameResult Run(RenamePlan plan) => Run(plan, null);

    /// <param name="beforeStep">Runs before each step with the name of the step. The tests throw in it to stop a
    /// step.</param>
    internal static RenameResult Run(RenamePlan plan, Action<string>? beforeStep)
    {
        var log = new List<string>();
        if (plan.Refusal != null)
            return new RenameResult(false, log, plan.Refusal);
        try
        {
            beforeStep?.Invoke(FolderStep);
            Directory.Move(plan.OldFolder, plan.NewFolder);
            log.Add($"Moved the mod folder {plan.OldFolder} to {plan.NewFolder}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RenameResult(false, log, PublishMessages.RenameFailed(FolderStep, e.Message));
        }

        var kept = new List<(string Path, byte[] Bytes)>();
        var historyMoved = false;
        var step = ModManifest.FileName;
        try
        {
            beforeStep?.Invoke(step);
            var manifest = Path.Combine(plan.NewFolder, ModManifest.FileName);
            kept.Add((manifest, File.ReadAllBytes(manifest)));
            var file = JsonFile.Read(manifest);
            file.Root["id"] = plan.NewId;
            AtomicFile.WriteAllBytes(manifest, file.ToBytes());
            log.Add($"Wrote the id {plan.NewId} into {manifest}.");
            foreach (var profile in plan.Profiles)
            {
                step = profile;
                beforeStep?.Invoke(step);
                kept.Add((profile, File.ReadAllBytes(profile)));
                ProfileFile.RenameMod(profile, plan.OldFolder, plan.NewFolder);
                log.Add($"Wrote the new mod folder into {profile}.");
            }
            if (plan.HistoryFrom != null && plan.HistoryTo != null)
            {
                step = HistoryStep;
                beforeStep?.Invoke(step);
                var history = Path.Combine(plan.HistoryFrom, PublishHistory.FileName);
                if (File.Exists(history))
                {
                    kept.Add((history, File.ReadAllBytes(history)));
                    var json = JsonFile.Read(history);
                    json.Root["modId"] = plan.NewId;
                    json.Root["modFolder"] = plan.NewFolder;
                    AtomicFile.WriteAllBytes(history, json.ToBytes());
                }
                Directory.Move(plan.HistoryFrom, plan.HistoryTo);
                historyMoved = true;
                log.Add($"Moved the publish history {plan.HistoryFrom} to {plan.HistoryTo}.");
            }
            return new RenameResult(true, log, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ModLoadException)
        {
            log.Add($"The step {step} failed: {e.Message}");
            Restore(plan, kept, historyMoved, log);
            return new RenameResult(false, log, PublishMessages.RenameFailed(step, e.Message));
        }
    }

    /// <summary>The refusals of section 12.3 of the M11c design, in order, or null.</summary>
    private static string? Refusal(RenameRequest request, ModManifest manifest, string oldFolder, string newFolder,
        IReadOnlyList<(string Folder, ModManifest Manifest)> others)
    {
        if (!ModManifest.IsValidId(request.NewId))
            return ModManifest.IdRule;
        if (request.NewId == manifest.Id || Path.Exists(newFolder) || others.Any(m => m.Manifest.Id == request.NewId))
            return PublishMessages.IdInUse(request.NewId);
        if (others.FirstOrDefault(m => m.Manifest.DependsOn.Contains(manifest.Id, StringComparer.Ordinal)) is { Manifest: { } needs })
            return PublishMessages.RenameRefusedDependsOn(needs.Id, manifest.Id);
        if (RepoCheck.RepoRoot(oldFolder) != null)
            return PublishMessages.RenameRepoMod;
        if (ModWrite.GameFolderOf(oldFolder, request.GameRoots) is { } game)
            return PublishMessages.ModInGameFolder(game);
        if (SaveTransaction.HasJournal(oldFolder))
            return PublishMessages.RenamePendingSave;
        if (request.Busy)
            return PublishMessages.RenameBusy;
        if (!request.SessionHolds && ModLock.HeldBy(oldFolder, manifest.Id) is { } held)
            return held;
        return null;
    }

    /// <summary>The reason why OpenLancer cannot write a profile file, or null.</summary>
    private static string? Writable(string profile)
    {
        try
        {
            JsonFile.Read(profile);
            return null;
        }
        catch (InvalidDataException e)
        {
            return $"{profile}: {e.Message}";
        }
    }

    /// <summary>The mods of <c>&lt;workspace&gt;\mods</c> whose <c>mod.json</c> loads.</summary>
    internal static IEnumerable<(string Folder, ModManifest Manifest)> WorkspaceMods(string workspace)
    {
        var mods = Path.Combine(Path.GetFullPath(workspace), ModWorkspace.ModsFolder);
        if (!Directory.Exists(mods))
            yield break;
        foreach (var folder in Directory.EnumerateDirectories(mods).Order(StringComparer.OrdinalIgnoreCase))
        {
            ModManifest manifest;
            try
            {
                manifest = ModManifest.Load(folder);
            }
            catch (ModLoadException)
            {
                continue;
            }
            yield return (folder, manifest);
        }
    }

    /// <summary>Moves the history back, writes back each kept file in the reverse order, and moves the mod folder
    /// back. Each step that fails goes to the log, and the next step still runs.</summary>
    private static void Restore(RenamePlan plan, List<(string Path, byte[] Bytes)> kept, bool historyMoved, List<string> log)
    {
        if (historyMoved)
            Try($"Moved the publish history back to {plan.HistoryFrom}.", () => Directory.Move(plan.HistoryTo!, plan.HistoryFrom!));
        for (var i = kept.Count - 1; i >= 0; i--)
        {
            var (path, bytes) = kept[i];
            Try($"Restored {path}.", () => AtomicFile.WriteAllBytes(path, bytes));
        }
        Try($"Moved the mod folder back to {plan.OldFolder}.", () => Directory.Move(plan.NewFolder, plan.OldFolder));

        void Try(string done, Action action)
        {
            try
            {
                action();
                log.Add(done);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Add($"OpenLancer could not undo a step: {e.Message}");
            }
        }
    }
}

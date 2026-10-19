using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>What the write guard of the Mods window knows.</summary>
/// <param name="GameRoots">The base of the open profile and the root of each linked game.</param>
/// <param name="RepoAllowed">True when the user allowed edits of the mod folder in the OpenLancer repo (decision D584):
/// the allowance of the Mods window for the folder, or the allowance of the session for its active mod.</param>
public sealed record WriteContext(IReadOnlyCollection<string> GameRoots, Func<string, bool> RepoAllowed)
{
    /// <summary>A context with the game roots and no allowance for a repo mod.</summary>
    public static WriteContext Of(IReadOnlyCollection<string> gameRoots) => new(gameRoots, _ => false);
}

/// <summary>
/// The write guard of the Mods window (decision D587 and section 10.8 of the M11c design). Each write into a mod
/// refuses a mod in a game folder, a repo mod without the allowance and a mod with a save that is not complete. For a
/// mod that the session does not hold, the write takes the lock of the mod for its duration.
/// </summary>
public static class ModWrite
{
    /// <summary>The reason why the Mods window cannot write the mod, or null.</summary>
    public static string? Guard(string modFolder, WriteContext context)
    {
        modFolder = Path.GetFullPath(modFolder);
        if (GameFolderOf(modFolder, context.GameRoots) is { } root)
            return PublishMessages.ModInGameFolder(root);
        if (RepoCheck.RepoRoot(modFolder) != null && !context.RepoAllowed(modFolder))
            return PublishMessages.RepoMod;
        if (SaveTransaction.HasJournal(modFolder))
            return PublishMessages.PendingSave;
        return null;
    }

    /// <summary>
    /// Runs a write into a mod after the guard. When the session does not hold the mod, the write holds the lock of the
    /// mod and releases it at the end.
    /// </summary>
    /// <param name="sessionHolds">True when the edit session holds the lock of the mod, because the mod is its active
    /// mod.</param>
    /// <returns>The reason why the write did not run, or null.</returns>
    public static string? Run(string modFolder, string modId, WriteContext context, bool sessionHolds, Action write)
    {
        if (Guard(modFolder, context) is { } reason)
            return reason;
        if (sessionHolds)
        {
            write();
            return null;
        }
        using var held = ModLock.TryTake(modFolder, modId, out var lockReason);
        if (held == null)
            return lockReason;
        write();
        return null;
    }

    /// <summary>The reason why OpenLancer cannot write a profile file, or null: the profile is in a game
    /// folder.</summary>
    public static string? ProfileGuard(string profilePath, IReadOnlyCollection<string> gameRoots) =>
        GameFolderOf(Path.GetFullPath(profilePath), gameRoots) != null ? GameTexts.ProfileInGame : null;

    /// <summary>The game folder that holds a path, or null: a folder with <c>EXE\freelancer.ini</c> above the path or
    /// above its real path, or a game root that overlaps it.</summary>
    internal static string? GameFolderOf(string path, IReadOnlyCollection<string> gameRoots)
    {
        try
        {
            return GameRoot.Containing(path) ?? GameRoot.Containing(PathRules.RealPath(path)) ??
                   gameRoots.FirstOrDefault(root => GameFolders.Overlaps(path, root));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return GameRoot.Containing(path) ?? gameRoots.FirstOrDefault(root => PathRules.Overlaps(path, root));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Games;

/// <summary>
/// What the game link needs from the editor. <see cref="EditorWindow"/> implements it. The tests give a fake, because an
/// editor window needs a GPU and reads and writes the real settings file.
/// </summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IGameLinkHost : IUIThread
{
    EditorSettings Settings { get; }

    ConsoleLog Log { get; }

    /// <summary>The profile files that the editor knows: the files of the profile folders and of the recent
    /// list.</summary>
    IReadOnlyList<string> KnownProfiles();

    /// <summary>The profile that a relink opens again: the open profile, or the profile that did not open because its
    /// base folder is missing. Null when there is neither.</summary>
    string? ProfileToReopen { get; }

    /// <summary>Opens a profile. With unsaved edits, the editor asks first.</summary>
    void OpenProfile(string path);

    /// <summary>Asks a question with buttons. The first button is the default button.</summary>
    void Ask(string title, string message, IReadOnlyList<Choice> choices);
}

/// <summary>
/// The linked game folders of the editor (milestone M11a): the entries of the settings, the reports of this session,
/// the checks, link, relink and remove, and the link of the base folder of an opened profile (decision D195).
/// </summary>
/// <remarks>
/// <para>A check reads files on a background thread and gives its report on the UI thread. It never writes into the
/// game folder (see <see cref="GameDetector"/>).</para>
/// <para>The preview bench must not change the settings of the user, so a disabled instance runs no check and links
/// nothing.</para>
/// </remarks>
internal sealed class GameLinks(IGameLinkHost host, bool enabled, KnownBuilds? builds = null)
{
    /// <summary>The Console source of the game link.</summary>
    public const string Source = "Game link";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, GameReport> reports = new(PathComparer);
    private readonly Dictionary<string, DateTime> checkedUtc = new(PathComparer);
    private readonly HashSet<string> checking = new(PathComparer);
    private readonly Dictionary<string, string> errors = new(PathComparer);
    private Dictionary<string, List<string>> usedBy = new(PathComparer);

    /// <summary>The linked games, in the order of their links.</summary>
    public IReadOnlyList<LinkedGame> Games => host.Settings.Games;

    /// <summary>
    /// True when the first-run screen shows at the start (decision D193): a normal start without a linked game, without
    /// a recent profile file that exists, without a profile on the command line and without a problem of the command
    /// line. Never in the bench.
    /// </summary>
    public static bool FirstRunApplies(EditorOptions options, EditorSettings settings, Func<string, bool> fileExists) =>
        !options.Bench && options.Problem == null && options.Profile == null && settings.Games.Count == 0 &&
        !settings.RecentProfiles.Any(r => fileExists(r.Path));

    /// <summary>The entry of a linked game folder, or null.</summary>
    public LinkedGame? Find(string root) => Games.FirstOrDefault(g => GameFolders.Same(g.Path, root));

    /// <summary>The report of the last check of a folder in this session, or null.</summary>
    public GameReport? ReportOf(string root) => reports.GetValueOrDefault(GameFolders.Normal(root));

    /// <summary>True while a check of the folder runs.</summary>
    public bool IsChecking(string root) => checking.Contains(GameFolders.Normal(root));

    /// <summary>The error of the last relink of a game, or null.</summary>
    public string? ErrorOf(string root) => errors.GetValueOrDefault(GameFolders.Normal(root));

    /// <summary>The profile files whose base folder is the root, as the last <see cref="RefreshUsedBy"/> found
    /// them.</summary>
    public IReadOnlyList<string> UsedBy(string root) =>
        usedBy.TryGetValue(GameFolders.Normal(root), out var profiles) ? profiles : [];

    /// <summary>Checks each linked game in the background, at the start of the editor.</summary>
    public void CheckAll()
    {
        foreach (var game in Games)
        {
            Check(game.Path);
        }
    }

    /// <summary>Checks each linked game whose last check is older than the age, for example when the Game link window
    /// opens.</summary>
    public void CheckOlderThan(TimeSpan age)
    {
        foreach (var game in Games)
        {
            if (!checkedUtc.TryGetValue(GameFolders.Normal(game.Path), out var time) || DateTime.UtcNow - time > age)
            {
                Check(game.Path);
            }
        }
    }

    /// <summary>Checks a game root in the background, without a search for the root. The report replaces the last
    /// report of the root.</summary>
    public void Check(string root)
    {
        root = GameFolders.Normal(root);
        if (!enabled || !checking.Add(root))
        {
            return;
        }

        Run(cancel => GameDetector.CheckRoot(root, builds, cancel), CancellationToken.None, report =>
        {
            checking.Remove(root);
            Store(root, report);
        }, () => checking.Remove(root));
    }

    /// <summary>
    /// Finds the game root from a folder and checks it in the background, without a link. The report comes on the UI
    /// thread, unless the token stopped the check.
    /// </summary>
    public void Detect(string folder, CancellationToken cancel, Action<GameReport> done) =>
        Run(token => GameDetector.Detect(folder, builds, token), cancel, report =>
        {
            if (report.Root != null)
            {
                Store(report.Root, report);
            }

            done(report);
        }, null);

    /// <summary>Finds the game in a folder and links it. A folder without a game gets no link. The report comes on the
    /// UI thread.</summary>
    public void Link(string folder, Action<GameReport> done) =>
        Detect(folder, CancellationToken.None, report =>
        {
            if (report.IsGame)
            {
                Add(report);
            }

            done(report);
        });

    /// <summary>Links the game of a report. A game that is linked already keeps its entry with the new name.</summary>
    /// <exception cref="ArgumentException">The report has no game.</exception>
    public LinkedGame Add(GameReport report)
    {
        if (report.Root is not { } root || !report.IsGame)
        {
            throw new ArgumentException("The report has no game.", nameof(report));
        }

        root = GameFolders.Normal(root);
        reports[root] = report;
        checkedUtc[root] = DateTime.UtcNow;
        var index = IndexOf(root);
        var entry = index < 0
            ? new LinkedGame(root, DateTime.UtcNow, report.Name, report.Build)
            : host.Settings.Games[index] with { LastName = report.Name, LastBuild = report.Build };
        if (index < 0)
        {
            host.Settings.Games.Add(entry);
            host.Log.Add(ConsoleSeverity.Info, Source, $"Linked {report.Name} in {root}.");
        }
        else
        {
            host.Settings.Games[index] = entry;
        }

        host.Settings.Save();
        return entry;
    }

    /// <summary>Removes the link to a game. No file is deleted (decision D206).</summary>
    public void Remove(string root)
    {
        var entry = Find(root);
        if (entry == null)
        {
            return;
        }

        host.Settings.Games.Remove(entry);
        host.Settings.Save();
        host.Log.Add(ConsoleSeverity.Info, Source,
            $"Removed the link to {entry.LastName} in {entry.Path}. OpenLancer deleted no file.");
    }

    /// <summary>
    /// Links the base folder of a profile that opened, when it is a game root and is not linked (decision D195). The user
    /// chose the profile, so this is no search of the disk. A linked base gets a check when it has no report yet.
    /// </summary>
    public void ProfileOpened(ModProfile profile)
    {
        if (!enabled)
        {
            return;
        }

        var root = GameFolders.Normal(profile.BaseFolder);
        if (Find(root) != null)
        {
            if (!reports.ContainsKey(root))
            {
                Check(root);
            }

            return;
        }

        if (!checking.Add(root))
        {
            return;
        }

        Run(cancel => GameDetector.CheckRoot(root, builds, cancel), CancellationToken.None, report =>
        {
            checking.Remove(root);
            Store(root, report);
            if (report.IsGame && Find(root) == null)
            {
                Add(report);
                host.Log.Add(ConsoleSeverity.Info, Source,
                    $"The profile {profile.Name} uses the game folder {root}, so OpenLancer linked it.");
            }
        }, () => checking.Remove(root));
    }

    /// <summary>Finds again the profiles of each base folder in the background, for the "Used by" lists. A profile file
    /// that is not valid is left out.</summary>
    public void RefreshUsedBy()
    {
        var profiles = host.KnownProfiles();
        Task.Run(() =>
        {
            var found = new Dictionary<string, List<string>>(PathComparer);
            foreach (var profile in profiles)
            {
                if (BaseOf(profile) is { } baseFolder)
                {
                    if (!found.TryGetValue(baseFolder, out var list))
                    {
                        found[baseFolder] = list = [];
                    }

                    list.Add(profile);
                }
            }

            host.QueueUIThread(() => usedBy = found);
        });
    }

    /// <summary>
    /// Gives a linked game a new folder, for example after the game moved (decision D203). The editor checks the new
    /// folder. With the same build, the base of each profile of the old folder changes at once. With another build, the
    /// editor asks first. A folder without a game changes nothing, and <see cref="ErrorOf"/> tells why.
    /// </summary>
    public void Relink(string oldRoot, string folder)
    {
        oldRoot = GameFolders.Normal(oldRoot);
        errors.Remove(oldRoot);
        Detect(folder, CancellationToken.None, report =>
        {
            if (!report.IsGame)
            {
                errors[oldRoot] = report.Error ?? GameTexts.NotAGameFolder;
                return;
            }

            // A source build cannot be the base of the profiles of the old folder (section 8.5 of the M11 design).
            if (!report.CanBeBase && ProfilesOf(oldRoot).Count > 0)
            {
                errors[oldRoot] = GameTexts.SourceBuildNoBase;
                return;
            }

            var entry = Find(oldRoot);
            if (entry == null || entry.LastBuild == report.Build)
            {
                ApplyRelink(oldRoot, report);
                return;
            }

            var count = ProfilesOf(oldRoot).Count;
            host.Ask("Relink", $"The new folder contains {report.Name}. The old folder contained {entry.LastName}. " +
                               $"Lists and checks can change. Use the new folder for {count} profiles?",
            [
                new Choice("Relink", () => ApplyRelink(oldRoot, report)),
                new Choice("Cancel", () => { }),
            ]);
        });
    }

    /// <summary>Changes the entry and the base of each profile of the old root. Then the profile that the relink is for
    /// opens again.</summary>
    private void ApplyRelink(string oldRoot, GameReport report)
    {
        var newRoot = GameFolders.Normal(report.Root!);
        var gameRoots = Games.Select(g => g.Path).Append(oldRoot).Append(newRoot).ToList();
        var changed = new List<string>();
        foreach (var profile in ProfilesOf(oldRoot))
        {
            if (SkipReason(profile, gameRoots) is { } skip)
            {
                host.Log.Add(ConsoleSeverity.Warning, Source, $"Relink did not change the profile {profile}: {skip}");
                continue;
            }

            try
            {
                ModWorkspace.SetBase(profile, newRoot);
                changed.Add(profile);
                host.Log.Add(ConsoleSeverity.Info, Source,
                    $"Relink changed the base folder of the profile {profile} to {newRoot}.");
            }
            catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException)
            {
                host.Log.Add(ConsoleSeverity.Warning, Source, $"Relink did not change the profile {profile}: {e.Message}");
            }
        }

        // The entry keeps its place in the list. When the new folder is linked already, the old entry goes.
        var index = IndexOf(oldRoot);
        var other = IndexOf(newRoot);
        if (index >= 0 && other >= 0 && other != index)
        {
            host.Settings.Games.RemoveAt(index);
        }
        else if (index >= 0)
        {
            // The play copy of the old folder copies the old folder, so the new folder gets the default folder again.
            host.Settings.Games[index] = host.Settings.Games[index] with { Path = newRoot, PlayCopy = null };
        }

        Add(report);
        host.Log.Add(ConsoleSeverity.Info, Source, $"Relinked {report.Name}: {oldRoot} is now {newRoot}. " +
                                                   $"{changed.Count} profiles changed.");
        RefreshUsedBy();
        if (host.ProfileToReopen is { } reopen && changed.Any(p => GameFolders.Same(p, reopen)))
        {
            host.OpenProfile(reopen);
        }
    }

    /// <summary>Why a relink must not change a profile file, or null (decision D214): the file is in a game folder, it
    /// is part of the OpenLancer repo, so no user path goes into a tracked file, or it has a JSON comment.</summary>
    private static string? SkipReason(string profile, IReadOnlyList<string> gameRoots)
    {
        if (gameRoots.Any(root => GameFolders.Overlaps(profile, root)))
        {
            return "The profile file is in a game folder. OpenLancer does not write into the game folder.";
        }

        return RepoCheck.RepoRoot(Path.GetDirectoryName(profile)!) is { } repo
            ? $"The profile file is part of the OpenLancer repo at {repo}. Change its base folder by hand."
            : null;
    }

    /// <summary>The known profile files whose base folder is the root, read now.</summary>
    private List<string> ProfilesOf(string root) =>
        host.KnownProfiles().Where(p => BaseOf(p) is { } baseFolder && GameFolders.Same(baseFolder, root))
            .Distinct(PathComparer).ToList();

    /// <summary>The base folder of a profile file, or null when the file is not a valid profile.</summary>
    private static string? BaseOf(string profile)
    {
        try
        {
            return GameFolders.Normal(ModProfile.BaseFolderOf(profile));
        }
        catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private int IndexOf(string root) => host.Settings.Games.FindIndex(g => GameFolders.Same(g.Path, root));

    /// <summary>Keeps a report. When the build of a linked game changed since the last check, the Console says so, and
    /// the entry gets the new name.</summary>
    private void Store(string root, GameReport report)
    {
        root = GameFolders.Normal(root);
        reports[root] = report;
        checkedUtc[root] = DateTime.UtcNow;
        var index = IndexOf(root);
        if (index < 0 || !report.IsGame)
        {
            return;
        }

        var entry = host.Settings.Games[index];
        if (entry.LastBuild == report.Build && entry.LastName == report.Name)
        {
            return;
        }

        if (entry.LastBuild != null && entry.LastBuild != report.Build)
        {
            host.Log.Add(ConsoleSeverity.Info, Source,
                $"The game in {root} changed. It was {entry.LastName}. Now it is {report.Name}.");
        }

        host.Settings.Games[index] = entry with { LastName = report.Name, LastBuild = report.Build };
        host.Settings.Save();
    }

    /// <summary>Runs a check on a background thread and gives the report on the UI thread. An error that the check does
    /// not expect goes to the Console.</summary>
    private void Run(Func<CancellationToken, GameReport> check, CancellationToken cancel, Action<GameReport> done,
        Action? failed)
    {
        Task.Run(() =>
        {
            try
            {
                var report = check(cancel);
                host.QueueUIThread(() =>
                {
                    if (!cancel.IsCancellationRequested)
                    {
                        done(report);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                if (failed != null)
                {
                    host.QueueUIThread(failed);
                }
            }
            catch (Exception e)
            {
                host.QueueUIThread(() =>
                {
                    failed?.Invoke();
                    host.Log.Add(ConsoleSeverity.Error, Source, $"The check of the game folder failed: {e.Message}");
                });
            }
        }, CancellationToken.None);
    }
}

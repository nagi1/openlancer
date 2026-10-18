using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.PlayCopies;

/// <summary>A play copy refused an action. Nothing changed, or the play copy keeps its record of the last
/// action.</summary>
public sealed class PlayCopyException(string message) : Exception(message);

/// <summary>The result of an install, an uninstall or a check of a play copy.</summary>
/// <param name="Problems">The warnings: changed linked files, replaced links, kept files and differences.</param>
/// <param name="Written">The number of files that the install wrote.</param>
/// <param name="Restored">The number of files of the last install that got their kind of the make again.</param>
/// <param name="Links">The number of hard links in the play copy.</param>
/// <param name="Copies">The number of real files in the play copy.</param>
public sealed record PlayCopyResult(IReadOnlyList<Diagnostic> Problems, int Written, int Restored, int Links, int Copies);

/// <summary>
/// A play copy (section 8 of the M11 design, decision D308) is a folder with the layout of a game. It holds hard links
/// to the large binary files of the game and real copies of the other files. The editor installs the mods into it and
/// starts the game program of the copy. OpenLancer never writes, deletes or changes the attributes of a file of the
/// original game.
/// </summary>
/// <remarks>
/// <para>An open play copy holds its lock (<see cref="PlayCopyLock"/>) until <see cref="Dispose"/>. Only this class and
/// <see cref="HardLinks"/> change a play copy. They never open a name of a play copy for write. They put each new file in
/// place with a rename, and they delete names one by one. They check each linked file through a handle.</para>
/// <para>The record (<see cref="PlayCopyRecord"/>) keeps the kind of each file, the stamps of each link, the files of
/// the last install and the own edits. A make that stopped leaves the state Incomplete, and the next
/// <see cref="Make"/> completes the copy.</para>
/// </remarks>
public sealed class PlayCopy : IDisposable
{
    /// <summary>The read-me file in the root of a play copy.</summary>
    public const string ReadMeName = "OPENLANCER PLAY COPY - READ ME.txt";

    /// <summary>The folder for the files that OpenLancer keeps, for example an installed file that changed outside
    /// OpenLancer.</summary>
    public const string KeepFolder = ".openlancer-playcopy";

    /// <summary>The end of the name of the default play copy folder, after the name of the game folder.</summary>
    public const string FolderSuffix = " - OpenLancer play";

    private const string FlplusplusPath = @"EXE\flplusplus.ini";
    private const string SaveFolder = "SAVE";

    private readonly PlayCopyLock lockFile;
    private readonly HardLinks links;
    private bool disposed;

    private PlayCopy(string root, PlayCopyRecord record, PlayCopyLock lockFile, HardLinks links)
    {
        Root = root;
        Record = record;
        this.lockFile = lockFile;
        this.links = links;
    }

    /// <summary>The full path of the root of the play copy.</summary>
    public string Root { get; }

    public PlayCopyRecord Record { get; }

    /// <summary>The game that the play copy copies.</summary>
    public GameBase Source => new(Record.Source, Record.Layout);

    /// <summary>The program folder of the play copy: <c>bin</c> for a source build, else <c>EXE</c>. The game starts
    /// with it as its working folder.</summary>
    public string ProgramFolder => Path.Combine(Root, PathRules.WithDiskCase(Root, Source.ToDisk("EXE")));

    /// <summary>The game program of the play copy, or null when it has none.</summary>
    public string? ProgramPath => FileIn(ProgramFolder, "Freelancer.exe");

    /// <summary>The server program of the play copy, or null when it has none.</summary>
    public string? ServerPath => FileIn(ProgramFolder, "FLServer.exe");

    /// <summary>True when the saves of the game go into the play copy (decision D314).</summary>
    public bool SavesInCopy => Record.Own.Count > 0;

    /// <summary>The bytes of the real files of the play copy. The links take no space.</summary>
    public long SizeOnDisk => Record.SizeOnDisk(Root);

    /// <summary>The default folder of the play copy of a game (decision D309). It is next to the game folder, with the
    /// name of the game folder and <see cref="FolderSuffix"/>.</summary>
    public static string DefaultFolder(string gameRoot)
    {
        var root = GameFolders.Normal(gameRoot);
        return Path.Combine(Path.GetDirectoryName(root) ?? root, Path.GetFileName(root) + FolderSuffix);
    }

    /// <summary>
    /// The reason why a folder cannot be the play copy of a game, or null. The folder must not overlap the game folder
    /// or be in a game folder. It must not be a link, or hold files and have no record of a play copy.
    /// </summary>
    public static string? FolderProblem(string folder, string gameRoot)
    {
        var root = GameFolders.Normal(folder);
        var game = GameFolders.Normal(gameRoot);
        if (GameFolders.Overlaps(root, game))
            return PlayCopyTexts.OverlapsGame(root, game);
        if (Path.GetDirectoryName(root) is { } parent && GameRoot.Containing(parent) is { } other)
            return PlayCopyTexts.InGameFolder(root, other);
        if (!Directory.Exists(root))
            return null;
        if (IsLink(root))
            return PlayCopyTexts.IsALink(root);
        return !PlayCopyRecord.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()
            ? PlayCopyTexts.FolderInUse(root)
            : null;
    }

    /// <summary>
    /// The folders that a play copy must not overlap (decision D309, step 6): the workspace, the mod folders of the
    /// profile, and the OpenLancer repo that holds the profile or the program. A play copy there puts game files into
    /// the mods, the workspace or the repo.
    /// </summary>
    /// <param name="workspace">The workspace folder. <c>oltool</c> gives the folder of the profile file.</param>
    /// <param name="profile">The open profile, or null.</param>
    public static IReadOnlyList<string> ProtectedFolders(string workspace, ModProfile? profile)
    {
        var folders = new List<string> { workspace };
        if (profile != null)
        {
            folders.AddRange(profile.Mods.Select(m => m.Folder));
            if (RepoCheck.RepoRoot(profile.FilePath) is { } profileRepo)
                folders.Add(profileRepo);
        }
        if (RepoCheck.RepoRoot(AppContext.BaseDirectory) is { } repo)
            folders.Add(repo);
        return folders;
    }

    /// <summary>The reason why a play copy folder overlaps one of the <see cref="ProtectedFolders"/>, or null.</summary>
    public static string? ProtectedProblem(string folder, IEnumerable<string> protectedFolders) =>
        protectedFolders.FirstOrDefault(f => GameFolders.Overlaps(folder, f)) is { } other
            ? PlayCopyTexts.OverlapsProtected(GameFolders.Normal(folder), other)
            : null;

    /// <summary>
    /// Checks that OpenLancer can make a play copy with hard links in the folder (decision D309). The probe makes a hard
    /// link to the first file of <c>DATA</c> of the game, and deletes it. It makes a read-only file of its own, links
    /// it, and replaces and deletes that link with the flags that ignore the read-only attribute. The probe makes the
    /// folder, and deletes it again when it fails.
    /// </summary>
    /// <returns>Null when the probe passed, else the reason.</returns>
    public static string? Probe(GameBase game, string folder) => Probe(game, folder, HardLinks.Instance);

    internal static string? Probe(GameBase game, string folder, HardLinks links)
    {
        var root = GameFolders.Normal(folder);
        if (FolderProblem(root, game.Root) is { } problem)
            return problem;
        var parent = Path.GetDirectoryName(root);
        if (parent == null || !Directory.Exists(parent))
            return $"The folder {parent} does not exist.";
        var data = GameRoot.Resolve(game.Root, "DATA");
        var sample = data == null
            ? null
            : PlayCopyPlan.Files(data).Select(f => f.Relative).Order(StringComparer.Ordinal).FirstOrDefault();
        if (sample == null)
            return "The game has no file in DATA.";

        var made = !Directory.Exists(root);
        var id = Guid.NewGuid().ToString("N");
        string[] names = [Path.Combine(root, $".openlancer-probe-{id}"), Path.Combine(root, $".openlancer-probe-{id}-own"),
            Path.Combine(root, $".openlancer-probe-{id}-link")];
        var passed = false;
        try
        {
            Directory.CreateDirectory(root);
            links.Link(Path.Combine(data!, sample), names[0]);
            links.Delete(names[0]);
            links.CreateReadOnly(names[1]);
            links.Link(names[1], names[2]);
            links.WriteNew(names[2], [2]);
            links.Delete(names[2]);
            links.Delete(names[1]);
            passed = true;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return GameRoot.Sentence(e.Message);
        }
        finally
        {
            foreach (var name in names)
                TryDelete(links, name);
            if (made && !passed)
                TryDelete(links, root);
        }
    }

    /// <summary>The bytes that a full copy of the game needs.</summary>
    public static long FullCopySize(GameBase game) => PlayCopyPlan.Of(game, PlayCopyMode.Full).Values.Sum(f => f.Size);

    /// <summary>
    /// Makes the play copy of a game in a folder, or completes a play copy whose make stopped. A complete play copy of
    /// the same game stays as it is. The result holds the lock of the play copy.
    /// </summary>
    /// <param name="build">The build id of the game, for the record, or null.</param>
    /// <exception cref="PlayCopyException">The folder cannot be the play copy of the game, or another window holds the
    /// lock.</exception>
    /// <exception cref="IOException">A file could not be made. The next make completes the copy.</exception>
    public static PlayCopy Make(GameBase game, string folder, PlayCopyMode mode, string? build) =>
        Make(game, folder, mode, build, HardLinks.Instance);

    internal static PlayCopy Make(GameBase game, string folder, PlayCopyMode mode, string? build, HardLinks links)
    {
        var root = GameFolders.Normal(folder);
        var source = game with { Root = GameFolders.Normal(game.Root) };
        if (FolderProblem(root, source.Root) is { } problem)
            throw new PlayCopyException(problem);
        PlayCopy copy;
        if (PlayCopyRecord.Exists(root))
        {
            copy = Open(root, links);
            var refusal = !GameFolders.Same(copy.Record.Source, source.Root)
                ? PlayCopyTexts.OtherSource(root, copy.Record.Source)
                : copy.Record.Mode != mode
                    ? $"The play copy {root} is a {Name(copy.Record.Mode)}. Remove it first, or pick another folder."
                    : null;
            if (refusal != null)
            {
                copy.Dispose();
                throw new PlayCopyException(refusal);
            }
            if (copy.Record.State == PlayCopyState.Complete)
                return copy;
        }
        else
        {
            Directory.CreateDirectory(root);
            var lockFile = PlayCopyLock.Take(root);
            var record = new PlayCopyRecord
            {
                Source = source.Root, Layout = source.Layout, Build = build, Created = DateTime.UtcNow,
                State = PlayCopyState.Incomplete, Mode = mode,
            };
            copy = new PlayCopy(root, record, lockFile, links);
        }

        try
        {
            copy.Complete();
        }
        catch
        {
            copy.Dispose();
            throw;
        }
        return copy;
    }

    /// <summary>Opens a play copy and takes its lock.</summary>
    /// <exception cref="PlayCopyException">The folder is not a play copy, or another window holds the lock.</exception>
    public static PlayCopy Open(string folder) => Open(folder, HardLinks.Instance);

    internal static PlayCopy Open(string folder, HardLinks links)
    {
        var root = GameFolders.Normal(folder);
        if (!Directory.Exists(root) || IsLink(root))
            throw new PlayCopyException(PlayCopyTexts.NotAPlayCopy(root));
        PlayCopyRecord? record;
        try
        {
            record = PlayCopyRecord.Read(root);
        }
        catch (InvalidDataException e)
        {
            throw new PlayCopyException($"{PlayCopyTexts.NotAPlayCopy(root)} {e.Message}");
        }
        if (record == null)
            throw new PlayCopyException(PlayCopyTexts.NotAPlayCopy(root));
        if (GameFolders.Overlaps(root, record.Source))
            throw new PlayCopyException(PlayCopyTexts.OverlapsGame(root, record.Source));
        return new PlayCopy(root, record, PlayCopyLock.Take(root), links);
    }

    /// <summary>Removes a play copy: each name one by one, from the deepest folder up. It never follows a link. The
    /// folder must hold a valid record of a play copy.</summary>
    /// <exception cref="PlayCopyException">The folder is not a play copy, or another window holds the lock.</exception>
    public static void Remove(string folder)
    {
        using var copy = Open(folder);
        copy.Remove();
    }

    /// <summary>
    /// Makes the play copy equal to the game plus the files (section 8.4 of the M11 design). It checks each link
    /// through a handle first. It keeps an installed file that changed outside OpenLancer. It writes each file that
    /// differs from the file in the copy. Each file of the last install that the files do not have gets its kind of
    /// the make again.
    /// </summary>
    /// <param name="files">The files of a merge on the game of this play copy.</param>
    /// <param name="profile">The profile file of the install, for the record.</param>
    /// <exception cref="PlayCopyException">The files come from another game.</exception>
    public PlayCopyResult Install(TargetFiles files, string? profile)
    {
        if (!GameFolders.Same(files.Base.Root, Record.Source))
            throw new PlayCopyException($"The files come from the game {files.Base.Root}, and the play copy {Root} " +
                                        $"copies {Record.Source}. OpenLancer did not install them.");
        return Apply(files.Files.Select(f => (f.Path, f.Bytes)).ToList(), profile);
    }

    /// <summary>Gives each file of the last install its kind of the make again (section 8.4).</summary>
    public PlayCopyResult Uninstall() => Apply([], null);

    /// <summary>
    /// Checks the play copy and changes nothing (section 8.4). It checks the links through handles and the files of the
    /// last install by their SHA-256. It also finds the files that the game added or removed. Each difference is a
    /// warning.
    /// </summary>
    public PlayCopyResult Check()
    {
        ThrowIfDisposed();
        var problems = new List<Diagnostic>();
        CheckLinks(problems, update: false);
        CheckInstalled(problems, keep: false);
        var plan = PlayCopyPlan.Of(Source, Record.Mode);
        foreach (var path in plan.Keys.Where(p => !Record.Entries.ContainsKey(p) && !Record.Installed.ContainsKey(p)))
            problems.Add(Diagnostic.Warning(Full(path), PlayCopyTexts.NewSourceFile(path)));
        foreach (var path in Record.Entries.Keys.Where(p => !plan.ContainsKey(p) && !Record.Installed.ContainsKey(p)))
            problems.Add(Diagnostic.Warning(Full(path), PlayCopyTexts.SourceFileGone(path)));
        return new PlayCopyResult(problems, 0, 0, Record.LinkCount, Record.CopyCount);
    }

    /// <summary>Removes this play copy, and releases its lock. Section 8.2, rule 9.</summary>
    public void Remove()
    {
        ThrowIfDisposed();
        if (IsLink(Root) || GameFolders.Overlaps(Root, Record.Source))
            throw new PlayCopyException(PlayCopyTexts.OverlapsGame(Root, Record.Source));
        DeleteContent(Root, top: true);
        Dispose();
        links.Delete(Path.Combine(Root, PlayCopyLock.FileName));
        links.Delete(PlayCopyRecord.PathIn(Root));
        links.Delete(Root);
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lockFile.Dispose();
    }

    /// <summary>Makes each planned file that the play copy does not have, the own edits and the read-me file, then
    /// sets the state Complete (section 8.3).</summary>
    private void Complete()
    {
        WriteRecord();
        var folders = new Folders(Root);
        foreach (var (path, file) in PlayCopyPlan.Of(Source, Record.Mode))
        {
            if (!Record.Entries.ContainsKey(path) || links.TryStamp(Full(path)) == null)
                MakeEntry(path, file, folders);
        }
        ApplyOwnEdits(folders);
        var text = Record.Mode == PlayCopyMode.Full ? PlayCopyTexts.ReadMeFull(Record.Source) : PlayCopyTexts.ReadMe(Record.Source);
        links.WriteNew(Path.Combine(Root, ReadMeName), Encoding.UTF8.GetBytes(text + "\r\n"));
        Record.State = PlayCopyState.Complete;
        WriteRecord();
    }

    /// <summary>The steps of section 8.4 for the files of an install.</summary>
    private PlayCopyResult Apply(IReadOnlyList<(string Path, byte[] Bytes)> files, string? profile)
    {
        ThrowIfDisposed();
        var problems = new List<Diagnostic>();
        var folders = new Folders(Root);
        var plan = PlayCopyPlan.Of(Source, Record.Mode);
        var wanted = new SortedDictionary<string, byte[]>(VfsPath.Comparer);
        foreach (var (path, bytes) in files)
            wanted[PathRules.WithDiskCase(Root, Source.ToDisk(path))] = bytes;
        bool Planned(string path) => !wanted.ContainsKey(path) && !Record.Installed.ContainsKey(path);

        // Step 2: the links. A replaced or removed link, and a new version of the game file, need a new link. The install
        // writes its own files over their names.
        var refresh = CheckLinks(problems, update: true).Where(p => plan.ContainsKey(p) && !wanted.ContainsKey(p))
            .ToList();
        // The files that the game added, and the copies of game files that changed.
        refresh.AddRange(plan.Where(p => Planned(p.Key) && (!Record.Entries.TryGetValue(p.Key, out var entry) ||
                                                              entry.Kind == PlayCopyKind.Copy &&
                                                              (entry.Size != p.Value.Size || entry.Time != p.Value.Time)))
            .Select(p => p.Key));
        var gone = Record.Entries.Keys.Where(p => !plan.ContainsKey(p) && Planned(p)).ToList();
        // Step 3: an installed file that changed outside OpenLancer.
        CheckInstalled(problems, keep: true);

        // Step 4: the files of the install that differ from the files in the copy.
        var written = 0;
        foreach (var (path, bytes) in wanted)
        {
            var full = Full(path);
            if (File.Exists(full) && File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes))
                continue;
            folders.Ensure(Path.GetDirectoryName(full)!);
            links.WriteNew(full, bytes);
            written++;
        }

        // Step 5: the files of the last install that this install does not have.
        var restored = 0;
        foreach (var path in Record.Installed.Keys.Where(p => !wanted.ContainsKey(p)).ToList())
        {
            if (plan.TryGetValue(path, out var file))
                MakeEntry(path, file, folders);
            else
                DeleteName(path);
            restored++;
        }

        // Step 6: the links again, and the changes of the game.
        foreach (var path in refresh.Distinct(VfsPath.Comparer))
            MakeEntry(path, plan[path], folders);
        foreach (var path in gone)
            DeleteName(path);

        // Step 7 and 8: the own edits, then the record.
        ApplyOwnEdits(folders);
        var now = DateTime.UtcNow;
        Record.Installed = new SortedDictionary<string, PlayCopyInstall>(
            wanted.Keys.ToDictionary(p => p, p => new PlayCopyInstall(DataDigest.HashFile(Full(p)), profile, now)),
            VfsPath.Comparer);
        WriteRecord();
        return new PlayCopyResult(problems, written, restored, Record.LinkCount, Record.CopyCount);
    }

    /// <summary>
    /// Checks each link of the make through handles (decision D323). A name that a program replaced or removed in the
    /// play copy needs a new link, and the game did not change. A linked file whose size or write time changed changed
    /// the game too. A game file that the game replaced, for example in an update, needs a new link.
    /// </summary>
    /// <param name="update">True to record the new stamps of a changed linked file.</param>
    /// <returns>The paths that need a new link.</returns>
    private List<string> CheckLinks(List<Diagnostic> problems, bool update)
    {
        var relink = new List<string>();
        foreach (var (path, entry) in Record.Entries
                     .Where(e => e.Value.Kind == PlayCopyKind.Link && !Record.Installed.ContainsKey(e.Key)).ToList())
        {
            var full = Full(path);
            if (links.TryStamp(full) is not { } name)
            {
                problems.Add(Diagnostic.Warning(full, PlayCopyTexts.LinkRemoved(path)));
                relink.Add(path);
                continue;
            }
            if (!entry.IsFileOf(name))
            {
                problems.Add(Diagnostic.Warning(full, PlayCopyTexts.LinkReplaced(path)));
                relink.Add(path);
                continue;
            }
            if (name.Size != entry.Size || name.WriteTime != entry.Time)
            {
                problems.Add(Diagnostic.Warning(full, PlayCopyTexts.LinkedFileChanged(full, SourcePath(path))));
                if (update)
                    Record.Entries[path] = entry with { Size = name.Size, Time = name.WriteTime };
            }
            if (links.TryStamp(SourcePath(path)) is { } source && !entry.IsFileOf(source))
            {
                problems.Add(Diagnostic.Warning(full, PlayCopyTexts.SourceReplaced(path)));
                relink.Add(path);
            }
        }
        return relink;
    }

    /// <summary>Checks the files of the last install by their SHA-256. A file that changed outside OpenLancer is a
    /// warning.</summary>
    /// <param name="keep">True to keep a copy of each changed file in <see cref="KeepFolder"/>.</param>
    private void CheckInstalled(List<Diagnostic> problems, bool keep)
    {
        foreach (var (path, installed) in Record.Installed)
        {
            var full = Full(path);
            if (!File.Exists(full) || DataDigest.HashFile(full) == installed.Sha256)
                continue;
            var kept = Path.Combine(Root, KeepFolder, "changed", path);
            Folders.Check(Root, Path.GetDirectoryName(full)!);
            if (keep)
            {
                new Folders(Root).Ensure(Path.GetDirectoryName(kept)!);
                links.CopyNew(full, kept);
            }
            problems.Add(Diagnostic.Warning(full, PlayCopyTexts.InstalledFileChanged(path, kept)));
        }
    }

    /// <summary>Makes one file of the game in the copy: a hard link or a real copy, and its entry.</summary>
    private void MakeEntry(string path, PlannedFile file, Folders folders)
    {
        var target = Full(path);
        folders.Ensure(Path.GetDirectoryName(target)!);
        var source = SourcePath(path);
        if (file.Kind == PlayCopyKind.Link)
        {
            links.Link(source, target);
            var stamp = links.Stamp(target);
            Record.Entries[path] = new PlayCopyEntry(PlayCopyKind.Link, stamp.Size, stamp.WriteTime)
            {
                Volume = stamp.Volume, FileIndex = stamp.Index,
            };
        }
        else
        {
            links.CopyNew(source, target);
            Record.Entries[path] = new PlayCopyEntry(PlayCopyKind.Copy, file.Size, file.Time);
        }
    }

    /// <summary>
    /// The own edit of the play copy (decision D314) is <c>save_in_directory = true</c> in the copy of
    /// <c>EXE\flplusplus.ini</c>. Then the saves of the modded game go into <c>SAVE</c> of the play copy. HD Edition has
    /// this file. A game without it gets no edit.
    /// </summary>
    private void ApplyOwnEdits(Folders folders)
    {
        if (Record.Layout != GameLayout.Standard)
            return;
        var path = PathRules.WithDiskCase(Root, FlplusplusPath);
        var full = Full(path);
        if (!File.Exists(full))
            return;
        var bytes = File.ReadAllBytes(full);
        if (OwnEdits.SaveInDirectory(path, bytes) is not { } edited)
            return;
        if (!edited.AsSpan().SequenceEqual(bytes))
            links.WriteNew(full, edited);
        Record.Own[path] = OwnEdits.SaveInDirectoryLine;
        folders.Ensure(Full(PathRules.WithDiskCase(Root, SaveFolder)));
    }

    /// <summary>Deletes a name of the copy, and each folder above it that is empty and that the game does not
    /// have.</summary>
    private void DeleteName(string path)
    {
        var full = Full(path);
        Folders.Check(Root, Path.GetDirectoryName(full)!);
        if (links.TryStamp(full) != null)
            links.Delete(full);
        Record.Entries.Remove(path);
        for (var folder = Path.GetDirectoryName(full); folder != null && !GameFolders.Same(folder, Root);
             folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(Path.Combine(Record.Source, Path.GetRelativePath(Root, folder))) ||
                !Directory.Exists(folder) || IsLink(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
                break;
            links.Delete(folder);
        }
    }

    /// <summary>Deletes each name below a folder, from the deepest folder up. A link to a folder loses its name only.
    /// The root keeps its record and its lock.</summary>
    private void DeleteContent(string folder, bool top)
    {
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions
                 {
                     AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false,
                 }).ToList())
        {
            if (top && (info.Name.Equals(PlayCopyRecord.FileName, StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Equals(PlayCopyLock.FileName, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (info is DirectoryInfo && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                DeleteContent(info.FullName, top: false);
            links.Delete(info.FullName);
        }
    }

    private void WriteRecord() => links.WriteNew(PlayCopyRecord.PathIn(Root), Record.ToBytes());

    /// <summary>The full path of a path of the copy. It must stay inside the root.</summary>
    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path));
        return PathRules.IsInside(full, Root) && !GameFolders.Same(full, Root)
            ? full
            : throw new PlayCopyException($"The path {path} is not inside the play copy {Root}.");
    }

    private string SourcePath(string path) => Path.Combine(Record.Source, path);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static string Name(PlayCopyMode mode) => mode == PlayCopyMode.Full ? "full copy" : "play copy with hard links";

    private static bool IsLink(string folder) => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0;

    /// <summary>The file in the folder with the letter case on disk, or null when it does not exist.</summary>
    private static string? FileIn(string folder, string name) =>
        Path.Combine(folder, PathRules.WithDiskCase(folder, name)) is var path && File.Exists(path) ? path : null;

    private static void TryDelete(HardLinks links, string path)
    {
        try
        {
            if (links.TryStamp(path) != null)
                links.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The probe gives its own error. A name that stays has no link to the game.
        }
    }

    /// <summary>The folders of a play copy that the code made or checked. Each folder between the root and a changed
    /// file must be a plain folder. So no write goes through a link to another folder (section 8.2, rule 8).</summary>
    private sealed class Folders(string root)
    {
        private readonly HashSet<string> known = new(VfsPath.Comparer);

        /// <summary>Makes the folder and each folder above it up to the root. A link among them stops the
        /// action.</summary>
        public void Ensure(string folder)
        {
            if (GameFolders.Same(folder, root) || !known.Add(folder))
                return;
            if (!PathRules.IsInside(folder, root))
                throw new PlayCopyException($"The folder {folder} is not inside the play copy {root}.");
            Ensure(Path.GetDirectoryName(folder)!);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            else if (IsLink(folder))
                throw new PlayCopyException(PlayCopyTexts.IsALink(folder));
        }

        /// <summary>Refuses a link among the folders between the root and a folder that exist. It makes no
        /// folder.</summary>
        public static void Check(string root, string folder)
        {
            for (var current = folder; !GameFolders.Same(current, root); current = Path.GetDirectoryName(current)!)
            {
                if (!PathRules.IsInside(current, root))
                    throw new PlayCopyException($"The folder {current} is not inside the play copy {root}.");
                if (Directory.Exists(current) && IsLink(current))
                    throw new PlayCopyException(PlayCopyTexts.IsALink(current));
            }
        }
    }
}

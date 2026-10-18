using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Overlay;

/// <summary>
/// The deploy refused to run. Nothing was written.
/// </summary>
public sealed class DeployException(string message) : Exception(message);

/// <summary>
/// Writes the merged view of a profile to <c>&lt;profile folder&gt;\&lt;profile name&gt;\</c>, so that tools that read a plain
/// folder (lancer.exe, LancerEdit) see the mods. The folder is managed: files that are not in the view are deleted.
/// </summary>
/// <remarks>
/// <para>The deploy copies files. A later deploy copies only files that changed, so a deploy after an edit is fast.</para>
/// <para>Safety rules: the deploy writes only into a folder that it created (it holds <see cref="MarkerFileName"/>)
/// or into a new or empty folder. The folder must not overlap the base data, a mod or the profile file, and must
/// not be or contain a link. Each file is written to a temporary file and then moved into place, so a write never
/// goes through a hard link. The deploy never clears a read-only attribute, so a read-only file in the folder
/// stops the deploy.</para>
/// </remarks>
public static class ProfileDeployer
{
    /// <summary>The file that marks a deploy folder as managed. It also records whether the last deploy completed.</summary>
    public const string MarkerFileName = ".openlancer-deploy";

    private const string Complete = "complete";
    private const string Incomplete = "incomplete";

    /// <param name="PreviousDeployIncomplete">The deploy before this one stopped before it completed.</param>
    public sealed record Result(string Folder, int Copied, int Written, int Deleted, int Unchanged,
        bool PreviousDeployIncomplete);

    public static string DeployFolder(ModProfile profile) =>
        Path.Combine(Path.GetDirectoryName(profile.FilePath)!, profile.Name);

    public static string GameConfigPath(ModProfile profile) =>
        Path.Combine(Path.GetDirectoryName(profile.FilePath)!, profile.Name + ".lancer.ini");

    /// <exception cref="DeployException">The overlay has errors, or the deploy folder is not safe to write.</exception>
    /// <exception cref="IOException">A file could not be written. Deploy again to complete the folder.</exception>
    public static Result Deploy(ModOverlay overlay, ModProfile profile)
    {
        if (overlay.HasErrors)
            throw new DeployException("The mod overlay has errors. Fix them before a deploy.");
        var target = Path.GetFullPath(DeployFolder(profile));
        CheckTarget(target, overlay, profile);

        var marker = Path.Combine(target, MarkerFileName);
        var previousIncomplete = File.Exists(marker) && File.ReadAllText(marker).Trim() != Complete;
        Directory.CreateDirectory(target);
        File.WriteAllText(marker, Incomplete);

        var view = BuildView(overlay);
        int copied = 0, written = 0, unchanged = 0;
        foreach (var (relative, source) in view)
        {
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            switch (source)
            {
                case string diskFile when IsSameCopy(diskFile, destination):
                case byte[] bytes when File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes):
                    unchanged++;
                    break;
                case string diskFile:
                    AtomicFile.Replace(destination, temp =>
                    {
                        // A stream copy makes a new, writable file. File.Copy would copy the read-only attribute
                        // of a base file, and a read-only temp file left by a stopped deploy blocks the next one.
                        using (var input = File.OpenRead(diskFile))
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                            input.CopyTo(output);
                        File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(diskFile));
                    });
                    copied++;
                    break;
                case byte[] bytes:
                    AtomicFile.WriteAllBytes(destination, bytes);
                    written++;
                    break;
            }
        }

        var keep = new HashSet<string>(view.Keys, VfsPath.Comparer) { MarkerFileName };
        var deleted = DeleteExtraFiles(target, keep);
        WriteGameConfig(GameConfigPath(profile), target);
        File.WriteAllText(marker, Complete);
        return new Result(target, copied, written, deleted, unchanged, previousIncomplete);
    }

    private static void CheckTarget(string target, ModOverlay overlay, ModProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new DeployException($"The profile file {profile.FilePath} has no name.");
        if (target.IndexOfAny([',', ';']) >= 0)
            throw new DeployException($"The deploy folder {target} contains ',' or ';'. lancer.exe cannot read such a path. Move the profile.");
        for (var dir = new DirectoryInfo(target); dir != null; dir = dir.Parent)
        {
            if (dir.Exists && dir.LinkTarget != null)
                throw new DeployException($"{dir.FullName} is a link. The deploy folder and its parents must be plain folders.");
        }
        // The base folder or a mod folder can be a link, so compare the real paths too.
        if (PathRules.Overlaps(target, overlay.BaseFolder) ||
            PathRules.Overlaps(target, PathRules.RealPath(overlay.BaseFolder)))
            throw new DeployException($"The deploy folder {target} overlaps the base folder {overlay.BaseFolder}.");
        foreach (var mod in overlay.Mods.Where(m => PathRules.Overlaps(target, m.Folder) ||
                                                     PathRules.Overlaps(target, PathRules.RealPath(m.Folder))))
            throw new DeployException($"The deploy folder {target} overlaps the folder of mod '{mod.Id}'.");
        if (PathRules.IsInside(profile.FilePath, target) || PathRules.IsInside(PathRules.RealPath(profile.FilePath), target))
            throw new DeployException($"The deploy folder {target} contains the profile file.");
        if (!Directory.Exists(target))
            return;

        var hasMarker = File.Exists(Path.Combine(target, MarkerFileName));
        if (!hasMarker && Directory.EnumerateFileSystemEntries(target).Any())
            throw new DeployException($"The folder {target} exists and was not made by a deploy. Remove it or rename the profile.");
        foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (info.LinkTarget != null)
                throw new DeployException($"{info.FullName} is a link. The deploy folder must contain only plain files and folders.");
            if (info is FileInfo { IsReadOnly: true })
                throw new DeployException($"{info.FullName} is read-only. The deploy does not change it, because it can be a link to the base data.");
        }
    }

    /// <summary>
    /// Relative path (with the casing of the base when the path or its folder exists there) to its source:
    /// a file path on disk, or generated bytes.
    /// </summary>
    private static Dictionary<string, object> BuildView(ModOverlay overlay)
    {
        var view = new Dictionary<string, object>(VfsPath.Comparer);
        var folders = new Dictionary<string, string>(VfsPath.Comparer);
        foreach (var file in Directory.EnumerateFiles(overlay.BaseFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(overlay.BaseFolder, file);
            view[relative] = file;
            for (var folder = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                folders.TryAdd(VfsPath.Normalize(folder)!, folder);
        }

        var files = view.Keys.ToDictionary(k => VfsPath.Normalize(k)!, k => k, VfsPath.Comparer);
        foreach (var file in overlay.Files)
        {
            var relative = files.GetValueOrDefault(file.Path) ?? LocalPath(file.Path, folders);
            view[relative] = file.DiskPath != null ? file.DiskPath : file.ReadAllBytes();
        }
        return view;
    }

    /// <summary>Converts a normal path to a local relative path, with the casing of the deepest base folder in it.</summary>
    private static string LocalPath(string normalPath, Dictionary<string, string> baseFolders)
    {
        var parent = VfsPath.Parent(normalPath);
        var name = normalPath[(normalPath.LastIndexOf('\\') + 1)..];
        var suffix = new List<string>();
        while (parent.Length > 0 && !baseFolders.ContainsKey(parent))
        {
            suffix.Insert(0, parent[(parent.LastIndexOf('\\') + 1)..]);
            parent = VfsPath.Parent(parent);
        }
        var parts = new List<string>();
        if (parent.Length > 0)
            parts.Add(baseFolders[parent]);
        parts.AddRange(suffix);
        parts.Add(name);
        return Path.Combine(parts.ToArray());
    }

    private static int DeleteExtraFiles(string target, HashSet<string> keep)
    {
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList())
        {
            if (keep.Contains(Path.GetRelativePath(target, file)))
                continue;
            File.Delete(file);
            deleted++;
        }
        foreach (var dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        return deleted;
    }

    private static bool IsSameCopy(string source, string destination)
    {
        if (!File.Exists(destination))
            return false;
        var s = new FileInfo(source);
        var d = new FileInfo(destination);
        return s.Length == d.Length && s.LastWriteTimeUtc == d.LastWriteTimeUtc;
    }

    /// <summary>
    /// Creates the lancer.exe config for the profile, or sets only its <c>freelancer_path</c>, so that the settings
    /// that the game saved stay. The file starts with a UTF-8 byte order mark, so the engine reads a non-ASCII
    /// path correctly. The game writes the file again without one when it saves its settings.
    /// </summary>
    private static void WriteGameConfig(string configPath, string dataFolder)
    {
        var pathLine = $"freelancer_path = {dataFolder}";
        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : ["[Librelancer]"];
        var index = lines.FindIndex(l => l.TrimStart().StartsWith("freelancer_path", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            lines[index] = pathLine;
        else
        {
            var header = lines.FindIndex(l => l.Trim().Equals("[Librelancer]", StringComparison.OrdinalIgnoreCase));
            if (header < 0)
                lines.InsertRange(0, ["[Librelancer]", pathLine]);
            else
                lines.Insert(header + 1, pathLine);
        }
        File.WriteAllLines(configPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}

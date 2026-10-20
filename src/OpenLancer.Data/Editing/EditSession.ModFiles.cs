using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>The editor files of the active mod that the session opened, by their key (see
    /// <see cref="ModFileDocument"/>).</summary>
    private readonly Dictionary<string, ModFileDocument> modFiles = new(VfsPath.Comparer);

    /// <summary>The keys of the editor files that a command changed, also when an undo took the change back.</summary>
    private readonly HashSet<string> touchedModFiles = new(VfsPath.Comparer);

    /// <summary>
    /// The text of an editor file of the active mod in W (section 6.8 of the M6b design), or null when the file does
    /// not exist or the session has no active mod.
    /// </summary>
    /// <param name="relativePath">The path relative to the mod folder, for example
    /// <c>graphs\DATA\MISSIONS\M02\m02.ini</c>.</param>
    /// <exception cref="ArgumentException">The path is not the path of an editor file.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    internal string? ModFileText(string relativePath) => ModFile(relativePath)?.Text;

    /// <summary>
    /// The paths of the editor files of the active mod in a folder that exist in W (section 6.8 of the M6b design): the
    /// files on disk and the files that a command made, without the files that a command deleted. Each path is relative
    /// to the mod folder.
    /// </summary>
    /// <param name="folder">The folder relative to the mod folder, with <c>\</c> at its end, for example
    /// <c>prefabs\missions\</c>.</param>
    internal IReadOnlyList<string> ModFilesIn(string folder)
    {
        if (ActiveMod is not { } active)
            return [];
        var paths = new HashSet<string>(VfsPath.Comparer);
        var disk = Path.Combine(active.Folder, folder.TrimEnd('\\').Replace('\\', Path.DirectorySeparatorChar));
        try
        {
            if (Directory.Exists(disk))
            {
                foreach (var file in Directory.EnumerateFiles(disk, "*.ini"))
                    paths.Add(folder + Path.GetFileName(file));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that the editor cannot read gives the files that the session opened.
        }
        foreach (var document in modFiles.Values)
        {
            if (document.RelativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                document.RelativePath.IndexOf('\\', folder.Length) < 0)
                paths.Add(document.RelativePath);
        }
        return paths.Where(p => ModFileDocument.Normalize(p) != null && SafeText(p) != null)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The text of an editor file of the active mod, or null when it does not exist or cannot be read.</summary>
    private string? SafeText(string relativePath)
    {
        try
        {
            return ModFileText(relativePath);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The document of an editor file of the active mod. Opens it at the first use. Null without an active
    /// mod.</summary>
    /// <exception cref="ArgumentException">The path is not the path of an editor file.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be read.</exception>
    private ModFileDocument? ModFile(string relativePath)
    {
        var normal = ModFileDocument.Normalize(relativePath) ??
                     throw new ArgumentException(ModFileDocument.PathProblem(relativePath), nameof(relativePath));
        if (ActiveMod is not { } active)
            return null;
        var key = ModFileDocument.KeyPrefix + normal;
        if (!modFiles.TryGetValue(key, out var document))
        {
            document = ModFileDocument.Open(active.Folder, normal);
            modFiles[key] = document;
        }
        return document;
    }
}

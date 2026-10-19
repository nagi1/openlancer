using System;
using System.Collections.Generic;
using System.IO;
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

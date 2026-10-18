using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.IO;

namespace OpenLancer.Data.IO;

/// <summary>The files of a Librelancer file system, for example a view of the game data.</summary>
public static class VfsFiles
{
    /// <summary>
    /// The install paths of the files with an extension in a folder and in its subfolders. The names sort without case,
    /// so the order is always the same.
    /// </summary>
    /// <param name="extension">The extension with its dot, for example <c>.ini</c>. Case does not matter.</param>
    /// <param name="folder">The install path of the folder, or an empty string for the install root.</param>
    public static IEnumerable<string> Find(FileSystem view, string extension, string folder = "") =>
        Find(view, [extension], folder);

    /// <summary>
    /// The install paths of the files with one of the extensions in a folder and in its subfolders, in one walk of the
    /// folders (milestone M23). The names sort without case, so the order is always the same.
    /// </summary>
    /// <param name="extensions">The extensions with their dots, for example <c>.cmp</c> and <c>.3db</c>. Case does not
    /// matter.</param>
    /// <param name="folder">The install path of the folder, or an empty string for the install root.</param>
    public static IEnumerable<string> Find(FileSystem view, IReadOnlyList<string> extensions, string folder = "")
    {
        foreach (var name in view.GetFiles(folder).Order(VfsPath.Comparer))
        {
            if (extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                yield return Join(folder, name);
        }
        foreach (var name in view.GetDirectories(folder).Order(VfsPath.Comparer))
        {
            foreach (var file in Find(view, extensions, Join(folder, name)))
                yield return file;
        }
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "\\" + name;
}

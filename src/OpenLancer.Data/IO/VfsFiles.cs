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
    public static IEnumerable<string> Find(FileSystem view, string extension, string folder = "")
    {
        foreach (var name in view.GetFiles(folder).Order(VfsPath.Comparer))
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                yield return Join(folder, name);
        }
        foreach (var name in view.GetDirectories(folder).Order(VfsPath.Comparer))
        {
            foreach (var file in Find(view, extension, Join(folder, name)))
                yield return file;
        }
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "\\" + name;
}

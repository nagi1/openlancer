using System;
using System.IO;
using System.Linq;

namespace OpenLancer.Data.IO;

/// <summary>Rules for paths on disk.</summary>
internal static class PathRules
{
    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or is inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        if (relative == ".")
            return true;
        if (Path.IsPathRooted(relative))
            return false;
        return !(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
                 relative.StartsWith(".." + Path.AltDirectorySeparatorChar));
    }

    /// <summary>
    /// Gives a normal install path (see <see cref="VfsPath"/>) the letter case of the files and folders that exist
    /// under <paramref name="root"/>. A part that does not exist keeps its letter case, and so do the parts after it.
    /// </summary>
    public static string WithDiskCase(string root, string normalPath)
    {
        var parts = normalPath.Split('\\');
        var current = root;
        for (var i = 0; i < parts.Length && Directory.Exists(current); i++)
        {
            var match = Directory.EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => string.Equals(name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (match == null)
                break;
            parts[i] = match;
            current = Path.Combine(current, match);
        }
        return string.Join('\\', parts);
    }
}

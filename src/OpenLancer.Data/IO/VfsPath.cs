using System;
using System.Collections.Generic;

namespace OpenLancer.Data.IO;

/// <summary>
/// Paths relative to the game install root, in the form that Librelancer's <c>FileSystem</c> uses.
/// </summary>
/// <remarks>
/// The engine asks for paths such as <c>EXE\..\data\ships\shiparch.ini</c>, with either separator and in any case.
/// The normal form resolves <c>.</c> and <c>..</c> and uses <c>\</c>. Compare normal forms with <see cref="Comparer"/>.
/// </remarks>
public static class VfsPath
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Returns the normal form of <paramref name="path"/>, or null when the path leaves the install root.
    /// </summary>
    public static string? Normalize(string path)
    {
        // Most paths are in the normal form already, for example the path of each section reference.
        if (IsNormal(path))
            return path;
        var parts = new List<string>();
        foreach (var part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('\\', parts);
    }

    /// <summary>True when a path has the normal form: no <c>/</c>, no empty part, and no <c>.</c> or <c>..</c>
    /// part.</summary>
    private static bool IsNormal(string path)
    {
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '\\')
            {
                if (path[i] == '/')
                    return false;
                continue;
            }
            var length = i - start;
            if (length == 0 || path[start] == '.' && (length == 1 || length == 2 && path[start + 1] == '.'))
                return false;
            start = i + 1;
        }
        return true;
    }

    /// <summary>
    /// Returns the folder part of a normal-form path, or an empty string for a file in the root.
    /// </summary>
    public static string Parent(string normalPath)
    {
        var index = normalPath.LastIndexOf('\\');
        return index < 0 ? "" : normalPath[..index];
    }
}

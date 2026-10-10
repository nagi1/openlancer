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

    /// <summary>
    /// Returns the folder part of a normal-form path, or an empty string for a file in the root.
    /// </summary>
    public static string Parent(string normalPath)
    {
        var index = normalPath.LastIndexOf('\\');
        return index < 0 ? "" : normalPath[..index];
    }
}

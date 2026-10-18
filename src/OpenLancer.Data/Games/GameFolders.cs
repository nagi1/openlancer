using System;
using System.IO;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Games;

/// <summary>
/// Path checks for the game folders and the folders that OpenLancer writes (the write guard of milestone M11a). They
/// use <see cref="PathRules"/>, also with the real paths of links.
/// </summary>
public static class GameFolders
{
    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>True when the two paths name the same folder: without letter case on Windows, and without a final
    /// separator.</summary>
    public static bool Same(string a, string b) => Comparer.Equals(Normal(a), Normal(b));

    /// <summary>True when one path is the other or is inside it, also through a junction or a symbolic link.</summary>
    public static bool Overlaps(string a, string b) =>
        PathRules.Overlaps(a, b) || PathRules.Overlaps(PathRules.RealPath(a), PathRules.RealPath(b));

    /// <summary>The full path without a final separator, as the editor stores the path of a game.</summary>
    public static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

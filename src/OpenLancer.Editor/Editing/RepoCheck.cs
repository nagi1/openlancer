using System.IO;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// Finds out when a mod folder is part of the OpenLancer repo, for example the sample mod. Its files are tracked
/// files, so the editor asks once for each session before it edits such a mod (decision D5).
/// </summary>
internal static class RepoCheck
{
    /// <summary>The file in the root folder of the OpenLancer repo.</summary>
    public const string MarkerFile = "OpenLancer.slnx";

    /// <summary>The root folder of the OpenLancer repo that holds the folder, or null.</summary>
    public static string? RepoRoot(string folder)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(folder)); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, MarkerFile)))
                return dir.FullName;
        }
        return null;
    }
}

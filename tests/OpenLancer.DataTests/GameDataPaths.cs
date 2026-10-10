using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Finds the private folders that hold the extracted retail data. These folders are never in the repo.
/// </summary>
internal static class GameDataPaths
{
    public const string BaseVariable = "OPENLANCER_BASE";

    /// <summary>
    /// Returns the extracted vanilla data folder, or skips the test when it is not available.
    /// <c>OPENLANCER_BASE</c> overrides the default, which is <c>..\GameData\base</c> next to the repo.
    /// </summary>
    public static string RequireBase()
    {
        var folder = Environment.GetEnvironmentVariable(BaseVariable);
        if (string.IsNullOrEmpty(folder))
        {
            var repoRoot = FindRepoRoot();
            folder = repoRoot == null ? null : Path.Combine(repoRoot, "..", "GameData", "base");
        }

        Assert.SkipUnless(folder != null && Directory.Exists(folder),
            $"No extracted game data. Set {BaseVariable} or extract the ISO to ..\\GameData\\base.");
        return Path.GetFullPath(folder!);
    }

    /// <summary>
    /// The folder for local test output (dumps, log baselines). It is next to the base folder, outside the repo.
    /// </summary>
    public static string OutFolder(string baseFolder)
    {
        var folder = Path.Combine(Path.GetDirectoryName(baseFolder)!, "out");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A mod from the repo's <c>samples/mods</c> folder.</summary>
    public static string SampleMod(string id) =>
        Path.Combine(FindRepoRoot() ?? throw new InvalidOperationException("The repo root was not found."),
            "samples", "mods", id);

    /// <summary>
    /// Finds the repo from the test binaries, or else from this source file. The second search works when the
    /// build output is outside the repo, for example with <c>-p:UseArtifactsOutput=true</c>.
    /// </summary>
    private static string? FindRepoRoot() => FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(SourceFolder());

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenLancer.slnx")))
                return dir.FullName;
        }
        return null;
    }

    private static string SourceFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

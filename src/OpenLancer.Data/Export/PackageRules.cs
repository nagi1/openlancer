using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Export;

/// <summary>
/// The rules of what a package holds (milestone M11). A package holds only the files of the mod and the merged files
/// that the mod changes. It never holds a game file.
/// </summary>
public static class PackageRules
{
    /// <summary>The configuration files of the game that a retail package must not replace as a whole file.</summary>
    public static readonly IReadOnlyList<string> RefusedWholeFiles =
        [@"EXE\freelancer.ini", @"EXE\dacom.ini", @"EXE\dacomsrv.ini", @"EXE\flplusplus.ini"];

    private static readonly Lazy<Dictionary<string, (string Build, string Path)>> GameHashes = new(() =>
        Hashes(KnownBuilds.Embedded));

    /// <summary>
    /// The reason why a package leaves out a file of a mod, or null. The rules follow the import rules of Unity: a
    /// hidden or system file, a name that starts with <c>.</c>, <c>Thumbs.db</c>, <c>desktop.ini</c>, a temporary file
    /// (<c>*.oltmp</c>, <c>*.tmp</c>) and a name that ends with <c>~</c>.
    /// </summary>
    /// <param name="diskPath">The file on disk.</param>
    /// <param name="relative">The path of the file in the mod folder, or null to check the file name only.</param>
    public static string? SkipReason(string diskPath, string? relative = null)
    {
        var name = Path.GetFileName(diskPath);
        var parts = (relative ?? name).Split('\\', '/');
        if (parts.Any(p => p.StartsWith('.')))
            return "its name starts with '.'";
        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            return "Windows makes this file";
        if (name.EndsWith(AtomicFile.TempSuffix, StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith('~'))
            return "it is a temporary file";
        try
        {
            if ((File.GetAttributes(diskPath) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                return "it is a hidden or system file";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that OpenLancer cannot read gives its own error when the export reads it.
        }
        return null;
    }

    /// <summary>The warning of a file that a package leaves out.</summary>
    public static string SkippedText(string file, string reason) =>
        $"OpenLancer did not export {file}: hidden, system and temporary files are not part of a package ({reason}).";

    /// <summary>The error of a whole file of a mod with the content of the game file in another form.</summary>
    public static string GameFileInOtherForm(string file, string path) =>
        $"{file} has the content of {path} of the game in another form. A mod must not contain game files. Remove the file.";

    /// <summary>The warning of a whole file of a mod that replaces a game file with changes.</summary>
    public static string ReplacesGameFile(string path) =>
        $"The file replaces {path} of the game. It undoes the changes of HD Edition and of other mods in this file. A " +
        "patch changes only your lines.";

    /// <summary>The errors of the whole configuration files of a mod for a retail target.</summary>
    public static IEnumerable<Diagnostic> RefusedFiles(Mod mod) =>
        RefusedWholeFiles.Where(mod.Files.ContainsKey).Select(path => Diagnostic.Error(mod.Files[path],
            $"A whole {path} replaces the lines of the game. Use a patch with {Ini.IniPatcher.DirectiveAppend}, or remove " +
            "the file."));

    /// <summary>The error of a package file with the bytes of a known game file, or null.</summary>
    /// <param name="file">The path of the file in the package, for the message.</param>
    public static Diagnostic? GameFile(string file, byte[] bytes) => GameFile(file, bytes, (KnownBuilds?)null);

    /// <summary>The error of a package file with the bytes of a file of the builds, or null.</summary>
    /// <param name="builds">The builds, or null for the embedded manifests.</param>
    internal static Diagnostic? GameFile(string file, byte[] bytes, KnownBuilds? builds) =>
        Find(file, bytes, builds == null ? GameHashes.Value : Hashes(builds));

    private static Diagnostic? Find(string file, byte[] bytes, IReadOnlyDictionary<string, (string Build, string Path)> hashes)
    {
        if (bytes.Length == 0 || !hashes.TryGetValue(Convert.ToHexStringLower(SHA256.HashData(bytes)), out var known))
            return null;
        return Diagnostic.Error(file, $"{file} has the same bytes as {known.Path} of {GameDetector.BuildName(known.Build)}. " +
                                      "A mod must not contain game files. Remove the file.");
    }

    /// <summary>The hash of each file of the builds, to the build and the path. An empty file is no game content, so it
    /// is not in the list.</summary>
    private static Dictionary<string, (string Build, string Path)> Hashes(KnownBuilds builds)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var build in builds.Ids)
        {
            foreach (var row in builds.Rows(build).Values.Where(r => r.Size > 0))
                result.TryAdd(row.Sha256, (build, row.Path));
        }
        return result;
    }
}

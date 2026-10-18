using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Games;

/// <summary>
/// Writes the build manifest of a game folder (milestone M11): one line of <see cref="DataDigest.Line"/> for each file,
/// in ordinal order, with LF line ends. The lines hold paths, sizes and hashes only. The writer only reads the game
/// folder.
/// </summary>
/// <remarks>
/// The manifest leaves out the files that differ between two installs of one build. These are the uninstaller of the
/// installer, the folder <c>SAVE</c> in the root, and each <c>*.log</c> file. The uninstaller is <c>unins*</c> in the
/// root, in any letter case, for example <c>unins000.exe</c> or <c>UNINSTAL.EXE</c>.
/// </remarks>
public static class ManifestWriter
{
    /// <summary>The file that marks an OpenLancer play copy of a game.</summary>
    public const string PlayCopyRecord = ".openlancer-playcopy.json";

    /// <summary>The manifest lines of each file of the game root, in ordinal order.</summary>
    /// <exception cref="InvalidOperationException">The root is a play copy.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A file cannot be read.</exception>
    public static IReadOnlyList<string> Lines(string root)
    {
        root = Path.GetFullPath(root);
        if (File.Exists(Path.Combine(root, PlayCopyRecord)))
            throw new InvalidOperationException(
                $"{root} is an OpenLancer play copy. Write the file list of the game folder that the play copy comes from.");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file)))
            .Where(f => !LeftOut(f.Relative))
            .Select(f => DataDigest.Line(f.Relative, new FileInfo(f.File).Length, DataDigest.HashFile(f.File)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Writes the manifest of a game root to a file outside each game folder.</summary>
    /// <returns>The number of lines.</returns>
    /// <exception cref="InvalidOperationException">The root is a play copy, or the file is in the root or in another
    /// game folder.</exception>
    public static int Write(string root, string file)
    {
        if (PathRules.IsInside(file, root))
            throw new InvalidOperationException(
                $"{Path.GetFullPath(file)} is in the game folder. OpenLancer does not write into the game folder.");
        if (GameRoot.Containing(file) is { } game)
            throw new InvalidOperationException(
                $"{Path.GetFullPath(file)} is in the game folder {game}. OpenLancer does not write into a game folder.");
        var lines = Lines(root);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        AtomicFile.WriteAllBytes(file, Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))));
        return lines.Count;
    }

    /// <summary>True for a file that the manifest leaves out, by its path relative to the root.</summary>
    internal static bool LeftOut(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length == 1 && parts[0].StartsWith("unins", StringComparison.OrdinalIgnoreCase))
            return true;
        if (parts.Length > 1 && parts[0].Equals("SAVE", StringComparison.OrdinalIgnoreCase))
            return true;
        return parts[^1].EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }
}

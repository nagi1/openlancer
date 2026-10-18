using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OpenLancer.Data.IO;

/// <summary>
/// The content digest of a game data folder. It gives the same result as <c>tools/verify_base.py</c>.
/// </summary>
/// <remarks>
/// The digest is the SHA-256 of a sorted list of lines, one line for each file:
/// <c>&lt;lowercase relative path with '/'&gt;\t&lt;size&gt;\t&lt;lowercase sha256&gt;\n</c>.
/// </remarks>
public static class DataDigest
{
    /// <summary>The digest of the extracted English retail v1.0 disc.</summary>
    public const string RetailV1 = "5b4ee64cf64bb9c5525cee786f54380170dbde5617d1aaf5ef3655237c9cfaeb";

    public const int RetailV1FileCount = 8418;

    /// <param name="exclude">Relative paths of files to leave out, for example a deploy marker.</param>
    public static (int FileCount, string Digest) Compute(string folder, IReadOnlyCollection<string>? exclude = null)
    {
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var lines = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file => !excluded.Contains(Path.GetRelativePath(folder, file)))
            .Select(file => Line(Path.GetRelativePath(folder, file), new FileInfo(file).Length, HashFile(file)) + "\n")
            .Order(StringComparer.Ordinal)
            .ToList();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)));
        return (lines.Count, Convert.ToHexStringLower(digest));
    }

    /// <summary>
    /// One line of a manifest, without the line end: <c>&lt;lowercase relative path with '/'&gt;\t&lt;size&gt;\t&lt;lowercase
    /// sha256&gt;</c>. The build manifests in <c>data-manifest</c> have the same lines.
    /// </summary>
    internal static string Line(string relativePath, long size, string sha256) =>
        $"{ManifestPath(relativePath)}\t{size}\t{sha256.ToLowerInvariant()}";

    /// <summary>A relative path as a manifest names it: lowercase, with '/'.</summary>
    internal static string ManifestPath(string relativePath) => relativePath.Replace('\\', '/').ToLowerInvariant();

    /// <summary>The lowercase SHA-256 of a file. The file opens for read only, and other programs can keep it
    /// open.</summary>
    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

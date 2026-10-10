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
            .Select(file =>
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/').ToLowerInvariant();
                return $"{relative}\t{new FileInfo(file).Length}\t{HashFile(file)}\n";
            })
            .Order(StringComparer.Ordinal)
            .ToList();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)));
        return (lines.Count, Convert.ToHexStringLower(digest));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace OpenLancer.Data.Export;

/// <summary>
/// Writes a zip file whose bytes depend only on its entries (milestone M11): the entries in ordinal order, entry names
/// with <c>/</c>, the time 1980-01-01 00:00 for each entry, no extra fields, and Deflate. So two exports of the same mod
/// give the same bytes on the same OpenLancer build. Another .NET runtime can give other Deflate bytes.
/// </summary>
internal static class DeterministicZip
{
    /// <summary>The time of each entry: the first time that a zip file can hold.</summary>
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="entries">The name of each entry, with <c>/</c> or <c>\</c>, and its bytes.</param>
    /// <exception cref="ArgumentException">Two entries have the same name.</exception>
    public static byte[] Write(IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        var list = entries.Select(e => (Name: e.Name.Replace('\\', '/'), e.Bytes))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var duplicate = list.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"The zip has two entries with the name {duplicate.Key}.", nameof(entries));

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in list)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = EntryTime;
                using var output = entry.Open();
                output.Write(bytes);
            }
        }
        return stream.ToArray();
    }
}

using System;
using System.IO;

namespace OpenLancer.Data.IO;

/// <summary>
/// Writes a file through a temporary file next to it, then moves the temporary file into place. A reader sees the
/// old file or the new file, never a part of the new file. A write never goes through a hard link.
/// </summary>
/// <remarks>
/// <para>The temporary file is <c>&lt;file&gt;.oltmp</c>. A process that stops during a write can leave it. The next
/// write to the same file deletes it.</para>
/// <para><see cref="WriteAllBytes"/> flushes the temporary file to the disk before the move. So after a power loss,
/// the file is also the old file or the complete new file. A <see cref="Replace"/> write gets this only when it
/// flushes its file.</para>
/// </remarks>
internal static class AtomicFile
{
    public const string TempSuffix = ".oltmp";

    /// <param name="write">Writes the new content to the temporary file at the path that it gets.</param>
    public static void Replace(string destination, Action<string> write)
    {
        var temp = destination + TempSuffix;
        if (File.Exists(temp))
            File.Delete(temp);
        write(temp);
        File.Move(temp, destination, overwrite: true);
    }

    public static void WriteAllBytes(string destination, byte[] bytes) =>
        Replace(destination, temp => WriteDurable(temp, bytes));

    /// <summary>Writes a file and flushes it to the disk, so that it is complete before the next step uses it.</summary>
    public static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

using System.IO;
using System.Text;

namespace OpenLancer.Data.IO;

/// <summary>
/// Reads a file of a mod while the editor can save the mod (decision D51). Each read lets other processes read the
/// file, and delete or replace it. So a save of the editor can put a new version in place (see
/// <see cref="AtomicFile.MoveOver"/>) while the game or oltool reads the old version.
/// </summary>
/// <remarks>
/// A read does not let another process write the file in place, and it fails while a process writes the file. So a
/// parser never gets a part of a file that a text editor or a sync tool writes at that moment. The editor does not
/// write a mod file in place: it writes a new file and moves it over the old one.
/// </remarks>
internal static class SharedFile
{
    private const FileShare Share = FileShare.Read | FileShare.Delete;

    public static Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, Share);

    public static byte[] ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Reads the text as <see cref="File.ReadAllText(string)"/> does: UTF-8, or the encoding of a byte
    /// order mark.</summary>
    public static string ReadAllText(string path)
    {
        using var reader = new StreamReader(OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

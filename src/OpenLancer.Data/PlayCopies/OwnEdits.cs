using System;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.PlayCopies;

/// <summary>
/// The edits that a play copy makes in its own copies of game files (decision D314). The edit changes one line with the
/// text splice of the export (<see cref="IniTextEdit"/>), so the comments and the other lines of the file stay.
/// </summary>
internal static class OwnEdits
{
    private const string SaveInDirectoryKey = "save_in_directory";

    /// <summary>The line of the edit of <c>flplusplus.ini</c>.</summary>
    public const string SaveInDirectoryLine = SaveInDirectoryKey + " = true";

    /// <summary>
    /// The bytes of a text <c>flplusplus.ini</c> with <c>save_in_directory = true</c>. With that value, flplusplus puts
    /// the saves into the folder <c>SAVE</c> next to <c>EXE</c>. The same bytes come back when the value is true already.
    /// </summary>
    /// <returns>Null when the file has no such key, or when it is not text.</returns>
    public static byte[]? SaveInDirectory(string path, byte[] bytes)
    {
        IniDocument document;
        try
        {
            document = IniDocument.Parse(path, bytes);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        if (document.IsBinary)
            return null;
        var merged = document.Clone();
        var entry = merged.Sections.SelectMany(s => s)
            .FirstOrDefault(e => e.Name.Equals(SaveInDirectoryKey, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return null;
        if (entry.Count == 1 && entry[0].ToString()!.Equals("true", StringComparison.OrdinalIgnoreCase))
            return bytes;
        entry.Clear();
        entry.Add(new BooleanValue(true) { Entry = entry });
        var diff = IniSectionDiff.Compare(document.Sections, merged.Sections);
        return IniTextEdit.Write(bytes, document, merged, diff).Bytes;
    }
}

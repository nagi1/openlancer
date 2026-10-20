using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing.Fields;

/// <summary>
/// The text of the raw INI editor and the line of each section and entry (section 11.5 of the M6a design, decision
/// D529).
/// </summary>
/// <remarks>
/// <para>File text: when the file is a text file that no mod changes, the text is the file text with its comments. The
/// scan of the raw lines (<see cref="RawIni"/>) gives the map. When the scan fails, or its section and entry counts
/// differ from the parsed file, the canonical text replaces it.</para>
/// <para>Canonical text: each section starts with <c>[Type]</c>, each entry gives one line of <see cref="IniLines"/>,
/// and an empty line separates the sections.</para>
/// <para>The round trip check: the engine text parser reads the canonical text of each section again. When the entries
/// are not engine-equal to the section, the section is read-only in the raw editor, with a reason. Only a binary file of
/// a mod can have such a value: a string with a comma, a semicolon or a blank at its start or end.</para>
/// </remarks>
public sealed class RawText
{
    /// <summary>The reason of a section that the canonical text cannot hold (section 13.1 of the M6a design).</summary>
    public const string ReadOnlyReason =
        "The editor cannot write this section as text without a change. A value holds a comma, a semicolon or a blank at " +
        "its start or end.";

    private readonly int[] headers;
    private readonly int[][] entries;
    private readonly int[] sectionOfLine;
    private readonly int[] entryOfLine;
    private readonly Dictionary<SectionIdentity, int> sectionIndex;
    private string? text;

    private RawText(IReadOnlyList<string> lines, bool isFileText, string? reason, IReadOnlyList<SectionIdentity> ids,
        int[] headers, int[][] entries, IReadOnlyDictionary<int, string> readOnly)
    {
        Lines = lines;
        IsFileText = isFileText;
        CanonicalReason = reason;
        Ids = ids;
        this.headers = headers;
        this.entries = entries;
        ReadOnlySections = readOnly;
        sectionIndex = new Dictionary<SectionIdentity, int>();
        for (var i = 0; i < ids.Count; i++)
            sectionIndex.TryAdd(ids[i], i);
        sectionOfLine = Enumerable.Repeat(-1, lines.Count).ToArray();
        entryOfLine = Enumerable.Repeat(-1, lines.Count).ToArray();
        for (var k = 0; k < headers.Length; k++)
        {
            var end = k + 1 < headers.Length ? headers[k + 1] : lines.Count;
            for (var line = headers[k]; line < end; line++)
                sectionOfLine[line] = k;
            for (var j = 0; j < entries[k].Length; j++)
                entryOfLine[entries[k][j]] = j;
        }
    }

    /// <summary>The lines of the text, without line ends.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The text with LF line ends, for the editor. The text is made once, because the editor reads it in each
    /// frame.</summary>
    public string Text => text ??= string.Join('\n', Lines);

    /// <summary>True for the file text with its comments, false for the canonical text.</summary>
    public bool IsFileText { get; }

    /// <summary>Why the text is canonical text although the file text was asked for, or null.</summary>
    public string? CanonicalReason { get; }

    /// <summary>The identity of each section, in file order.</summary>
    public IReadOnlyList<SectionIdentity> Ids { get; }

    /// <summary>The sections that the canonical text cannot hold, by section index, with the reason.</summary>
    public IReadOnlyDictionary<int, string> ReadOnlySections { get; }

    /// <summary>The line of the header of a section.</summary>
    public int HeaderLine(int section) => headers[section];

    /// <summary>The line of an entry of a section.</summary>
    public int EntryLine(int section, int entry) => entries[section][entry];

    /// <summary>The number of entries of a section.</summary>
    public int EntryCount(int section) => entries[section].Length;

    /// <summary>The index of a section of the text, or -1.</summary>
    public int SectionOf(SectionIdentity id) => sectionIndex.GetValueOrDefault(id, -1);

    /// <summary>The line of an entry of a section, or of its header for an entry of -1. Null for a section or an entry
    /// that the text does not have.</summary>
    public int? LineOf(SectionIdentity id, int entry)
    {
        if (SectionOf(id) is var k and >= 0)
            return entry < 0 ? headers[k] : entry < entries[k].Length ? entries[k][entry] : headers[k];
        return null;
    }

    /// <summary>The section and the entry of a line: the section that holds the line, and the entry of the line, or -1
    /// for a header, a comment or an empty line. The section is -1 for a line before the first section.</summary>
    public (int Section, int Entry) At(int line) =>
        line >= 0 && line < Lines.Count ? (sectionOfLine[line], entryOfLine[line]) : (-1, -1);

    /// <summary>The raw text of a file: the file text of a text file when <paramref name="fileText"/> is true and the
    /// scan maps each entry, else the canonical text.</summary>
    /// <param name="bytes">The bytes of the file, or null.</param>
    public static RawText Of(string path, IReadOnlyList<Section> sections, bool isBinary, byte[]? bytes, bool fileText)
    {
        if (!fileText || isBinary || bytes == null)
            return Canonical(path, sections, null);
        var raw = RawIni.Scan(bytes, out var reason);
        if (raw == null)
            return Canonical(path, sections, reason);
        if (raw.Sections.Count != sections.Count || raw.Sections.Where((s, k) => s.Entries.Count != sections[k].Count).Any())
            return Canonical(path, sections, "OpenLancer cannot match the lines of the file with its sections");
        return new RawText(raw.Lines, true, null, SectionIdentity.Identify(sections),
            raw.Sections.Select(s => s.Header).ToArray(), raw.Sections.Select(s => s.Entries.ToArray()).ToArray(),
            ReadOnly(path, sections));
    }

    /// <summary>A text as it is, without a map of its lines to sections, for example an editor file that the game does not
    /// read. A view shows it read-only.</summary>
    public static RawText Plain(string text) =>
        new(text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'), true, null, [], [], [], new Dictionary<int, string>());

    /// <summary>The canonical text of sections.</summary>
    /// <param name="reason">Why the file text is not used, or null.</param>
    /// <param name="ids">The identities of the sections in their file, or null to identify them in the list. A list
    /// with one section of a file needs the identity of the section in the file. Then the marks of its problems find
    /// their lines, also for the second of two sections with one nickname.</param>
    public static RawText Canonical(string path, IReadOnlyList<Section> sections, string? reason = null,
        IReadOnlyList<SectionIdentity>? ids = null)
    {
        var lines = new List<string>();
        var headers = new int[sections.Count];
        var entries = new int[sections.Count][];
        for (var k = 0; k < sections.Count; k++)
        {
            if (k > 0)
                lines.Add("");
            headers[k] = lines.Count;
            lines.Add($"[{sections[k].Name}]");
            entries[k] = new int[sections[k].Count];
            for (var j = 0; j < sections[k].Count; j++)
            {
                entries[k][j] = lines.Count;
                lines.Add(IniLines.Line(sections[k][j]));
            }
        }
        if (ids != null && ids.Count != sections.Count)
            throw new ArgumentException("The list of identities must have one identity for each section.", nameof(ids));
        return new RawText(lines, false, reason, ids ?? SectionIdentity.Identify(sections), headers, entries,
            ReadOnly(path, sections));
    }

    /// <summary>The sections whose canonical text does not give the same entries.</summary>
    private static Dictionary<int, string> ReadOnly(string path, IReadOnlyList<Section> sections)
    {
        var result = new Dictionary<int, string>();
        for (var k = 0; k < sections.Count; k++)
        {
            if (!RoundTrips(path, sections[k]))
                result[k] = ReadOnlyReason;
        }
        return result;
    }

    /// <summary>True when the engine text parser reads the canonical text of a section as the same entries, engine-equal
    /// in text form (section 14.7 item 3 of the M6a design).</summary>
    public static bool RoundTrips(string path, Section section)
    {
        // Each value must give one value again: no comma, no semicolon, and no blank at its start or end.
        foreach (var entry in section)
        {
            foreach (var value in entry)
            {
                var text = RetailText.Of(value);
                if (text.Contains(',') || text.Contains(';') || text != text.Trim(' ', '\t'))
                    return false;
            }
        }
        IReadOnlyList<Section> again;
        try
        {
            again = IniDocument.Parse(path, IniDocument.TextEncoding.GetBytes(string.Join(IniLines.LineEnd,
                IniLines.SectionLines(section)) + IniLines.LineEnd)).Sections;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
        if (again.Count != 1 || again[0].Count != section.Count)
            return false;
        for (var j = 0; j < section.Count; j++)
        {
            var a = section[j];
            var b = again[0][j];
            if (!a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) || a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
            {
                if (!IniValueComparer.EngineEqual(a[i], b[i], binary: false))
                    return false;
            }
        }
        return true;
    }
}

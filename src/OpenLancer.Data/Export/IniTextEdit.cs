using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Export;

/// <summary>
/// The raw lines of a text INI file, with the lines that the engine parser reads as sections and entries (milestone
/// M11). The index follows the skip rules of the parser. So section k and entry j of the index are section k and entry
/// j of the parsed file.
/// </summary>
internal sealed class RawIni
{
    private static readonly char[] Blanks = [' ', '\t'];

    private RawIni(List<string> lines, List<string> ends, string lineEnd, List<RawSection> sections)
    {
        Lines = lines;
        Ends = ends;
        LineEnd = lineEnd;
        Sections = sections;
    }

    /// <summary>The text of each line, without its line end.</summary>
    public List<string> Lines { get; }

    /// <summary>The line end of each line: CR LF, LF, CR, or empty for a last line without a line end.</summary>
    public List<string> Ends { get; }

    /// <summary>The line end of the file. CR LF when the file has no line end.</summary>
    public string LineEnd { get; }

    public List<RawSection> Sections { get; }

    /// <summary>
    /// Indexes the raw lines of a file, or gives the reason why it cannot: the file has a byte order mark, it mixes
    /// line ends, or it has an LF CR pair.
    /// </summary>
    public static RawIni? Scan(byte[] bytes, out string? reason)
    {
        reason = null;
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ||
            bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ||
            bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            reason = "the file starts with a byte order mark";
            return null;
        }
        var text = IniDocument.TextEncoding.GetString(bytes);
        var lines = new List<string>();
        var ends = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\r' or '\n'))
                continue;
            if (c == '\n' && i + 1 < text.Length && text[i + 1] == '\r')
            {
                reason = "the file has an LF CR pair";
                return null;
            }
            var end = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : c.ToString();
            lines.Add(text[start..i]);
            ends.Add(end);
            i += end.Length - 1;
            start = i + 1;
        }
        if (start < text.Length)
        {
            lines.Add(text[start..]);
            ends.Add("");
        }
        var forms = ends.Where(e => e.Length > 0).Distinct().ToList();
        if (forms.Count > 1)
        {
            reason = "the file mixes line ends";
            return null;
        }

        // The skip rules of the engine parser (LancerTextIniParser).
        var sections = new List<RawSection>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].AsSpan().Trim(Blanks);
            if (line.IsWhiteSpace() || line[0] is ';' or '@')
                continue;
            if (line[0] == '[')
            {
                sections.Add(new RawSection(i, []));
                continue;
            }
            if (sections.Count == 0)
                continue;
            var comment = line.IndexOf(';');
            if (comment >= 0)
                line = line[..comment];
            if (line.IsWhiteSpace())
                continue;
            sections[^1].Entries.Add(i);
        }
        return new RawIni(lines, ends, forms.Count == 1 ? forms[0] : "\r\n", sections);
    }

    /// <summary>The blanks at the start of a line.</summary>
    public string Indent(int line) => Lines[line][..(Lines[line].Length - Lines[line].TrimStart(Blanks).Length)];

    /// <summary>The comment of a line: the text from its first <c>;</c>, with the blanks before it. Empty when the line
    /// has no comment.</summary>
    public string Comment(int line)
    {
        var text = Lines[line];
        var index = text.IndexOf(';');
        if (index < 0)
            return "";
        while (index > 0 && text[index - 1] is ' ' or '\t')
            index--;
        return text[index..];
    }

    /// <summary>The key of an entry line as the file writes it: the text before <c>=</c>, or the whole line without its
    /// comment.</summary>
    public string RawKey(int line)
    {
        var text = Lines[line].Trim(Blanks);
        var comment = text.IndexOf(';');
        if (comment >= 0)
            text = text[..comment];
        var equals = text.IndexOf('=');
        return (equals >= 0 ? text[..equals] : text).Trim(Blanks);
    }

    /// <summary>True when a line is a comment line: its first character after blanks is <c>;</c>.</summary>
    public bool IsComment(int line) => Lines[line].TrimStart(Blanks).StartsWith(';');

    /// <summary>The last line of the section at <paramref name="index"/>: the line before the next section header, or
    /// the last line of the file.</summary>
    public int SectionEnd(int index) =>
        index + 1 < Sections.Count ? Sections[index + 1].Header - 1 : Lines.Count - 1;

    /// <summary>The raw text of the lines from <paramref name="from"/> to <paramref name="to"/>. Each line except the
    /// last keeps its own line end.</summary>
    public string Join(int from, int to) =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => Lines[i] + (i < to ? Ends[i] : "")));
}

/// <param name="Header">The line of the section header.</param>
/// <param name="Entries">The line of each entry, in order.</param>
internal sealed record RawSection(int Header, List<int> Entries);

/// <summary>
/// Writes a merged text INI file from the raw text of its base file (milestone M11). Only the lines that the merge
/// changes change. The other lines keep their text, their comments and their line ends.
/// </summary>
/// <remarks>
/// The raw lines can fail to match the parsed base file, or the edited text can fail to give the merged sections. Then
/// the writer writes the canonical text of the merged file (<see cref="IniDocument.ToText"/>) and gives the reason.
/// </remarks>
internal static class IniTextEdit
{
    /// <param name="Bytes">The bytes of the merged file.</param>
    /// <param name="Fallback">Null when the writer kept the raw text, else the reason why it wrote the canonical
    /// text.</param>
    internal sealed record Result(byte[] Bytes, string? Fallback);

    public static Result Write(byte[] baseBytes, IniDocument baseDocument, IniDocument merged, FileDiff diff)
    {
        var raw = RawIni.Scan(baseBytes, out var reason);
        if (raw == null)
            return Canonical(merged, reason!);
        if (raw.Sections.Count != baseDocument.Sections.Count ||
            raw.Sections.Where((s, k) => s.Entries.Count != baseDocument.Sections[k].Count).Any())
            return Canonical(merged, "OpenLancer cannot match the lines of the file with its sections");

        var removed = new bool[raw.Lines.Count];
        var replaced = new string?[raw.Lines.Count];
        var after = new List<string>[raw.Lines.Count];
        var beforeFirst = new List<string>();
        List<string> After(int line) => after[line] ??= [];

        // The last raw line of each matched base section that the result keeps, for the sections that follow it.
        var lastLine = new Dictionary<int, int>();
        foreach (var pair in diff.Matched)
        {
            var section = raw.Sections[pair.Base];
            var mergedSection = merged.Sections[pair.Merged];
            foreach (var i in pair.Removed)
                removed[section.Entries[i]] = true;
            foreach (var i in pair.Changed)
            {
                var line = section.Entries[i];
                replaced[line] = raw.Indent(line) + IniLines.Line(raw.RawKey(line), mergedSection[pair.MergedOf[i]]) +
                                 raw.Comment(line);
            }
            // An inserted line goes after the line of the merged entry before it, or after the header.
            var anchor = section.Header;
            for (var j = 0; j < pair.BaseOf.Length; j++)
            {
                if (pair.BaseOf[j] >= 0)
                {
                    anchor = section.Entries[pair.BaseOf[j]];
                    continue;
                }
                After(anchor).Add(raw.Indent(anchor) + IniLines.Line(mergedSection[j]));
            }
            lastLine[pair.Merged] = section.Entries.Where(e => !removed[e]).DefaultIfEmpty(section.Header).Max();
        }

        foreach (var k in diff.Removed)
        {
            var section = raw.Sections[k];
            var end = raw.SectionEnd(k);
            // The comment lines directly before the next header belong to the next section.
            if (k + 1 < raw.Sections.Count)
            {
                while (end > section.Header && raw.IsComment(end))
                    end--;
            }
            for (var line = section.Header; line <= end; line++)
                removed[line] = true;
        }

        // A new section goes after the section before it in the merged file, with an empty line before it.
        var anchorOf = new Dictionary<int, int>();
        foreach (var m in diff.Added)
        {
            var lines = IniLines.SectionLines(merged.Sections[m]);
            var anchor = m == 0 ? -1 : lastLine.TryGetValue(m - 1, out var line) ? line : anchorOf.GetValueOrDefault(m - 1, -1);
            anchorOf[m] = anchor;
            if (anchor < 0)
                beforeFirst.AddRange(lines.Append(""));
            else
                After(anchor).AddRange(lines.Prepend(""));
        }

        var output = new List<(string Text, string End)>();
        var firstHeader = raw.Sections.Count > 0 ? raw.Sections[0].Header : raw.Lines.Count;
        for (var line = 0; line <= raw.Lines.Count; line++)
        {
            if (line == firstHeader)
                output.AddRange(beforeFirst.Select(t => (t, raw.LineEnd)));
            if (line == raw.Lines.Count)
                break;
            if (!removed[line])
                output.Add((replaced[line] ?? raw.Lines[line], raw.Ends[line]));
            if (after[line] != null)
                output.AddRange(after[line].Select(t => (t, raw.LineEnd)));
        }
        // A line that gets a line after it gets the line end of the file.
        var text = new StringBuilder();
        for (var i = 0; i < output.Count; i++)
            text.Append(output[i].Text).Append(output[i].End.Length == 0 && i + 1 < output.Count ? raw.LineEnd : output[i].End);
        var bytes = IniDocument.TextEncoding.GetBytes(text.ToString());

        IniDocument again;
        try
        {
            again = IniDocument.Parse(merged.Path, bytes);
        }
        catch (Exception)
        {
            return Canonical(merged, "the edited text does not parse");
        }
        return SameSections(again.Sections, merged.Sections)
            ? new Result(bytes, null)
            : Canonical(merged, "the edited text does not give the merged sections");
    }

    /// <summary>True when two lists of sections have the same names, the same entry names and the same value texts.
    /// Names compare without letter case.</summary>
    internal static bool SameSections(IReadOnlyList<Section> a, IReadOnlyList<Section> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Name.Equals(p.Second.Name, StringComparison.OrdinalIgnoreCase) && p.First.Count == p.Second.Count &&
            p.First.Zip(p.Second).All(e => IniSectionDiff.LineKey(e.First) == IniSectionDiff.LineKey(e.Second)));

    private static Result Canonical(IniDocument merged, string reason) => new(merged.ToText(), reason);
}

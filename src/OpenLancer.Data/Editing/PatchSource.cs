using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The line of one entry of a loaded patch section, with the lines above it that the engine's text parser skips.
/// </summary>
/// <param name="Parsed">The entry, as the engine's text parser reads it.</param>
/// <param name="Above">The comment, blank and <c>@</c> lines between the line before and this line.</param>
/// <param name="Text">The text of the line.</param>
/// <param name="Part">The loaded section that has the line: 0 for the first loaded section of the target, 1 for the
/// second, and so on (see <see cref="SourceSection"/>).</param>
internal sealed record SourceLine(Entry Parsed, IReadOnlyList<string> Above, string Text, int Part);

/// <summary>
/// The text of the loaded patch sections that change, delete or add one section.
/// </summary>
/// <remarks>
/// Usually one loaded section does this. When more than one loaded section changes the same section, this joins them
/// in file order. It keeps the header line of the first, and drops the header line and the key line of each later
/// one, because the writer writes one section. The lines that the parser skips near a dropped line go to the next
/// entry line, or to the tail.
/// </remarks>
/// <param name="Name">The section name of the first loaded section, as the parser reads it.</param>
/// <param name="Leading">The comment and <c>@</c> lines directly above the header line, with no blank line between.</param>
/// <param name="Header">The header line.</param>
/// <param name="Lines">The entry lines, the directives included.</param>
/// <param name="Tail">The lines after the last entry line, up to the leading lines of the next section.</param>
internal sealed record SourceSection(
    string Name,
    IReadOnlyList<string> Leading,
    string Header,
    IReadOnlyList<SourceLine> Lines,
    IReadOnlyList<string> Tail)
{
    /// <summary>All lines, in their loaded order.</summary>
    public IEnumerable<string> AllLines =>
        Leading.Append(Header).Concat(Lines.SelectMany(l => l.Above.Append(l.Text))).Concat(Tail);
}

/// <summary>
/// The patch file of the active mod, as the editor loads it: its sections, the text of all its lines, and its
/// encoding.
/// </summary>
/// <remarks>
/// <para>The writer keeps each line of the loaded patch that no edit touched (decisions D4 and D25). For this, the
/// source splits the file into these parts:</para>
/// <list type="bullet">
/// <item>The header: the lines before the leading lines of the first section.</item>
/// <item>For each section: the leading lines (the comment and <c>@</c> lines directly above the header line, with
/// no blank line between), the header line, each entry line with the lines above it, and the tail. The tail is the
/// lines after the last entry line, up to the leading lines of the next section.</item>
/// <item>The footer: the lines after the last entry line of the last section.</item>
/// </list>
/// <para>The lines split and decode as the engine's text parser reads them: in the encoding of the byte order mark,
/// or in code page 1252 when the file has no byte order mark. A binary file has no line text.</para>
/// </remarks>
internal sealed class PatchSource
{
    private static readonly char[] SpacesAndTabs = [' ', '\t'];

    /// <summary>The loaded sections that change or delete a section of B, by type and key.</summary>
    private readonly Dictionary<SectionIdentity, SourceSection> existing = [];

    /// <summary>The loaded sections that add a section, by type and key, in file order.</summary>
    private readonly Dictionary<SectionIdentity, List<SourceSection>> added = [];

    private PatchSource(IReadOnlyList<Section> sections, Encoding encoding, IReadOnlyList<string> header,
        IReadOnlyList<string> footer, IReadOnlyList<SourceSection>? texts)
    {
        Sections = sections;
        Encoding = encoding;
        Header = header;
        Footer = footer;
        if (texts != null)
            Group(texts);
    }

    /// <summary>The sections of the file, as the engine's text parser reads them.</summary>
    public IReadOnlyList<Section> Sections { get; }

    /// <summary>The encoding that the engine's text parser reads the file with. Its preamble is the byte order mark.</summary>
    public Encoding Encoding { get; }

    /// <summary>The lines before the leading lines of the first section.</summary>
    public IReadOnlyList<string> Header { get; }

    /// <summary>The lines after the last entry line of the last section.</summary>
    public IReadOnlyList<string> Footer { get; }

    /// <exception cref="Exception">The engine's parser cannot read the file.</exception>
    /// <exception cref="InvalidDataException">The lines do not give the sections that the engine's parser reads.</exception>
    public static PatchSource Parse(string path, byte[] data)
    {
        var document = IniDocument.Parse(path, data);
        if (document.IsBinary)
            return new PatchSource(document.Sections, IniDocument.TextEncoding, [], [], null);
        // The same reader as the engine's text parser, so the lines and the encoding are the same.
        using var reader = new StreamReader(new MemoryStream(data, false), IniDocument.TextEncoding,
            detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return Split(path, document.Sections, reader.CurrentEncoding, lines);
    }

    /// <summary>
    /// The loaded sections that change or delete the section of B with this type and key, as one section. Null when
    /// the file has none.
    /// </summary>
    /// <param name="type">The section type.</param>
    /// <param name="key">The key of the section (see <see cref="SectionIdentity.KeyOf"/>), or null.</param>
    public SourceSection? Existing(string type, string? key) => existing.GetValueOrDefault(new SectionIdentity(type, key));

    /// <summary>
    /// The loaded sections that add a section with this type and key, as one section. The <paramref name="ordinal"/>-th
    /// new section of W with this type and key gets the <paramref name="ordinal"/>-th such section of the file that the
    /// file does not delete again. Null when there is no such section.
    /// </summary>
    /// <param name="type">The section type.</param>
    /// <param name="key">The key of the section (see <see cref="SectionIdentity.KeyOf"/>), or null.</param>
    /// <param name="ordinal">The number of new sections of W with this type and key before it.</param>
    public SourceSection? Added(string type, string? key, int ordinal) =>
        added.TryGetValue(new SectionIdentity(type, key), out var list) && ordinal < list.Count ? list[ordinal] : null;

    /// <summary>True for a line that the engine's text parser skips as blank.</summary>
    public static bool IsBlank(string line) => line.AsSpan().Trim(SpacesAndTabs).IsWhiteSpace();

    /// <summary>Splits the lines into the header, the text of each section and the footer.</summary>
    private static PatchSource Split(string path, IReadOnlyList<Section> sections, Encoding encoding, List<string> lines)
    {
        var kinds = new LineKind[lines.Count];
        var headers = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            kinds[i] = Kind(lines[i], inSection: headers.Count > 0);
            if (kinds[i] == LineKind.Header)
                headers.Add(i);
        }
        if (headers.Count != sections.Count)
            throw Mismatch(path, $"{headers.Count} header lines give {sections.Count} sections");
        if (sections.Count == 0)
            return new PatchSource(sections, encoding, lines, [], []);

        var texts = new List<SourceSection>();
        var leading = LeadingStart(kinds, headers[0], 0);
        var header = lines[..leading];
        IReadOnlyList<string> footer = [];
        for (var s = 0; s < sections.Count; s++)
        {
            var start = headers[s];
            var end = s + 1 < headers.Count ? headers[s + 1] : lines.Count;
            var section = sections[s];
            if (section.Line != start + 1)
                throw Mismatch(path, $"the parser gives line {section.Line} to the header on line {start + 1}");
            var entries = new List<SourceLine>();
            var last = start;
            for (var i = start + 1; i < end; i++)
            {
                if (kinds[i] != LineKind.Entry)
                    continue;
                // The parser gives no line number to an entry without '='.
                var k = entries.Count;
                if (k == section.Count || section[k].Line >= 1 && section[k].Line != i + 1)
                    throw Mismatch(path, $"line {i + 1} gives no entry of the section on line {start + 1}");
                entries.Add(new SourceLine(section[k], lines[(last + 1)..i], lines[i], 0));
                last = i;
            }
            if (entries.Count != section.Count)
                throw Mismatch(path, $"the section on line {start + 1} has {section.Count} entries, not {entries.Count}");

            var next = end == lines.Count ? last + 1 : LeadingStart(kinds, end, last + 1);
            texts.Add(new SourceSection(section.Name, lines[leading..start], lines[start], entries, lines[(last + 1)..next]));
            if (end == lines.Count)
                footer = lines[(last + 1)..];
            leading = next;
        }
        return new PatchSource(sections, encoding, header, footer, texts);
    }

    /// <summary>The first leading line of the header line at <paramref name="header"/>: the first line of the comment
    /// lines directly above it, but not before <paramref name="first"/>.</summary>
    private static int LeadingStart(LineKind[] kinds, int header, int first)
    {
        var start = header;
        while (start > first && kinds[start - 1] == LineKind.Comment)
            start--;
        return start;
    }

    /// <summary>What the engine's text parser does with a line (LancerTextIniParser.ParseIniFile).</summary>
    /// <param name="inSection">True after the first header line.</param>
    private static LineKind Kind(string line, bool inSection)
    {
        var text = line.AsSpan().Trim(SpacesAndTabs);
        if (text.IsWhiteSpace())
            return LineKind.Blank;
        if (text[0] is ';' or '@')
            return LineKind.Comment;
        if (text[0] == '[')
            return LineKind.Header;
        // The parser removes an inline comment. A line that is then blank gives no entry, in a section or before it.
        var comment = text.IndexOf(';');
        if (comment >= 0 && text[..comment].IsWhiteSpace())
            return LineKind.Comment;
        return inSection ? LineKind.Entry : LineKind.Outside;
    }

    private static InvalidDataException Mismatch(string path, string detail) =>
        new($"{path}: the editor cannot split the lines of this patch as the engine reads them ({detail}).");

    /// <summary>
    /// Groups the loaded sections by the section that they change. A section that adds a section with a key, a
    /// singleton section, or a section without a line of an optional key, starts a group. The later sections with the
    /// same type and key join the group, up to a section that deletes it. After a section with <c>ol.rename</c>, the
    /// later sections with the new key join the group. Another section without a key only adds a section.
    /// </summary>
    private void Group(IReadOnlyList<SourceSection> texts)
    {
        var actions = IniPatcher.Actions(Sections);
        var groups = new List<(SectionIdentity Id, LoadedGroup Group)>();
        var open = new Dictionary<SectionIdentity, LoadedGroup>();
        for (var i = 0; i < Sections.Count; i++)
        {
            var key = SectionIdentity.KeyOf(Sections[i]);
            var id = new SectionIdentity(Sections[i].Name, key);
            if (key == null && !SectionKeys.FindsWithoutKey(Sections[i]))
            {
                if (actions[i] == PatchAction.Add)
                    AddTo(id, texts[i]);
                continue;
            }
            if (actions[i] == PatchAction.Add || !open.TryGetValue(id, out var group))
            {
                group = new LoadedGroup(actions[i] == PatchAction.Add);
                groups.Add((id, group));
                open[id] = group;
            }
            group.Parts.Add(texts[i]);
            if (actions[i] == PatchAction.Delete)
            {
                group.Deleted = true;
                open.Remove(id);
            }
            else if (IniPatcher.RenameOf(Sections[i]) is { } newKey)
            {
                // After ol.rename, a later section finds the section by its new key. The group keeps the old key,
                // because the writer finds the section of B by it.
                open.Remove(id);
                open[new SectionIdentity(Sections[i].Name, newKey)] = group;
            }
        }
        foreach (var (id, group) in groups)
        {
            // A valid patch changes a section of B in one group at most: after a delete, only an add can follow.
            if (!group.Adds)
                existing.TryAdd(id, Join(group.Parts));
            else if (!group.Deleted)
                AddTo(id, Join(group.Parts));
        }
    }

    private void AddTo(SectionIdentity id, SourceSection section)
    {
        if (!added.TryGetValue(id, out var list))
            added[id] = list = [];
        list.Add(section);
    }

    /// <summary>Joins the loaded sections of one group into one section (see <see cref="SourceSection"/>).</summary>
    private static SourceSection Join(List<SourceSection> parts)
    {
        if (parts.Count == 1)
            return parts[0];
        var keyEntry = SectionKeys.For(parts[0].Name).KeyEntry;
        var lines = parts[0].Lines.ToList();
        var carried = new List<string>();
        var tail = parts[0].Tail;
        for (var p = 1; p < parts.Count; p++)
        {
            carried.AddRange(tail);
            carried.AddRange(parts[p].Leading);
            var keyDropped = false;
            foreach (var line in parts[p].Lines)
            {
                if (!keyDropped && keyEntry != null && line.Parsed.Name.Equals(keyEntry, StringComparison.OrdinalIgnoreCase))
                {
                    keyDropped = true;
                    carried.AddRange(line.Above);
                    continue;
                }
                lines.Add(line with { Above = [.. carried, .. line.Above], Part = p });
                carried.Clear();
            }
            tail = parts[p].Tail;
        }
        return parts[0] with { Lines = lines, Tail = [.. carried, .. tail] };
    }

    private enum LineKind
    {
        /// <summary>A blank line. The parser skips it.</summary>
        Blank,

        /// <summary>A comment or <c>@</c> line. The parser skips it.</summary>
        Comment,

        Header,

        /// <summary>A line in a section that gives an entry.</summary>
        Entry,

        /// <summary>A line before the first header line that is not blank and not a comment. The parser skips it.</summary>
        Outside,
    }

    /// <summary>The loaded sections that change, delete or add one section.</summary>
    /// <param name="adds">True when the first section adds the section.</param>
    private sealed class LoadedGroup(bool adds)
    {
        public bool Adds { get; } = adds;

        public List<SourceSection> Parts { get; } = [];

        /// <summary>True when the last section deletes the section.</summary>
        public bool Deleted { get; set; }
    }
}

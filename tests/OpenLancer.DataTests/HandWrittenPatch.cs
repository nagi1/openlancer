using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenLancer.Data.Ini;

namespace OpenLancer.DataTests;

/// <summary>A patch file as a modder can write it by hand.</summary>
/// <param name="File">The bytes of the file.</param>
/// <param name="Expected">The bytes that the writer must give for the file when no edit touches it: the same lines
/// with CRLF line ends, in the same encoding.</param>
/// <param name="Utf8">True when the file is UTF-8 with a byte order mark, false for code page 1252.</param>
internal sealed record HandWrittenPatch(byte[] File, byte[] Expected, bool Utf8)
{
    private static readonly string[] Comments = ["café", "über", "€ 5", "TODO", "kept by hand"];
    private static readonly string[] Spaces = ["", " ", "  ", "\t"];
    private static readonly Regex Decimal = new(@"^-?\d+\.\d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Writes a patch of the writer again as a modder can write it by hand. The merge reads the result as the same
    /// patch.
    /// </summary>
    /// <remarks>
    /// The changes: a file header, comment lines above sections and entries, comment lines after sections, a block at
    /// the end of the file, blank, <c>@</c> and white-space lines, inline comments, other spaces around <c>=</c> and
    /// <c>,</c>, key, directive and header lines in other letter case, <c>1.50</c> for <c>1.5</c>, CR, LF and CRLF
    /// line ends, and code page 1252 or UTF-8 with a byte order mark. Comments hold characters outside ASCII. A blank
    /// line stays between two sections, because the writer adds one when a section does not end with a blank line.
    /// </remarks>
    /// <param name="text">The text of a patch that the writer wrote: sections with one blank line between them.</param>
    public static HandWrittenPatch From(string text, Random random)
    {
        var lines = new List<string>();
        if (random.Next(3) == 0)
            lines.AddRange(["; " + Comment(random), ""]);
        var sections = SplitSections(text);
        for (var s = 0; s < sections.Count; s++)
        {
            if (s > 0)
            {
                if (random.Next(3) == 0)
                    lines.Add("; end " + Comment(random));
                lines.Add("");
                if (random.Next(4) == 0)
                    lines.Add("");
            }
            if (random.Next(3) == 0)
                lines.Add("; about " + Comment(random));
            var name = sections[s][0][1..^1];
            lines.Add(Header(name, random));
            var keyEntry = SectionKeys.For(name).KeyEntry;
            foreach (var line in sections[s].Skip(1))
            {
                if (random.Next(4) == 0)
                    lines.Add(Above(random));
                lines.Add(EntryLine(line, keyEntry, random));
            }
        }
        if (random.Next(3) == 0)
            lines.AddRange(["", "; footer " + Comment(random)]);

        var utf8 = random.Next(2) == 0;
        var encoding = utf8 ? Encoding.UTF8 : IniDocument.TextEncoding;
        var file = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            file.Append(lines[i]);
            var last = i + 1 == lines.Count;
            if (last && lines[i].Length > 0 && random.Next(2) == 0)
                break;
            // A CR before an empty line with an LF end gives one CRLF, so it comes only before a line with text.
            file.Append(random.Next(3) switch
            {
                0 => "\n",
                1 when !last && lines[i + 1].Length > 0 => "\r",
                _ => "\r\n",
            });
        }
        var expected = string.Concat(lines.Select(l => l + "\r\n"));
        return new HandWrittenPatch([.. encoding.GetPreamble(), .. encoding.GetBytes(file.ToString())],
            [.. encoding.GetPreamble(), .. encoding.GetBytes(expected)], utf8);
    }

    /// <summary>The lines of each section of a patch that the writer wrote.</summary>
    private static List<List<string>> SplitSections(string text)
    {
        var sections = new List<List<string>>();
        foreach (var line in text.Split("\r\n").SkipLast(1))
        {
            if (line.Length == 0)
                continue;
            if (line.StartsWith('['))
                sections.Add([]);
            sections[^1].Add(line);
        }
        return sections;
    }

    private static string Header(string name, Random random)
    {
        var header = "[" + (random.Next(8) == 0 ? name.ToUpperInvariant() : name) + "]";
        return random.Next(4) == 0 ? header + Space(random) + "; " + Comment(random) : header;
    }

    /// <summary>A line that the parser skips: a comment, an <c>@</c> line, or a blank or white-space line.</summary>
    private static string Above(Random random) => random.Next(6) switch
    {
        0 => "; " + Comment(random),
        1 => "@note " + Comment(random),
        2 => "",
        3 => "\t",
        4 => "  ; indented " + Comment(random),
        _ => " ",
    };

    /// <summary>The line of an entry with other spaces, letter case, number text or an inline comment.</summary>
    /// <param name="line">The line as the writer writes it: <c>name = v1, v2</c> or <c>name =</c>.</param>
    /// <param name="keyEntry">The key entry name of the section type, or null.</param>
    private static string EntryLine(string line, string? keyEntry, Random random)
    {
        var equals = line.IndexOf(" =", StringComparison.Ordinal);
        var name = line[..equals];
        var rest = line[(equals + 2)..].TrimStart(' ');
        var values = rest.Length == 0 ? [] : rest.Split(',').Select(v => v.Trim()).ToArray();
        var isKey = keyEntry != null && name.Equals(keyEntry, StringComparison.OrdinalIgnoreCase);
        if ((isKey || name.StartsWith("ol.", StringComparison.OrdinalIgnoreCase)) && random.Next(3) == 0)
        {
            // The merge compares the key, the directives and their texts without letter case.
            name = name.ToUpperInvariant();
            values = values.Select(v => random.Next(2) == 0 ? v.ToUpperInvariant() : v).ToArray();
        }
        else if (!isKey)
        {
            if (random.Next(10) == 0)
                name = name.ToUpperInvariant();
            values = values.Select(v => Decimal.IsMatch(v) && random.Next(2) == 0 ? v + "0" : v).ToArray();
        }

        var text = (random.Next(8) == 0 ? Space(random) : "") + name + Space(random) + "=";
        if (values.Length > 0)
            text += Space(random) + string.Join(random.Next(2) == 0 ? ", " : Space(random) + "," + Space(random), values);
        return random.Next(5) == 0 ? text + Space(random) + "; " + Comment(random) : text;
    }

    private static string Comment(Random random) => Comments[random.Next(Comments.Length)];

    private static string Space(Random random) => Spaces[random.Next(Spaces.Length)];
}

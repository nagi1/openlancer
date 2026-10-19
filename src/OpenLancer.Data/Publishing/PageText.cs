using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenLancer.Data.Publishing;

/// <summary>The kind of a line of a page text.</summary>
public enum PageBlockKind
{
    /// <summary><c># Heading</c>.</summary>
    Heading1,

    /// <summary><c>## Heading</c>.</summary>
    Heading2,

    /// <summary><c>- item</c> or <c>* item</c>.</summary>
    Bullet,

    /// <summary><c>1. item</c>.</summary>
    Number,

    /// <summary>Other text.</summary>
    Paragraph,

    /// <summary>An empty line, which starts a new paragraph.</summary>
    Empty,
}

/// <summary>The kind of a part of a line.</summary>
public enum PageSpanKind
{
    Text,

    /// <summary><c>**bold**</c>.</summary>
    Bold,

    /// <summary><c>[text](https://link)</c>.</summary>
    Link,

    /// <summary>A BBCode tag, for example <c>[color=red]</c>. Nexus reads it, and the other texts drop it.</summary>
    Tag,
}

/// <summary>A part of a line.</summary>
/// <param name="Url">The link of a <see cref="PageSpanKind.Link"/>, else null.</param>
public sealed record PageSpan(PageSpanKind Kind, string Text, string? Url = null)
{
    /// <summary>True for the text of a link that does not start with <c>http://</c> or <c>https://</c>. The text stays
    /// as it is, and the preview marks it with a warning.</summary>
    public bool BadLink { get; init; }
}

/// <summary>One line of a page text.</summary>
/// <param name="Marker">The number of a <see cref="PageBlockKind.Number"/> line with its dot, for example
/// <c>2.</c>, else empty.</param>
public sealed record PageBlock(PageBlockKind Kind, IReadOnlyList<PageSpan> Spans, string Marker = "");

/// <summary>
/// The text format of the description of a mod (decision D564 and section 8.4 of the M11c design): a small Markdown
/// subset. One parser serves the Nexus BBCode, the plain text of the README, the ASCII text of the FLMM script and the
/// preview of the editor, so that the preview and the files agree.
/// </summary>
/// <remarks>
/// The subset has headings (<c>#</c> and <c>##</c>), lists (<c>-</c>, <c>*</c> and <c>1.</c>), <c>**bold**</c>,
/// links and empty lines. It has no italic, because the editor font has no italic face. A <c>**</c> without its pair
/// stays text. A link that does not start with <c>http://</c> or <c>https://</c> stays text.
/// </remarks>
public static partial class PageText
{
    /// <summary>The BBCode tags that pass through to Nexus. The plain text drops them and keeps their text.</summary>
    private static readonly HashSet<string> KnownTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "url", "img", "list", "*", "size", "color", "font", "center", "left", "right", "quote",
        "code", "spoiler", "line", "youtube",
    };

    /// <summary>The ASCII form of the common typographic characters (decision D580).</summary>
    private static readonly Dictionary<int, string> Typographic = new()
    {
        // Quotes.
        [0x2018] = "'", [0x2019] = "'", [0x201A] = "'", [0x201B] = "'", [0x2032] = "'",
        [0x201C] = "\"", [0x201D] = "\"", [0x201E] = "\"", [0x201F] = "\"", [0x2033] = "\"",
        // Dashes and the minus sign.
        [0x2010] = "-", [0x2011] = "-", [0x2012] = "-", [0x2013] = "-", [0x2014] = "-", [0x2015] = "-", [0x2212] = "-",
        // The ellipsis.
        [0x2026] = "...",
        // The no-break space and the narrow spaces.
        [0x00A0] = " ", [0x2007] = " ", [0x2009] = " ", [0x202F] = " ",
    };

    /// <summary>Parses a text into lines. The parse removes control characters except the line end and the tab, and
    /// drops the empty lines at the start and at the end.</summary>
    public static IReadOnlyList<PageBlock> Parse(string? text)
    {
        var blocks = new List<PageBlock>();
        foreach (var raw in RemoveControl(text ?? "", keepLineEnds: true, keepTabs: true).Split('\n'))
            blocks.Add(Line(raw));
        while (blocks.Count > 0 && blocks[0].Kind == PageBlockKind.Empty)
            blocks.RemoveAt(0);
        while (blocks.Count > 0 && blocks[^1].Kind == PageBlockKind.Empty)
            blocks.RemoveAt(blocks.Count - 1);
        return blocks;
    }

    /// <summary>The Nexus BBCode of the lines (section 8.4 of the M11c design).</summary>
    public static IReadOnlyList<string> ToBBCode(IReadOnlyList<PageBlock> blocks)
    {
        var lines = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var text = string.Concat(block.Spans.Select(BBCodeSpan));
            switch (block.Kind)
            {
                case PageBlockKind.Heading1:
                    lines.Add($"[size=5][b]{text}[/b][/size]");
                    break;
                case PageBlockKind.Heading2:
                    lines.Add($"[size=4][b]{text}[/b][/size]");
                    break;
                case PageBlockKind.Bullet or PageBlockKind.Number:
                    if (i == 0 || blocks[i - 1].Kind != block.Kind)
                        lines.Add(block.Kind == PageBlockKind.Bullet ? "[list]" : "[list=1]");
                    lines.Add("[*]" + text);
                    if (i == blocks.Count - 1 || blocks[i + 1].Kind != block.Kind)
                        lines.Add("[/list]");
                    break;
                case PageBlockKind.Empty:
                    lines.Add("");
                    break;
                default:
                    lines.Add(text);
                    break;
            }
        }
        return lines;
    }

    /// <summary>The plain text of the lines, for the README: the lists stay, the marks go, a link shows its address,
    /// and a BBCode tag goes but its text stays.</summary>
    public static IReadOnlyList<string> ToPlainText(IReadOnlyList<PageBlock> blocks)
    {
        var lines = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var text = string.Concat(block.Spans.Select(PlainSpan));
            switch (block.Kind)
            {
                case PageBlockKind.Heading1:
                    lines.Add(text);
                    if (i < blocks.Count - 1 && blocks[i + 1].Kind != PageBlockKind.Empty)
                        lines.Add("");
                    break;
                case PageBlockKind.Bullet:
                    lines.Add("- " + text);
                    break;
                case PageBlockKind.Number:
                    lines.Add(block.Marker + " " + text);
                    break;
                default:
                    lines.Add(text);
                    break;
            }
        }
        return lines;
    }

    /// <summary>The BBCode of a text, with LF line ends.</summary>
    public static string BBCode(string? text) => string.Join("\n", ToBBCode(Parse(text)));

    /// <summary>The plain text of a text, with LF line ends.</summary>
    public static string PlainText(string? text) => string.Join("\n", ToPlainText(Parse(text)));

    /// <summary>
    /// The ASCII form of a text for the FLMM script (decision D580). The common typographic characters get their ASCII
    /// form: quotes, dashes, the ellipsis and the no-break space. A tab becomes four spaces. Each other character
    /// outside ASCII becomes <c>?</c>, and <paramref name="lost"/> gets it.
    /// </summary>
    public static string ToAscii(string text, ICollection<string>? lost = null)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in RemoveControl(text, keepLineEnds: true, keepTabs: true).EnumerateRunes())
        {
            if (rune.Value == '\t')
                result.Append("    ");
            else if (rune.IsAscii)
                result.Append((char)rune.Value);
            else if (Typographic.TryGetValue(rune.Value, out var ascii))
                result.Append(ascii);
            else
            {
                result.Append('?');
                if (lost != null && !lost.Contains(rune.ToString()))
                    lost.Add(rune.ToString());
            }
        }
        return result.ToString();
    }

    /// <summary>The characters of a text that the FLMM script cannot hold, after the typographic changes.</summary>
    public static IReadOnlyList<string> LostInAscii(string? text)
    {
        var lost = new List<string>();
        ToAscii(text ?? "", lost);
        return lost;
    }

    /// <summary>
    /// The text without control characters. <c>\r\n</c> and <c>\r</c> become <c>\n</c> first. A line end stays only
    /// with <paramref name="keepLineEnds"/>, and a tab only with <paramref name="keepTabs"/>.
    /// </summary>
    public static string RemoveControl(string text, bool keepLineEnds, bool keepTabs = false)
    {
        var normal = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normal.Any(char.IsControl))
            return normal;
        return new string(normal.Where(c => !char.IsControl(c) || c == '\n' && keepLineEnds || c == '\t' && keepTabs)
            .ToArray());
    }

    /// <summary>True when the link starts with <c>http://</c> or <c>https://</c>.</summary>
    public static bool IsWebLink(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static PageBlock Line(string raw)
    {
        var line = raw.TrimStart();
        if (line.Trim().Length == 0)
            return new PageBlock(PageBlockKind.Empty, []);
        if (HeadingPattern().Match(line) is { Success: true } heading)
            return new PageBlock(heading.Groups[1].Length == 1 ? PageBlockKind.Heading1 : PageBlockKind.Heading2,
                Inline(heading.Groups[2].Value.TrimEnd()));
        if (BulletPattern().Match(line) is { Success: true } bullet)
            return new PageBlock(PageBlockKind.Bullet, Inline(bullet.Groups[1].Value.TrimEnd()));
        if (NumberPattern().Match(line) is { Success: true } number)
            return new PageBlock(PageBlockKind.Number, Inline(number.Groups[2].Value.TrimEnd()), number.Groups[1].Value + ".");
        return new PageBlock(PageBlockKind.Paragraph, Inline(raw.TrimEnd()));
    }

    /// <summary>The parts of a line: bold text, links and BBCode tags between plain text.</summary>
    private static List<PageSpan> Inline(string line)
    {
        var spans = new List<PageSpan>();
        var text = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '*' && i + 2 < line.Length && line[i + 1] == '*' && BoldEnd(line, i + 2) is var end and > 0)
            {
                Flush();
                spans.Add(new PageSpan(PageSpanKind.Bold, line[(i + 2)..end]));
                i = end + 2;
                continue;
            }
            if (line[i] == '[')
            {
                if (LinkPattern().Match(line, i) is { Success: true } link)
                {
                    Flush();
                    var url = link.Groups[2].Value;
                    spans.Add(IsWebLink(url)
                        ? new PageSpan(PageSpanKind.Link, link.Groups[1].Value, url)
                        : new PageSpan(PageSpanKind.Text, link.Value) { BadLink = true });
                    i += link.Length;
                    continue;
                }
                if (TagPattern().Match(line, i) is { Success: true } tag && KnownTags.Contains(tag.Groups[2].Value))
                {
                    Flush();
                    spans.Add(new PageSpan(PageSpanKind.Tag, tag.Value));
                    i += tag.Length;
                    continue;
                }
            }
            text.Append(line[i]);
            i++;
        }
        Flush();
        return spans;

        void Flush()
        {
            if (text.Length == 0)
                return;
            spans.Add(new PageSpan(PageSpanKind.Text, text.ToString()));
            text.Clear();
        }
    }

    /// <summary>
    /// The start of the <c>**</c> that closes a bold text from <paramref name="start"/>, or -1. As in Markdown, the
    /// bold text does not start or end with a space and is not empty.
    /// </summary>
    private static int BoldEnd(string line, int start)
    {
        if (char.IsWhiteSpace(line[start]) || line[start] == '*')
            return -1;
        for (var end = line.IndexOf("**", start + 1, StringComparison.Ordinal); end > 0;
             end = line.IndexOf("**", end + 1, StringComparison.Ordinal))
        {
            if (!char.IsWhiteSpace(line[end - 1]))
                return end;
        }
        return -1;
    }

    private static string BBCodeSpan(PageSpan span) => span.Kind switch
    {
        PageSpanKind.Bold => $"[b]{span.Text}[/b]",
        PageSpanKind.Link => $"[url={span.Url}]{span.Text}[/url]",
        _ => span.Text,
    };

    private static string PlainSpan(PageSpan span) => span.Kind switch
    {
        PageSpanKind.Link => $"{span.Text} ({span.Url})",
        PageSpanKind.Tag => "",
        _ => span.Text,
    };

    [GeneratedRegex(@"^(#{1,2})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^[-*]\s+(.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(\d{1,9})\.\s+(.*)$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\G\[([^\[\]\n]+)\]\(([^()\s]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\G\[(/?)([A-Za-z]+|\*)(?:=[^\[\]\n]*)?\]")]
    private static partial Regex TagPattern();
}

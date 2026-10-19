using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;

namespace OpenLancer.Data.Ini;

/// <summary>
/// The one canonical line writer of INI text (section 11.5 of the M6a design, review finding RV10): <c>key = v1, v2</c>
/// with the values of <see cref="RetailText.Of"/>, and a bare key for an entry without a value. The export, the merged
/// text files and the raw INI editor use it.
/// </summary>
internal static class IniLines
{
    /// <summary>The line end of canonical text.</summary>
    public const string LineEnd = "\r\n";

    /// <summary>The text of an entry line, without its indent and comment: <c>key = value, value</c>, or the key alone
    /// for an entry without a value.</summary>
    /// <remarks>An entry is a list of values, so a caller can give an entry with another key, for example the key as a
    /// file writes it.</remarks>
    public static string Line(string key, IEnumerable<ValueBase> values)
    {
        var texts = values.Select(RetailText.Of).ToList();
        return texts.Count == 0 ? key : key + " = " + string.Join(", ", texts);
    }

    /// <summary>The text of an entry line with the key of the entry.</summary>
    public static string Line(Entry entry) => Line(entry.Name, entry);

    /// <summary>The lines of a section: its header and its entries.</summary>
    public static List<string> SectionLines(Section section) => [$"[{section.Name}]", .. section.Select(Line)];

    /// <summary>The canonical text of sections: the lines of each section, with an empty line between two sections,
    /// and a line end after each line.</summary>
    public static string Text(IEnumerable<Section> sections)
    {
        var text = new StringBuilder();
        var first = true;
        foreach (var section in sections)
        {
            if (!first)
                text.Append(LineEnd);
            first = false;
            foreach (var line in SectionLines(section))
                text.Append(line).Append(LineEnd);
        }
        return text.ToString();
    }
}

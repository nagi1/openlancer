using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>
/// Compares the lines of sections as the engine reads them. Values compare engine-equal (see
/// <see cref="IniValueComparer.EngineEqual"/>), and entry names compare with letter case.
/// </summary>
internal static class SectionComparer
{
    /// <summary>
    /// True when the engine reads the same data from both sections. They have the same name and, in each unit, the
    /// same lines in the same order.
    /// </summary>
    /// <remarks>
    /// A unit is the lines of one name, or the lines of all names of one linked group (see
    /// <see cref="SectionKeys.LinkedGroups"/>), because the engine attaches each line of a group to the line before
    /// it. The order of the units does not count. For a type whose line order counts in the file (see
    /// <see cref="SectionKeys.IsOrderSensitive(string, string)"/>), the whole section is one unit.
    /// </remarks>
    /// <param name="path">The install path of the file of the sections.</param>
    public static bool Equivalent(Section a, Section b, bool binary, string path)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Name != b.Name || a.Count != b.Count)
            return false;
        if (SectionKeys.IsOrderSensitive(a.Name, path))
            return SameLines(a.ToList(), b.ToList(), binary);
        var unitsA = Lines(a, Unit);
        var unitsB = Lines(b, Unit);
        return unitsA.Count == unitsB.Count &&
               unitsA.All(u => unitsB.TryGetValue(u.Key, out var lines) && SameLines(u.Value, lines, binary));
    }

    /// <summary>The lines of a section by name, in file order. Names compare without letter case.</summary>
    public static Dictionary<string, List<Entry>> ByName(Section section) => Lines(section, (_, name) => name);

    /// <summary>True when both lists hold the same lines in the same order.</summary>
    public static bool SameLines(IReadOnlyList<Entry> a, IReadOnlyList<Entry> b, bool binary)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!SameLine(a[i], b[i], binary))
                return false;
        }
        return true;
    }

    private static bool SameLine(Entry a, Entry b, bool binary)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Name != b.Name || a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]) && !IniValueComparer.EngineEqual(a[i], b[i], binary))
                return false;
        }
        return true;
    }

    /// <summary>The unit of a name: the first name of its linked group, or the name itself.</summary>
    private static string Unit(string sectionName, string entryName) =>
        SectionKeys.LinkedGroups(sectionName)
            .FirstOrDefault(g => g.Contains(entryName, StringComparer.OrdinalIgnoreCase))?[0] ?? entryName;

    private static Dictionary<string, List<Entry>> Lines(Section section, Func<string, string, string> keyOf)
    {
        var lines = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in section)
        {
            var key = keyOf(section.Name, entry.Name);
            if (!lines.TryGetValue(key, out var list))
                lines[key] = list = [];
            list.Add(entry);
        }
        return lines;
    }
}

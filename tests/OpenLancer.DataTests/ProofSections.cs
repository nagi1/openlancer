using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.DataTests;

/// <summary>
/// Section helpers for the retail proofs of the patch writer. They follow the merge rules with their own code, so a
/// proof does not depend on the code of the writer that it tests.
/// </summary>
internal static class ProofSections
{
    /// <summary>
    /// For each (type, key), how many sections a patch section with that key finds. The merge finds a section by any
    /// of its key entries.
    /// </summary>
    public static Dictionary<(string Type, string Key), int> KeyCounts(IEnumerable<Section> sections)
    {
        var counts = new Dictionary<(string, string), int>(TypeAndKey.Instance);
        foreach (var section in sections)
        {
            var match = SectionKeys.For(section.Name);
            if (match.Kind != SectionMatchKind.Keyed)
                continue;
            var keys = section.Where(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase) && e.Count > 0)
                .Select(e => e[0].ToString()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
                counts[(section.Name, key)] = counts.GetValueOrDefault((section.Name, key)) + 1;
        }
        return counts;
    }

    /// <summary>The key of a section: the value of its one key entry with one value, or null.</summary>
    public static string? KeyOf(Section section)
    {
        var match = SectionKeys.For(section.Name);
        if (match.Kind != SectionMatchKind.Keyed)
            return null;
        var keys = section.Where(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase)).ToList();
        return keys.Count == 1 && keys[0].Count == 1 ? keys[0][0].ToString() : null;
    }

    /// <summary>The key that <c>ol.parent</c> compares: the first value of the first key entry with a value, or null.</summary>
    public static string? ParentKey(Section section)
    {
        var match = SectionKeys.For(section.Name);
        return match.Kind != SectionMatchKind.Keyed
            ? null
            : section.FirstOrDefault(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase) && e.Count > 0)?[0].ToString();
    }

    /// <summary>The number of child sections that directly follow the section at <paramref name="index"/>.</summary>
    public static int ChildCount(IReadOnlyList<Section> sections, int index)
    {
        var count = 0;
        while (index + 1 + count < sections.Count && SectionKeys.IsChild(sections[index + 1 + count].Name))
            count++;
        return count;
    }

    /// <summary>The index of the closest section before <paramref name="index"/> that is not a child section, or -1.</summary>
    public static int Parent(IReadOnlyList<Section> sections, int index)
    {
        var parent = index - 1;
        while (parent >= 0 && SectionKeys.IsChild(sections[parent].Name))
            parent--;
        return parent;
    }

    public static bool IsCapitalBoolString(ValueBase value) =>
        value is StringValue && bool.TryParse(value.ToString(), out _) && value.ToString() is not ("true" or "false");

    /// <summary>A copy of the entry with each bool string in lowercase.</summary>
    public static Entry Lowercase(Entry entry)
    {
        var copy = new Entry(entry.Section, entry.Name, entry.Count);
        foreach (var value in entry)
            copy.Add(IsCapitalBoolString(value) ? new StringValue(value.ToString().ToLowerInvariant()) : value);
        return copy;
    }

    /// <summary>
    /// A copy of a section, as the editor makes it for a new section. In a binary file, a bool string is lowercase,
    /// because the merge stores a new boolean as lowercase text.
    /// </summary>
    public static Section Copy(Section section, bool binary)
    {
        var copy = new Section(section.Name);
        foreach (var entry in section)
            copy.Add(binary ? Lowercase(entry) : entry);
        return copy;
    }

    /// <summary>Compares (type, key) pairs without letter case, as the merge does.</summary>
    private sealed class TypeAndKey : IEqualityComparer<(string, string)>
    {
        public static readonly TypeAndKey Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Runtime.Scene;

/// <summary>The lines of a section of W as the scene reads and compares them.</summary>
internal static class SceneLines
{
    /// <summary>The values of the last line of a key, or null when the section has no line of the key. The engine reads
    /// only the last line.</summary>
    public static IReadOnlyList<ValueBase>? Last(SectionView view, string key)
    {
        var entries = view.Entries;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                return entries[i].Values;
        }
        return null;
    }

    /// <summary>True when two lines hold the same values: the same objects, or values of the same type with the same
    /// text.</summary>
    public static bool Same(IReadOnlyList<ValueBase> a, IReadOnlyList<ValueBase> b) =>
        a.Count == b.Count && a.Zip(b).All(p => ReferenceEquals(p.First, p.Second) ||
                                                p.First.GetType() == p.Second.GetType() &&
                                                p.First.ToString() == p.Second.ToString());

    /// <summary>True when the last line of a key holds a text placeholder of the active mod, for example
    /// <c>ids_name = $str:li01_depot</c>.</summary>
    public static bool HoldsPlaceholder(SectionView view, string key) =>
        Last(view, key) is [var value, ..] && StringPlaceholder.TryParse(value, out _);

    /// <summary>
    /// The keys whose lines differ between two views of a section: a key whose lines differ in count, or in a value of
    /// another type or text (see <see cref="Same"/>).
    /// </summary>
    public static IEnumerable<string> ChangedKeys(IReadOnlyList<EntryView> before, IReadOnlyList<EntryView> after)
    {
        var a = ByKey(before);
        var b = ByKey(after);
        return a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Where(key =>
            !a.TryGetValue(key, out var x) || !b.TryGetValue(key, out var y) || x.Count != y.Count ||
            x.Zip(y).Any(p => !Same(p.First, p.Second)));
    }

    /// <summary>The lines of each key, by key. The keys compare without letter case.</summary>
    private static Dictionary<string, List<IReadOnlyList<ValueBase>>> ByKey(IReadOnlyList<EntryView> entries)
    {
        var keys = new Dictionary<string, List<IReadOnlyList<ValueBase>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!keys.TryGetValue(entry.Name, out var lines))
                keys[entry.Name] = lines = [];
            lines.Add(entry.Values);
        }
        return keys;
    }
}

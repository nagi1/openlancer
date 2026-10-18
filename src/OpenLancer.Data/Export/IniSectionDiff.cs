using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;

namespace OpenLancer.Data.Export;

/// <summary>
/// The lines of one section that a merge keeps, changes, removes and inserts. A base line and a merged line are a pair
/// when the merge keeps or changes the line.
/// </summary>
/// <param name="Base">The index of the section in the base file.</param>
/// <param name="Merged">The index of the section in the merged file.</param>
/// <param name="MergedOf">For each base line: the index of its merged line, or -1 when the merge removes it.</param>
/// <param name="BaseOf">For each merged line: the index of its base line, or -1 when the merge inserts it.</param>
/// <param name="ValueChanged">For each base line: true when its merged line has other values.</param>
internal sealed record SectionDiff(int Base, int Merged, int[] MergedOf, int[] BaseOf, bool[] ValueChanged)
{
    /// <summary>True when the merge changes, removes or inserts a line of the section.</summary>
    public bool HasChanges => MergedOf.Contains(-1) || BaseOf.Contains(-1) || ValueChanged.Contains(true);

    /// <summary>The base lines that the merge removes.</summary>
    public IEnumerable<int> Removed => Enumerable.Range(0, MergedOf.Length).Where(i => MergedOf[i] < 0);

    /// <summary>The base lines that the merge gives other values.</summary>
    public IEnumerable<int> Changed => Enumerable.Range(0, ValueChanged.Length).Where(i => ValueChanged[i]);

    /// <summary>The merged lines that the merge inserts.</summary>
    public IEnumerable<int> Inserted => Enumerable.Range(0, BaseOf.Length).Where(i => BaseOf[i] < 0);
}

/// <summary>
/// The difference between the sections of a base file and of its merged file (milestone M11). An empty difference
/// means that the merge does not change the file, also when the bytes of the two files differ.
/// </summary>
internal sealed class FileDiff
{
    public FileDiff(IReadOnlyList<Section> baseSections, IReadOnlyList<Section> mergedSections,
        IReadOnlyList<SectionDiff> matched, IReadOnlyList<int> removed, IReadOnlyList<int> added)
    {
        BaseSections = baseSections;
        MergedSections = mergedSections;
        Matched = matched;
        Removed = removed;
        Added = added;
    }

    public IReadOnlyList<Section> BaseSections { get; }

    public IReadOnlyList<Section> MergedSections { get; }

    /// <summary>The sections of both files, in file order. A section with no change is in the list too.</summary>
    public IReadOnlyList<SectionDiff> Matched { get; }

    /// <summary>The base sections that the merge removes, in file order.</summary>
    public IReadOnlyList<int> Removed { get; }

    /// <summary>The merged sections that the merge adds, in file order.</summary>
    public IReadOnlyList<int> Added { get; }

    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0 && Matched.All(m => !m.HasChanges);

    /// <summary>The sections that the merge changes, in file order.</summary>
    public IEnumerable<SectionDiff> Changed => Matched.Where(m => m.HasChanges);
}

/// <summary>
/// Compares the sections of a base file with the sections of its merged file (milestone M11). The export writes a text
/// file and an FLMM script from the result.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Sections match by <see cref="SectionIdentity"/>, in file order. A section type without a key matches by its
/// place among the sections of its type. A section whose identity changes, for example after a rename, is a removed
/// section and an added section.</item>
/// <item>The lines of a matched section compare by name and by the text of their values
/// (<see cref="RetailText.Of"/>), with a longest common subsequence. Between two lines that match, a removed line and
/// an inserted line with the same name are a changed line.</item>
/// </list>
/// </remarks>
internal static class IniSectionDiff
{
    public static FileDiff Compare(IReadOnlyList<Section> baseSections, IReadOnlyList<Section> mergedSections)
    {
        var baseIds = SectionIdentity.Identify(baseSections);
        var mergedIds = SectionIdentity.Identify(mergedSections);
        var baseIndex = new Dictionary<SectionIdentity, int>();
        for (var i = 0; i < baseIds.Length; i++)
            baseIndex[baseIds[i]] = i;

        // The base index of each merged section with the same identity. Only an increasing run of them can match, so
        // that both files keep their order.
        var candidates = mergedIds.Select(id => baseIndex.GetValueOrDefault(id, -1)).ToArray();
        var keep = IncreasingRun(candidates);
        var matched = new List<SectionDiff>();
        var baseMatched = new bool[baseSections.Count];
        var added = new List<int>();
        for (var m = 0; m < mergedSections.Count; m++)
        {
            if (!keep[m])
            {
                added.Add(m);
                continue;
            }
            var b = candidates[m];
            baseMatched[b] = true;
            matched.Add(CompareLines(b, m, baseSections[b], mergedSections[m]));
        }
        var removed = Enumerable.Range(0, baseSections.Count).Where(b => !baseMatched[b]).ToList();
        return new FileDiff(baseSections, mergedSections, matched, removed, added);
    }

    /// <summary>The text that two lines must share to match: the name without letter case, and the values.</summary>
    internal static string LineKey(Entry entry) =>
        entry.Name.ToLowerInvariant() + "\u0001" + string.Join("\u0002", entry.Select(ValueKey));

    /// <summary>The text of a value for a compare. The game reads <c>true</c> and <c>false</c> in any letter case, and a
    /// binary file holds them as text, for example <c>True</c>. So their letter case does not count.</summary>
    private static string ValueKey(ValueBase value)
    {
        var text = RetailText.Of(value);
        return bool.TryParse(text, out var truth) ? (truth ? "true" : "false") : text;
    }

    /// <summary>Compares the lines of one section pair.</summary>
    private static SectionDiff CompareLines(int baseIndex, int mergedIndex, Section a, Section b)
    {
        var x = a.Select(LineKey).ToArray();
        var y = b.Select(LineKey).ToArray();
        var mergedOf = Enumerable.Repeat(-1, x.Length).ToArray();
        var baseOf = Enumerable.Repeat(-1, y.Length).ToArray();
        foreach (var (i, j) in CommonLines(x, y))
        {
            mergedOf[i] = j;
            baseOf[j] = i;
        }

        // Pairs the removed and the inserted lines with the same name between two lines that match, in order.
        var changed = new bool[x.Length];
        int nextBase = 0, nextMerged = 0;
        while (nextBase < x.Length || nextMerged < y.Length)
        {
            var endBase = nextBase;
            while (endBase < x.Length && mergedOf[endBase] < 0)
                endBase++;
            var endMerged = nextMerged;
            while (endMerged < y.Length && baseOf[endMerged] < 0)
                endMerged++;
            var from = nextBase;
            for (var j = nextMerged; j < endMerged; j++)
            {
                for (var i = from; i < endBase; i++)
                {
                    if (!a[i].Name.Equals(b[j].Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    mergedOf[i] = j;
                    baseOf[j] = i;
                    changed[i] = true;
                    from = i + 1;
                    break;
                }
            }
            nextBase = endBase + 1;
            nextMerged = endMerged + 1;
        }
        return new SectionDiff(baseIndex, mergedIndex, mergedOf, baseOf, changed);
    }

    /// <summary>The index pairs of a longest common subsequence of two lists of line keys.</summary>
    private static List<(int, int)> CommonLines(string[] x, string[] y)
    {
        var pairs = new List<(int, int)>();
        var start = 0;
        while (start < x.Length && start < y.Length && x[start] == y[start])
        {
            pairs.Add((start, start));
            start++;
        }
        var end = 0;
        while (end < x.Length - start && end < y.Length - start && x[^(end + 1)] == y[^(end + 1)])
            end++;

        var n = x.Length - start - end;
        var m = y.Length - start - end;
        if (n > 0 && m > 0)
        {
            // lengths[i, j] is the length of the common subsequence of x[start + i..] and y[start + j..].
            var lengths = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    lengths[i, j] = x[start + i] == y[start + j]
                        ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
                }
            }
            for (int i = 0, j = 0; i < n && j < m;)
            {
                if (x[start + i] == y[start + j])
                {
                    pairs.Add((start + i, start + j));
                    i++;
                    j++;
                }
                else if (lengths[i + 1, j] >= lengths[i, j + 1])
                    i++;
                else
                    j++;
            }
        }
        for (var k = 0; k < end; k++)
            pairs.Add((x.Length - end + k, y.Length - end + k));
        return pairs;
    }

    /// <summary>
    /// The items of a longest strictly increasing run of the values that are 0 or more, as flags. A value of -1 is never
    /// in the run.
    /// </summary>
    private static bool[] IncreasingRun(int[] values)
    {
        // tails[k] is the index of the smallest last value of an increasing run of length k + 1.
        var tails = new List<int>();
        var previous = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] < 0)
                continue;
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (values[tails[middle]] < values[i])
                    low = middle + 1;
                else
                    high = middle;
            }
            previous[i] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count)
                tails.Add(i);
            else
                tails[low] = i;
        }
        var result = new bool[values.Length];
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i])
            result[i] = true;
        return result;
    }
}

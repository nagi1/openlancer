using System;
using System.Collections.Generic;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The holes of an <c>[Exclusion Zones]</c> section of a nebula or asteroid field file (milestone M3, decision D143 of
/// the M25 design). The engine starts a new hole at each <c>exclusion</c> or <c>exclude</c> line, and gives the hole the
/// lines after it, up to the next such line.
/// </summary>
/// <remarks>Retail starts a hole only at <c>exclusion</c>. The one retail <c>exclude</c> line is the last line of its
/// section, so both readings give the same lines. The runs follow the fork.</remarks>
internal static class ExclusionHoles
{
    private static readonly string[] Starts = ["exclusion", "exclude"];

    /// <summary>
    /// The runs of the holes of a zone in a section, in file order: the index of the first line among all lines of the
    /// section, and the number of lines. The zone compares without letter case. A section can name one zone more than
    /// one time, so the result is a list.
    /// </summary>
    public static IReadOnlyList<(int First, int Count)> Runs(IReadOnlyList<Entry> lines, string zone)
    {
        var result = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= lines.Count; i++)
        {
            var starts = i < lines.Count && IsStart(lines[i]);
            if (i < lines.Count && !starts)
                continue;
            if (start >= 0)
                result.Add((start, i - start));
            start = starts && lines[i].Count > 0 && lines[i][0].ToString().Trim().Equals(zone, StringComparison.OrdinalIgnoreCase)
                ? i
                : -1;
        }
        return result;
    }

    /// <summary>The runs of a section (see <see cref="Runs(IReadOnlyList{Entry}, string)"/>).</summary>
    public static IReadOnlyList<(int First, int Count)> Runs(Section section, string zone)
    {
        var lines = new List<Entry>(section.Count);
        foreach (var entry in section)
            lines.Add(entry);
        return Runs(lines, zone);
    }

    private static bool IsStart(Entry line) =>
        Array.Exists(Starts, s => s.Equals(line.Name, StringComparison.OrdinalIgnoreCase));
}

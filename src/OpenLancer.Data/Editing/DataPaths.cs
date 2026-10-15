using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>The data files that <c>freelancer.ini</c> lists, as the engine finds them.</summary>
internal static class DataPaths
{
    /// <summary>
    /// Each entry of the <c>[Data]</c> sections that has a value, in file order: its name and the install path of the
    /// file that it names. The path is relative to the folder of <c>[Freelancer] data path</c>. The result is empty when
    /// <c>freelancer.ini</c> has no data path.
    /// </summary>
    public static IEnumerable<(string Name, string Path)> Entries(IReadOnlyList<Section> freelancerIni)
    {
        var dataPath = freelancerIni.Where(s => Is(s.Name, "Freelancer"))
            .Select(s => ItemIndex.First(s, "data path"))
            .FirstOrDefault(p => p != null);
        if (dataPath == null)
            yield break;
        foreach (var entry in freelancerIni.Where(s => Is(s.Name, "Data")).SelectMany(s => s))
        {
            if (entry.Count > 0 && VfsPath.Normalize($@"EXE\{dataPath}\{entry[0]}") is { } path)
                yield return (entry.Name, path);
        }
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

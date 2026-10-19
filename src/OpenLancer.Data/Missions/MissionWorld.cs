using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The names of the game data outside the mission scripts, and the files of the edit view (section 12.3 of the M6a
/// design). The UI thread makes it from the data lists of the edit view. The worker of the checks reads it only, so it
/// holds plain sets and no session.
/// </summary>
public sealed class MissionWorld
{
    private readonly Dictionary<MissionValueKind, HashSet<string>> names = [];
    private readonly Func<string, bool>? fileExists;

    /// <summary>A world without names and files. The checks of names outside the script then find nothing.</summary>
    public static readonly MissionWorld Empty = new(new Dictionary<MissionValueKind, IEnumerable<string>>(), null);

    /// <param name="names">The names of each kind outside the script, for example the systems.</param>
    /// <param name="fileExists">Tells whether a file below the data folder exists, by its relative path, or null when
    /// the world does not know the files.</param>
    public MissionWorld(IReadOnlyDictionary<MissionValueKind, IEnumerable<string>> names, Func<string, bool>? fileExists)
    {
        foreach (var (kind, values) in names)
            this.names[kind] = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        this.fileExists = fileExists;
    }

    /// <summary>True when the world knows the names of a kind, or the files for a file kind.</summary>
    public bool Knows(MissionValueKind kind) =>
        MissionSchema.ScopeOf(kind) == MissionKindScope.File ? fileExists != null : names.ContainsKey(kind);

    /// <summary>True when the world has a name of a kind, without letter case.</summary>
    public bool Has(MissionValueKind kind, string name) =>
        names.TryGetValue(kind, out var set) && set.Contains(name.Trim());

    /// <summary>True when a file below the data folder exists. A world that does not know the files says yes.</summary>
    public bool FileExists(string relativePath) => fileExists?.Invoke(relativePath) ?? true;

    /// <summary>The number of names of a kind, for the tests.</summary>
    internal int Count(MissionValueKind kind) => names.TryGetValue(kind, out var set) ? set.Count : 0;
}

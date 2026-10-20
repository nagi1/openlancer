using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;

namespace OpenLancer.Data.Missions;

/// <summary>One ship build of the NPC ship files: an <c>[NPCShipArch]</c> section (section 10.6 of the M6a
/// design).</summary>
/// <param name="Ref">The section in its file.</param>
/// <param name="Section">The section of the parser, with its lines in file order.</param>
public sealed record NpcShipBuild(string Nickname, SectionRef Ref, Section Section)
{
    /// <summary>The names of the build that name the game data: the ship archetype, the loadout and the pilot. The
    /// game drops a mission ship whose build names one that the game data does not have.</summary>
    public static readonly IReadOnlyList<(string Key, MissionValueKind Kind)> PartKeys =
    [
        ("ship_archetype", MissionValueKind.ShipArch), ("loadout", MissionValueKind.Loadout),
        ("pilot", MissionValueKind.Pilot),
    ];

    /// <summary>The first value of the first line of a key, or null.</summary>
    public string? Value(string key) =>
        Section.FirstOrDefault(e => MissionIndex.Is(e.Name, key) && e.Count > 0) is { } entry
            ? MissionIndex.Text(entry[0])
            : null;

    /// <summary>The names of <see cref="PartKeys"/> that the build has, in that order.</summary>
    public IReadOnlyList<(MissionValueKind Kind, string Name)> Parts =>
        PartKeys.Select(p => (p.Kind, Name: Value(p.Key))).Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => (p.Kind, p.Name!.Trim())).ToList();
}

/// <summary>
/// The ship builds of the NPC ship files of a view (section 10.6 of the M6a design). The worker of the mission checks
/// reads them from its snapshot. So the completeness of a mission ship compares the build of the edit view with the
/// build of the base game (decision D551).
/// </summary>
public sealed class NpcShipBuilds
{
    private readonly Dictionary<string, NpcShipBuild> builds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>No ship builds.</summary>
    public static readonly NpcShipBuilds Empty = new();

    /// <summary>The number of ship builds.</summary>
    public int Count => builds.Count;

    /// <summary>Reads the <c>[NPCShipArch]</c> sections of files. The first build of a nickname counts, in the order of the
    /// files and of the sections. A file that does not exist or does not parse gives no build.</summary>
    /// <param name="parsed">Gives the sections of a file that the run already parsed, or null.</param>
    /// <param name="absent">True for a file that the run must not read, because W does not have it now. Null for
    /// none.</param>
    public static NpcShipBuilds Read(FileSystem files, IEnumerable<string> paths,
        Func<string, MissionDocument?>? parsed = null, Func<string, bool>? absent = null)
    {
        var result = new NpcShipBuilds();
        foreach (var path in paths)
        {
            if (absent?.Invoke(path) == true)
                continue;
            var document = parsed?.Invoke(path) ?? Parse(files, path);
            foreach (var section in document?.Sections.Where(s => MissionIndex.Is(s.Type, "NPCShipArch")) ?? [])
            {
                if (section.Nickname is { } nickname)
                    result.builds.TryAdd(nickname.Trim(), new NpcShipBuild(nickname.Trim(), section.Ref, section.Section));
            }
        }
        return result;
    }

    private static MissionDocument? Parse(FileSystem files, string path)
    {
        try
        {
            return files.FileExists(path) ? MissionDocument.Parse(path, files.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The build of a nickname, without letter case, or null.</summary>
    public NpcShipBuild? Find(string nickname) => builds.GetValueOrDefault(nickname.Trim());
}

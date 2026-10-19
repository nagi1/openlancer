using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>A nickname of an <c>[Object]</c> or a <c>[zone]</c> of a system file, with its FLHash.</summary>
internal sealed record SceneName(string Name, uint Hash, SectionRef Section)
{
    /// <summary>True for a zone, false for an object.</summary>
    public bool IsZone => Section.Id.Type.Equals("zone", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The nicknames of the objects and zones of all system files of W, with their FLHash (decisions D240 and D265 of the M3
/// design). Retail keeps the zones of all systems in one map by the hash of the zone nickname, and the visit flags of
/// objects and zones by the hash of the nickname. So Create, Duplicate and Rename take a nickname only when no object or
/// zone of any system has it, without letter case, or has its FLHash.
/// </summary>
/// <remarks>
/// <para>The system files are the files of <c>universe.ini</c>, so a file that <c>universe.ini</c> does not name does not
/// count. The index builds one part for each file and builds a part again only when the W state of its file changes.
/// The first use opens the system files that the session did not open.</para>
/// <para>A light nickname must be unique in its file only (see <see cref="LightTaken"/>).</para>
/// </remarks>
internal sealed class Nicknames(EditSession session)
{
    private readonly Dictionary<string, (FileTable Table, List<SceneName> Names)> parts = new(VfsPath.Comparer);
    private int revision = int.MinValue;
    private List<SceneName> all = [];
    private Dictionary<string, List<SceneName>> byName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, List<SceneName>> byHash = [];

    /// <summary>The objects and zones of all system files, in the order of the files.</summary>
    public IReadOnlyList<SceneName> All
    {
        get
        {
            Refresh();
            return all;
        }
    }

    /// <summary>The object or zone that has the name, or a name with its FLHash, other than
    /// <paramref name="except"/>. Null when no other section has it.</summary>
    public SceneName? Taken(string name, SectionRef? except = null)
    {
        Refresh();
        var same = byName.GetValueOrDefault(name.Trim()) ?? [];
        var hashed = byHash.GetValueOrDefault(FLHash.CreateID(name.Trim())) ?? [];
        return same.Concat(hashed).FirstOrDefault(n => except == null || !n.Section.Equals(except));
    }

    /// <summary>The light of a file that has the name, without letter case, other than
    /// <paramref name="except"/>, or null.</summary>
    public SectionRef? LightTaken(string path, string name, SectionRef? except = null) =>
        session.Sections(path).FirstOrDefault(s => s.Id.Type.Equals("LightSource", StringComparison.OrdinalIgnoreCase) &&
                                                  string.Equals(s.Id.Key, name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                                  (except == null || !s.Equals(except)));

    /// <summary>
    /// The first name <c>&lt;stem&gt;_&lt;n&gt;</c> with n from 1, written with at least <paramref name="digits"/>
    /// digits, that no object or zone has and that <paramref name="reserved"/> does not hold (decisions D235 and D262).
    /// </summary>
    /// <param name="taken">Tells whether another name rule takes a name, for example a light of the file, or null.</param>
    public string FirstFree(string stem, int digits, IReadOnlySet<string>? reserved = null, Func<string, bool>? taken = null)
    {
        Func<string, bool> isTaken = taken ?? (x => Taken(x) != null);
        var format = new string('0', Math.Max(1, digits));
        for (var n = 1; ; n++)
        {
            var name = $"{stem}_{n.ToString(format, CultureInfo.InvariantCulture)}";
            var hash = FLHash.CreateID(name);
            if (isTaken(name) || reserved?.Any(r => FLHash.CreateID(r) == hash) == true)
                continue;
            return name;
        }
    }

    /// <summary>
    /// The nickname of a copy (decision D262): a nickname that ends with <c>_</c> and digits keeps its stem and takes the
    /// first free number with the same count of digits. Another nickname gets <c>_1</c>, or the next free number.
    /// </summary>
    public string CopyName(string nickname, IReadOnlySet<string>? reserved = null, Func<string, bool>? taken = null)
    {
        var at = nickname.LastIndexOf('_');
        var number = at > 0 ? nickname[(at + 1)..] : "";
        return number.Length > 0 && number.All(char.IsAsciiDigit)
            ? FirstFree(nickname[..at], number.Length, reserved, taken)
            : FirstFree(nickname, 1, reserved, taken);
    }

    /// <summary>
    /// The groups of objects or of zones whose nicknames are equal without letter case or have one FLHash, across all
    /// system files. Each group has more than one section.
    /// </summary>
    /// <param name="zones">True for the zones, false for the objects.</param>
    public IReadOnlyList<IReadOnlyList<SceneName>> Collisions(bool zones)
    {
        Refresh();
        // The Inspector asks in each frame, so the groups stay until the names change.
        if (collisions.TryGetValue(zones, out var known) && ReferenceEquals(known.All, all))
            return known.Groups;
        var result = new List<IReadOnlyList<SceneName>>();
        var seen = new HashSet<SectionRef>();
        foreach (var name in all.Where(n => n.IsZone == zones))
        {
            if (seen.Contains(name.Section))
                continue;
            var group = byName.GetValueOrDefault(name.Name)!.Concat(byHash.GetValueOrDefault(name.Hash)!)
                .Where(n => n.IsZone == zones).Distinct().ToList();
            if (group.Count < 2)
                continue;
            seen.UnionWith(group.Select(g => g.Section));
            result.Add(group);
        }
        collisions[zones] = (all, result);
        return result;
    }

    private readonly Dictionary<bool, (List<SceneName> All, IReadOnlyList<IReadOnlyList<SceneName>> Groups)> collisions = [];

    /// <summary>
    /// The lines of a system file that name a name as an object or a zone, when no section of the file has the name
    /// (review finding R29). A new item with the name adopts these lines.
    /// </summary>
    public IReadOnlyList<ListReference> Dangling(string name, string systemPath)
    {
        var path = VfsPath.Normalize(systemPath) ?? systemPath;
        if (session.Sections(path).Any(s => (Is(s.Id.Type, "Object") || Is(s.Id.Type, "zone")) &&
                                           string.Equals(s.Id.Key, name, StringComparison.OrdinalIgnoreCase)))
            return [];
        return session.Lists.ReferencesTo("object_of_system", name, path)
            .Concat(session.Lists.ReferencesTo("zone_of_system", name, path))
            .ToList();
    }

    private void Refresh()
    {
        if (revision == session.CacheRevision)
            return;
        revision = session.CacheRevision;
        var changed = false;
        var files = session.Systems.Files;
        foreach (var path in files)
        {
            if (session.Table.File(path) is not { } table)
                continue;
            if (parts.TryGetValue(path, out var part) && ReferenceEquals(part.Table, table))
                continue;
            parts[path] = (table, Names(table));
            changed = true;
        }
        foreach (var gone in parts.Keys.Where(p => !files.Contains(p, VfsPath.Comparer)).ToList())
        {
            parts.Remove(gone);
            changed = true;
        }
        if (!changed && all.Count > 0)
            return;
        all = files.Where(parts.ContainsKey).SelectMany(p => parts[p].Names).ToList();
        byName = all.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        byHash = all.GroupBy(n => n.Hash).ToDictionary(g => g.Key, g => g.ToList());
    }

    private static List<SceneName> Names(FileTable table)
    {
        var result = new List<SceneName>();
        foreach (var type in new[] { "Object", "zone" })
        {
            foreach (var index in table.OfType(type))
            {
                if (SectionIdentity.KeyOf(table.Working[index]) is { Length: > 0 } key)
                    result.Add(new SceneName(key, FLHash.CreateID(key), table.Ref(index)));
            }
        }
        return result;
    }

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

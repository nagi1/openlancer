using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Ini;

/// <summary>
/// Records, in load order, which mod changes which entries of each file. The merge uses it to report conflicts
/// between mods. The editor uses it to tell which mod gives a value.
/// </summary>
/// <remarks>
/// <para>A mod can change the lines of a name (it sets or removes them), delete a section, add a section, or replace
/// the whole file. A whole-file override discards the earlier changes of the file. So <see cref="Record"/> and
/// <see cref="Changes"/> see only the changes after the last override. <see cref="Owner"/> and
/// <see cref="ModsThatChange"/> see the override itself.</para>
/// <para>Sections compare by <see cref="SectionIdentity"/>, and names compare without letter case.</para>
/// </remarks>
internal sealed class PatchLedger
{
    private readonly Dictionary<string, FileLog> files = new(VfsPath.Comparer);

    /// <summary>
    /// Records that <paramref name="modId"/> changes the lines of a name, or deletes the section when the name is
    /// null. Returns the mod that made the same change last, after the last whole-file override, or null.
    /// </summary>
    public string? Record(string path, SectionIdentity section, string? entryName, string modId)
    {
        var log = Log(path);
        var previous = log.Changes(section)
            .LastOrDefault(c => c.Sequence > log.LastOverride && c.IsSame(entryName))?.ModId;
        log.Add(section, new Change(log.Next(), entryName == null ? ChangeKind.Delete : ChangeKind.Lines, entryName, modId));
        return previous;
    }

    /// <summary>Records that <paramref name="modId"/> adds a section.</summary>
    public void RecordAdd(string path, SectionIdentity section, string modId)
    {
        var log = Log(path);
        log.Add(section, new Change(log.Next(), ChangeKind.Add, null, modId));
    }

    /// <summary>Records that <paramref name="modId"/> replaces the whole file, which discards the earlier changes.</summary>
    public void RecordFile(string path, string modId)
    {
        var log = Log(path);
        log.Overrides.Add((log.Next(), modId));
    }

    /// <summary>
    /// The changes of a file that <paramref name="modId"/> made last, after the last whole-file override, as (section,
    /// entry name). A null name is a section that the mod deleted. Added sections are not in the list.
    /// </summary>
    public IEnumerable<(SectionIdentity Section, string? Name)> Changes(string path, string modId)
    {
        if (!files.TryGetValue(path, out var log))
            yield break;
        foreach (var (section, changes) in log.Sections)
        {
            var last = changes
                .Where(c => c.Sequence > log.LastOverride && c.Kind != ChangeKind.Add)
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last());
            foreach (var change in last.Where(c => c.ModId == modId))
                yield return (section, change.Name);
        }
    }

    /// <summary>
    /// The mod whose change gives the lines of a name in the merged file, or null when the lines come from the base
    /// file. That is the last mod that changed the name, added or deleted the section, or replaced the file.
    /// </summary>
    public string? Owner(string path, SectionIdentity section, string name)
    {
        if (!files.TryGetValue(path, out var log))
            return null;
        var change = log.Changes(section).LastOrDefault(c => c.Affects(name));
        if (log.Overrides.Count > 0 && (change == null || log.LastOverride > change.Sequence))
            return log.Overrides[^1].ModId;
        return change?.ModId;
    }

    /// <summary>
    /// The mods that change the lines of a name, add or delete the section, or replace the file, in load order.
    /// </summary>
    public IReadOnlyList<string> ModsThatChange(string path, SectionIdentity section, string name)
    {
        if (!files.TryGetValue(path, out var log))
            return [];
        return log.Changes(section).Where(c => c.Affects(name)).Select(c => (c.Sequence, c.ModId))
            .Concat(log.Overrides)
            .OrderBy(c => c.Sequence)
            .Select(c => c.ModId)
            .Distinct()
            .ToList();
    }

    private FileLog Log(string path)
    {
        if (!files.TryGetValue(path, out var log))
            files[path] = log = new FileLog();
        return log;
    }

    private enum ChangeKind
    {
        /// <summary>The mod sets or removes the lines of a name.</summary>
        Lines,

        Delete,

        Add,
    }

    /// <param name="Sequence">The position of the change among all changes of the file.</param>
    /// <param name="Name">The entry name of a <see cref="ChangeKind.Lines"/> change, else null.</param>
    private sealed record Change(int Sequence, ChangeKind Kind, string? Name, string ModId)
    {
        /// <summary>True when this change is the same kind of change: of the same name, or a delete for null.</summary>
        public bool IsSame(string? entryName) =>
            entryName == null ? Kind == ChangeKind.Delete : Kind == ChangeKind.Lines && Is(entryName);

        /// <summary>True when this change sets the lines of the name in the merged file.</summary>
        public bool Affects(string entryName) => Kind != ChangeKind.Lines || Is(entryName);

        private bool Is(string entryName) => string.Equals(Name, entryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The changes of one file.</summary>
    private sealed class FileLog
    {
        private int count;

        public Dictionary<SectionIdentity, List<Change>> Sections { get; } = [];

        /// <summary>The whole-file overrides, as (sequence, mod).</summary>
        public List<(int Sequence, string ModId)> Overrides { get; } = [];

        /// <summary>The sequence of the last whole-file override, or -1.</summary>
        public int LastOverride => Overrides.Count > 0 ? Overrides[^1].Sequence : -1;

        public int Next() => count++;

        public IReadOnlyList<Change> Changes(SectionIdentity section) => Sections.GetValueOrDefault(section) ?? [];

        public void Add(SectionIdentity section, Change change)
        {
            if (!Sections.TryGetValue(section, out var list))
                Sections[section] = list = [];
            list.Add(change);
        }
    }
}

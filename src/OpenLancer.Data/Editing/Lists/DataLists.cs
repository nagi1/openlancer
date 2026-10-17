using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>
/// The data lists of a session (milestone M21): the entries that a reference key can name, from the sections that the
/// engine reads, in W of the edit view. Mods extend a list when they add sections. Only engine-fixed sets come from C#
/// enums.
/// </summary>
/// <remarks>
/// <para>The rule data (see <see cref="ListRules"/>) maps a value of a key of a section type to a list. A list is
/// available when its files have at least one file in the edit view, or when it is an enum. An unavailable list gives
/// no result, so a synthetic profile without the files of a role gets no list check.</para>
/// <para>Each cache keeps the revision of W at its last check (see <see cref="EditSession.Revision"/>). A call at the same
/// revision returns at once. At a new revision, a cache compares the W states of its own files only, so an edit of one
/// file builds again only the lists and the reverse index parts of that file.</para>
/// <para>"Missing" is a pure function of W: no state stores it. So an undo of a delete heals each referrer at once
/// (decision D81).</para>
/// <para>A file that the active mod replaces with a whole file has its lower data in W. The lists read such a file as
/// the edit view gives it, so they hold its entries.</para>
/// </remarks>
public sealed class DataLists
{
    private readonly EditSession session;
    private readonly ListRules rules;
    private readonly Dictionary<(string Ids, string? Scope), CachedView> views = [];
    private readonly Dictionary<string, ReferencePart> parts = new(VfsPath.Comparer);
    private readonly Dictionary<string, MergedReferences> merged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SectionRef, IReadOnlyList<ListProblem>> problems = [];
    private readonly Dictionary<string, (ModOverlay View, FileTable? Table)> wholeFiles = new(VfsPath.Comparer);
    private readonly Dictionary<string, RemovedPart> removedParts = new(VfsPath.Comparer);
    private int problemsRevision = int.MinValue;
    private (int Revision, IReadOnlyList<RemovedEntry> Entries, Dictionary<(string, string, string), RemovedEntry> Keys)? removed;

    internal DataLists(EditSession session, ListRules rules)
    {
        this.session = session;
        this.rules = rules;
    }

    internal ListRules RuleData => rules;

    private DataFiles Files => session.DataFiles;

    /// <summary>The rules of a key of a section, by value index. Empty when no rule of the roles of its file has the
    /// key.</summary>
    public IReadOnlyList<ListRule> Rules(SectionRef section, string key)
    {
        var roles = Files.RolesOf(section.Path);
        return roles.Count switch
        {
            0 => [],
            1 => rules.Rules(roles[0], section.Id.Type, key),
            _ => roles.SelectMany(r => rules.Rules(r, section.Id.Type, key)).OrderBy(r => r.Index).ToList(),
        };
    }

    /// <summary>
    /// The list of a rule for a line of a section, or null when the list is not available: its files are not in the
    /// edit view, or a <c>systemOf:N</c> scope names no known system.
    /// </summary>
    /// <param name="context">The section of the line.</param>
    /// <param name="line">The values of the line, for a scope that another value gives, or null.</param>
    public ListView? List(ListRule rule, SectionRef context, IReadOnlyList<ValueBase>? line) =>
        ScopeOf(rule, context.Path, line, out var scope) ? View(rule.Lists, scope) : null;

    /// <summary>A list that has no scope, or null when it is not available or the rule data has no such list.</summary>
    public ListView? List(string id) => View([id], null);

    /// <summary>A scoped list of the sections of one file, for example the objects of a system file.</summary>
    public ListView? List(string id, string scopeFile) => View([id], VfsPath.Normalize(scopeFile));

    /// <summary>
    /// Each line value that names an entry of a list, in all referrer files of the list, also files that the session
    /// did not open yet. A scoped list needs <paramref name="scopeFile"/>, the file of the entry, to tell the entries of
    /// different files apart. Without it, the result has the references of all files.
    /// </summary>
    public IReadOnlyList<ListReference> ReferencesTo(string list, string value, string? scopeFile = null)
    {
        var map = Merged(list);
        var normal = scopeFile == null ? null : VfsPath.Normalize(scopeFile);
        if (normal != null || !IsScoped(list))
            return map.GetValueOrDefault((normal?.ToLowerInvariant(), value.Trim().ToLowerInvariant())) ?? [];
        var lower = value.Trim().ToLowerInvariant();
        return map.Where(p => p.Key.Value == lower).SelectMany(p => p.Value).ToList();
    }

    /// <summary>The number of line values that name an entry (see <see cref="ReferencesTo"/>).</summary>
    public int CountReferences(string list, string value, string? scopeFile = null) =>
        ReferencesTo(list, value, scopeFile).Count;

    /// <summary>
    /// The entries that the lower layer has and the active mod removes: a delete, an <c>ol.delete</c> patch, a whole
    /// file without the entry, or a rename. Only files that the active mod changes can remove an entry. A name that
    /// another file of the list still gives is not removed.
    /// </summary>
    public IReadOnlyList<RemovedEntry> Removed() => RemovedState().Entries;

    /// <summary>The list problems of a section (layer 2 of roadmap section 5.4), with the severities of section 11.1
    /// of the M21 design. Empty when the edit view has no such section.</summary>
    public IReadOnlyList<ListProblem> Problems(SectionRef target)
    {
        if (problemsRevision != session.Revision)
        {
            problems.Clear();
            problemsRevision = session.Revision;
        }
        if (!problems.TryGetValue(target, out var result))
        {
            result = ListChecks.Check(session, this, target);
            problems[target] = result;
        }
        return result;
    }

    /// <summary>The entry of the list that the active mod removes, or null (see <see cref="Removed"/>).</summary>
    /// <param name="scope">The file of a scoped list, or null.</param>
    internal RemovedEntry? RemovedOf(string list, string? scope, string value) =>
        RemovedState().Keys.GetValueOrDefault((list.ToLowerInvariant(), IsScoped(list) ? scope?.ToLowerInvariant() ?? "" : "",
            value.Trim().ToLowerInvariant()));

    /// <summary>The scope file of a rule for a line, or false when the scope names no known system. Null for a list
    /// without a scope.</summary>
    internal bool ScopeOf(ListRule rule, string path, IReadOnlyList<ValueBase>? line, out string? scope)
    {
        scope = null;
        if (rule.SystemValue is >= 0 and var index)
        {
            if (line == null || index >= line.Count || session.SystemPath(line[index].ToString()) is not { } system)
                return false;
            scope = system;
            return true;
        }
        if (rule.Scope == "file" || rule.Lists.Any(IsScoped))
            scope = VfsPath.Normalize(path);
        return true;
    }

    /// <summary>True for a list whose rule gives the file.</summary>
    internal bool IsScoped(string list) => rules.Lists.TryGetValue(list, out var d) && d.Source is { Scoped: true };

    /// <summary>The definitions of the lists whose entries a section of a file can give.</summary>
    internal IEnumerable<ListDefinition> SourcesOf(string path, string sectionType)
    {
        var roles = Files.RolesOf(path);
        var isSystem = session.IsSystemFile(path);
        bool Gives(ListSource s) =>
            (s.Scoped ? isSystem : s.Role != null && roles.Contains(s.Role, StringComparer.OrdinalIgnoreCase)) &&
            s.Values == null && s.Sections.Contains(sectionType, StringComparer.OrdinalIgnoreCase);
        return rules.Lists.Values.Where(d => d.Source is { } s && Gives(s));
    }

    /// <summary>The view of one or more lists in order, for a scope, or null when none is available.</summary>
    private ListView? View(IReadOnlyList<string> ids, string? scope)
    {
        var key = (string.Join("+", ids).ToLowerInvariant(), scope?.ToLowerInvariant());
        if (!views.TryGetValue(key, out var cached))
            views[key] = cached = new CachedView();
        if (cached.Revision == session.Revision)
            return cached.View;
        cached.Revision = session.Revision;
        var paths = new List<string>();
        foreach (var id in ids)
        {
            if (rules.Lists.TryGetValue(id, out var definition) && definition.Source is { } source)
                paths.AddRange(source.Scoped ? scope == null ? [] : [scope] : Files.Paths(source.Role!));
        }
        var states = paths.Select(StateOf).ToArray();
        if (cached.Built && cached.Paths.SequenceEqual(paths, VfsPath.Comparer) &&
            cached.States.Zip(states).All(p => ReferenceEquals(p.First, p.Second)))
            return cached.View;
        cached.Built = true;
        cached.Paths = paths;
        cached.States = states;
        cached.View = Build(ids, scope);
        return cached.View;
    }

    /// <summary>Builds the view of lists in order. The first list with a name gives the entry.</summary>
    private ListView? Build(IReadOnlyList<string> ids, string? scope)
    {
        var definitions = ids.Select(id => rules.Lists.GetValueOrDefault(id)).OfType<ListDefinition>().ToList();
        if (definitions.Count == 0)
            return null;
        var entries = new List<ListEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var available = false;
        var closed = true;
        var collisions = new List<IReadOnlyList<string>>();
        foreach (var definition in definitions)
        {
            var one = BuildOne(definition, scope);
            if (one == null)
                continue;
            available = true;
            closed &= one.Value.Closed;
            collisions.AddRange(one.Value.Collisions);
            foreach (var entry in one.Value.Entries)
            {
                if (seen.Add(entry.Value))
                    entries.Add(entry);
            }
        }
        if (!available)
            return null;
        var first = definitions[0];
        return new ListView(definitions.Count == 1 ? first.Id : string.Join("+", definitions.Select(d => d.Id)),
            definitions.Count == 1 ? first.Label : string.Join(" or ", definitions.Select(d => d.Label)), first.Kind,
            closed, definitions.All(d => d.Enum != null), entries, collisions);
    }

    /// <summary>The entries of one list for a scope, or null when the list is not available.</summary>
    private (IReadOnlyList<ListEntry> Entries, bool Closed, IReadOnlyList<IReadOnlyList<string>> Collisions)? BuildOne(
        ListDefinition definition, string? scope)
    {
        if (definition.Enum != null)
        {
            var type = typeof(FLHash).Assembly.GetType(definition.Enum);
            return type is { IsEnum: true } ? (ListView.Of(type).Entries, true, []) : null;
        }
        if (definition.Source is not { } source)
            return null;
        IReadOnlyList<string> paths = source.Scoped ? scope == null ? [] : [scope] : Files.Paths(source.Role!);
        var order = new List<string>();
        var byName = new Dictionary<string, (ListEntry Entry, int Count)>(StringComparer.OrdinalIgnoreCase);
        var available = false;
        foreach (var path in paths)
        {
            if (TableOf(path) is not { } table)
                continue;
            available = true;
            var types = source.Values is { } values ? [values.Section] : source.Sections;
            foreach (var type in types)
            {
                foreach (var index in table.OfType(type))
                {
                    var section = table.Working[index];
                    if (!Matches(section, source.Where))
                        continue;
                    foreach (var name in Names(section, source))
                    {
                        var entry = Entry(table, index, name, definition);
                        if (byName.TryGetValue(name, out var known))
                        {
                            // The last section with the name gives the entry, as in the engine. A values list keeps the
                            // first section, because each section only shares the value.
                            byName[name] = (source.Values == null ? entry : known.Entry, known.Count + 1);
                        }
                        else
                        {
                            byName[name] = (entry, 1);
                            order.Add(name);
                        }
                    }
                }
            }
        }
        if (!available)
            return null;
        var entries = order.Select(n =>
        {
            var (entry, count) = byName[n];
            return definition.Unique && count > 1
                ? entry with
                {
                    Usable = false,
                    Problem = $"{count} sections have the name '{entry.Value}', so the engine finds none of them.",
                }
                : entry;
        }).ToList();
        var collisions = definition.Hash ? Collisions(order) : [];
        var closed = source.Scoped || source.Role == null || Files.Engine(source.Role);
        return (entries, closed, collisions);
    }

    /// <summary>The entry of a name of a section.</summary>
    private ListEntry Entry(FileTable table, int index, string name, ListDefinition definition)
    {
        var section = table.Working[index];
        var id = table.Ids[index];
        var keyName = SectionKeys.For(section.Name).KeyEntry ?? SectionKeys.DefaultKey;
        var (origin, mod, inW, unsaved) = session.EntryOrigin(table, index, keyName);
        var missing = definition.Requires.Where(k => !section.Any(e => Is(e.Name, k) && e.Count > 0)).ToList();
        return new ListEntry(name, inW ? new SectionRef(table.Path, id) : null, origin, mod, Detail(section, definition))
        {
            Usable = missing.Count == 0,
            Problem = missing.Count == 0 ? null : $"The [{section.Name}] '{name}' has no '{missing[0]}' line.",
            Unsaved = unsaved,
        };
    }

    /// <summary>The detail text of an entry: the first line of each detail key, as "key = values".</summary>
    private static string Detail(Section section, ListDefinition definition)
    {
        var text = new StringBuilder();
        foreach (var key in definition.Detail)
        {
            if (section.FirstOrDefault(e => Is(e.Name, key)) is not { } entry)
                continue;
            if (text.Length > 0)
                text.Append(", ");
            text.Append(entry.Name).Append(" = ").Append(string.Join(", ", entry.Select(v => v.ToString())));
        }
        return text.ToString();
    }

    /// <summary>The groups of distinct names with one FLHash. The engine compares the names without letter
    /// case.</summary>
    private static List<IReadOnlyList<string>> Collisions(IEnumerable<string> names) =>
        names.Where(n => n.Length > 0)
            .GroupBy(FLHash.CreateID)
            .Select(g => (IReadOnlyList<string>)g.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            .Where(g => g.Count > 1)
            .ToList();

    /// <summary>True when the section has each key value of a filter: the first value of the last line of the key,
    /// without letter case.</summary>
    private static bool Matches(Section section, IReadOnlyDictionary<string, string> where)
    {
        foreach (var (key, value) in where)
        {
            if (section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0) is not { } line ||
                !Is(line[0].ToString().Trim(), value))
                return false;
        }
        return true;
    }

    /// <summary>The names that a section gives to a list: the first value of the last line of each name key, or the
    /// value of a values list.</summary>
    internal static IEnumerable<string> Names(Section section, ListSource source)
    {
        if (source.Values is { } values)
        {
            foreach (var line in section.Where(e => Is(e.Name, values.Key)))
            {
                if (line.Count > values.Index && line[values.Index].ToString().Trim() is { Length: > 0 } value)
                    yield return value;
            }
            yield break;
        }
        var keys = source.Keys ?? [SectionKeys.For(section.Name).KeyEntry ?? SectionKeys.DefaultKey];
        foreach (var key in keys)
        {
            if (section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0) is { } line &&
                line[0].ToString().Trim() is { Length: > 0 } name)
                yield return name;
        }
    }

    /// <summary>The sections of a file for the lists: W, or the edit view of a whole file of the active mod. Null when
    /// the edit view has no such INI file.</summary>
    internal FileTable? TableOf(string path)
    {
        if (session.ActiveMod is { } active && active.Files.ContainsKey(path))
            return WholeFile(path);
        return session.Table.File(path);
    }

    /// <summary>The object that changes when the sections of a file for the lists change.</summary>
    private object? StateOf(string path) => TableOf(path);

    /// <summary>A whole file of the active mod as the edit view gives it. The edit view changes only at a save.</summary>
    private FileTable? WholeFile(string path)
    {
        var view = session.EditView;
        if (wholeFiles.TryGetValue(path, out var known) && ReferenceEquals(known.View, view))
            return known.Table;
        var sections = session.EditViewSections(path);
        var table = sections == null ? null : new FileTable(VfsPath.Normalize(path) ?? path, sections);
        wholeFiles[path] = (view, table);
        return table;
    }

    private (IReadOnlyList<RemovedEntry> Entries, Dictionary<(string, string, string), RemovedEntry> Keys) RemovedState()
    {
        if (removed is { } known && known.Revision == session.Revision)
            return (known.Entries, known.Keys);
        var entries = new List<RemovedEntry>();
        if (session.ActiveMod is { } active)
        {
            foreach (var path in session.ActiveChangedPaths())
            {
                if (session.OpenDocument(path) is not { } document || TableOf(document.Path) is not { } now)
                    continue;
                var modPath = active.Patches.GetValueOrDefault(document.Path) ??
                              active.Files.GetValueOrDefault(document.Path) ?? document.Path;
                foreach (var (definition, name, lower) in RemovedPartOf(document, now).Names)
                {
                    // A name that another file of the list still gives is not removed.
                    if (definition.Source!.Scoped || View([definition.Id], null)?.Find(name) == null)
                        entries.Add(new RemovedEntry(definition.Id, name, lower, modPath));
                }
            }
        }
        var keys = new Dictionary<(string, string, string), RemovedEntry>();
        foreach (var e in entries)
            keys.TryAdd((e.List.ToLowerInvariant(), IsScoped(e.List) ? e.LowerSection.Path.ToLowerInvariant() : "",
                e.Value.ToLowerInvariant()), e);
        removed = (session.Revision, entries, keys);
        return (entries, keys);
    }

    /// <summary>
    /// The names of each list that B of a file gives and the sections of the file for the lists do not give. The part is
    /// built again only when one of these states changes, or when the file becomes or stops being a system file. So a
    /// new revision reads again only the files that changed (review finding R7).
    /// </summary>
    private RemovedPart RemovedPartOf(EditDocument document, FileTable now)
    {
        var isSystem = session.IsSystemFile(document.Path);
        if (removedParts.TryGetValue(document.Path, out var part) && ReferenceEquals(part.Now, now) &&
            ReferenceEquals(part.Base, document.Base) && part.IsSystem == isSystem)
            return part;
        var lower = new FileTable(document.Path, document.Base);
        var names = new List<(ListDefinition, string, SectionRef)>();
        foreach (var definition in rules.Lists.Values)
        {
            if (definition.Source is not { Values: null } source || !(source.Scoped
                    ? isSystem
                    : Files.Paths(source.Role!).Contains(document.Path, VfsPath.Comparer)))
                continue;
            var current = NamesOf(now, source);
            foreach (var (name, index) in NamesWithIndex(lower, source))
            {
                if (!current.Contains(name))
                    names.Add((definition, name, lower.Ref(index)));
            }
        }
        part = new RemovedPart(now, document.Base, isSystem, names);
        removedParts[document.Path] = part;
        return part;
    }

    private static HashSet<string> NamesOf(FileTable table, ListSource source) =>
        NamesWithIndex(table, source).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string Name, int Index)> NamesWithIndex(FileTable table, ListSource source)
    {
        foreach (var type in source.Sections)
        {
            foreach (var index in table.OfType(type))
            {
                if (!Matches(table.Working[index], source.Where))
                    continue;
                foreach (var name in Names(table.Working[index], source))
                    yield return (name, index);
            }
        }
    }

    /// <summary>The merged reverse index of a list, built from the parts of its referrer files.</summary>
    private Dictionary<(string? Scope, string Value), List<ListReference>> Merged(string list)
    {
        if (!merged.TryGetValue(list, out var cached))
            merged[list] = cached = new MergedReferences();
        if (cached.Revision == session.Revision)
            return cached.Map;
        cached.Revision = session.Revision;
        var roles = rules.All.Where(r => r.Lists.Contains(list, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Role).Distinct(StringComparer.OrdinalIgnoreCase);
        var paths = roles.SelectMany(Files.Paths).Distinct(VfsPath.Comparer).ToList();
        var fileParts = paths.Select(Part).ToArray();
        if (cached.Parts != null && cached.Parts.Length == fileParts.Length &&
            cached.Parts.Zip(fileParts).All(p => ReferenceEquals(p.First, p.Second)))
            return cached.Map;
        cached.Parts = fileParts;
        var map = new Dictionary<(string?, string), List<ListReference>>();
        var lower = list.ToLowerInvariant();
        foreach (var part in fileParts.OfType<ReferencePart>())
        {
            foreach (var ((id, scope, value), references) in part.Map)
            {
                if (id != lower)
                    continue;
                if (!map.TryGetValue((scope, value), out var all))
                    map[(scope, value)] = all = [];
                all.AddRange(references);
            }
        }
        cached.Map = map;
        return map;
    }

    /// <summary>The reverse index part of one referrer file, built again only when W of the file changes, or when W of
    /// universe.ini changes for a file with <c>systemOf</c> references. A whole file of the active mod gives the
    /// lines of the edit view, as for the lists. A reference of such a file names a section that W does not have, so
    /// it gives no check result: the editor cannot change a whole file (review finding R22).</summary>
    private ReferencePart? Part(string path)
    {
        var table = TableOf(path);
        if (table == null)
        {
            parts.Remove(path);
            return null;
        }
        var universe = session.UniversePath is { } u ? session.Table.File(u) : null;
        if (parts.TryGetValue(path, out var part) && ReferenceEquals(part.Table, table) &&
            (!part.UsesSystems || ReferenceEquals(part.Universe, universe)))
            return part;
        part = BuildPart(table, universe);
        parts[path] = part;
        return part;
    }

    private ReferencePart BuildPart(FileTable table, FileTable? universe)
    {
        var map = new Dictionary<(string, string?, string), List<ListReference>>();
        var usesSystems = false;
        var roles = Files.RolesOf(table.Path);
        for (var i = 0; i < table.Working.Count; i++)
        {
            var section = table.Working[i];
            var byKey = roles.SelectMany(r => rules.ByRoleAndSection.GetValueOrDefault((r.ToLowerInvariant(),
                    section.Name.ToLowerInvariant())) ?? [])
                .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            if (byKey.Count == 0)
                continue;
            var target = table.Ref(i);
            var lineOfKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in section)
            {
                if (!byKey.TryGetValue(entry.Name, out var keyRules))
                    continue;
                var line = lineOfKey.GetValueOrDefault(entry.Name);
                lineOfKey[entry.Name] = line + 1;
                foreach (var rule in keyRules)
                {
                    if (rule.Index >= entry.Count || entry[rule.Index].ToString().Trim() is not { Length: > 0 } value)
                        continue;
                    usesSystems |= rule.SystemValue >= 0;
                    if (!ScopeOf(rule, table.Path, entry.ToList(), out var scope))
                        continue;
                    var reference = new ListReference(target, entry.Name, line, rule.Index, rule);
                    foreach (var list in rule.Lists)
                    {
                        var key = (list.ToLowerInvariant(), scope?.ToLowerInvariant(), value.ToLowerInvariant());
                        if (!map.TryGetValue(key, out var all))
                            map[key] = all = [];
                        all.Add(reference);
                    }
                }
            }
        }
        return new ReferencePart(table, universe, usesSystems, map);
    }

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A cached view and the states of its files when it was built.</summary>
    private sealed class CachedView
    {
        public int Revision = int.MinValue;
        public bool Built;
        public List<string> Paths = [];
        public object?[] States = [];
        public ListView? View;
    }

    private sealed class MergedReferences
    {
        public int Revision = int.MinValue;
        public ReferencePart?[]? Parts;
        public Dictionary<(string? Scope, string Value), List<ListReference>> Map = [];
    }

    /// <param name="Now">The sections of the file for the lists when the part was built.</param>
    /// <param name="Base">B of the file when the part was built.</param>
    /// <param name="IsSystem">True when the file was a system file when the part was built.</param>
    /// <param name="Names">The names that B gives and <paramref name="Now"/> does not, with their lists and their
    /// sections in B.</param>
    private sealed record RemovedPart(FileTable Now, IReadOnlyList<Section> Base, bool IsSystem,
        IReadOnlyList<(ListDefinition Definition, string Name, SectionRef Lower)> Names);

    /// <param name="Universe">The table of universe.ini when the part was built.</param>
    /// <param name="UsesSystems">True when a line of the file has a <c>systemOf</c> scope, so the part depends on
    /// universe.ini.</param>
    private sealed record ReferencePart(FileTable Table, FileTable? Universe, bool UsesSystems,
        Dictionary<(string List, string? Scope, string Value), List<ListReference>> Map);
}

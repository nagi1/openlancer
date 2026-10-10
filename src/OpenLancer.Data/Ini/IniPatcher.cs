using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Ini;

/// <summary>
/// Applies the sections of a mod patch file to a parsed INI file.
/// </summary>
/// <remarks>
/// Each patch section names its target with its key entry (see <see cref="SectionKeys"/>). Then:
/// <list type="bullet">
/// <item>The patch's entries replace all target entries with the same names. They are written as one block, in
/// patch order, at the position of the first replaced entry, or at the end when no entry is replaced. So lines
/// that belong together, such as <c>encounter</c> and its <c>faction</c> lines, stay in order.</item>
/// <item><c>ol.remove = name</c> removes all target entries with that name.</item>
/// <item><c>ol.delete = true</c> removes the target section, and its child sections.</item>
/// <item><c>ol.new = true</c> adds the section. No section with that key may exist.</item>
/// <item>A new child section (see <see cref="SectionKeys.IsChild"/>) needs <c>ol.parent = &lt;parent key&gt;</c>.
/// Child sections that directly follow a new parent section in the patch belong to that parent.</item>
/// </list>
/// Directives use the <c>ol.</c> prefix because the upstream text parser drops lines that start with <c>@</c>.
/// String values <c>$str:key</c> and <c>$ifc:key</c> become the resource ids of the mod's text.
/// <para>The merge never changes a section or an entry of the target in place. It makes new sections and puts them
/// into the section list. So a copy of the section list keeps the original sections unchanged.</para>
/// </remarks>
internal static class IniPatcher
{
    /// <summary>Entry names with this prefix are directives. Content entries cannot use it.</summary>
    internal const string DirectivePrefix = "ol.";
    internal const string DirectiveNew = "ol.new";
    internal const string DirectiveDelete = "ol.delete";
    internal const string DirectiveRemove = "ol.remove";
    internal const string DirectiveParent = "ol.parent";

    public static void Apply(IniDocument target, IReadOnlyList<Section> patch, PatchContext context)
    {
        Section? newParent = null;
        foreach (var section in patch)
        {
            var parsed = Parse(section, context);
            if (parsed == null)
            {
                newParent = null;
                continue;
            }
            if (newParent != null && JoinsNewParent(section, parsed))
            {
                InsertChild(target, target.Sections.IndexOf(newParent), section, parsed.Content, context);
                continue;
            }
            newParent = ApplySection(target, section, parsed, context);
        }
    }

    /// <summary>
    /// What the merge does with each section of a patch: it adds a section, deletes its target, or changes its
    /// target. For a patch that has an error, the result can differ from the merge.
    /// </summary>
    internal static PatchAction[] Actions(IReadOnlyList<Section> patch)
    {
        var result = new PatchAction[patch.Count];
        var afterNewParent = false;
        for (var i = 0; i < patch.Count; i++)
        {
            var parsed = Parse(patch[i], null);
            if (parsed == null)
            {
                afterNewParent = false;
                continue;
            }
            if (afterNewParent && JoinsNewParent(patch[i], parsed))
            {
                result[i] = PatchAction.Add;
                continue;
            }
            var append = SectionKeys.For(patch[i].Name).Kind == SectionMatchKind.Append;
            result[i] = parsed.IsNew || append ? PatchAction.Add : parsed.IsDelete ? PatchAction.Delete : PatchAction.Change;
            // The same rule as ApplySection: a new section that is not a child and not appended becomes a parent.
            afterNewParent = parsed.IsNew && !append && !SectionKeys.IsChild(patch[i].Name) && parsed.ParentKey == null;
        }
        return result;
    }

    /// <summary>True when a child section after a new parent section belongs to that parent.</summary>
    private static bool JoinsNewParent(Section section, PatchDirectives p) =>
        SectionKeys.IsChild(section.Name) && !p.IsDelete && p.RemoveNames.Count == 0 && p.ParentKey == null;

    /// <summary>Resolves the text placeholders in every value of a whole mod file.</summary>
    public static void ResolvePlaceholders(IniDocument document, PatchContext context)
    {
        for (var i = 0; i < document.Sections.Count; i++)
        {
            var s = document.Sections[i];
            document.Sections[i] = Build(s.Name, s.File, s.Line, s.ToList(), context, document.IsBinary);
        }
    }

    /// <summary>True when any value of the document is a text placeholder.</summary>
    public static bool HasPlaceholders(IniDocument document) =>
        document.Sections.SelectMany(s => s).SelectMany(e => e)
            .Any(v => StringPlaceholder.TryParse(v.ToString(), out _));

    /// <summary>Applies one section. Returns the section that it added when it is a new parent section.</summary>
    private static Section? ApplySection(IniDocument target, Section patch, PatchDirectives p, PatchContext context)
    {
        var match = SectionKeys.For(patch.Name);
        var isChild = SectionKeys.IsChild(patch.Name);
        if (p.ParentKey != null && !(p.IsNew && isChild))
        {
            context.Error($"[{patch.Name}]: {DirectiveParent} is only for a new child section.");
            return null;
        }
        if (p.IsNew && isChild)
        {
            AddChild(target, patch, p, context);
            return null;
        }

        if (match.Kind == SectionMatchKind.Append)
        {
            if (p.IsDelete || p.RemoveNames.Count > 0)
                context.Error($"[{patch.Name}] sections can only be added. {DirectiveDelete} and {DirectiveRemove} are not allowed.");
            else
            {
                target.Sections.Add(Build(patch.Name, patch.File, patch.Line, p.Content, context, target.IsBinary));
                context.RecordAdd(SectionIdentity.Of(target.Sections, target.Sections.Count - 1));
            }
            return null;
        }

        string? keyValue = null;
        if (match.Kind == SectionMatchKind.Keyed)
        {
            var keyEntries = p.Content.Where(e => Is(e, match.KeyEntry!)).ToList();
            if (keyEntries.Count != 1 || keyEntries[0].Count != 1)
            {
                context.Error($"[{patch.Name}] needs exactly one '{match.KeyEntry}' entry with one value to find its target.");
                return null;
            }
            keyValue = keyEntries[0][0].ToString();
        }

        var identity = new SectionIdentity(patch.Name, keyValue);
        var label = identity.ToString();
        var candidates = target.Sections
            .Select((section, index) => (section, index))
            .Where(c => c.section.Name.Equals(patch.Name, StringComparison.OrdinalIgnoreCase) &&
                        (keyValue == null || KeyEquals(c.section, match.KeyEntry!, keyValue)))
            .ToList();

        if (p.IsNew)
        {
            if (candidates.Count > 0)
            {
                context.Error($"{label}: {DirectiveNew} is set, but the section already exists.");
                return null;
            }
            var added = Build(patch.Name, patch.File, patch.Line, p.Content, context, target.IsBinary);
            target.Sections.Add(added);
            context.RecordAdd(identity);
            return added;
        }
        if (candidates.Count == 0)
        {
            context.Error($"{label}: no section to patch in {target.Path}. Set {DirectiveNew} = true to add it.");
            return null;
        }
        if (candidates.Count > 1)
        {
            context.Error($"{label}: matches {candidates.Count} sections in {target.Path}. Use a whole-file override.");
            return null;
        }

        var (original, position) = candidates[0];
        // The identity of a child section also names its parent (see SectionIdentity).
        if (isChild)
            identity = SectionIdentity.Of(target.Sections, position);
        if (p.IsDelete)
        {
            if (p.RemoveNames.Count > 0 || p.Content.Any(e => keyValue == null || !Is(e, match.KeyEntry!)))
            {
                context.Error($"{label}: {DirectiveDelete} cannot be used with other entries.");
                return null;
            }
            var children = isChild ? 0 : SectionKeys.ChildCount(target.Sections, position);
            target.Sections.RemoveRange(position, 1 + children);
            context.Record(identity, null);
            return null;
        }

        var changes = p.Content.Where(e => keyValue == null || !Is(e, match.KeyEntry!)).ToList();
        target.Sections[position] = Modify(original, changes, p.RemoveNames, identity, context, target.IsBinary);
        return null;
    }

    private static void AddChild(IniDocument target, Section patch, PatchDirectives p, PatchContext context)
    {
        if (p.ParentKey == null)
        {
            context.Error($"A new [{patch.Name}] needs '{DirectiveParent} = <parent key>', or must follow a new parent section in the patch.");
            return;
        }
        var parents = target.Sections
            .Select((section, index) => (section, index))
            .Where(c => !SectionKeys.IsChild(c.section.Name) &&
                        string.Equals(KeyOf(c.section), p.ParentKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (parents.Count != 1)
        {
            context.Error($"[{patch.Name}] {DirectiveParent} = {p.ParentKey}: {parents.Count} sections in {target.Path} have that key.");
            return;
        }
        InsertChild(target, parents[0].index, patch, p.Content, context);
    }

    /// <summary>
    /// Adds a new child section after the child sections of the parent at <paramref name="parentIndex"/>.
    /// A child with a key must not repeat the key of another child of the same type under that parent.
    /// </summary>
    private static void InsertChild(IniDocument target, int parentIndex, Section patch, List<Entry> content,
        PatchContext context)
    {
        var child = Build(patch.Name, patch.File, patch.Line, content, context, target.IsBinary);
        var key = KeyOf(child);
        var siblings = target.Sections.Skip(parentIndex + 1).Take(SectionKeys.ChildCount(target.Sections, parentIndex));
        if (key != null && siblings.Any(s => s.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(KeyOf(s), key, StringComparison.OrdinalIgnoreCase)))
        {
            context.Error($"[{child.Name}] {SectionKeys.For(child.Name).KeyEntry} = {key}: {DirectiveNew} is set, " +
                          $"but its parent {KeyOf(target.Sections[parentIndex])} already has this section.");
            return;
        }
        var index = parentIndex + 1 + SectionKeys.ChildCount(target.Sections, parentIndex);
        target.Sections.Insert(index, child);
        context.RecordAdd(SectionIdentity.Of(target.Sections, index));
    }

    private static Section Modify(Section original, List<Entry> changes, List<string> removeNames,
        SectionIdentity identity, PatchContext context, bool binary)
    {
        var label = identity.ToString();
        var replaced = new HashSet<string>(changes.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(removeNames, StringComparer.OrdinalIgnoreCase);

        foreach (var group in SectionKeys.LinkedGroups(original.Name))
        {
            var touched = group.Where(n => replaced.Contains(n) || removed.Contains(n)).ToList();
            var left = group.Where(n => !touched.Contains(n, StringComparer.OrdinalIgnoreCase) &&
                                        original.Any(e => Is(e, n))).ToList();
            if (touched.Count > 0 && left.Count > 0)
            {
                context.Error($"{label}: the {string.Join(", ", group)} lines belong together. The patch changes " +
                              $"{string.Join(", ", touched)}, so it must also list {string.Join(", ", left)}.");
                return original;
            }
        }

        // The original lines of each name, to keep the value types of a binary file. The n-th patch line of a name
        // replaces the n-th original line of that name.
        var originalLines = original.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Entry CopyChange(Section into, Entry e)
        {
            var n = occurrence.GetValueOrDefault(e.Name);
            occurrence[e.Name] = n + 1;
            var replacedLine = originalLines.TryGetValue(e.Name, out var lines) && n < lines.Count ? lines[n] : null;
            return Copy(into, e, context, binary, replacedLine);
        }

        foreach (var name in removed)
        {
            if (!original.Any(e => Is(e, name)))
                context.Warning($"{label}: {DirectiveRemove} '{name}' has no entry to remove.");
            if (replaced.Contains(name))
                context.Warning($"{label}: '{name}' is in {DirectiveRemove} and is also set. It is set.");
            context.Record(identity, name);
        }

        var result = new Section(original.Name) { File = original.File, Line = original.Line };
        var blockWritten = false;
        foreach (var entry in original)
        {
            if (replaced.Contains(entry.Name))
            {
                if (!blockWritten)
                {
                    foreach (var e in changes)
                        result.Add(CopyChange(result, e));
                    blockWritten = true;
                }
                continue;
            }
            if (!removed.Contains(entry.Name))
                result.Add(Copy(result, entry, null, binary, null));
        }
        if (!blockWritten)
        {
            foreach (var e in changes)
                result.Add(CopyChange(result, e));
        }
        foreach (var name in replaced)
            context.Record(identity, name);
        return result;
    }

    /// <summary>The directives and the content entries of a patch section, or null after an error.</summary>
    /// <param name="context">Gets the error, or null.</param>
    private static PatchDirectives? Parse(Section patch, PatchContext? context)
    {
        var p = new PatchDirectives();
        foreach (var entry in patch)
        {
            if (Is(entry, DirectiveNew))
                p.IsNew = IsTrue(entry);
            else if (Is(entry, DirectiveDelete))
                p.IsDelete = IsTrue(entry);
            else if (Is(entry, DirectiveRemove))
                p.RemoveNames.AddRange(entry.Select(v => v.ToString()));
            else if (Is(entry, DirectiveParent))
                p.ParentKey = entry.Count == 1 ? entry[0].ToString() : null;
            else if (entry.Name.StartsWith(DirectivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                context?.Error($"[{patch.Name}]: unknown directive '{entry.Name}'.");
                return null;
            }
            else
                p.Content.Add(entry);
        }
        return p;
    }

    /// <summary>
    /// The key that <c>ol.parent</c> compares: the first value of the first key entry with a value, or null.
    /// </summary>
    internal static string? KeyOf(Section section)
    {
        var match = SectionKeys.For(section.Name);
        if (match.Kind != SectionMatchKind.Keyed)
            return null;
        var entry = section.FirstOrDefault(e => Is(e, match.KeyEntry!));
        return entry is { Count: > 0 } ? entry[0].ToString() : null;
    }

    private static Section Build(string name, string? file, int line, List<Entry> content, PatchContext context,
        bool binary)
    {
        var section = new Section(name) { File = file, Line = line };
        foreach (var entry in content)
            section.Add(Copy(section, entry, context, binary, null));
        return section;
    }

    /// <summary>
    /// Copies an entry into <paramref name="section"/>. With a context, string placeholders are resolved, and for a
    /// binary file each value gets a type that the file can store exactly (see <see cref="BinaryValueTypes.Store"/>).
    /// </summary>
    /// <param name="replaced">The target line that this entry replaces, to keep its value types, or null.</param>
    internal static Entry Copy(Section section, Entry source, PatchContext? context, bool binary, Entry? replaced)
    {
        var count = source.Count;
        if (context != null && binary && count > BinaryValueTypes.MaxValuesPerLine)
        {
            context.Error($"'{source.Name}': a line of a binary INI file holds at most {BinaryValueTypes.MaxValuesPerLine} " +
                          $"values, not {count}. The merge keeps the first {BinaryValueTypes.MaxValuesPerLine}.");
            count = BinaryValueTypes.MaxValuesPerLine;
        }
        var copy = new Entry(section, source.Name, count) { Line = source.Line };
        for (var i = 0; i < count; i++)
        {
            var value = source[i];
            if (context != null && StringPlaceholder.TryParse(value.ToString(), out var placeholder) &&
                context.ResolveString(placeholder) is { } id)
                copy.Add(new Int32Value(id) { Entry = copy, Line = value.Line });
            else if (context != null && binary)
                copy.Add(StoreBinary(source, i, replaced, copy, context));
            else
                copy.Add(value);
        }
        return copy;
    }

    /// <summary>
    /// Applies the binary type rules to one value (see <see cref="BinaryValueTypes.Store"/>). Reports a large integer
    /// that the file cannot store, and a large integer that the file stores as a float with other digits.
    /// </summary>
    private static ValueBase StoreBinary(Entry source, int index, Entry? replaced, Entry copy, PatchContext context)
    {
        var value = source[index];
        var stored = BinaryValueTypes.Store(source, index, replaced, copy);
        if (stored == null)
        {
            var number = value.ToInt64();
            context.Error($"'{source.Name} = {number}': a binary INI file cannot store this value exactly. " +
                          $"Write {number}.0 for a number{HashHint(number)}.");
            return value;
        }
        if (stored is SingleValue single && BinaryValueTypes.IsLargeInteger(value, out var large) &&
            !BinaryValueTypes.HoldsExactly(single, large))
        {
            var kept = ((double)(float)single).ToString("F0", CultureInfo.InvariantCulture);
            context.Warning($"'{source.Name} = {large}': the value replaces a float, and a float cannot hold this " +
                            $"number. The file stores {kept}. Write {kept}.0 for that number{HashHint(large)}.");
        }
        return stored;
    }

    /// <summary>
    /// The hint that names the signed form of a hash. A binary file stores the signed form as the same 32 bits. The
    /// hint is empty for a number outside the hash range.
    /// </summary>
    private static string HashHint(long number) =>
        BinaryValueTypes.IsUnsignedHash(number) ? $", or {unchecked((int)number)} for a hash" : "";

    private static bool KeyEquals(Section section, string keyEntry, string keyValue) =>
        section.Any(e => Is(e, keyEntry) && e.Count > 0 &&
                         e[0].ToString().Equals(keyValue, StringComparison.OrdinalIgnoreCase));

    private static bool Is(Entry entry, string name) => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(Entry entry) => entry.Count == 1 && entry[0].TryToBoolean(out var b) && b;

    private sealed class PatchDirectives
    {
        public bool IsNew;
        public bool IsDelete;
        public string? ParentKey;
        public readonly List<string> RemoveNames = [];
        public readonly List<Entry> Content = [];
    }
}

/// <summary>What the merge does with one section of a patch (see <see cref="IniPatcher.Actions"/>).</summary>
internal enum PatchAction
{
    /// <summary>The section changes its target.</summary>
    Change,

    /// <summary>The section adds a new section.</summary>
    Add,

    /// <summary>The section deletes its target.</summary>
    Delete,
}

/// <summary>
/// The state that one patch file needs: its mod, the string ids, the conflict ledger and the diagnostics.
/// </summary>
/// <param name="stringIds">The text ids, or null where text placeholders are not allowed.</param>
/// <param name="keepPlaceholders">True to keep the text placeholders as strings, with no error. The editor builds
/// its working copy of a file this way, because the text ids of a mod change when its strings change.</param>
internal sealed class PatchContext(
    string modId,
    string sourceFile,
    string targetPath,
    StringIds? stringIds,
    PatchLedger ledger,
    List<Diagnostic> diagnostics,
    bool keepPlaceholders = false)
{
    public void Error(string message) => diagnostics.Add(Diagnostic.Error(sourceFile, message));

    public void Warning(string message) => diagnostics.Add(Diagnostic.Warning(sourceFile, message));

    /// <summary>The text id of a placeholder, or null when the placeholder stays a string.</summary>
    public int? ResolveString(StringPlaceholder placeholder)
    {
        if (keepPlaceholders)
            return null;
        if (stringIds == null)
        {
            Error($"'{placeholder}': text placeholders cannot be used in {targetPath}.");
            return null;
        }
        if (stringIds.TryGet(modId, placeholder, out var id))
            return id;
        Error($"'{placeholder}' is not in {modId}'s strings.json.");
        return null;
    }

    /// <summary>Records that this mod changes an entry, or deletes the section when the name is null.</summary>
    public void Record(SectionIdentity section, string? entryName)
    {
        var previous = ledger.Record(targetPath, section, entryName, modId);
        if (previous != null && previous != modId)
            Warning($"{section}: '{entryName ?? "(section)"}' was also changed by mod '{previous}'. This mod loads later and wins.");
    }

    /// <summary>Records that this mod adds a section.</summary>
    public void RecordAdd(SectionIdentity section) => ledger.RecordAdd(targetPath, section, modId);
}

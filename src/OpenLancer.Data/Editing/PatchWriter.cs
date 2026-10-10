using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>One section of a patch file, as lines without line ends.</summary>
internal sealed record PatchSection(SectionIdentity Id, IReadOnlyList<string> Lines);

/// <summary>
/// The patch that <see cref="PatchWriter"/> writes for one file: its sections, or the problems that stop the save of
/// the file. A file without sections needs no patch.
/// </summary>
/// <param name="Source">The loaded patch of the file, or null. The file keeps its header, its footer and its
/// encoding.</param>
internal sealed record PatchText(
    IReadOnlyList<PatchSection> Sections,
    IReadOnlyList<Diagnostic> Problems,
    PatchSource? Source = null)
{
    public bool Succeeded => Problems.Count == 0;

    /// <summary>
    /// The text of the patch file: the header of the loaded patch, the sections, and the footer of the loaded patch.
    /// A blank line comes between two sections, unless the first section ends with a blank line. Each line ends with
    /// CRLF. A line with the text of the loaded patch can hold characters outside ASCII, for example in its comment.
    /// The text is empty when there are no sections.
    /// </summary>
    public string Text => Sections.Count == 0 ? "" : string.Concat(FileLines().Select(l => l + "\r\n"));

    /// <summary>
    /// The bytes of <see cref="Text"/> in the encoding of the loaded patch, after its byte order mark. So a line with
    /// the text of the loaded patch gets back the bytes that the loaded file holds. Without a loaded patch, the
    /// encoding is code page 1252, the code page of text INI files.
    /// </summary>
    public byte[] ToBytes()
    {
        if (Sections.Count == 0)
            return [];
        var encoding = Source?.Encoding ?? IniDocument.TextEncoding;
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(Text)];
    }

    private List<string> FileLines()
    {
        var lines = new List<string>(Source?.Header ?? []);
        for (var i = 0; i < Sections.Count; i++)
        {
            if (i > 0 && !PatchSource.IsBlank(lines[^1]))
                lines.Add("");
            lines.AddRange(Sections[i].Lines);
        }
        lines.AddRange(Source?.Footer ?? []);
        return lines;
    }
}

/// <summary>
/// Writes the patch that changes B, the document that the merge gives to the active mod's patch, into W, the
/// working copy of the editor.
/// </summary>
/// <remarks>
/// <para>Sections compare by <see cref="SectionIdentity"/>. The patch first has the changed and deleted sections in
/// the order of B, then the new sections in the order of W. So two saves of similar edits give similar text.</para>
/// <list type="bullet">
/// <item>Changed: the key line, then the lines of each name that changed, in the order of W, then
/// <c>ol.remove</c> with the names that W does not have. A name of a linked group adds the whole group. In an
/// order-sensitive section, a change writes all lines. A pinned name (a name that the loaded patch sets or removes,
/// and that no edit touched) is always written, so the mod keeps it when a lower mod changes.</item>
/// <item>Deleted: the key line and <c>ol.delete</c>. The merge also deletes the child sections.</item>
/// <item>New: <c>ol.new</c> and all lines. A new child of a section in B gets <c>ol.parent</c>. The children of
/// a new section follow it with no directive. A new <c>[BaseGood]</c> needs no directive.</item>
/// <item>Moved: a section of B at a position in W that the merge cannot keep is deleted and added again. So is a
/// section that an edit or the loaded patch deleted and added again with the same key, when no kept section comes
/// after it. See <c>Plan.Moved</c>.</item>
/// </list>
/// <para>A patch section finds its target by its key in the whole file. So the writer refuses to change or delete a
/// section without a key, a section whose key another section of its type also has, and a <c>[BaseGood]</c> of
/// B.</para>
/// <para>The writer keeps the lines of the loaded patch that no edit touched (see <see cref="PatchSectionBuilder"/>
/// and <see cref="PatchText"/>). It finds the loaded sections of a section by its type and key. A new section
/// without a key matches by its position among the new sections of its type. A loaded section that changes nothing
/// stays, so its comment lines stay.</para>
/// <para>Then the writer checks its own output: it applies the patch to a copy of B and compares the result with W
/// (see <see cref="SectionComparer.Equivalent"/>). When a section fails, the writer writes it again: all its lines,
/// and none with the text of the loaded patch. Then it checks again. When a section fails again, the writer reports
/// an error and gives no patch for the file, because a partial patch can lose an edit without a message.</para>
/// </remarks>
internal static class PatchWriter
{
    /// <summary>The value of <c>ol.new</c> and <c>ol.delete</c>.</summary>
    private static readonly string[] TrueValue = ["true"];

    /// <param name="path">The install path of the file, for messages.</param>
    /// <param name="b">The document that the merge gives to the patch.</param>
    /// <param name="w">The sections of the working copy.</param>
    /// <param name="pins">The pinned names of each section.</param>
    /// <param name="source">The loaded patch of the file, or null.</param>
    /// <param name="deleted">The sections that the loaded patch or an edit deleted, or null for none. When W has such
    /// a section of B again, the patch can delete it and add it again.</param>
    public static PatchText Write(string path, IniDocument b, IReadOnlyList<Section> w,
        Func<SectionIdentity, IReadOnlyCollection<string>> pins, PatchSource? source = null,
        IReadOnlySet<SectionIdentity>? deleted = null)
    {
        var plan = new Plan(path, b, w, pins, source, deleted ?? new HashSet<SectionIdentity>());
        var rewritten = new HashSet<SectionIdentity>();
        for (var pass = 1; ; pass++)
        {
            var sections = plan.Sections(rewritten);
            if (plan.Problems.Count > 0)
                return new PatchText([], plan.Problems);
            var patch = new PatchText(sections, [], source);
            var failures = plan.Check(patch);
            if (failures.Count == 0)
                return patch;
            if (pass == 2 || failures.Any(f => !f.CanRewrite))
            {
                foreach (var failure in failures)
                    plan.Problem(failure.Id, "the patch does not give the edited section: " + failure.Message);
                return new PatchText([], plan.Problems);
            }
            rewritten.UnionWith(failures.Select(f => f.Id!));
        }
    }

    /// <summary>
    /// Null when a patch can find a section of B in B, and in W when the section is kept. Otherwise the reason.
    /// </summary>
    /// <param name="section">The section of B.</param>
    /// <param name="kept">True when W has the section too.</param>
    /// <param name="bCounts">The key counts of B.</param>
    /// <param name="wCounts">The key counts of W.</param>
    internal static string? NotPatchable(Section section, bool kept, KeyCounts bCounts, KeyCounts wCounts)
    {
        var match = SectionKeys.For(section.Name);
        switch (match.Kind)
        {
            case SectionMatchKind.Append:
                return $"the merge only adds [{section.Name}] sections. Add a new section.";
            case SectionMatchKind.Singleton:
                return bCounts.Types(section.Name) == 1 && (!kept || wCounts.Types(section.Name) == 1)
                    ? null
                    : $"the file has more than one [{section.Name}] section.";
            default:
                var key = SectionIdentity.KeyOf(section);
                if (key == null)
                    return $"it has no single '{match.KeyEntry}' entry with one value.";
                return bCounts.Keys(section.Name, key) == 1 && (!kept || wCounts.Keys(section.Name, key) == 1)
                    ? null
                    : $"another [{section.Name}] section also has {match.KeyEntry} = {key}.";
        }
    }

    /// <summary>
    /// Null when a patch can change a section of B and keep the order of its lines. Otherwise the reason.
    /// </summary>
    /// <remarks>
    /// <para>A change of an order-sensitive section writes all lines except the key line. The merge puts these lines
    /// as one block at the place of the first line that they replace, and the key line stays in its place. So the
    /// order stays only when the key line is the first line.</para>
    /// <para>The editor takes no edit of such a section (see <see cref="EditDocument.ReadOnlyReasonAt"/>). The writer
    /// does not refuse the section, because it also writes the pinned lines of a loaded patch. The self-check then
    /// reports the error.</para>
    /// </remarks>
    internal static string? OrderNotKept(Section section)
    {
        if (!SectionKeys.IsOrderSensitive(section.Name) ||
            SectionKeys.For(section.Name) is not { Kind: SectionMatchKind.Keyed } match ||
            section.Count == 0 || section[0].Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase))
            return null;
        return $"the engine reads its lines in order, and a patch keeps that order only when the '{match.KeyEntry}' " +
               "line is the first line.";
    }

    /// <param name="CanRewrite">True when the writer can write the section in another way: all its lines for a
    /// section in B and W, or no line with the text of the loaded patch.</param>
    private sealed record Failure(SectionIdentity? Id, string Message, bool CanRewrite);

    private sealed class Plan
    {
        private readonly string path;
        private readonly IniDocument b;
        private readonly IReadOnlyList<Section> w;
        private readonly Func<SectionIdentity, IReadOnlyCollection<string>> pins;
        private readonly PatchSource? source;
        private readonly SectionIdentity[] bIds;
        private readonly SectionIdentity[] wIds;
        private readonly Dictionary<SectionIdentity, int> bIndex;
        private readonly Dictionary<SectionIdentity, int> wIndex;
        private readonly Lazy<KeyCounts> bCounts;
        private readonly Lazy<KeyCounts> wCounts;

        /// <summary>The sections of B that the patch deletes and adds again (see <see cref="Moved"/>).</summary>
        private readonly HashSet<SectionIdentity> moved;

        /// <summary>The sections of the last <see cref="Sections"/> call with a line of the loaded patch.</summary>
        private readonly HashSet<SectionIdentity> loaded = [];

        public Plan(string path, IniDocument b, IReadOnlyList<Section> w,
            Func<SectionIdentity, IReadOnlyCollection<string>> pins, PatchSource? source, IReadOnlySet<SectionIdentity> deleted)
        {
            this.path = path;
            this.b = b;
            this.w = w;
            this.pins = pins;
            this.source = source;
            bIds = SectionIdentity.Identify(b.Sections);
            wIds = SectionIdentity.Identify(w);
            bIndex = Index(bIds);
            wIndex = Index(wIds);
            bCounts = new(() => new KeyCounts(b.Sections));
            wCounts = new(() => new KeyCounts(w));
            moved = Moved(deleted);
        }

        public List<Diagnostic> Problems { get; } = [];

        private bool Binary => b.IsBinary;

        /// <summary>The patch sections. Adds a problem for each section that cannot be written.</summary>
        /// <param name="rewritten">The sections that get all their lines, and no line of the loaded patch.</param>
        public List<PatchSection> Sections(IReadOnlySet<SectionIdentity> rewritten)
        {
            Problems.Clear();
            loaded.Clear();
            var result = new List<PatchSection>();
            var parentDeleted = false;
            for (var i = 0; i < b.Sections.Count; i++)
            {
                var isChild = SectionKeys.IsChild(b.Sections[i].Name);
                var kept = IsKept(bIds[i]);
                if (kept)
                    Add(result, Changed(i, wIndex[bIds[i]], rewritten.Contains(bIds[i])));
                // The merge deletes the children of a deleted section with it.
                else if (!(isChild && parentDeleted))
                    Add(result, Deleted(i));
                if (!isChild)
                    parentDeleted = !kept;
            }
            // The n-th new section with a type and key gets the n-th loaded section that adds a section with them.
            var ordinals = new Dictionary<SectionIdentity, int>();
            for (var j = 0; j < w.Count; j++)
            {
                if (IsKept(wIds[j]))
                    continue;
                var typeAndKey = new SectionIdentity(w[j].Name, wIds[j].Key);
                var ordinal = ordinals.GetValueOrDefault(typeAndKey);
                ordinals[typeAndKey] = ordinal + 1;
                Add(result, New(j, source?.Added(w[j].Name, wIds[j].Key, ordinal), keepText: !rewritten.Contains(wIds[j])));
            }
            return result;
        }

        /// <summary>Applies the patch to a copy of B and compares the result with W.</summary>
        public List<Failure> Check(PatchText patch)
        {
            // A copy of the section list is enough: the merge never changes a section in place.
            var result = b.WithSections(b.Sections);
            var diagnostics = new List<Diagnostic>();
            IniPatcher.Apply(result, IniDocument.Parse(path, patch.ToBytes()).Sections,
                new PatchContext("editor", path, path, null, new PatchLedger(), diagnostics, keepPlaceholders: true));
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                return errors.Select(e => new Failure(null, "the merge reports: " + e.Message, false)).ToList();

            var ids = SectionIdentity.Identify(result.Sections);
            for (var k = 0; k < Math.Min(ids.Length, wIds.Length); k++)
            {
                if (!ids[k].Equals(wIds[k]))
                    return [new Failure(wIds[k], $"the merge gives {ids[k]} at its position.", false)];
            }
            if (ids.Length != wIds.Length)
                return [new Failure(null, $"the merge gives {ids.Length} sections, not {wIds.Length}.", false)];

            var failures = new List<Failure>();
            for (var k = 0; k < ids.Length; k++)
            {
                if (!SectionComparer.Equivalent(result.Sections[k], w[k], Binary))
                    failures.Add(new Failure(wIds[k], "the merge gives other lines.", IsKept(wIds[k]) || loaded.Contains(wIds[k])));
            }
            return failures;
        }

        public PatchSection? Problem(SectionIdentity? id, string message)
        {
            Problems.Add(Diagnostic.Error(path, id == null ? message : $"{id}: {message}") with
            {
                Target = id == null ? null : new SectionRef(path, id),
            });
            return null;
        }

        /// <summary>A section in B and W. Null when the patch needs no lines for it.</summary>
        /// <param name="rewritten">True to write all lines, and none with the text of the loaded patch.</param>
        private PatchSection? Changed(int i, int j, bool rewritten)
        {
            var original = b.Sections[i];
            var edited = w[j];
            var id = bIds[i];
            var pinned = pins(id);
            var keyName = SectionKeys.For(original.Name).Kind == SectionMatchKind.Keyed
                ? SectionKeys.For(original.Name).KeyEntry
                : null;
            bool IsKey(string name) => keyName != null && name.Equals(keyName, StringComparison.OrdinalIgnoreCase);
            var loadedSection = source?.Existing(original.Name, id.Key);
            // A loaded section that changes nothing stays, because no edit touched its lines.
            var keepEmpty = loadedSection != null && loadedSection.Lines.All(l => IsKey(l.Parsed.Name));
            if (ReferenceEquals(original, edited) && pinned.Count == 0 && !rewritten && !keepEmpty)
                return null;

            // The identity compares the key without letter case, but the merge does not change the key.
            var key = SectionIdentity.KeyOf(edited);
            if (key != SectionIdentity.KeyOf(original))
                return Problem(id, $"the {keyName} '{key}' differs only in letter case. A patch cannot change that.");
            var before = SectionComparer.ByName(original);
            var after = SectionComparer.ByName(edited);

            // The names to write: the changed lines, the removed names and the pinned names.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, editedLines) in after)
            {
                if (rewritten || !before.TryGetValue(name, out var originalLines) ||
                    !SectionComparer.SameLines(originalLines, editedLines, Binary))
                    names.Add(name);
            }
            names.UnionWith(before.Keys.Where(n => !after.ContainsKey(n)));
            names.UnionWith(pinned);
            names.RemoveWhere(IsKey);
            foreach (var group in SectionKeys.LinkedGroups(edited.Name).Where(g => g.Any(names.Contains)))
                names.UnionWith(group.Where(n => before.ContainsKey(n) || after.ContainsKey(n)));
            if (names.Count == 0 && !keepEmpty)
                return null;
            if (names.Count > 0 && SectionKeys.IsOrderSensitive(edited.Name))
                names.UnionWith(after.Keys.Where(n => !IsKey(n)));

            var reason = NotPatchable(original, kept: true);
            if (reason != null)
                return Problem(id, "a patch cannot change this section, because " + reason);
            var lines = Lines(loadedSection, inB: true, keepText: !rewritten);
            if (lines.Header(edited.Name) is { } headerError)
                return Problem(id, headerError);
            // The merge finds the section by its key line and does not change it.
            if (keyName != null && lines.Key(original.First(e => IsKey(e.Name))) is { } keyError)
                return Problem(id, keyError);

            // Each line replaces the line of the same name at the same position in B, which keeps its value type.
            var occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in edited.Where(e => names.Contains(e.Name)))
            {
                var n = occurrence.GetValueOrDefault(entry.Name);
                occurrence[entry.Name] = n + 1;
                var replaced = before.TryGetValue(entry.Name, out var old) && n < old.Count ? old[n] : null;
                if (lines.Entry(entry, replaced) is { } error)
                    return Problem(id, error);
            }

            // The removed names of B in file order, then the pinned names that neither B nor W has.
            var removed = original.Select(e => e.Name).Concat(pinned.Order(StringComparer.OrdinalIgnoreCase))
                .Where(n => names.Contains(n) && !after.ContainsKey(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (removed.Count > 0 && lines.Remove(removed) is { } removeError)
                return Problem(id, removeError);
            return Built(id, lines);
        }

        /// <summary>A section of B that W does not have.</summary>
        private PatchSection? Deleted(int i)
        {
            var original = b.Sections[i];
            var id = bIds[i];
            var reason = NotPatchable(original, kept: false);
            if (reason != null)
            {
                return Problem(id, wIndex.ContainsKey(id)
                    ? "the merge cannot keep this section at its position in the edit. A patch must delete it and add " +
                      "it again, but it cannot delete it, because " + reason
                    : "a patch cannot delete this section, because " + reason);
            }
            var lines = Lines(source?.Existing(original.Name, id.Key), inB: true, keepText: true);
            var match = SectionKeys.For(original.Name);
            var error = lines.Header(original.Name) ??
                        (match.Kind == SectionMatchKind.Keyed
                            ? lines.Key(original.First(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase)))
                            : null) ??
                        lines.Directive(IniPatcher.DirectiveDelete, TrueValue);
            return error != null ? Problem(id, error) : Built(id, lines);
        }

        /// <summary>A section of W that B does not have, or a moved section of B.</summary>
        /// <param name="loadedSection">The loaded sections that add the section, or null.</param>
        /// <param name="keepText">False to give each line new text.</param>
        private PatchSection? New(int j, SourceSection? loadedSection, bool keepText)
        {
            var section = w[j];
            var id = wIds[j];
            var lines = Lines(loadedSection, inB: false, keepText);
            if (lines.Header(section.Name) is { } headerError)
                return Problem(id, headerError);
            var match = SectionKeys.For(section.Name);
            string? error = null;
            if (SectionKeys.IsChild(section.Name))
            {
                var parent = j - 1;
                while (parent >= 0 && SectionKeys.IsChild(w[parent].Name))
                    parent--;
                if (parent < 0)
                    return Problem(id, "a child section needs a parent section before it.");
                // A new child of a new parent follows its parent in the patch, with no directive.
                if (IsKept(wIds[parent]))
                {
                    var key = IniPatcher.KeyOf(w[parent]);
                    if (key == null || wCounts.Value.Parents(key) != 1)
                        return Problem(id, $"{IniPatcher.DirectiveParent} needs a key of the parent section " +
                                           $"{wIds[parent]} that no other section of the file has.");
                    error = lines.Directive(IniPatcher.DirectiveNew, TrueValue) ??
                            lines.Directive(IniPatcher.DirectiveParent, [key]);
                }
            }
            else if (match.Kind == SectionMatchKind.Keyed)
            {
                var key = SectionIdentity.KeyOf(section);
                if (key == null)
                    return Problem(id, $"a new section needs exactly one '{match.KeyEntry}' entry with one value.");
                if (wCounts.Value.Keys(section.Name, key) != 1)
                    return Problem(id, $"another [{section.Name}] section has {match.KeyEntry} = {key}.");
                error = lines.Directive(IniPatcher.DirectiveNew, TrueValue);
            }
            else if (match.Kind == SectionMatchKind.Singleton)
            {
                if (wCounts.Value.Types(section.Name) != 1)
                    return Problem(id, $"the file can have only one [{section.Name}] section.");
                error = lines.Directive(IniPatcher.DirectiveNew, TrueValue);
            }
            // The merge always adds a [BaseGood] section, so it needs no directive.

            foreach (var entry in section)
                error ??= lines.Entry(entry, null);
            return error != null ? Problem(id, error) : Built(id, lines);
        }

        private string? NotPatchable(Section section, bool kept) =>
            PatchWriter.NotPatchable(section, kept, bCounts.Value, wCounts.Value);

        /// <summary>
        /// The sections of B that the patch deletes and adds again. The merge keeps the sections of B in their order. It
        /// adds a new section at the end of the file, and a new child at the end of the children of its parent. So these
        /// are:
        /// <list type="bullet">
        /// <item>a section of B before a section that comes earlier in B</item>
        /// <item>a section of B after a new or moved section, or a child after a new or moved child of its parent</item>
        /// <item>a section of B that the loaded patch or an edit deleted, and that W has again, when no kept section
        /// comes after it (in the file, or for a child, among the children of its parent). So the patch keeps the form
        /// of the edit, and the new section can hold what a change of the old one cannot, for example the bool string
        /// <c>true</c> where B has <c>True</c>.</item>
        /// </list>
        /// A moved section takes its children with it.
        /// </summary>
        /// <param name="deleted">The sections that the loaded patch or an edit deleted.</param>
        private HashSet<SectionIdentity> Moved(IReadOnlySet<SectionIdentity> deleted)
        {
            var result = new HashSet<SectionIdentity>();
            var lastKept = -1;
            var afterNew = false;
            var childAfterNew = false;
            for (var j = 0; j < w.Count; j++)
            {
                var isChild = SectionKeys.IsChild(w[j].Name);
                if (!isChild)
                    childAfterNew = false;
                var inB = bIndex.TryGetValue(wIds[j], out var i);
                if (inB && !afterNew && !(isChild && childAfterNew) && i > lastKept)
                {
                    lastKept = i;
                    continue;
                }
                if (inB)
                    result.Add(wIds[j]);
                if (isChild)
                    childAfterNew = true;
                else
                    afterNew = true;
            }

            // From the end: the sections that an edit deleted and added again, with no kept section after them.
            var keptAfter = false;
            var keptChildAfter = false;
            var children = new List<SectionIdentity>();
            for (var j = w.Count - 1; j >= 0; j--)
            {
                var isChild = SectionKeys.IsChild(w[j].Name);
                var inB = bIndex.ContainsKey(wIds[j]);
                var kept = inB && !result.Contains(wIds[j]);
                if (kept && deleted.Contains(wIds[j]) && !(isChild ? keptChildAfter : keptAfter))
                {
                    result.Add(wIds[j]);
                    kept = false;
                    if (!isChild)
                        result.UnionWith(children);
                }
                if (isChild)
                {
                    keptChildAfter |= kept;
                    if (inB)
                        children.Add(wIds[j]);
                }
                else
                {
                    keptAfter |= kept;
                    keptChildAfter = false;
                    children.Clear();
                }
            }
            return result;
        }

        /// <summary>True for a section of B that W has at a position that the merge keeps.</summary>
        private bool IsKept(SectionIdentity id) => bIndex.ContainsKey(id) && wIndex.ContainsKey(id) && !moved.Contains(id);

        private PatchSectionBuilder Lines(SourceSection? loadedSection, bool inB, bool keepText) =>
            new(loadedSection, Binary, inB, keepText);

        /// <summary>The section that a builder gives. Records it when it has text of the loaded patch.</summary>
        private PatchSection Built(SectionIdentity id, PatchSectionBuilder lines)
        {
            if (lines.UsesLoadedText)
                loaded.Add(id);
            return new PatchSection(id, lines.Lines);
        }

        private static void Add(List<PatchSection> sections, PatchSection? section)
        {
            if (section != null)
                sections.Add(section);
        }

        private static Dictionary<SectionIdentity, int> Index(SectionIdentity[] ids) =>
            ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
    }
}

/// <summary>
/// How many sections of a file the merge finds for a type, for a type and key, and for an <c>ol.parent</c> key.
/// </summary>
internal sealed class KeyCounts
{
    private readonly Dictionary<string, int> types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> parents = new(StringComparer.OrdinalIgnoreCase);

    public KeyCounts(IReadOnlyList<Section> sections)
    {
        foreach (var section in sections)
        {
            Increment(types, section.Name);
            var match = SectionKeys.For(section.Name);
            if (match.Kind == SectionMatchKind.Keyed)
            {
                if (!keys.TryGetValue(section.Name, out var ofType))
                    keys[section.Name] = ofType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                // The merge finds a section by any of its key entries.
                var values = section
                    .Where(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase) && e.Count > 0)
                    .Select(e => e[0].ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var value in values)
                    Increment(ofType, value);
            }
            if (!SectionKeys.IsChild(section.Name) && IniPatcher.KeyOf(section) is { } parentKey)
                Increment(parents, parentKey);
        }
    }

    public int Types(string type) => types.GetValueOrDefault(type);

    public int Keys(string type, string key) => keys.TryGetValue(type, out var ofType) ? ofType.GetValueOrDefault(key) : 0;

    public int Parents(string key) => parents.GetValueOrDefault(key);

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}

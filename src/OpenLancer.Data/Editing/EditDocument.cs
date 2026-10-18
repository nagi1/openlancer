using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The state of the working copy of one file: W, the pinned names and the deleted sections (see
/// <see cref="EditDocument"/>). The state cannot change, so the edit history keeps old states to undo edits.
/// </summary>
/// <param name="Working">The sections of W.</param>
/// <param name="Pins">The pinned names of each section.</param>
/// <param name="Deleted">The sections that the loaded patch or an edit deleted.</param>
internal sealed record DocumentState(
    ImmutableList<Section> Working,
    ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>> Pins,
    ImmutableHashSet<SectionIdentity> Deleted) : IFileState;

/// <summary>
/// One INI file in the editor: B, the document that the merge gives to the active mod's patch, and W, the working
/// copy that the edits change.
/// </summary>
/// <remarks>
/// <para>W starts as B with the active mod's patch applied. The text placeholders of the patch stay as text.</para>
/// <para>A pinned name is a name that the loaded patch sets or removes in a section of B, and that no edit has
/// touched since. The writer writes the pinned names again, also when their lines equal B. So the mod keeps its
/// values when a lower mod changes. An edit of a name removes its pin. The writer also keeps the lines of the
/// loaded patch that no edit touched: their text, the comment lines and the encoding (see
/// <see cref="PatchSectionBuilder"/>).</para>
/// <para>The document records the sections that the loaded patch or an edit deleted. When W has such a section again
/// at the end, the writer deletes it and adds it again, as the edit did.</para>
/// <para>The document gives no patch, and is read-only, in these cases:</para>
/// <list type="bullet">
/// <item>The loaded patch has an error. Then some of its lines are not in W, and a save would lose them.</item>
/// <item>The active mod replaces the file with its own file. The merge then applies the patch of the mod to that
/// file, not to B. W is then B, and the document only shows it.</item>
/// <item>The file is <c>EXE\freelancer.ini</c>, which a patch cannot change.</item>
/// </list>
/// <para>Edits never change a section or an entry in place. They put new sections into the list, so an old
/// <see cref="DocumentState"/> stays valid.</para>
/// </remarks>
internal sealed class EditDocument : IEditFile
{
    private static readonly ImmutableHashSet<string> NoPins = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    private readonly IniDocument b;
    private readonly PatchSource? source;

    /// <summary>Why the document gives no patch, or null.</summary>
    private readonly Diagnostic? refusal;

    private DocumentState state;
    private SectionIdentity[]? workingIds;
    private Dictionary<SectionIdentity, int>? workingIndex;
    private Dictionary<SectionIdentity, int>? baseIndex;
    private KeyCounts? baseCounts;
    private KeyCounts? workingCounts;
    private PatchText? initialText;
    private HashSet<SectionIdentity>? initialIds;

    private EditDocument(string path, IniDocument b, DocumentState state, PatchSource? source, Diagnostic? refusal,
        string? sourceHash)
    {
        Path = path;
        this.b = b;
        this.state = state;
        Initial = state;
        this.source = source;
        this.refusal = refusal;
        SourceHash = sourceHash;
    }

    /// <summary>The install path, in <see cref="VfsPath.Normalize"/> form.</summary>
    public string Path { get; }

    public bool IsBinary => b.IsBinary;

    /// <summary>The sections of B. They never change.</summary>
    public IReadOnlyList<Section> Base => b.Sections;

    /// <summary>The sections of W.</summary>
    public IReadOnlyList<Section> Working => state.Working;

    /// <summary>The state of W now. <see cref="Restore"/> puts back a state that this property gave.</summary>
    public DocumentState State => state;

    IFileState IEditFile.State => state;

    /// <summary>The state when the document opened: the state that the loaded patch gives.</summary>
    public DocumentState Initial { get; }

    /// <summary>Why the document gives no patch and cannot be edited, or null.</summary>
    public string? ReadOnlyReason => refusal?.Message;

    /// <summary>The SHA-256 of the loaded patch file, in lowercase hex, or null when the active mod had no patch for
    /// the file.</summary>
    public string? SourceHash { get; }

    /// <summary>
    /// Opens a file for editing. W is B with the patch of <paramref name="activeMod"/> for this path applied.
    /// </summary>
    /// <param name="path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
    /// <param name="b">The document that the merge gives to the active mod's patch. The editor owns it from now on.</param>
    /// <param name="activeMod">The mod that the edits go to, or null.</param>
    /// <param name="diagnostics">Gets the problems of the active mod's patch, and why the document gives no
    /// patch.</param>
    public static EditDocument Open(string path, IniDocument b, Mod? activeMod, List<Diagnostic> diagnostics)
    {
        if (activeMod == null)
            return Load(path, b, "", null, null, diagnostics);
        if (activeMod.Files.TryGetValue(path, out var file))
        {
            return Refused(path, b, diagnostics, Diagnostic.Error(file,
                $"The active mod '{activeMod.Id}' replaces {path} with this file. The editor cannot write a patch " +
                "for a file that the active mod replaces."));
        }
        if (VfsPath.Comparer.Equals(path, ModOverlay.FreelancerIniPath))
        {
            return Refused(path, b, diagnostics, Diagnostic.Error(path,
                $"{ModOverlay.FreelancerIniPath} cannot be patched in format v1."));
        }
        if (!activeMod.Patches.TryGetValue(path, out var patchFile))
            return Load(path, b, activeMod.Id, null, null, diagnostics);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(patchFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(path, b, diagnostics, Diagnostic.Error(patchFile, e.Message));
        }
        return Load(path, b, activeMod.Id, patchFile, bytes, diagnostics);
    }

    /// <summary>
    /// The document that the patch <paramref name="bytes"/> of the active mod gives, with the same B. The editor uses
    /// it after a save, so that W is what a reload of the saved files gives.
    /// </summary>
    /// <param name="modId">The id of the active mod.</param>
    /// <param name="patchFile">The patch file, for messages.</param>
    /// <param name="bytes">The bytes of the patch file, or null for no patch.</param>
    public EditDocument Reopen(string modId, string patchFile, byte[]? bytes, List<Diagnostic> diagnostics) =>
        Load(Path, b, modId, patchFile, bytes, diagnostics);

    /// <summary>The pinned names of a section.</summary>
    public IReadOnlyCollection<string> Pins(SectionIdentity id) => state.Pins.GetValueOrDefault(id, NoPins);

    public SectionIdentity IdentityAt(int index) => WorkingIds()[index];

    /// <summary>The index of a section in W, or -1.</summary>
    public int IndexOf(SectionIdentity id)
    {
        workingIndex ??= WorkingIds().Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        return workingIndex.GetValueOrDefault(id, -1);
    }

    /// <summary>True when B has a section with this identity.</summary>
    public bool InBase(SectionIdentity id) => BaseIndexOf(id) >= 0;

    /// <summary>True when W had a section with this identity when the document opened or the last save reopened it, so
    /// the mod files have it.</summary>
    public bool InInitial(SectionIdentity id) =>
        (initialIds ??= SectionIdentity.Identify(Initial.Working).ToHashSet()).Contains(id);

    /// <summary>The section of B with this identity, or null.</summary>
    public Section? BaseSection(SectionIdentity id) => BaseIndexOf(id) is >= 0 and var i ? b.Sections[i] : null;

    /// <summary>
    /// Why a patch cannot change the section at <paramref name="index"/> of W, or null. A section that B does not
    /// have is new, so a patch adds it.
    /// </summary>
    public string? ReadOnlyReasonAt(int index)
    {
        if (refusal != null)
            return refusal.Message;
        var i = BaseIndexOf(IdentityAt(index));
        if (i < 0)
            return null;
        baseCounts ??= new KeyCounts(b.Sections);
        workingCounts ??= new KeyCounts(state.Working);
        return PatchWriter.NotPatchable(b.Sections[i], kept: true, baseCounts, workingCounts) ??
               PatchWriter.OrderNotKept(b.Sections[i]);
    }

    /// <summary>
    /// Puts <paramref name="section"/> in place of the section at <paramref name="index"/>. When the section has
    /// another identity, the old section counts as deleted.
    /// </summary>
    /// <param name="touched">The names that the edit sets or removes. They lose their pins.</param>
    public void Replace(int index, Section section, IEnumerable<string> touched)
    {
        var old = IdentityAt(index);
        Change(state with { Working = state.Working.SetItem(index, section) });
        var same = IdentityAt(index).Equals(old);
        var pins = same && state.Pins.TryGetValue(old, out var names)
            ? state.Pins.SetItem(old, names.Except(touched))
            : state.Pins.Remove(old);
        state = state with { Pins = pins, Deleted = same ? state.Deleted : state.Deleted.Add(old) };
    }

    public void Insert(int index, Section section) => Change(state with { Working = state.Working.Insert(index, section) });

    /// <summary>Removes <paramref name="count"/> sections from <paramref name="index"/>, and their pins.</summary>
    public void Remove(int index, int count)
    {
        var removed = WorkingIds().Skip(index).Take(count).ToList();
        Change(new DocumentState(state.Working.RemoveRange(index, count), state.Pins.RemoveRange(removed),
            state.Deleted.Union(removed)));
    }

    /// <summary>Puts back a state that <see cref="State"/> gave, to undo or redo an edit.</summary>
    public void Restore(DocumentState restored) => Change(restored);

    void IEditFile.Restore(IFileState restored) => Restore((DocumentState)restored);

    /// <summary>Writes the patch that changes B into W.</summary>
    public PatchText Write() => Write(state);

    /// <summary>
    /// The patch for W, or null when the edits have no effect on the patch: W has its initial state, or the patch has
    /// the same bytes as the patch for the initial state. Then the patch file keeps its text (rule P2).
    /// </summary>
    public PatchText? ChangedPatch()
    {
        if (state == Initial)
            return null;
        var patch = Write();
        initialText ??= Write(Initial);
        return patch.Succeeded && initialText.Succeeded && patch.ToBytes().AsSpan().SequenceEqual(initialText.ToBytes())
            ? null
            : patch;
    }

    private PatchText Write(DocumentState s) =>
        refusal == null
            ? PatchWriter.Write(Path, b, s.Working, id => s.Pins.GetValueOrDefault(id, NoPins), source, s.Deleted)
            : new PatchText([], [refusal]);

    private static EditDocument Load(string path, IniDocument b, string modId, string? patchFile, byte[]? bytes,
        List<Diagnostic> diagnostics)
    {
        // A copy of the section list is enough: the merge never changes a section in place.
        var w = b.WithSections(b.Sections);
        var pins = ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>>.Empty;
        var deleted = ImmutableHashSet<SectionIdentity>.Empty;
        PatchSource? source = null;
        Diagnostic? refusal = null;
        if (bytes != null)
        {
            var count = diagnostics.Count;
            try
            {
                source = PatchSource.Parse(patchFile!, bytes);
            }
            catch (Exception e)
            {
                diagnostics.Add(Diagnostic.Error(patchFile!, e.Message));
            }
            if (source != null)
            {
                var ledger = new PatchLedger();
                IniPatcher.Apply(w, source.Sections, new PatchContext(modId, patchFile!, path, null, ledger,
                    diagnostics, keepPlaceholders: true));
                var changes = ledger.Changes(path, modId).ToList();
                pins = changes
                    .Where(c => c.Name != null)
                    .GroupBy(c => c.Section)
                    .ToImmutableDictionary(g => g.Key,
                        g => ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, g.Select(c => c.Name!)));
                deleted = changes.Where(c => c.Name == null).Select(c => c.Section).ToImmutableHashSet();
            }
            if (diagnostics.Skip(count).Any(d => d.Severity == DiagnosticSeverity.Error))
                refusal = Diagnostic.Error(patchFile!, "This patch has an error, so the editor cannot apply all its " +
                                                       "lines. Correct the file, then open it again.");
        }
        return new EditDocument(path, b, new DocumentState(w.Sections.ToImmutableList(), pins, deleted), source, refusal,
            bytes == null ? null : ContentHash.Of(bytes));
    }

    private static EditDocument Refused(string path, IniDocument b, List<Diagnostic> diagnostics, Diagnostic refusal)
    {
        diagnostics.Add(refusal);
        return new EditDocument(path, b, new DocumentState(b.Sections.ToImmutableList(),
            ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>>.Empty,
            ImmutableHashSet<SectionIdentity>.Empty), null, refusal, null);
    }

    private void Change(DocumentState changed)
    {
        if (!ReferenceEquals(changed.Working, state.Working))
        {
            workingIds = null;
            workingIndex = null;
            workingCounts = null;
        }
        state = changed;
    }

    private SectionIdentity[] WorkingIds() => workingIds ??= SectionIdentity.Identify(state.Working);

    private int BaseIndexOf(SectionIdentity id)
    {
        baseIndex ??= SectionIdentity.Identify(b.Sections).Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        return baseIndex.GetValueOrDefault(id, -1);
    }
}

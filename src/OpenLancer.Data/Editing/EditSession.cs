using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The edits of one profile, with one active mod that gets them. Each edit is a command. A save writes the edits as
/// patches of the active mod.
/// </summary>
/// <remarks>
/// <para>The profile's mods in load order are the lower mods, the active mod and the later mods. The session shows
/// the edit view E: the base data with the lower mods and the active mod (decision D2). For each file it keeps B, the
/// file as the lower mods leave it, and W, B with the active mod's patch and the edits (see
/// <see cref="EditDocument"/>). A save writes the patch that changes B into W.</para>
/// <para>Use a session on one thread only. To change the active mod, open a new session. The history then starts
/// empty, because B changes.</para>
/// </remarks>
public sealed partial class EditSession
{
    private readonly IStringResourceWriter stringWriter;
    private readonly IReadOnlyList<Mod> lower;
    private readonly ModOverlay lowerView;
    private readonly LayerInputs inputs;
    private readonly Dictionary<string, EditDocument> documents = new(VfsPath.Comparer);
    private readonly EditHistory history = new();
    private readonly Lazy<ModOverlay> fullView;

    /// <summary>Why the session cannot edit, or null.</summary>
    private readonly string? readOnlyReason;

    /// <summary>The paths that a command changed, also when an undo took the change back (rule P1).</summary>
    private readonly HashSet<string> touched = new(VfsPath.Comparer);

    private ModText text;
    private ChangeSet? transaction;

    /// <param name="recoveryFailure">Why the open could not complete an earlier save of the active mod or of a lower
    /// mod, or null.</param>
    private EditSession(ModProfile profile, IReadOnlyList<Mod> mods, int active, IStringResourceWriter stringWriter,
        IReadOnlyList<Diagnostic> openProblems, string? recoveryFailure)
    {
        Profile = profile;
        this.stringWriter = stringWriter;
        OpenProblems = openProblems;
        ActiveMod = active < 0 ? null : mods[active];
        lower = active < 0 ? mods : mods.Take(active).ToList();
        LaterMods = active < 0 ? [] : mods.Skip(active + 1).ToList();
        inputs = new LayerInputs(profile.BaseFolder, lower, ActiveMod);
        lowerView = ModOverlay.Build(profile.BaseFolder, lower, stringWriter, keepDocuments: true);
        EditView = ActiveMod == null ? lowerView : BuildEditView(ActiveMod);
        // Only the marks of the later mods need the whole profile, so it is built at the first use.
        fullView = new Lazy<ModOverlay>(() => ModOverlay.Build(profile.BaseFolder, mods, stringWriter));
        readOnlyReason = ActiveMod == null ? "There is no active mod. Select a mod to edit."
            : recoveryFailure
              ?? (lowerView.HasErrors ? "The mods that load before the active mod have merge errors. Correct them, then " +
                                        "open the profile again." : null);
        (text, textHash) = ActiveMod == null ? (ModText.Empty, null) : ReadText(ActiveMod);
        savedText = text;
        HasRecovery = ActiveMod != null && File.Exists(RecoveryFile.PathOf(ActiveMod.Folder));
    }

    public ModProfile Profile { get; }

    /// <summary>The mod that gets the edits, or null when the session is read-only.</summary>
    public Mod? ActiveMod { get; private set; }

    /// <summary>The mods that load after the active mod. The edit view does not show their changes.</summary>
    public IReadOnlyList<Mod> LaterMods { get; }

    /// <summary>
    /// True when the session takes edits. It takes none while <see cref="HasRecovery"/> is true: restore or discard the
    /// unsaved edits of the earlier session first.
    /// </summary>
    public bool CanEdit => ReadOnlyReason == null;

    /// <summary>
    /// Why the session takes no edits, or null: it has no active mod, the open could not complete an earlier save, the
    /// lower mods have merge errors, or the recovery file of an earlier session waits (see <see cref="HasRecovery"/>).
    /// </summary>
    public string? ReadOnlyReason =>
        readOnlyReason ?? (HasRecovery ? "The mod has unsaved edits of an earlier session. Restore or discard them first." : null);

    /// <summary>
    /// The problems that the open found in the earlier saves of the mods of the profile. An error tells that the open
    /// could not complete a save. When that save is of the active mod or of a lower mod, the session is read-only. A
    /// warning tells about a file that changed after a save stopped, and that the open kept.
    /// </summary>
    public IReadOnlyList<Diagnostic> OpenProblems { get; }

    /// <summary>
    /// The edit view E as the files on disk give it: the base data with the lower mods and the active mod. A save
    /// builds it again. Load the game data from a new <see cref="ModOverlay.CreateFileSystem"/> of it.
    /// </summary>
    public ModOverlay EditView { get; private set; }

    public bool CanUndo => history.CanUndo;

    public bool CanRedo => history.CanRedo;

    public string? UndoLabel => history.UndoLabel;

    public string? RedoLabel => history.RedoLabel;

    /// <summary>True when the session has edits that it did not save.</summary>
    public bool IsDirty => history.IsDirty;

    /// <summary>Tells what an edit, an undo or a redo changed.</summary>
    public event Action<EditChange>? Changed;

    /// <summary>
    /// Opens a session. When an earlier save of a mod of the profile stopped after its commit point, this completes
    /// it first (see <see cref="SaveTransaction"/>). <see cref="OpenProblems"/> tells what it could not complete.
    /// </summary>
    /// <param name="activeModId">The id of the mod that gets the edits, or null for a read-only session of the whole
    /// profile.</param>
    /// <param name="strings">Writes the text DLL of the merged views.</param>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    /// <exception cref="ModLoadException">A mod or its text is not valid.</exception>
    public static EditSession Open(ModProfile profile, string? activeModId, IStringResourceWriter strings)
    {
        var active = -1;
        if (activeModId != null)
        {
            active = profile.Mods.Select(m => m.Id).ToList().IndexOf(activeModId);
            if (active < 0)
                throw new ArgumentException($"The profile {profile.Name} has no mod '{activeModId}'.", nameof(activeModId));
        }
        var problems = new List<Diagnostic>();
        var mods = new List<Mod>();
        string? failure = null;
        for (var i = 0; i < profile.Mods.Count; i++)
        {
            var mod = profile.Mods[i];
            try
            {
                // A completed save changes the files of the mod, so the mod loads again.
                mods.Add(SaveTransaction.Recover(mod.Folder, problems) ? Mod.Load(mod.Folder) : mod);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var message = $"An earlier save of mod '{mod.Id}' is not complete: {e.Message} Correct the problem, " +
                              "then open the profile again.";
                problems.Add(Diagnostic.Error(mod.Folder, message));
                // Some files of the mod can be from the save and some from before it. Show them as they are.
                mods.Add(Mod.Load(mod.Folder));
                // The edit view does not show a later mod, so only a lower mod or the active mod stops the edits.
                if (i <= active)
                    failure ??= message;
            }
        }
        return new EditSession(profile, mods, active, strings, problems, failure);
    }

    /// <summary>A view of a section of the edit view, or null when the edit view has no such section.</summary>
    public SectionView? Get(SectionRef target)
    {
        if (!TryFind(target, out var document, out var index))
            return null;
        var lower = document.BaseSection(document.IdentityAt(index));
        // W shares each section of B that the patch and the edits leave as it is. A patch or an edit puts a new section
        // into W, also when its lines equal B, for example a pinned line.
        var changed = lower == null || !ReferenceEquals(lower, document.Working[index]);
        return new SectionView(document.Working[index], ReadOnlyReason ?? document.ReadOnlyReasonAt(index), lower == null,
            changed);
    }

    /// <summary>
    /// Where the lines of a name in a section come from: the active mod when it sets or removes the name (in its
    /// saved patch or with an edit), else the last lower mod that changes the name or the section, else the base
    /// data.
    /// </summary>
    /// <exception cref="ArgumentException">The edit view has no such section.</exception>
    public (ValueOrigin Origin, string? ModId) Origin(SectionRef target, string name)
    {
        var (document, index) = Find(target);
        var id = document.IdentityAt(index);
        if (ActiveMod != null && (!document.InBase(id) || document.Pins(id).Contains(name) ||
                                  !SameLines(document.BaseSection(id)!, document.Working[index], name, document.IsBinary)))
            return (ValueOrigin.ActiveMod, ActiveMod.Id);
        var owner = lowerView.Ledger.Owner(document.Path, id, name);
        return owner == null ? (ValueOrigin.Base, null) : (ValueOrigin.LowerMod, owner);
    }

    /// <summary>
    /// The later mods that change the lines of a name in a section, that add or delete the section, or that replace
    /// its file, in load order. The game shows their values, not the values of the edit view.
    /// </summary>
    /// <exception cref="ArgumentException">The edit view has no such section.</exception>
    public IReadOnlyList<string> LaterModsThatChange(SectionRef target, string name)
    {
        if (LaterMods.Count == 0)
            return [];
        var (document, index) = Find(target);
        var later = LaterMods.Select(m => m.Id).ToHashSet();
        return fullView.Value.Ledger.ModsThatChange(document.Path, document.IdentityAt(index), name)
            .Where(later.Contains)
            .ToList();
    }

    /// <summary>The text of a placeholder of the active mod, with the edits, or null when it has none.</summary>
    public string? Text(StringPlaceholder placeholder) => text.Get(placeholder.Kind, placeholder.Key);

    /// <summary>Runs a command as one entry of the edit history.</summary>
    /// <exception cref="ArgumentException">The command refers to a section or a file that the edit view does not
    /// have.</exception>
    /// <exception cref="InvalidOperationException">The session or the section is read-only, a transaction is open,
    /// or the command does not apply. The edit view does not change.</exception>
    public void Execute(EditCommand command)
    {
        ThrowIfTransaction();
        ThrowIfNoEdits();
        var change = new ChangeSet(command.Label);
        Run(change, command);
        Push(change);
    }

    /// <summary>Starts a transaction: one entry of the edit history for many steps.</summary>
    /// <exception cref="InvalidOperationException">A transaction is open, or the session takes no edits.</exception>
    public EditTransaction Begin(string label)
    {
        ThrowIfTransaction();
        ThrowIfNoEdits();
        transaction = new ChangeSet(label);
        return new EditTransaction(this, transaction);
    }

    /// <exception cref="InvalidOperationException">There is nothing to undo, or a transaction is open.</exception>
    public void Undo()
    {
        ThrowIfTransaction();
        var change = history.Undo();
        text = change.Undo(DocumentAt) ?? text;
        Changed?.Invoke(change.Change);
    }

    /// <exception cref="InvalidOperationException">There is nothing to redo, or a transaction is open.</exception>
    public void Redo()
    {
        ThrowIfTransaction();
        var change = history.Redo();
        text = change.Redo(DocumentAt) ?? text;
        Changed?.Invoke(change.Change);
    }

    /// <summary>Puts back the states before the transaction, then runs a command in it.</summary>
    internal void ApplyInTransaction(ChangeSet change, EditCommand command)
    {
        text = change.Undo(DocumentAt) ?? text;
        try
        {
            Run(change, command);
        }
        finally
        {
            // Also when the command throws: the edit view is then back at its state before the transaction, not at
            // the state of the step before.
            if (!change.IsEmpty)
                Changed?.Invoke(change.Change);
        }
    }

    internal void EndTransaction(ChangeSet change, bool commit)
    {
        transaction = null;
        if (commit)
        {
            Push(change);
            return;
        }
        text = change.Undo(DocumentAt) ?? text;
        if (!change.IsEmpty)
            Changed?.Invoke(change.Change);
    }

    /// <summary>Runs a command. When it throws, puts back the states before the change set.</summary>
    private void Run(ChangeSet change, EditCommand command)
    {
        try
        {
            command.Run(new CommandScope(this, change));
        }
        catch
        {
            text = change.Undo(DocumentAt) ?? text;
            throw;
        }
    }

    /// <summary>Adds a change set to the history, when it changed anything.</summary>
    private void Push(ChangeSet change)
    {
        change.Finish(DocumentAt, text);
        // A transaction whose last step threw ends in its state before, so it can change nothing.
        if (!change.ChangesState)
            return;
        history.Push(change);
        touched.UnionWith(change.Paths);
        Changed?.Invoke(change.Change);
    }

    private void ThrowIfTransaction()
    {
        if (transaction != null)
            throw new InvalidOperationException("A transaction is open. Commit or cancel it first.");
    }

    /// <summary>The document of a path that the session opened.</summary>
    private EditDocument DocumentAt(string path) => documents[path];

    /// <summary>The section of a reference.</summary>
    /// <exception cref="ArgumentException">The edit view has no such section.</exception>
    private (EditDocument Document, int Index) Find(SectionRef target) =>
        TryFind(target, out var document, out var index)
            ? (document, index)
            : throw new ArgumentException($"The edit view has no section {target}.", nameof(target));

    private bool TryFind(SectionRef target, out EditDocument document, out int index)
    {
        index = -1;
        if (!TryDocument(target.Path, out document!))
            return false;
        index = document.IndexOf(target.Id);
        return index >= 0;
    }

    /// <summary>The document of an INI file. Opens it at first use.</summary>
    private bool TryDocument(string path, out EditDocument? document)
    {
        document = null;
        var normal = VfsPath.Normalize(path);
        // An install path is relative to the game folder, so it has no drive.
        if (normal == null || normal.Contains(':') || !normal.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            return false;
        if (documents.TryGetValue(normal, out document))
            return true;
        // The fingerprint first: a change after it shows at the save.
        inputs.Remember(normal, lower);
        var b = lowerView.Document(normal);
        if (b == null)
            return false;
        document = EditDocument.Open(normal, b, ActiveMod, []);
        documents[normal] = document;
        return true;
    }

    private ModOverlay BuildEditView(Mod active) => ModOverlay.Build(Profile.BaseFolder, [.. lower, active], stringWriter);

    /// <summary>The text of a mod, and the hash of its strings.json, or null when it has none.</summary>
    private static (ModText Text, string? Hash) ReadText(Mod mod)
    {
        var path = Path.Combine(mod.Folder, Mod.StringsFile);
        if (!File.Exists(path))
            return (ModText.Empty, null);
        var bytes = File.ReadAllBytes(path);
        using var reader = new StreamReader(new MemoryStream(bytes, false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (ModText.From(ModStrings.Parse(path, reader.ReadToEnd())), ContentHash.Of(bytes));
    }

    private static bool SameLines(Section original, Section edited, string name, bool binary)
    {
        var before = SectionComparer.ByName(original).GetValueOrDefault(name) ?? [];
        var after = SectionComparer.ByName(edited).GetValueOrDefault(name) ?? [];
        return SectionComparer.SameLines(before, after, binary);
    }

    /// <summary>
    /// What a command can change: the documents of the session and the text. The scope records the state of each
    /// document before the first change to it, so the session can undo the command.
    /// </summary>
    internal sealed class CommandScope(EditSession session, ChangeSet change)
    {
        /// <summary>The text of the active mod.</summary>
        /// <exception cref="InvalidOperationException">The session is read-only.</exception>
        public ModText Text
        {
            get => session.text;
            set
            {
                session.ThrowIfReadOnly();
                change.CaptureText(session.text);
                session.text = value;
            }
        }

        /// <summary>The document of a file, to change it.</summary>
        /// <exception cref="ArgumentException">The edit view has no such INI file.</exception>
        /// <exception cref="InvalidOperationException">The session or the file is read-only.</exception>
        public EditDocument Document(string path)
        {
            if (!session.TryDocument(path, out var document))
                throw new ArgumentException($"The edit view has no INI file {path}.", nameof(path));
            return Editable(document!);
        }

        /// <summary>The document and the index of a section, to change it.</summary>
        /// <exception cref="ArgumentException">The edit view has no such section.</exception>
        /// <exception cref="InvalidOperationException">The session or the section is read-only.</exception>
        public (EditDocument Document, int Index) Find(SectionRef target)
        {
            var (document, index) = session.Find(target);
            Editable(document);
            if (document.ReadOnlyReasonAt(index) is { } reason)
                throw new InvalidOperationException($"{target}: {reason}");
            return (document, index);
        }

        public void Changed(SectionRef section, bool structural) =>
            change.Note(section with { Path = VfsPath.Normalize(section.Path) ?? section.Path }, structural);

        private EditDocument Editable(EditDocument document)
        {
            session.ThrowIfReadOnly();
            if (document.ReadOnlyReason != null)
                throw new InvalidOperationException(document.ReadOnlyReason);
            change.Capture(document);
            return document;
        }
    }

    private void ThrowIfNoEdits()
    {
        if (ReadOnlyReason is { } reason)
            throw new InvalidOperationException(reason);
    }

    /// <summary>Throws when the session is read-only. Unlike <see cref="ThrowIfNoEdits"/>, it allows the restore of a
    /// recovery file.</summary>
    private void ThrowIfReadOnly()
    {
        if (readOnlyReason != null)
            throw new InvalidOperationException(readOnlyReason);
    }
}

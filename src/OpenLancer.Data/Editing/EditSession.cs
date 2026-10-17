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
using OpenLancer.Data.Thn;

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
/// <para>A session holds the lock of its active mod (see <see cref="ModLock"/>) until it is disposed. A second session
/// on the same mod, also in another editor, is read-only. A disposed session is read-only too.</para>
/// </remarks>
public sealed partial class EditSession : IDisposable
{
    private readonly IStringResourceWriter stringWriter;

    /// <summary>Reads the cutscene scripts and their THN patches for the merged views.</summary>
    private readonly IThnEvaluator thn;

    private readonly IReadOnlyList<Mod> lower;
    private readonly ModOverlay lowerView;
    private readonly LayerInputs inputs;
    private readonly Dictionary<string, EditDocument> documents = new(VfsPath.Comparer);
    private readonly EditHistory history = new();
    private readonly Lazy<ModOverlay> fullView;

    /// <summary>Why the session cannot edit, or null. It changes only when the session closes (see
    /// <see cref="Close"/>).</summary>
    private string? readOnlyReason;

    /// <summary>The paths that a command changed, also when an undo took the change back (rule P1).</summary>
    private readonly HashSet<string> touched = new(VfsPath.Comparer);

    private ModText text;
    private ChangeSet? transaction;

    /// <summary>The lock of the active mod, or null when the session does not hold it. Without the lock, the session
    /// does not read, write or delete the recovery file.</summary>
    private ModLock? modLock;

    /// <param name="recoveryFailure">Why the open could not complete an earlier save of the active mod or of a lower
    /// mod, or null.</param>
    /// <param name="modLock">The lock of the active mod, or null.</param>
    /// <param name="lockReason">Why the open could not take the lock of the active mod, or null.</param>
    private EditSession(ModProfile profile, IReadOnlyList<Mod> mods, int active, IStringResourceWriter stringWriter,
        IThnEvaluator thn, IReadOnlyList<Diagnostic> openProblems, string? recoveryFailure, ModLock? modLock,
        string? lockReason)
    {
        this.modLock = modLock;
        Profile = profile;
        this.stringWriter = stringWriter;
        this.thn = thn;
        OpenProblems = openProblems;
        ActiveMod = active < 0 ? null : mods[active];
        lower = active < 0 ? mods : mods.Take(active).ToList();
        LaterMods = active < 0 ? [] : mods.Skip(active + 1).ToList();
        inputs = new LayerInputs(profile.BaseFolder, lower, ActiveMod);
        lowerView = ModOverlay.Build(profile.BaseFolder, lower, stringWriter, thn, keepDocuments: true);
        EditView = ActiveMod == null ? lowerView : BuildEditView(ActiveMod);
        // Only the marks of the later mods need the whole profile, so it is built at the first use.
        fullView = new Lazy<ModOverlay>(() => ModOverlay.Build(profile.BaseFolder, mods, stringWriter, thn));
        readOnlyReason = ActiveMod == null ? "There is no active mod. Select a mod to edit."
            : lockReason
              ?? recoveryFailure
              ?? (lowerView.HasErrors ? "The mods that load before the active mod have merge errors. Correct them, then " +
                                        "open the profile again." : null);
        (text, textHash) = ActiveMod == null ? (ModText.Empty, null) : ReadText(ActiveMod);
        savedText = text;
        HasRecovery = ActiveMod != null && modLock != null && File.Exists(RecoveryFile.PathOf(ActiveMod.Folder));
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
    /// Why the session takes no edits, or null: it has no active mod, another editor has the active mod open, the open
    /// could not complete an earlier save, the lower mods have merge errors, the session is closed, or the recovery
    /// file of an earlier session waits (see <see cref="HasRecovery"/>).
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

    /// <summary>
    /// The install paths of the files that a command of the session changed, also when an undo took the change back.
    /// A save writes the patches of these files. The session opened each of them, so a read of W of such a file opens
    /// no new file.
    /// </summary>
    public IReadOnlyList<string> EditedPaths => touched.Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Tells what an edit, an undo or a redo changed.</summary>
    public event Action<EditChange>? Changed;

    /// <summary>
    /// Opens a session. It takes the lock of the active mod first. When another editor has the mod open, the session
    /// is read-only. When an earlier save of a mod of the profile stopped after its commit point, this completes it
    /// (see <see cref="SaveTransaction"/>). <see cref="OpenProblems"/> tells what it could not complete.
    /// </summary>
    /// <param name="activeModId">The id of the mod that gets the edits, or null for a read-only session of the whole
    /// profile.</param>
    /// <param name="strings">Writes the text DLL of the merged views.</param>
    /// <param name="thn">Reads the cutscene scripts and their THN patches for the merged views.</param>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    /// <exception cref="ModLoadException">A mod or its text is not valid.</exception>
    public static EditSession Open(ModProfile profile, string? activeModId, IStringResourceWriter strings,
        IThnEvaluator thn)
    {
        var active = -1;
        if (activeModId != null)
        {
            active = profile.IndexOf(activeModId);
            if (active < 0)
                throw new ArgumentException($"The profile {profile.Name} has no mod '{activeModId}'.", nameof(activeModId));
        }
        // The lock comes before the recovery, so that the session never completes or deletes the files of a save that
        // another editor runs now.
        string? lockReason = null;
        var modLock = active < 0 ? null : ModLock.TryTake(profile.Mods[active], out lockReason);
        try
        {
            var problems = new List<Diagnostic>();
            var mods = new List<Mod>();
            string? failure = null;
            for (var i = 0; i < profile.Mods.Count; i++)
            {
                var mod = profile.Mods[i];
                // Some files of a save that is not complete can be from the save and some from before it. The open
                // shows them as they are. The edit view does not show a later mod, so only a lower mod or the active
                // mod stops the edits.
                void NotComplete(string message)
                {
                    problems.Add(Diagnostic.Error(mod.Folder, message));
                    if (i <= active)
                        failure ??= message;
                }

                try
                {
                    var completed = Recover(mod, i == active ? modLock : null, problems, out var blocked);
                    if (blocked != null)
                        NotComplete($"A save of mod '{mod.Id}' is not complete. {blocked}");
                    // A completed save changes the files of the mod, so the mod loads again.
                    mods.Add(completed ? Mod.Load(mod.Folder) : mod);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    NotComplete($"An earlier save of mod '{mod.Id}' is not complete: {e.Message} Correct the problem, " +
                                "then open the profile again.");
                    mods.Add(Mod.Load(mod.Folder));
                }
            }
            return new EditSession(profile, mods, active, strings, thn, problems, failure, modLock, lockReason);
        }
        catch
        {
            modLock?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Completes a save of a mod that stopped after its commit point, and deletes the staged files of a save that
    /// stopped before it (see <see cref="SaveTransaction.Recover"/>). It does this only with the lock of the mod,
    /// because another editor that has the mod open can be in the middle of a save. Without the lock, the files of a
    /// save stay as they are.
    /// </summary>
    /// <param name="held">The lock of the mod that the session holds, or null. Without it, the recovery takes the lock
    /// for its own time.</param>
    /// <param name="blocked">Why the open cannot complete the save that the journal of the mod holds, or null: the open
    /// could not take the lock of the mod.</param>
    /// <returns>True when it completed a save.</returns>
    /// <exception cref="IOException">A move or a delete of the recovery failed.</exception>
    /// <exception cref="UnauthorizedAccessException">A move or a delete of the recovery failed.</exception>
    /// <exception cref="InvalidDataException">The journal is not valid.</exception>
    private static bool Recover(Mod mod, ModLock? held, List<Diagnostic> problems, out string? blocked)
    {
        blocked = null;
        if (!Directory.Exists(Path.Combine(mod.Folder, SaveTransaction.FolderName)))
            return false;
        string? reason = null;
        using var recoveryLock = held == null ? ModLock.TryTake(mod, out reason) : null;
        if (held == null && recoveryLock == null)
        {
            if (SaveTransaction.HasJournal(mod.Folder))
                blocked = reason;
            return false;
        }
        return SaveTransaction.Recover(mod.Folder, problems);
    }

    /// <summary>Releases the lock of the active mod and closes the session (see <see cref="Close"/>).</summary>
    public void Dispose()
    {
        modLock?.Dispose();
        Close();
    }

    /// <summary>Closes the handle of the lock and deletes no file, as a crash does. Then it closes the session (see
    /// <see cref="Close"/>). Only the tests call it.</summary>
    internal void Abandon()
    {
        modLock?.Abandon();
        Close();
    }

    /// <summary>
    /// Makes the session read-only after its lock is gone. The session then takes no edits and does not save, and it
    /// does not read, write or delete the recovery file. So it changes no file of the mod, which another editor can
    /// have open now.
    /// </summary>
    private void Close()
    {
        modLock = null;
        readOnlyReason = "The edit session is closed. Open the profile again.";
        HasRecovery = false;
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
        text = change.Undo(FileAt) ?? text;
        Revision++;
        Changed?.Invoke(change.Change);
    }

    /// <exception cref="InvalidOperationException">There is nothing to redo, or a transaction is open.</exception>
    public void Redo()
    {
        ThrowIfTransaction();
        var change = history.Redo();
        text = change.Redo(FileAt) ?? text;
        Revision++;
        Changed?.Invoke(change.Change);
    }

    /// <summary>Puts back the states before the transaction, then runs a command in it.</summary>
    internal void ApplyInTransaction(ChangeSet change, EditCommand command)
    {
        text = change.Undo(FileAt) ?? text;
        try
        {
            Run(change, command);
        }
        finally
        {
            Revision++;
            // Also when the command throws: the edit view is then back at its state before the transaction, not at
            // the state of the step before.
            if (!change.IsEmpty)
                Changed?.Invoke(change.Change);
        }
    }

    internal void EndTransaction(ChangeSet change, bool commit)
    {
        transaction = null;
        Revision++;
        if (commit)
        {
            Push(change);
            return;
        }
        text = change.Undo(FileAt) ?? text;
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
            text = change.Undo(FileAt) ?? text;
            throw;
        }
        finally
        {
            // Also a command that throws: it changed W and put back the states before.
            Revision++;
        }
    }

    /// <summary>Adds a change set to the history, when it changed anything.</summary>
    private void Push(ChangeSet change)
    {
        Revision++;
        change.Finish(FileAt, text);
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

    /// <summary>The INI document or the cutscene of a path that the session opened.</summary>
    private IEditFile FileAt(string path) => documents.TryGetValue(path, out var document) ? document : cutscenes[path];

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

    private ModOverlay BuildEditView(Mod active) => ModOverlay.Build(Profile.BaseFolder, [.. lower, active], stringWriter, thn);

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
    /// What a command can change: the INI documents and the cutscenes of the session, and the text. The scope records
    /// the state of each file before the first change to it, so the session can undo the command.
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

        /// <summary>A cutscene, to change it.</summary>
        /// <exception cref="ArgumentException">The edit view has no such cutscene, or the editor cannot read it.</exception>
        /// <exception cref="InvalidOperationException">The session or the cutscene is read-only.</exception>
        public ThnDocument Cutscene(string path)
        {
            if (!session.TryCutscene(path, out var document))
                throw new ArgumentException(session.CutsceneProblem(path), nameof(path));
            session.ThrowIfReadOnly();
            if (document.ReadOnlyReason != null)
                throw new InvalidOperationException(document.ReadOnlyReason);
            change.Capture(document);
            change.NoteCutscene(document.Path);
            return document;
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

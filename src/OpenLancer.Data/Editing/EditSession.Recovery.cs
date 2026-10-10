using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>
    /// True when the active mod has a recovery file from an earlier session that <see cref="RestoreRecovery"/> or
    /// <see cref="DiscardRecovery"/> did not handle yet. Until then, the session takes no edits. So the session never
    /// has edits that the recovery file cannot keep.
    /// </summary>
    public bool HasRecovery { get; private set; }

    /// <summary>
    /// Writes the unsaved edits to the recovery file of the active mod, or deletes the file when there are none.
    /// Call it after edits, so that a window close or a crash does not lose them (decision D8). Does nothing while
    /// <see cref="HasRecovery"/> is true, because the file then holds the edits of the earlier session.
    /// </summary>
    /// <returns>The problems: a file whose patch the writer cannot write is not in the recovery file.</returns>
    public IReadOnlyList<Diagnostic> WriteRecovery()
    {
        if (ActiveMod == null || HasRecovery)
            return [];
        var problems = new List<Diagnostic>();
        var patches = new List<RecoveredPatch>();
        foreach (var path in touched.Order(StringComparer.OrdinalIgnoreCase))
        {
            var document = documents[path];
            var patch = document.ChangedPatch();
            if (patch == null)
                continue;
            if (patch.Succeeded)
                patches.Add(new RecoveredPatch(path, document.SourceHash, patch.Sections.Count == 0 ? null : patch.Text));
            else
                problems.AddRange(patch.Problems);
        }
        var recoveredText = text.SameAs(savedText) ? null : new RecoveredText(textHash, text);
        var file = RecoveryFile.PathOf(ActiveMod.Folder);
        try
        {
            if (patches.Count == 0 && recoveredText == null)
                DeleteRecoveryFile();
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.WriteAllBytes(file, new RecoveryFile(patches, recoveredText).ToBytes());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(Diagnostic.Error(file, e.Message));
        }
        return problems;
    }

    /// <summary>
    /// Restores the edits of the recovery file as one entry of the edit history, then deletes the file. When a file
    /// of the mod changed after the editor wrote the recovery file, this restores nothing and returns the problems.
    /// The edits then do not apply, so <see cref="DiscardRecovery"/> is the next step.
    /// </summary>
    /// <exception cref="InvalidOperationException">A transaction is open.</exception>
    public IReadOnlyList<Diagnostic> RestoreRecovery()
    {
        ThrowIfTransaction();
        if (ActiveMod == null || !HasRecovery)
            return [];
        var file = RecoveryFile.PathOf(ActiveMod.Folder);
        RecoveryFile recovery;
        try
        {
            recovery = RecoveryFile.Read(file);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return [Diagnostic.Error(file, e.Message)];
        }

        var problems = new List<Diagnostic>();
        var change = new ChangeSet("Restore unsaved edits");
        var scope = new CommandScope(this, change);
        const string changed = "The mod changed after the editor wrote the recovery file, so the edits do not apply to it.";
        try
        {
            foreach (var patch in recovery.Patches)
            {
                var document = scope.Document(patch.Path);
                if (document.SourceHash != patch.KnownHash)
                {
                    problems.Add(Diagnostic.Error(patch.Path, changed));
                    continue;
                }
                var diagnostics = new List<Diagnostic>();
                var bytes = patch.Text == null ? null : Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(patch.Text)).ToArray();
                var recovered = document.Reopen(ActiveMod.Id, file, bytes, diagnostics);
                if (recovered.ReadOnlyReason != null)
                    problems.AddRange(diagnostics.Where(IsError));
                else
                    document.Restore(recovered.State);
            }
            if (recovery.Text != null)
            {
                if (recovery.Text.KnownHash != textHash)
                    problems.Add(Diagnostic.Error(Path.Combine(ActiveMod.Folder, Mod.StringsFile), changed));
                else
                    scope.Text = recovery.Text.Text;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            problems.Add(Diagnostic.Error(file, e.Message));
        }
        if (problems.Count > 0)
        {
            text = change.Undo(DocumentAt) ?? text;
            return problems;
        }
        change.NoteStructural();
        Push(change);
        DiscardRecovery();
        return [];
    }

    /// <summary>Deletes the recovery file of an earlier session.</summary>
    /// <exception cref="IOException">The file could not be deleted.</exception>
    public void DiscardRecovery()
    {
        if (ActiveMod == null)
            return;
        DeleteRecoveryFile();
        HasRecovery = false;
    }

    private void DeleteRecoveryFile()
    {
        var file = RecoveryFile.PathOf(ActiveMod!.Folder);
        if (File.Exists(file))
            File.Delete(file);
        var folder = Path.GetDirectoryName(file)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }
}

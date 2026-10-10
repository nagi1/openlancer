using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>The text of the active mod at the open or at the last save.</summary>
    private ModText savedText;

    /// <summary>The hash of the active mod's strings.json at the open or at the last save, or null when it had
    /// none.</summary>
    private string? textHash;

    /// <summary>
    /// Writes the edits as patch files and the text as strings.json of the active mod. The save writes all files
    /// or none (review finding F1).
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>For each file that a command changed, the writer makes the patch and checks it. A file whose patch
    /// does not change keeps its text (rule P2). A patch without sections deletes its file (P4).</item>
    /// <item>Any problem stops the save: a patch that the writer cannot write, a text placeholder that the text of
    /// the mod does not have (S8), a patch line of a text key that holds the id of a text of a mod as a number (S8),
    /// a strings.json with comments (F13). When the text changes, the save checks the
    /// placeholders of all patches and whole INI files of the mod, because strings.json gives the text of each.</item>
    /// <item>A change of the lower layer since the open stops the save (F11): the base data, a lower mod, or the mod
    /// list of the profile. The session writes the recovery file, and the profile must open again.</item>
    /// <item>A file of the active mod that changed outside the editor stops the save, unless
    /// <paramref name="overwrite"/> is true (P7).</item>
    /// <item><see cref="SaveTransaction"/> writes the files (P5, P6). Then W becomes what the saved files give
    /// (F14, P10), the edit view is built again (P11), and the history is saved.</item>
    /// </list>
    /// </remarks>
    /// <param name="overwrite">True to replace the files of the active mod that changed outside the editor.</param>
    /// <exception cref="InvalidOperationException">A transaction is open.</exception>
    public SaveResult Save(bool overwrite = false) => Save(overwrite, null);

    /// <param name="afterSteps">Called with k after k moves and deletes of the save transaction, from k = 0 at its
    /// commit point. The fault tests use it.</param>
    internal SaveResult Save(bool overwrite, Action<int>? afterSteps)
    {
        ThrowIfTransaction();
        if (readOnlyReason != null)
            return NotSaved([Diagnostic.Error(Profile.FilePath, readOnlyReason)]);
        var active = ActiveMod!;
        var problems = new List<Diagnostic>();
        bool recovered;
        Mod activeNow;
        ModProfile profileNow;
        try
        {
            // A save of this session that stopped after its commit point is decided, so it completes first.
            recovered = SaveTransaction.Recover(active.Folder, problems);
            activeNow = Mod.Load(active.Folder);
            profileNow = ModProfile.Load(Profile.FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ModLoadException)
        {
            problems.Add(Diagnostic.Error(active.Folder, e.Message));
            return NotSaved(problems);
        }
        if (recovered)
        {
            // The completed save changed the files of the mod.
            ActiveMod = activeNow;
            EditView = BuildEditView(activeNow);
        }

        var plan = Plan(active, activeNow, problems);
        if (problems.Any(IsError))
            return NotSaved(problems);

        var writes = plan.Where(p => !p.SameAsDisk).ToList();
        if (writes.Count > 0)
        {
            var stale = inputs.Changed(plan.Where(p => p.Document != null).Select(p => p.Document!.Path), profileNow, active);
            if (stale.Count > 0)
                return Stale(stale, problems);
        }

        var changedOnDisk = writes.Where(p => ContentHash.OfFile(p.Change.Path) != p.KnownHash).Select(p => p.Change.Path).ToList();
        if (changedOnDisk.Count > 0 && !overwrite)
        {
            problems.AddRange(changedOnDisk.Select(path => Diagnostic.Error(path,
                "Something outside the editor changed this file after the editor read it. Save again with overwrite " +
                "to replace it.")));
            return NotSaved(problems, changedOnDisk);
        }

        Exception? incomplete = null;
        if (writes.Count > 0)
        {
            try
            {
                incomplete = SaveTransaction.Commit(active.Folder, writes.Select(p => p.Change).ToList(), problems, afterSteps);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problems.Add(Diagnostic.Error(active.Folder, "The save wrote nothing: " + e.Message));
                return NotSaved(problems);
            }
            if (incomplete != null)
                problems.Add(Diagnostic.Warning(active.Folder,
                    $"A file did not move into place: {incomplete.Message} The save journal holds the save. The " +
                    "editor moves the other files at the next save, or when the profile opens again."));
        }
        AfterSave(plan, problems);
        return new SaveResult(true,
            writes.Where(p => p.Change.Content != null).Select(p => p.Change.Path).ToList(),
            writes.Where(p => p.Change.Content == null).Select(p => p.Change.Path).ToList(),
            problems, []) { Incomplete = incomplete != null };
    }

    /// <summary>
    /// The files that the edits change, with their new content. Adds a problem for each file that the save cannot
    /// write.
    /// </summary>
    /// <param name="activeNow">The active mod as it is on disk now.</param>
    private List<PlannedFile> Plan(Mod active, Mod activeNow, List<Diagnostic> problems)
    {
        var plan = new List<PlannedFile>();
        foreach (var path in touched.Order(StringComparer.OrdinalIgnoreCase))
        {
            var document = documents[path];
            var patch = document.ChangedPatch();
            if (patch == null)
                continue;
            if (!patch.Succeeded)
            {
                problems.AddRange(patch.Problems);
                continue;
            }
            problems.AddRange(MissingText(document));
            problems.AddRange(FrozenTextIds(document.Path, patch));
            if (activeNow.Files.ContainsKey(path))
            {
                problems.Add(Diagnostic.Error(path, $"The active mod now replaces {path} with a whole file. Open the " +
                                                    "profile again."));
                continue;
            }
            var patches = Path.Combine(active.Folder, Mod.PatchesFolder);
            var target = activeNow.Patches.GetValueOrDefault(path) ?? Path.Combine(patches, LocalPath(path));
            if (!PathRules.IsInside(target, patches))
            {
                problems.Add(Diagnostic.Error(path, $"The patch file {target} is not in the folder {patches}."));
                continue;
            }
            plan.Add(new PlannedFile(new FileChange(target, patch.Sections.Count == 0 ? null : patch.ToBytes()),
                document.SourceHash, document));
        }
        if (!text.SameAs(savedText))
        {
            // The new text is the text of each placeholder of the mod, also in the files that the save keeps.
            var written = plan.Select(p => p.Document!.Path).ToHashSet(VfsPath.Comparer);
            problems.AddRange(MissingText(activeNow, written));
            var path = Path.Combine(active.Folder, Mod.StringsFile);
            try
            {
                plan.Add(new PlannedFile(new FileChange(path, text.ToFile(path)), textHash, null));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add(Diagnostic.Error(path, "The editor cannot write the text into this file: " + e.Message));
            }
        }
        return plan;
    }

    /// <summary>
    /// Stops a save because the lower layer changed (F11), and keeps the unsaved edits in the recovery file.
    /// </summary>
    /// <param name="sources">What changed: install paths, <see cref="LayerInputs.TextInputs"/> or the profile file.</param>
    private SaveResult Stale(List<string> sources, List<Diagnostic> problems)
    {
        problems.AddRange(sources.Select(source => Diagnostic.Error(source,
            "The base data, a lower mod or the mod list of the profile changed after the profile opened, so the " +
            "edits apply to old data. Open the profile again.")));
        var file = RecoveryFile.PathOf(ActiveMod!.Folder);
        var notKept = WriteRecovery();
        problems.AddRange(notKept);
        problems.Add(notKept.Count == 0
            ? Diagnostic.Warning(file, "The editor kept the unsaved edits in this file. The next open of the profile " +
                                       "offers to restore them.")
            : Diagnostic.Error(file, "The editor could not keep all unsaved edits in this file. When the profile " +
                                     "opens again, the edits that are not in the file are lost."));
        return NotSaved(problems);
    }

    /// <summary>Makes the saved state the state of the session (rules P10 and P11).</summary>
    private void AfterSave(List<PlannedFile> plan, List<Diagnostic> problems)
    {
        var active = ActiveMod!;
        try
        {
            ActiveMod = Mod.Load(active.Folder);
        }
        catch (ModLoadException e)
        {
            problems.Add(Diagnostic.Warning(active.Folder, "The saved mod does not load: " + e.Message));
        }
        foreach (var file in plan)
        {
            if (file.Document == null)
            {
                textHash = file.Change.Content == null ? null : ContentHash.Of(file.Change.Content);
                continue;
            }
            // W becomes what the saved file gives, so it equals the state that a reload gives (F14).
            var diagnostics = new List<Diagnostic>();
            documents[file.Document.Path] = file.Document.Reopen(active.Id, file.Change.Path, file.Change.Content, diagnostics);
            problems.AddRange(diagnostics.Where(IsError).Select(d => d with { Severity = DiagnosticSeverity.Warning }));
        }
        savedText = text;
        history.MarkSaved();

        if (plan.Any(p => !p.SameAsDisk))
        {
            var before = EditView;
            EditView = BuildEditView(ActiveMod!);
            problems.AddRange(Diagnostic.NewIn(before.Diagnostics, EditView.Diagnostics).Where(IsError));
        }
        if (!HasRecovery)
        {
            try
            {
                DeleteRecoveryFile();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add(Diagnostic.Warning(RecoveryFile.PathOf(active.Folder), e.Message));
            }
        }
    }

    /// <summary>The text placeholders of W that the text of the session does not have (rule S8).</summary>
    private IEnumerable<Diagnostic> MissingText(EditDocument document)
    {
        for (var i = 0; i < document.Working.Count; i++)
        {
            foreach (var placeholder in document.Working[i].SelectMany(MissingPlaceholders))
            {
                var id = document.IdentityAt(i);
                yield return Diagnostic.Error(document.Path, $"{id}: {MissingTextMessage(placeholder)}") with
                {
                    Target = new SectionRef(document.Path, id),
                };
            }
        }
    }

    /// <summary>
    /// The text placeholders that the text of the session does not have, in the patches and the whole INI files of the
    /// active mod on disk.
    /// </summary>
    /// <param name="mod">The active mod as it is on disk now.</param>
    /// <param name="written">The install paths whose patch the save writes. They are not read from the disk.</param>
    private IEnumerable<Diagnostic> MissingText(Mod mod, IReadOnlySet<string> written)
    {
        foreach (var (file, section, entry) in ResolvedLines(mod, written.Contains))
        {
            foreach (var placeholder in MissingPlaceholders(entry))
            {
                var line = entry.Line > 0 ? $", line {entry.Line}" : "";
                yield return Diagnostic.Error(file, $"[{section.Name}]{line}: {MissingTextMessage(placeholder)}");
            }
        }
    }

    /// <summary>The text placeholders of the values of a line that the text of the session does not have.</summary>
    private IEnumerable<StringPlaceholder> MissingPlaceholders(Entry entry) =>
        Placeholders(entry).Where(p => text.Get(p.Kind, p.Key) == null);

    /// <summary>
    /// The lines of a patch that hold the id of a text of a mod as a number (rule S8). The merge gives the id from the
    /// position of the text in the generated text DLL, so the line shows other text when a mod adds text.
    /// </summary>
    private IEnumerable<Diagnostic> FrozenTextIds(string path, PatchText patch)
    {
        foreach (var written in patch.Sections)
        {
            var bytes = IniDocument.TextEncoding.GetBytes(string.Join("\n", written.Lines));
            foreach (var section in IniDocument.Parse(path, bytes).Sections)
            {
                foreach (var entry in section.Where(e => SchemaCatalog.TextKind(section.Name, e.Name) != null))
                {
                    if (entry.Count > 0 && entry[0] is Int32Value && entry[0].TryToInt32(out var id) && IsModTextId(id))
                    {
                        var message = $"{written.Id}: '{entry.Name} = {id}' holds the id of a text of a mod, which " +
                                      "changes when a mod adds text. Copy this text into the mod.";
                        yield return Diagnostic.Error(path, message) with { Target = new SectionRef(path, written.Id) };
                    }
                }
            }
        }
    }

    private string MissingTextMessage(StringPlaceholder placeholder) =>
        $"'{placeholder}' is not in the strings.json of mod '{ActiveMod!.Id}'. Add the text, or change the value.";

    /// <summary>
    /// The path of a new patch file, relative to the patches folder: the install path with the letter case of the
    /// file in the last lower mod that has it, or else in the base folder (rule P8).
    /// </summary>
    private string LocalPath(string installPath)
    {
        foreach (var mod in lower.Reverse())
        {
            if (mod.Patches.TryGetValue(installPath, out var patch))
                return Path.GetRelativePath(Path.Combine(mod.Folder, Mod.PatchesFolder), patch);
            if (mod.Files.TryGetValue(installPath, out var file))
                return Path.GetRelativePath(Path.Combine(mod.Folder, Mod.FilesFolder), file);
        }
        return PathRules.WithDiskCase(Profile.BaseFolder, installPath).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static SaveResult NotSaved(List<Diagnostic> problems, List<string>? changedOnDisk = null) =>
        new(false, [], [], problems, changedOnDisk ?? []);

    private static bool IsError(Diagnostic d) => d.Severity == DiagnosticSeverity.Error;

    /// <summary>One file of a save.</summary>
    /// <param name="KnownHash">The hash of the file when the session read it, or null when it did not exist.</param>
    /// <param name="Document">The document of a patch file, or null for strings.json.</param>
    private sealed record PlannedFile(FileChange Change, string? KnownHash, EditDocument? Document)
    {
        /// <summary>True when the file on disk already has the new content, so it needs no write.</summary>
        public bool SameAsDisk { get; } = Change.Content == null
            ? !File.Exists(Change.Path)
            : File.Exists(Change.Path) && File.ReadAllBytes(Change.Path).AsSpan().SequenceEqual(Change.Content);
    }
}

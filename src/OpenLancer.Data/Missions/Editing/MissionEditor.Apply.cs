using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions.Editing;

public sealed partial class MissionEditor
{
    /// <summary>The commit check of the open batch, or null.</summary>
    private (EditBatch Batch, BatchCheck Check)? current;

    /// <summary>The revision at the last change of the history that the editor saw.</summary>
    private long seenRevision;

    /// <summary>The result of each request, after its commit (section 4.4 of the M6b design).</summary>
    public event Action<MissionEditResult>? Applied;

    /// <summary>Each change of a mission script by the history: a request, an undo, a redo or a restore. It comes from
    /// <see cref="EditSession.Changed"/>.</summary>
    public event Action<MissionScriptsChange>? ScriptsChanged;

    /// <summary>Runs one command in the open batch of the caller, or in its own batch (section 4.0.4 of the M6b
    /// design).</summary>
    public MissionEditResult Apply(MissionCommand command, MissionEditOptions? options = null) =>
        Apply(new MissionBatch(command.HistoryLabel, [command]) { Options = options ?? MissionEditOptions.Default });

    /// <summary>
    /// Runs a batch in the open batch of the caller, or in its own batch (section 4.5 of the M6b design). A refusal of
    /// any command cancels the whole batch: nothing changes, and no history entry exists. In the batch of a caller, the
    /// caller commits, and the commit check of the layer runs then.
    /// </summary>
    public MissionEditResult Apply(MissionBatch batch) => Run(batch, preview: false);

    /// <summary>A preview: runs a batch, reports the result, and puts back the state before. No history entry. The
    /// revision and the script versions stay.</summary>
    public MissionEditResult Preview(MissionBatch batch) => Run(batch, preview: true);

    private MissionEditResult Run(MissionBatch batch, bool preview)
    {
        if (Refusal(batch) is { } refusal)
            return Refused(batch, [refusal]);
        var open = session.OpenBatch;
        if (open != null && preview)
            return Refused(batch, [MissionErrors.BusyError() with
            {
                Message = "A batch is open, so the editor cannot show a preview now. Complete the batch, then try again.",
            }]);
        EditBatch editBatch;
        try
        {
            editBatch = open ?? session.BeginBatch(batch.EntryLabel, batch.Origin);
        }
        catch (InvalidOperationException e)
        {
            return Refused(batch, [MissionErrors.NotEditableError(e.Message)]);
        }
        var check = CheckOf(editBatch);
        check.Join(batch.Options);
        var plan = new MissionPlanCommand(batch.EntryLabel, this, batch.Commands);
        try
        {
            editBatch.Run(plan);
        }
        catch (EditException e)
        {
            return Refused(batch, [e.Error]);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return Refused(batch, [new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, e.Message)
            {
                Index = plan.FailedIndex >= 0 ? plan.FailedIndex : null,
            }]);
        }
        var draft = plan.Draft!;
        check.Add(draft);
        var confirmations = draft.Confirmations.ToList();
        if (batch.Origin == EditOrigin.Agent && draft.Missing.Count > 0)
            confirmations.Add(MissionErrors.MissingLinesError(draft.Missing.Count));
        if (confirmations.Count > 0 && !batch.Confirm)
        {
            editBatch.Cancel();
            return Refused(batch, confirmations) with { NeedsConfirmation = true, Missing = draft.Missing };
        }
        var inverse = InverseOf(draft);
        editBatch.Report(Part(draft));
        if (open != null)
        {
            // The caller commits. The layer gives its event after that commit.
            void Committed(EditResult result)
            {
                editBatch.Committed -= Committed;
                if (result.Status == EditStatus.Applied)
                    Applied?.Invoke(Final(result, draft, inverse));
            }
            editBatch.Committed += Committed;
            return MissionEditResult.From(Part(draft)) with
            {
                Label = batch.EntryLabel,
                Inverse = inverse,
                RowMoves = draft.RowMoves,
                Revision = session.Revision,
            };
        }
        var final = Final(preview ? editBatch.Preview() : editBatch.Commit(), draft, inverse);
        if (final.Status == EditStatus.Applied)
            Applied?.Invoke(final);
        return final;
    }

    /// <summary>The refusals before a batch runs: the session takes no edits, a transaction is open, or an expected
    /// version or the expected revision differs (section 4.5 of the M6b design).</summary>
    private EditError? Refusal(MissionBatch batch)
    {
        if (session.ReadOnlyReason is { } reason)
            return MissionErrors.NotEditableError(reason);
        if (session.InTransaction)
            return MissionErrors.BusyError();
        if (session.RevisionGuard(batch.IfRevision) is { } stale)
            return stale;
        foreach (var (script, expected) in batch.ExpectVersions)
        {
            var path = InstallPaths.TryParse(script) ?? script;
            if (Version(path) != expected)
                return MissionErrors.ScriptChangedError(path);
        }
        return null;
    }

    private MissionEditResult Refused(MissionBatch batch, IReadOnlyList<EditError> errors) =>
        new(EditStatus.Refused)
        {
            Label = batch.EntryLabel,
            Errors = errors,
            Revision = session.Revision,
            Summary = errors.Count > 0 ? errors[0].Message : null,
        };

    /// <summary>The mission result of a commit or a preview, with the inverse and the versions of the touched
    /// scripts.</summary>
    private MissionEditResult Final(EditResult result, MissionDraft draft, MissionCommand? inverse)
    {
        var scripts = draft.Touched.Values.Where(d => d.HoldsScript).Select(d => d.Path);
        return MissionEditResult.From(result) with
        {
            Inverse = result.Status is EditStatus.Applied or EditStatus.Previewed ? inverse : null,
            RowMoves = result.Status is EditStatus.Applied or EditStatus.Previewed ? draft.RowMoves : [],
            Versions = scripts.ToDictionary(p => p, Version, VfsPath.Comparer),
            Summary = result.Status switch
            {
                EditStatus.Applied => $"The editor applied '{result.Label}'.",
                EditStatus.Previewed => $"The preview of '{result.Label}' changed nothing.",
                EditStatus.NoChange => $"'{result.Label}' changed nothing.",
                _ => result.Errors.Count > 0 ? result.Errors[0].Message : null,
            },
        };
    }

    /// <summary>The part of the result of the plan: the adds, changes, deletes, renames, "Missing" lines, notes and
    /// the selection.</summary>
    private static EditResult Part(MissionDraft draft) => new(EditStatus.Applied)
    {
        Adds = draft.Adds.Distinct().ToList(),
        Changes = draft.Changes.Distinct().Where(c => !draft.Adds.Contains(c)).ToList(),
        Deletes = draft.Deletes,
        Renames = draft.Renames,
        Missing = draft.Missing,
        Notes = draft.Notes.Distinct().ToList(),
        Warnings = draft.Warnings,
        Select = draft.Select,
    };

    /// <summary>
    /// The inverse of a batch (section 4.8 of the M6b design): a group of the inverses in reverse order, with a body
    /// guard for each section that the batch changed or added. The guard is the engine-form hash of the section in W
    /// after the batch (decision D666). The inverse holds no script version, so an edit of another section does not
    /// stop it.
    /// </summary>
    private MissionCommand? InverseOf(MissionDraft draft)
    {
        if (draft.Inverses.Count == 0)
            return null;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, before) in draft.Before)
        {
            var document = draft.Touched[path];
            if (!document.State.Exists)
                continue;
            var old = new HashSet<LibreLancer.Data.Ini.Section>(before.Working, ReferenceEqualityComparer.Instance);
            for (var i = 0; i < document.Working.Count; i++)
            {
                if (!old.Contains(document.Working[i]))
                    bodies[ItemRefs.Canonical(session, new SectionRef(path, document.IdentityAt(i)))] =
                        SectionHash.Of(document.Working[i]);
            }
        }
        var commands = draft.Inverses.AsEnumerable().Reverse().ToList();
        return new CommandGroup(commands) { ExpectBodies = bodies };
    }

    private BatchCheck CheckOf(EditBatch batch)
    {
        if (current is var (known, check) && ReferenceEquals(known, batch) && batch.IsOpen)
            return check;
        var made = new BatchCheck(this);
        batch.AddCommitCheck(made);
        current = (batch, made);
        return made;
    }

    /// <summary>Gives the new versions and the trigger renames of each change of the history (section 4.2 of the M6b
    /// design). A step of a transaction, a preview and a cancel do not change the revision, so they change no
    /// version.</summary>
    private void OnHistory(EditChange change)
    {
        if (session.Revision == seenRevision)
            return;
        seenRevision = session.Revision;
        var changed = new Dictionary<string, long>(VfsPath.Comparer);
        foreach (var path in change.Files)
        {
            if (session.OpenedDocument(path) is { HoldsScript: true })
            {
                versions[path] = Version(path) + 1;
                changed[path] = versions[path];
            }
        }
        if (changed.Count == 0)
            return;
        var renames = change.Renames
            .Where(r => MissionIndex.Is(r.From.Id.Type, "Trigger") && r.From.Id.Key != null && r.To.Id.Key != null)
            .Select(r => (new TriggerRef(r.From.Path, r.From.Id.Key!), new TriggerRef(r.To.Path, r.To.Id.Key!)))
            .ToList();
        ScriptsChanged?.Invoke(new MissionScriptsChange(changed, renames));
    }

    /// <summary>
    /// The commit check of the mission part of a batch (section 4.5 of the M6b design): it builds the model of each
    /// touched script once, compares the problems with the problems before the batch by their subject with counts, and
    /// refuses a batch that adds an error in strict mode. A "Missing" line of a delete or a rename does not count.
    /// </summary>
    private sealed class BatchCheck(MissionEditor editor) : IBatchCheck
    {
        private readonly Dictionary<string, (EditDocument Document, DocumentState Before)> scripts = new(VfsPath.Comparer);
        private readonly HashSet<string> gone = new(StringComparer.OrdinalIgnoreCase);
        private MissionEditOptions options = MissionEditOptions.Default;

        public void Join(MissionEditOptions joined) => options = new MissionEditOptions(
            options.RejectNewErrors || joined.RejectNewErrors, options.RejectNewWarnings || joined.RejectNewWarnings);

        public void Add(MissionDraft draft)
        {
            foreach (var (path, before) in draft.Before)
            {
                var document = draft.Touched[path];
                if (document.HoldsScript)
                    scripts.TryAdd(path, (document, before));
            }
            gone.UnionWith(draft.GoneNames);
        }

        public EditResult Check(EditBatch batch)
        {
            var added = new List<Problem>();
            var fixedProblems = new List<Problem>();
            var newErrors = new List<Problem>();
            foreach (var (path, (document, before)) in scripts)
            {
                var old = before.Exists ? editor.ModelOf(document, before).Problems : [];
                var now = document.State.Exists ? editor.ModelOf(document, document.State).Problems : [];
                var (plus, minus) = Compare(old, now);
                added.AddRange(plus.Select(p => ProblemOf(path, p)));
                fixedProblems.AddRange(minus.Select(p => ProblemOf(path, p)));
                newErrors.AddRange(plus.Where(p => Counts(p)).Select(p => ProblemOf(path, p)));
            }
            var errors = newErrors.Count > 0
                ? [MissionErrors.NewErrorsError(newErrors.Count, newErrors[0].Message)]
                : new List<EditError>();
            return new EditResult(EditStatus.Applied)
            {
                NewProblems = added,
                FixedProblems = fixedProblems,
                Errors = errors,
            };
        }

        /// <summary>True when a new problem refuses the batch in strict mode.</summary>
        private bool Counts(Diagnostic problem)
        {
            var refuses = problem.Severity == DiagnosticSeverity.Error && options.RejectNewErrors ||
                          problem.Severity == DiagnosticSeverity.Warning && options.RejectNewWarnings;
            if (!refuses)
                return false;
            // A "Missing" line of a delete or a rename names a name that the batch took away (decision D81).
            return !(problem.Rule is "M05" or "M06" or "M21" or "M22" or "M23" && problem.Subject is { } subject &&
                     gone.Any(n => subject.EndsWith("|" + n, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>The problems that are new and the problems that went, by their subject with counts, as
        /// <see cref="MissionOrigins"/> compares them (review finding RB26).</summary>
        private static (List<Diagnostic> Added, List<Diagnostic> Fixed) Compare(IReadOnlyList<Diagnostic> before,
            IReadOnlyList<Diagnostic> after) => (Minus(after, before), Minus(before, after));

        /// <summary>The problems of a list that the other list does not have, by subject with counts.</summary>
        private static List<Diagnostic> Minus(IReadOnlyList<Diagnostic> list, IReadOnlyList<Diagnostic> other)
        {
            static string Key(Diagnostic d) => d.Subject ?? d.Message;
            var counts = other.CountBy(Key).ToDictionary(c => c.Key, c => c.Value);
            var result = new List<Diagnostic>();
            foreach (var problem in list)
            {
                var key = Key(problem);
                if (counts.TryGetValue(key, out var n) && n > 0)
                    counts[key] = n - 1;
                else
                    result.Add(problem);
            }
            return result;
        }

        private Problem ProblemOf(string path, Diagnostic problem)
        {
            string? reference = null;
            if (problem.Target is { } target)
                reference = problem.EntryIndex >= 0
                    ? new RowRef(new ItemRef(target.Path, target.Id), problem.EntryIndex).Reference
                    : ItemRefs.Canonical(editor.session, target);
            return Problem.From(problem, reference ?? new ScriptRef(path).ToString());
        }
    }
}

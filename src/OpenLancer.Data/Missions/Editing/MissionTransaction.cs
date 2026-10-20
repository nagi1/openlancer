using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// One history entry for a gesture with many steps, for example a drag of a number field or a typed text (section 4.11
/// of the M6b design). Each <see cref="Apply"/> puts back the state before the transaction and runs a batch, so it takes
/// the place of the step before. A step runs no analysis on the thread of the session: <see cref="AnalysisWork"/> gives
/// a function that a worker runs over a snapshot of W. <see cref="Commit"/> runs the full analysis and the strict check,
/// and pushes one history entry.
/// </summary>
public sealed class MissionTransaction : IDisposable
{
    private readonly MissionEditor editor;
    private readonly EditTransaction transaction;
    private readonly MissionEditOptions options;
    private readonly string label;

    /// <summary>The names of the game data before the transaction, for the problems before it.</summary>
    private readonly MissionInputs names;

    private MissionDraft? draft;
    private bool ended;

    internal MissionTransaction(MissionEditor editor, EditTransaction transaction, string label, MissionEditOptions options,
        MissionInputs names)
    {
        this.editor = editor;
        this.transaction = transaction;
        this.label = label;
        this.options = options;
        this.names = names;
    }

    /// <summary>True until the commit or the cancel.</summary>
    public bool IsOpen => !ended;

    /// <summary>
    /// Puts back the state before the transaction and runs the batch. The batch keeps the rules of
    /// <see cref="MissionEditor.Apply(MissionBatch)"/>: the expected revision and versions, and the confirmations of the
    /// plan. A refusal leaves the state before the transaction, and the transaction stays open.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transaction has ended.</exception>
    public MissionEditResult Apply(MissionBatch batch)
    {
        ThrowIfEnded();
        if (editor.Refusal(batch) is { } refusal)
            return Refuse([refusal]);
        var plan = new MissionPlanCommand(label, editor, batch.Commands);
        try
        {
            transaction.Apply(plan);
        }
        catch (EditException e)
        {
            draft = null;
            return Refused([e.Error]);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            draft = null;
            return Refused([new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, e.Message)]);
        }
        draft = plan.Draft;
        if (draft!.Confirmations.Count > 0 && !batch.Confirm)
            return Refuse(draft.Confirmations.DistinctBy(c => (c.Detail, c.Message)).ToList()) with { NeedsConfirmation = true };
        return new MissionEditResult(EditStatus.Applied) { Label = label, Changes = draft.Changes, Notes = draft.Notes };
    }

    /// <summary>Puts back the state before the transaction, and gives a refusal.</summary>
    private MissionEditResult Refuse(IReadOnlyList<EditError> errors)
    {
        transaction.Apply(new MissionPlanCommand(label, editor, []));
        draft = null;
        return Refused(errors);
    }

    private MissionEditResult Refused(IReadOnlyList<EditError> errors) =>
        new(EditStatus.Refused) { Label = label, Errors = errors, Summary = errors[0].Message, Revision = editor.Session.Revision };

    /// <summary>
    /// A function that analyzes the touched scripts of the last step over a snapshot of W. The function reads only
    /// immutable data, so a worker can run it while the user drags. The editor drops the result of an older step.
    /// </summary>
    public Func<IReadOnlyList<MissionAnalysis>> AnalysisWork()
    {
        ThrowIfEnded();
        return editor.AnalysisWork(draft?.Touched.Values.Where(d => d is { HoldsScript: true, State.Exists: true }).ToList() ?? []);
    }

    /// <summary>
    /// Runs the full analysis and the strict check, then adds one entry to the history when the steps changed data. A
    /// check that refuses cancels the transaction, and the result has the errors.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transaction has ended.</exception>
    public MissionEditResult Commit()
    {
        ThrowIfEnded();
        var revision = editor.Session.Revision;
        EditResult part = new(EditStatus.Applied);
        if (draft != null)
        {
            part = editor.CheckTransaction(draft, options, names);
            if (part.Errors.Count > 0)
            {
                Cancel();
                return MissionEditResult.From(part) with
                {
                    Status = EditStatus.Refused, Label = label, Revision = editor.Session.Revision,
                    Summary = part.Errors[0].Message,
                };
            }
        }
        ended = true;
        transaction.Commit();
        var applied = editor.Session.Revision != revision;
        var result = MissionEditResult.From(part) with
        {
            Status = applied ? EditStatus.Applied : EditStatus.NoChange,
            Inverse = applied && draft != null ? editor.InverseOf(draft) : null,
            RowMoves = applied && draft != null ? draft.RowMoves : [],
            Label = label,
            UndoLabel = applied ? label : null,
            Revision = editor.Session.Revision,
            Changes = draft?.Changes.Distinct().ToList() ?? [],
            Notes = draft?.Notes.Distinct().ToList() ?? [],
            Versions = draft?.Touched.Values.Where(d => d.HoldsScript)
                .ToDictionary(d => d.Path, d => editor.Version(d.Path), VfsPath.Comparer) ?? new Dictionary<string, long>(),
        };
        if (applied)
            editor.RaiseApplied(result);
        return result;
    }

    /// <summary>Puts back the state before the transaction and ends it. Does nothing after the end.</summary>
    public void Cancel()
    {
        if (ended)
            return;
        ended = true;
        transaction.Cancel();
    }

    public void Dispose() => Cancel();

    private void ThrowIfEnded()
    {
        if (ended)
            throw new InvalidOperationException("The transaction has ended.");
    }
}

public sealed partial class MissionEditor
{
    /// <summary>The edit session of the editor.</summary>
    internal EditSession Session => session;

    /// <summary>
    /// Opens a transaction: one history entry for a drag or a text field (section 4.11 of the M6b design).
    /// </summary>
    /// <exception cref="EditException"><c>NotEditable</c> or <c>Busy</c>.</exception>
    public MissionTransaction Begin(string label, MissionEditOptions? options = null)
    {
        if (session.ReadOnlyReason is { } reason)
            throw new EditException(MissionErrors.NotEditableError(reason));
        if (session.InTransaction || session.OpenBatch != null)
            throw new EditException(MissionErrors.BusyError());
        var names = WorldNow();
        return new MissionTransaction(this, session.Begin(label), label, options ?? MissionEditOptions.Default, names);
    }

    /// <summary>The check of the last step of a transaction: the new and the fixed problems, and the errors of strict
    /// mode.</summary>
    /// <param name="names">The names of the game data before the transaction.</param>
    internal EditResult CheckTransaction(MissionDraft draft, MissionEditOptions options, MissionInputs names)
    {
        var check = new BatchCheck(this, names);
        check.Join(options);
        check.Add(draft, draft.ModTextBefore);
        return check.Check();
    }

    /// <summary>Raises <see cref="Applied"/> for a request that the editor did not run as a batch.</summary>
    internal void RaiseApplied(MissionEditResult result) => Applied?.Invoke(result);

    /// <summary>
    /// A function that analyzes the states of W of some scripts now. The overlay and the base-game runs come from the
    /// thread of the session, so the function reads only immutable data.
    /// </summary>
    internal Func<IReadOnlyList<MissionAnalysis>> AnalysisWork(IReadOnlyList<EditDocument> scripts)
    {
        var snapshot = world.Inputs();
        cache.Use(snapshot);
        var overlay = new Dictionary<string, MissionOverlayFile>(session.MissionOverlay(), VfsPath.Comparer);
        var inputs = snapshot.WithOverlay(overlay);
        var work = new List<(string Path, IReadOnlyList<LibreLancer.Data.Ini.Section> Sections, bool Binary, MissionRun.BaseRun? Base)>();
        foreach (var document in scripts)
        {
            var state = document.State;
            overlay[document.Path] = session.OverlayOf(document, state);
            work.Add((document.Path, state.Working, document.IsBinary, cache.BaseRun(MissionRun.Info(inputs, document.Path))));
        }
        inputs = snapshot.WithOverlay(overlay);
        return () => work.Select(w => MissionRun.AnalyzeScript(inputs, w.Path, w.Sections, w.Binary, _ => w.Base)).ToList();
    }

    /// <summary>
    /// A function that analyzes a script with other sections in place of W, for example the parsed text of a raw edit
    /// while the user types (section 8.8 of the M6b design). The overlay and the base-game run come from the thread of
    /// the session, so the function reads only immutable data.
    /// </summary>
    internal Func<MissionAnalysis> DraftWork(EditDocument document, IReadOnlyList<LibreLancer.Data.Ini.Section> sections)
    {
        var snapshot = world.Inputs();
        cache.Use(snapshot);
        var overlay = new Dictionary<string, MissionOverlayFile>(session.MissionOverlay(), VfsPath.Comparer)
        {
            [document.Path] = session.OverlayOf(document,
                document.State with { Working = System.Collections.Immutable.ImmutableList.CreateRange(sections) }),
        };
        var inputs = snapshot.WithOverlay(overlay);
        var baseRun = cache.BaseRun(MissionRun.Info(inputs, document.Path));
        var path = document.Path;
        var binary = document.IsBinary;
        return () => MissionRun.AnalyzeScript(inputs, path, sections, binary, _ => baseRun);
    }
}

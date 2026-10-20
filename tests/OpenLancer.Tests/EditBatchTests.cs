using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The batches of the shared command layer (section 4.0.4 of the M6b design, MCP1 section 13.1 item 4): steps on top of
/// each other, one history entry, commit checks, cancel and preview, and the edit origins of the history.
/// </summary>
public sealed class EditBatchTests
{
    [Fact]
    public void StepsApplyOnTopAndACommitIsOneEntryWithOneChange()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        var batch = session.BeginBatch("Price and volume");
        batch.Run(Set(Good("g1"), "price", new Int32Value(300)));
        // The second step reads the result of the first step.
        Assert.Equal(["300"], Values(session, Good("g1"), "price"));
        batch.Run(Set(Good("g2"), "price", new Int32Value(400)));
        Assert.Empty(changes);
        var result = batch.Commit();

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Price and volume", result.Label);
        Assert.Equal("Price and volume", result.UndoLabel);
        Assert.Equal([GoodsPath], result.Files);
        Assert.Single(changes);
        Assert.Equal([Good("g1"), Good("g2")], changes[0].Sections);
        Assert.Equal([GoodsPath], session.EditedPaths);
        Assert.Equal("Price and volume", session.UndoLabel);
        Assert.Null(session.OpenBatch);

        session.Undo();
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.Equal(["200"], Values(session, Good("g2"), "price"));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ACancelPutsBackTheExactStatesWithoutAChange()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "price", new Int32Value(1)));
        var document = session.OpenDocument(GoodsPath)!;
        var before = document.State;
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        var batch = session.BeginBatch();
        batch.Run(Set(Good("g1"), "price", new Int32Value(2)));
        batch.Run(Set(Good("g2"), "price", new Int32Value(3)));
        batch.Run(Set(Gun("gun1"), "damage", new Int32Value(4)));
        batch.Cancel();

        Assert.Same(before, document.State);
        Assert.Same(session.OpenDocument(WeaponsPath)!.Initial, session.OpenDocument(WeaponsPath)!.State);
        Assert.Empty(changes);
        Assert.Equal([GoodsPath], session.EditedPaths);
        Assert.Equal("Set price", session.UndoLabel);
        Assert.False(batch.IsOpen);
        Assert.Null(session.OpenBatch);
        // A cancel after the end does nothing.
        batch.Cancel();
    }

    [Fact]
    public void WhileABatchIsOpenTheSessionRefusesOtherEdits()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(Set(Good("g1"), "price", new Int32Value(1)));
        session.Undo();

        var batch = session.BeginBatch("Batch");
        Assert.Same(batch, session.OpenBatch);
        Assert.Throws<InvalidOperationException>(() => session.BeginBatch());
        Assert.Throws<InvalidOperationException>(() => session.Execute(Set(Good("g1"), "price", new Int32Value(2))));
        Assert.Throws<InvalidOperationException>(() => session.Begin("Drag"));
        Assert.Throws<InvalidOperationException>(session.Redo);
        Assert.Throws<InvalidOperationException>(() => session.Save());
        batch.Run(Set(Good("g1"), "price", new Int32Value(5)));
        Assert.Throws<InvalidOperationException>(session.Undo);
        batch.Commit();

        // After the commit, the session takes edits again, and a transaction blocks a batch.
        using var transaction = session.Begin("Drag");
        Assert.Throws<InvalidOperationException>(() => session.BeginBatch());
    }

    [Fact]
    public void AStepThatThrowsCancelsTheWholeBatch()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var before = session.OpenDocument(GoodsPath)!.State;
        var revision = session.Revision;

        var batch = session.BeginBatch("Batch");
        batch.Run(Set(Good("g1"), "price", new Int32Value(7)));
        Assert.Throws<ArgumentException>(() => batch.Run(Set(Good("missing"), "price", new Int32Value(8))));

        Assert.False(batch.IsOpen);
        Assert.Null(session.OpenBatch);
        Assert.Same(before, session.OpenDocument(GoodsPath)!.State);
        Assert.False(session.CanUndo);
        Assert.Equal(revision, session.Revision);
        Assert.Throws<InvalidOperationException>(() => batch.Run(Set(Good("g1"), "price", new Int32Value(9))));
    }

    [Fact]
    public void ACommitCheckWithAnErrorRefusesAndCancels()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;
        var error = new EditError(EditErrorKind.NewErrors, "NewErrors", "The edit adds 1 error, so the editor did not apply it.");
        var check = new FakeCheck(new EditResult(EditStatus.Applied) { Errors = [error] });

        var batch = session.BeginBatch("Checked");
        batch.Run(Set(Good("g1"), "price", new Int32Value(7)));
        batch.AddCommitCheck(check);
        batch.AddCommitCheck(check);
        var result = batch.Commit();

        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.Equal([error], result.Errors);
        Assert.Equal(1, check.Runs);
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.False(session.CanUndo);
        Assert.Empty(changes);
        Assert.Null(session.OpenBatch);
    }

    [Fact]
    public void TheChecksRunOnceAndTheirPartsJoinTheResult()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var problem = new Problem(OpenLancer.Data.DiagnosticSeverity.Warning, "A warning of the check.");
        var check = new FakeCheck(new EditResult(EditStatus.Applied) { NewProblems = [problem] });

        var batch = session.BeginBatch(null);
        batch.Run(Set(Good("g1"), "price", new Int32Value(7)));
        batch.AddCommitCheck(check);
        batch.Report(new EditResult(EditStatus.Applied) { Changes = ["sec:DATA\\EQUIPMENT\\goods.ini#Good/g1"] });
        var finished = batch.Finish();
        var result = batch.Commit();

        Assert.Equal(1, check.Runs);
        Assert.Equal(EditStatus.Applied, finished.Status);
        Assert.Equal([problem], result.NewProblems);
        Assert.Equal(["sec:DATA\\EQUIPMENT\\goods.ini#Good/g1"], result.Changes);
        // A batch without a label takes the label of its first step.
        Assert.Equal("Set price", result.Label);
        Assert.Equal("Set price", session.UndoLabel);
    }

    [Fact]
    public void ABatchWithoutAChangeGivesNoChangeAndNoEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var revision = session.Revision;

        // A revert of a section that is as the lower layer has it changes no state.
        var batch = session.BeginBatch("Revert");
        batch.Run(EditCommand.Revert(Good("g1")));
        var result = batch.Commit();

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.Null(result.UndoLabel);
        Assert.False(session.CanUndo);
        Assert.Equal(revision, session.Revision);
    }

    [Fact]
    public void APreviewGivesTheResultAndChangesNothing()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var before = session.OpenDocument(GoodsPath)!.State;
        var revision = session.Revision;
        var changes = new List<EditChange>();
        session.Changed += changes.Add;
        var check = new FakeCheck(new EditResult(EditStatus.Applied));

        var batch = session.BeginBatch("Preview", EditOrigin.Agent);
        batch.Run(Set(Good("g1"), "price", new Int32Value(7)));
        batch.AddCommitCheck(check);
        var result = batch.Preview();

        Assert.Equal(EditStatus.Previewed, result.Status);
        Assert.Equal(1, check.Runs);
        Assert.Equal([GoodsPath], result.Files);
        Assert.Same(before, session.OpenDocument(GoodsPath)!.State);
        Assert.Equal(revision, session.Revision);
        Assert.False(session.IsDirty);
        Assert.Empty(session.EditedPaths);
        Assert.False(session.CanUndo);
        Assert.Empty(changes);
    }

    [Fact]
    public void TheHistoryKeepsWhoMadeAndWhoUndidEachEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Assert.Null(session.UndoOrigin);

        session.Execute(Set(Good("g1"), "price", new Int32Value(1)));
        var batch = session.BeginBatch("Set the price", EditOrigin.Agent);
        batch.Run(Set(Good("g2"), "price", new Int32Value(2)));
        var result = batch.Commit();

        Assert.Equal("Agent: Set the price", result.UndoLabel);
        Assert.Equal("Agent: Set the price", session.UndoLabel);
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);
        session.Undo(EditOrigin.Agent);
        Assert.Equal(EditOrigin.Agent, session.RedoOrigin);
        Assert.Equal(EditOrigin.Agent, session.RedoUndoneBy);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        session.Redo();
        session.Undo();
        Assert.Equal(EditOrigin.User, session.RedoUndoneBy);
    }

    [Fact]
    public void ACheckThatThrowsCancelsTheBatch()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var before = session.OpenDocument(GoodsPath)!.State;

        var commit = session.BeginBatch("Commit");
        commit.Run(Set(Good("g1"), "price", new Int32Value(7)));
        commit.AddCommitCheck(new ThrowingCheck());
        Assert.Throws<InvalidOperationException>(() => commit.Commit());

        Assert.False(commit.IsOpen);
        Assert.Null(session.OpenBatch);
        Assert.Same(before, session.OpenDocument(GoodsPath)!.State);

        var preview = session.BeginBatch("Preview");
        preview.Run(Set(Good("g1"), "price", new Int32Value(8)));
        preview.AddCommitCheck(new ThrowingCheck());
        Assert.Throws<InvalidOperationException>(() => preview.Preview());

        // The session takes edits, an undo and a save again.
        Assert.Null(session.OpenBatch);
        Assert.Same(before, session.OpenDocument(GoodsPath)!.State);
        session.Execute(Set(Good("g1"), "price", new Int32Value(9)));
        session.Undo();
        Assert.True(session.Save().Saved);
    }

    [Fact]
    public void APartWithAnErrorRefusesTheBatchAndTheResultKeepsEachError()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var refused = new EditError(EditErrorKind.Refused, "PlanRefused", "The member refuses the edit.");
        var newErrors = new EditError(EditErrorKind.NewErrors, "NewErrors", "The edit adds 1 error, so the editor did not apply it.");

        var batch = session.BeginBatch("Refused");
        batch.Run(Set(Good("g1"), "price", new Int32Value(7)));
        batch.Report(new EditResult(EditStatus.Refused) { Errors = [refused] });
        batch.AddCommitCheck(new FakeCheck(new EditResult(EditStatus.Applied) { Errors = [newErrors] }));
        var result = batch.Commit();

        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.Equal([refused, newErrors], result.Errors);
        Assert.Equal(["100"], Values(session, Good("g1"), "price"));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void TheRevisionGuardRefusesARequestAfterAChange()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var read = session.Revision;

        Assert.Null(session.RevisionGuard(null));
        Assert.Null(session.RevisionGuard(read));
        session.Execute(Set(Good("g1"), "price", new Int32Value(7)));
        var error = session.RevisionGuard(read);

        Assert.NotNull(error);
        Assert.Equal(EditErrorKind.StaleRevision, error.Kind);
        Assert.Equal(EditSession.RevisionChanged, error.Detail);
        Assert.Equal($"The edit view changed after revision {read}. Read the items again, then edit.", error.Message);
        Assert.Null(session.RevisionGuard(session.Revision));
    }

    /// <summary>A commit check that gives a fixed part and counts its runs.</summary>
    private sealed class FakeCheck(EditResult part) : IBatchCheck
    {
        public int Runs { get; private set; }

        public EditResult Check(EditBatch batch)
        {
            Runs++;
            return part;
        }
    }

    /// <summary>A commit check that fails.</summary>
    private sealed class ThrowingCheck : IBatchCheck
    {
        public EditResult Check(EditBatch batch) => throw new InvalidOperationException("The check failed.");
    }
}

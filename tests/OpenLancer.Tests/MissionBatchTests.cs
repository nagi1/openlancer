using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The batches of the mission editor (sections 4.0.4, 4.0.5 and 4.5 of the M6b design).</summary>
public sealed class MissionBatchTests
{
    [Fact]
    public void ARefusalAtCommandKChangesNothingAndAddsNoHistoryEntry()
    {
        using var m = Create();
        var before = m.State();
        var revision = m.Session.Revision;

        var result = m.Missions.Apply(new MissionBatch("Two edits",
            [new AddTrigger(TestScript), new RemoveTrigger(T("ol_nowhere"))]));

        Assert.Equal(EditStatus.Refused, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal((EditErrorKind.NotFound, MissionErrors.TriggerNotFound, 1), (error.Kind, error.Detail, error.Index));
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
        Assert.Equal(revision, m.Session.Revision);
        Assert.Null(m.Session.OpenBatch);
    }

    [Fact]
    public void ABatchIsOneHistoryEntryAndALaterCommandSeesAnEarlierOne()
    {
        using var m = Create();
        var before = m.State();

        var result = m.Missions.Apply(new MissionBatch("Add a wave",
        [
            new AddTrigger(TestScript) { Nickname = "ol_wave_2", Condition = LineSpec.Of("Cnd_True") },
            new AddAction(T("ol_wave_2"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_end"))),
            new RenameTrigger(T("ol_wave_2"), "ol_wave_3"),
        ]));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Add a wave", m.Session.UndoLabel);
        Assert.Equal(["nickname = ol_wave_3", "Cnd_True =", "Act_ActTrig = ol_end"], m.Lines("ol_wave_3"));
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void ABatchWithoutAChangeGivesNoChangeAndNoEntry()
    {
        using var m = Create();

        var result = m.Missions.Apply(new MissionBatch("Nothing", [new SetTriggerStart(T("ol_start"), true)]));

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.False(m.Session.CanUndo);
        Assert.Null(result.Inverse);
    }

    [Fact]
    public void StrictModeRefusesANewErrorAndKeepsW()
    {
        using var m = Create();
        var before = m.State();

        var result = m.Missions.Apply(new MissionBatch("Start nothing",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere")))])
        {
            Options = MissionEditOptions.Strict,
        });

        Assert.Equal(EditStatus.Refused, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal((EditErrorKind.NewErrors, MissionErrors.NewErrors), (error.Kind, error.Detail));
        Assert.StartsWith("The edit adds 1 error, so the editor did not apply it. Start trigger names trigger 'ol_nowhere'.",
            error.Message);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void TheDefaultModeAppliesANewErrorAndReportsIt()
    {
        using var m = Create();

        var result = m.Apply(new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere"))));

        var problem = Assert.Single(result.NewProblems, p => p.Rule == "M05");
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal(OriginKind.Mod, problem.Origin);
        Assert.Equal(Row("ol_wave", 6).Reference, problem.Ref);
        m.Session.Undo();
        var removed = m.Apply(new RemoveAction(Row("ol_wave", 3)));
        Assert.DoesNotContain(removed.NewProblems, p => p.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectNewWarningsRefusesANewWarning()
    {
        using var m = Create();

        var result = m.Missions.Apply(new MissionBatch("Lonely", [new AddTrigger(TestScript) { Condition = LineSpec.Of("Cnd_True") }])
        {
            Options = new MissionEditOptions(RejectNewWarnings: true),
        });

        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.Contains("Nothing starts trigger 'ol_test_trigger_1'.", result.Errors[0].Message);
    }

    [Fact]
    public void AnExpectedVersionThatDiffersRefusesTheBatch()
    {
        using var m = Create();
        m.Apply(new SetTriggerStart(T("ol_wave"), true));

        var ok = m.Missions.Apply(new MissionBatch("A", [new SetTriggerStart(T("ol_end"), true)])
        {
            ExpectVersions = new Dictionary<string, long> { [Script.ToLowerInvariant().Replace('\\', '/')] = 2 },
        });
        var stale = m.Missions.Apply(new MissionBatch("B", [new SetTriggerStart(T("ol_end"), false)])
        {
            ExpectVersions = new Dictionary<string, long> { [Script] = 2 },
        });

        Assert.Equal(EditStatus.Applied, ok.Status);
        Assert.Equal(3, ok.Versions[Script]);
        var error = Assert.Single(stale.Errors);
        Assert.Equal((EditErrorKind.StaleRevision, MissionErrors.ScriptChanged), (error.Kind, error.Detail));
        Assert.Equal($"Script {Script} changed after you read it. Read the script again, then try again.", error.Message);
        var revision = m.Missions.Apply(new MissionBatch("C", [new SetTriggerStart(T("ol_end"), false)]) { IfRevision = 1 });
        Assert.Equal(EditSession.RevisionChanged, revision.Errors[0].Detail);
    }

    [Fact]
    public void AnAgentBatchHasTheAgentLabelAndAConfirmationForMissingLines()
    {
        using var m = Create();

        var unconfirmed = m.Missions.Apply(new MissionBatch("Remove the end", [new RemoveTrigger(T("ol_end"))])
        {
            Origin = EditOrigin.Agent,
        });
        var confirmed = m.Missions.Apply(new MissionBatch("Remove the end", [new RemoveTrigger(T("ol_end"))])
        {
            Origin = EditOrigin.Agent, Confirm = true,
        });

        Assert.Equal(EditStatus.Refused, unconfirmed.Status);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.Equal((EditErrorKind.NeedsConfirmation, MissionErrors.MissingLines),
            (unconfirmed.Errors[0].Kind, unconfirmed.Errors[0].Detail));
        Assert.Equal(EditStatus.Applied, confirmed.Status);
        Assert.Equal("Agent: Remove the end", confirmed.UndoLabel);
        Assert.Equal(EditOrigin.Agent, m.Session.UndoOrigin);
        // The studio does not ask before a delete (section 4.10 of the M6b design).
        m.Session.Undo();
        Assert.Equal(EditStatus.Applied, m.Missions.Apply(new RemoveTrigger(T("ol_end"))).Status);
    }

    [Fact]
    public void ABatchJoinsTheOpenBatchOfACallerAndItsStrictCheckRunsAtTheCommit()
    {
        using var m = Create();
        var applied = new List<MissionEditResult>();
        m.Missions.Applied += applied.Add;

        var batch = m.Session.BeginBatch("Caller");
        var first = m.Missions.Apply(new MissionBatch("Name a later trigger",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_later")))]) { Options = MissionEditOptions.Strict });
        m.Missions.Apply(new MissionBatch("Add it", [new AddTrigger(TestScript) { Nickname = "ol_later", Condition = LineSpec.Of("Cnd_True") }]));
        Assert.Empty(applied);
        var result = batch.Commit();

        Assert.Equal(EditStatus.Applied, first.Status);
        Assert.NotNull(first.Inverse);
        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(2, applied.Count);
        Assert.Equal("Caller", m.Session.UndoLabel);

        var second = m.Session.BeginBatch("Caller 2");
        m.Missions.Apply(new MissionBatch("Name nothing",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere")))]) { Options = MissionEditOptions.Strict });
        var refused = second.Commit();
        Assert.Equal(EditStatus.Refused, refused.Status);
        Assert.Equal(MissionErrors.NewErrors, refused.Errors[0].Detail);
        Assert.Equal("Caller", m.Session.UndoLabel);
    }

    [Fact]
    public void APreviewLeavesTheRevisionTheVersionsAndTheHistory()
    {
        using var m = Create();
        var before = m.State();
        var revision = m.Session.Revision;

        var result = m.Missions.Preview(new MissionBatch("Preview",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere")))]));

        Assert.Equal(EditStatus.Previewed, result.Status);
        Assert.Contains(result.NewProblems, p => p.Rule == "M05");
        Assert.NotNull(result.Inverse);
        Assert.Equal(before, m.State());
        Assert.Equal(revision, m.Session.Revision);
        Assert.Equal(1, m.Missions.Version(Script));
        Assert.False(m.Session.CanUndo);
        Assert.False(m.Session.IsDirty);
    }

    [Fact]
    public void ARefusedCommandChangesNoRevision()
    {
        using var m = Create();
        var revision = m.Session.Revision;

        m.Refuse(new RenameTrigger(T("ol_wave"), "ol_end"), MissionErrors.NameTaken);

        Assert.Equal(revision, m.Session.Revision);
        Assert.Equal(1, m.Missions.Version(Script));
    }

    [Fact]
    public void AReadOnlySessionAndAnOpenTransactionRefuse()
    {
        using var m = Create();
        using (var transaction = m.Session.Begin("Drag"))
            Assert.Equal(MissionErrors.Busy, m.Missions.Apply(new SetTriggerStart(T("ol_wave"), true)).Errors[0].Detail);
        var second = m.Ws.SecondSession("act");

        var result = second.Missions.Apply(new SetTriggerStart(T("ol_wave"), true));

        Assert.Equal((EditErrorKind.ReadOnly, MissionErrors.NotEditable), (result.Errors[0].Kind, result.Errors[0].Detail));
        Assert.StartsWith("The editor cannot change missions now. ", result.Errors[0].Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task ATransactionStepRunsNoAnalysisAndTheCommitIsOneEntry()
    {
        using var m = Create();
        m.Missions.Model(Script);
        var analyses = m.Missions.Cache.Count;

        using var transaction = m.Missions.Begin("Drag the delay");
        for (var seconds = 1; seconds <= 5; seconds++)
            Assert.Equal(EditStatus.Applied, transaction.Apply(new MissionBatch("Drag",
                [new SetConditionValue(T("ol_wave"), "seconds", seconds)])).Status);
        Assert.Equal(analyses, m.Missions.Cache.Count);
        var work = transaction.AnalysisWork();
        var analysis = Assert.Single(await System.Threading.Tasks.Task.Run(work, TestContext.Current.CancellationToken));
        Assert.Equal("Cnd_Timer = 5", MissionEdits.Line(analysis.Index.Trigger("ol_wave")!.Condition!.Line));
        var result = transaction.Commit();

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Drag the delay", m.Session.UndoLabel);
        Assert.Equal("Cnd_Timer = 5", m.Lines("ol_wave")![2]);
        m.Session.Undo();
        Assert.Equal("Cnd_Timer = 3", m.Lines("ol_wave")![2]);
    }
}

using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The model cache and the overlay of the worker run (section 7 of the M6b design).</summary>
public sealed class MissionModelCacheTests
{
    [Fact]
    public void TheCacheGivesTheResultsOfTheWorkerRun()
    {
        using var m = Create();

        var model = m.Missions.Model(Script);
        var run = MissionRun.Execute(m.Missions.RunInputs()).Of(Script)!;

        Assert.Equal(run.Problems.Select(Key), model.Problems.Select(Key));
        Assert.Equal(run.Index.Triggers.Select(t => t.Nickname), model.Index.Triggers.Select(t => t.Nickname));
        Assert.Equal(1, model.Version);
        Assert.Null(model.ReadOnlyReason);
    }

    [Fact]
    public void OneStateHasOneModelAndAnUndoFindsItAgain()
    {
        using var m = Create();
        var first = m.Missions.Model(Script);

        Assert.Same(first.Analysis, m.Missions.Model(Script).Analysis);
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 5));
        var second = m.Missions.Model(Script);
        Assert.NotSame(first.Analysis, second.Analysis);
        Assert.Equal("5", second.Index.Trigger("ol_wave")!.Condition!.Value(0));
        m.Session.Undo();

        var back = m.Missions.Model(Script);
        Assert.Same(first.Analysis, back.Analysis);
        Assert.Equal(3, back.Version);
    }

    [Fact]
    public void TheWorkerRunReadsTheOverlayOfW()
    {
        using var m = Create();
        m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));
        m.Apply(new CreateScript(@"DATA\MISSIONS\OL_NEW\ol_new.ini"));

        var inputs = m.Missions.RunInputs();
        var run = MissionRun.Execute(inputs);

        var analysis = run.Of(Script)!;
        Assert.NotNull(analysis.Index.Trigger("ol_finish"));
        Assert.Null(analysis.Index.Trigger("ol_end"));
        Assert.Equal((MissionScriptState.Changed, "act"), (analysis.Script.State, analysis.Script.ModId));
        Assert.False(analysis.HasFileBytes);
        Assert.True(run.IsScript(@"DATA\MISSIONS\OL_NEW\ol_new.ini"));
        m.Missions.TakeRun(inputs, run);
        Assert.Same(analysis, m.Missions.Model(Script).Analysis);
    }

    [Fact]
    public void AProblemOfAnUnsavedEditHasTheOriginOfTheActiveMod()
    {
        using var m = Create();

        m.Apply(new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere"))));

        var problem = Assert.Single(m.Missions.Model(Script).Problems, p => p.Rule == "M05");
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal(OriginKind.Mod, problem.Origin!.Kind);
        Assert.Equal("act", problem.Origin.ModId);
    }

    [Fact]
    public void AScriptThatTheSessionDidNotChangeShowsTheTextOfItsFile()
    {
        using var m = Create();

        var model = m.Missions.Model(Script);

        Assert.True(model.Analysis.HasFileBytes);
        Assert.True(model.Analysis.RawText().IsFileText);
        Assert.Throws<OpenLancer.Data.Editing.Commands.EditException>(() => m.Missions.Model(@"DATA\MISSIONS\none.ini"));
    }

    private static string Key(Diagnostic d) => $"{d.Severity}|{d.Rule}|{d.Subject}|{d.Origin?.Kind}";
}

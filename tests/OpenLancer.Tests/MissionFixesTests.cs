using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The completeness fixes of section 8.7 of the M6b design: each fix is one <see cref="ApplyFix"/> command and one undo
/// entry, and it gives the lines that the table names. The scripts use invented names.
/// </summary>
public sealed class MissionFixesTests
{
    /// <summary>Runs the fix of the first problem of a rule, and checks that one Undo takes it back. Returns the fix.</summary>
    private static MissionFix Fix(MissionEdits m, string rule, string? id = null)
    {
        var analysis = m.Missions.Model(Script).Analysis;
        var problem = analysis.Problems.First(p => p.Rule == rule);
        var fixes = MissionFixes.Of(analysis, problem);
        var fix = id == null ? fixes.Single() : fixes.Single(f => f.Id == id);
        var before = m.State();

        var result = m.Apply(new ApplyFix(TestScript, problem.Subject!, fix.Id));

        Assert.Equal("Fix", result.UndoLabel);
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Subject == problem.Subject);
        var after = m.State();
        m.Session.Undo();
        Assert.Equal(before, m.State());
        m.Session.Redo();
        Assert.Equal(after, m.State());
        return fix;
    }

    private static MissionEdits With(string triggers) => Create(Text + "\n" + triggers);

    [Fact]
    public void M01AddsTheConditionAtOnce()
    {
        using var m = With("[Trigger]\nnickname = ol_bare\nAct_ActTrig = ol_end\n");

        Assert.Equal("Add the condition \"At once\"", Fix(m, "M01").Label);
        Assert.Equal(["nickname = ol_bare", "Cnd_True = no_params", "Act_ActTrig = ol_end"], m.Lines("ol_bare"));
    }

    [Fact]
    public void M03KeepsTheLastCondition()
    {
        using var m = With("[Trigger]\nnickname = ol_two\nCnd_True = no_params\nCnd_Timer = 2\nCnd_Timer = 5\nAct_ActTrig = ol_end\n");

        Assert.Equal("Keep the last condition", Fix(m, "M03").Label);
        Assert.Equal(["nickname = ol_two", "Cnd_Timer = 5", "Act_ActTrig = ol_end"], m.Lines("ol_two"));
    }

    [Fact]
    public void M04RemovesTheLineOrMovesItAfterTheNickname()
    {
        using var removed = Create();
        Fix(removed, "M04", "remove");
        Assert.Equal("nickname = ol_wave", removed.Lines("ol_wave")[0]);

        using var moved = Create();
        Assert.Equal("Move it after the nickname", Fix(moved, "M04", "move").Label);
        Assert.Equal(["nickname = ol_wave", "system = ol_system"], moved.Lines("ol_wave").Take(2));
    }

    [Theory]
    [InlineData("M05", "Act_ActTrig = ol_nowhere")]
    [InlineData("M06", "Act_SpawnShip = ol_no_ship")]
    [InlineData("M23", "Act_SetRep = Player, ol_no_faction, 0.5")]
    public void ANameThatDoesNotResolveRemovesTheLine(string rule, string line)
    {
        using var m = With($"[Trigger]\nnickname = ol_bad\nCnd_True = no_params\n{line}\n");

        Assert.Equal("Remove the line", Fix(m, rule).Label);
        Assert.Equal(["nickname = ol_bad", "Cnd_True = no_params"], m.Lines("ol_bad"));
    }

    [Fact]
    public void M07SpawnsTheFormation()
    {
        using var m = With("[Trigger]\nnickname = ol_spawn\nCnd_True = no_params\nAct_SpawnShip = ol_ship_2, no_ol, 1, 2, 3\n");

        Assert.Equal("Spawn the formation", Fix(m, "M07").Label);
        Assert.Equal("Act_SpawnFormation = ol_form, 1, 2, 3", m.Lines("ol_spawn")[^1]);
    }

    [Fact]
    public void M08MovesTheShipBeforeTheFormation()
    {
        using var m = With("[MsnFormation]\nnickname = ol_form_2\nformation = ol_shape\nship = ol_late\n\n[MsnShip]\nnickname = ol_late\nnpc = ol_npc_1\n");

        Fix(m, "M08");
        var sections = m.Missions.Model(Script).Document.Sections.Select(s => s.Nickname).ToList();
        Assert.True(sections.IndexOf("ol_late") < sections.IndexOf("ol_form_2"));
    }

    [Fact]
    public void M09MovesTheShipKeyBeforeTheFirstShipLine()
    {
        using var m = With("[MsnFormation]\nnickname = ol_form_3\nformation = ol_shape\nship = ol_ship_1\nlabel = ol_group\n");

        Assert.Equal("Move the line before 'formation'", Fix(m, "M09").Label);
        Assert.Equal(["nickname = ol_form_3", "label = ol_group", "formation = ol_shape", "ship = ol_ship_1"],
            m.Lines(Item("MsnFormation", "ol_form_3")));
    }

    [Fact]
    public void M13WritesTheLetterCaseOfTheName()
    {
        using var m = With("[Trigger]\nnickname = ol_case\nCnd_True = no_params\nAct_SpawnShip = OL_SHIP_1\n");

        Assert.Equal("Write 'ol_ship_1'", Fix(m, "M13").Label);
        Assert.Equal("Act_SpawnShip = ol_ship_1", m.Lines("ol_case")[^1]);
    }

    [Fact]
    public void M14WritesFalseAndM15WritesTheWholePart()
    {
        using var m = With("[Trigger]\nnickname = ol_values\nCnd_Destroyed = ol_ship_1, 2.5\nAct_Invulnerable = ol_ship_1, maybe\n");

        Assert.Equal("Write 'false'", Fix(m, "M14").Label);
        Assert.Equal("Write 2", Fix(m, "M15").Label);
        Assert.Equal(["nickname = ol_values", "Cnd_Destroyed = ol_ship_1, 2", "Act_Invulnerable = ol_ship_1, false"],
            m.Lines("ol_values"));
    }

    /// <summary>Runs the fix of a missing step of a prefab instance with a core trigger, and checks that the step is done
    /// after it. The triggers of the instance come from the active mod, so the base game does not miss the step too
    /// (decision D551).</summary>
    private static void StepFix(MissionEdits m, string prefab, string core, string step, string label)
    {
        var analysis = m.Missions.Model(Script).Analysis;
        var instance = analysis.Prefabs!.Instances.First(i => i.Definition.Id == prefab && i.Core?.Nickname == core);
        var report = MissionCompleteness.OfInstance(analysis, instance);
        var missing = report.Steps.Single(s => s.Name == instance.Definition.Steps.Single(d => d.Id == step).Name);
        Assert.Equal(CompletenessState.Missing, missing.State);
        Assert.Equal(MissionFixes.StepSubject(instance, step), missing.Fix);
        var fix = Assert.Single(MissionFixes.OfSubject(analysis, missing.Fix!));
        Assert.Equal(label, fix.Label);

        m.Apply(new ApplyFix(TestScript, missing.Fix!, fix.Id));

        var after = m.Missions.Model(Script).Analysis;
        var now = after.Prefabs!.Instances.First(i => i.Definition.Id == prefab && i.Core?.Nickname == instance.Core?.Nickname);
        Assert.Equal(CompletenessState.Done, MissionCompleteness.OfInstance(after, now).Steps.Single(s => s.Name == missing.Name).State);
    }

    [Fact]
    public void TheWatchersOfABranchGetTheStopLines()
    {
        using var m = Create();
        m.Apply(new AddTrigger(TestScript)
        {
            Nickname = "ol_test_on", Condition = LineSpec.Raw("Cnd_WatchTrigger", "ol_end", "ON"),
            Actions = [LineSpec.Raw("Act_ActTrig", "ol_start")],
        });
        m.Apply(new AddTrigger(TestScript)
        {
            Nickname = "ol_test_off", Condition = LineSpec.Raw("Cnd_WatchTrigger", "ol_end", "OFF"),
            Actions = [LineSpec.Raw("Act_ActTrig", "ol_wave")],
        });

        StepFix(m, "branch", "ol_end", "stopEachOther", "Add the stop lines");
        Assert.Contains("Act_DeactTrig = ol_test_off", m.Lines("ol_test_on"));
        Assert.Contains("Act_DeactTrig = ol_test_on", m.Lines("ol_test_off"));
    }

    [Fact]
    public void ASpawnWaveWithOneSpawnedItemGetsAnEndTrigger()
    {
        using var m = Create();
        m.Apply(new AddTrigger(TestScript)
        {
            Nickname = "ol_wave_b", Condition = LineSpec.Raw("Cnd_True", "no_params"),
            Actions = [LineSpec.Raw("Act_SpawnShip", "ol_ship_3")],
        });

        StepFix(m, "spawnWave", "ol_wave_b", "end", "Add an end trigger");
        Assert.Equal(["nickname = ol_wave_b_end", "Cnd_Destroyed = ol_ship_3, -1"], m.Lines("ol_wave_b_end"));
        Assert.Equal("Act_ActTrig = ol_wave_b_end", m.Lines("ol_wave_b")[^1]);
    }

    [Fact]
    public void AnOfferGetsADeclineTrigger()
    {
        using var m = Create();
        m.Apply(new AddTrigger(TestScript)
        {
            Nickname = "ol_accept", Condition = LineSpec.Raw("Cnd_MsnResponse", "accept"),
            Actions = [LineSpec.Raw("Act_ActTrig", "ol_start")],
        });
        m.Apply(new LinkTriggers(T("ol_start"), "ol_accept", LinkKind.Start));

        StepFix(m, "offer", "ol_accept", "decline", "Add a decline trigger");
        Assert.Equal(["nickname = ol_accept_decline", "Cnd_MsnResponse = reject"], m.Lines("ol_accept_decline"));
        // The trigger that starts the accept trigger starts the decline trigger too, so the game runs it.
        Assert.Equal("Act_ActTrig = ol_accept_decline", m.Lines("ol_start")[^1]);
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule == "G05" &&
                                                                     p.Message.Contains("ol_accept_decline", System.StringComparison.Ordinal));
    }

    /// <summary>A decline trigger starts with the mission when the accept trigger does.</summary>
    [Fact]
    public void ADeclineTriggerStartsWithTheMissionWhenTheAcceptTriggerDoes()
    {
        using var m = Create();
        m.Apply(new AddTrigger(TestScript)
        {
            Nickname = "ol_accept", StartsWithMission = true, Condition = LineSpec.Raw("Cnd_MsnResponse", "accept"),
        });

        StepFix(m, "offer", "ol_accept", "decline", "Add a decline trigger");
        Assert.Equal(["nickname = ol_accept_decline", "InitState = ACTIVE", "Cnd_MsnResponse = reject"],
            m.Lines("ol_accept_decline"));
    }

    [Fact]
    public void AFixThatDoesNotApplyNowRefuses()
    {
        using var m = Create();
        var error = m.Refuse(new ApplyFix(TestScript, "M01|ol_none", "atOnce"), MissionErrors.FixNotFound);

        Assert.Equal(EditErrorKind.Refused, error.Kind);
        Assert.Equal($"This fix does not apply to {Script} now. The problem or the step changed. Read the problems again, " +
                     "then try again.", error.Message);
        Assert.False(MissionFixes.HasFix("M10"));
        Assert.False(MissionFixes.HasFix("G01"));
    }
    /// <summary>The Problems section of a page and a step of a rule give the fixes of their problem.</summary>
    [Fact]
    public void AProblemOfAPageAndAStepOfARuleHaveTheirFixes()
    {
        using var m = Create();
        var analysis = m.Missions.Model(Script).Analysis;
        var problem = analysis.Problems.First(p => p.Rule == "M04");

        var step = MissionCompleteness.RuleStep("Lines", "A reason.", [problem]);
        var page = Editor.Missions.MissionPageModel.For(new Editor.Missions.PageContext(
            MissionRun.Execute(m.Missions.RunInputs()), analysis, ShowBaseGame: true, false),
            new MissionRef(Script, MissionItemKind.Trigger, analysis.Index.Trigger("ol_wave")!.Section.Ref));
        var field = page.Section("problems")!.Fields.Single(f => f.Value == problem.Message);

        Assert.Equal(problem.Subject, step.Fix);
        Assert.Equal(["Remove the line", "Move it after the nickname"], field.Actions.Select(a => a.Label));
        Assert.Equal(new ApplyFix(TestScript, problem.Subject!, "move"), Assert.Single(field.Actions[1].Batch!.Commands));
        Assert.Null(MissionCompleteness.RuleStep("Lines", "A reason.",
            [new Diagnostic(DiagnosticSeverity.Warning, MissionChecks.Source, "A message.") { Rule = "M10", Subject = "M10|x" }]).Fix);
    }
}

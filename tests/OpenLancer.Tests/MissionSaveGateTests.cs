using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Export;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Mods;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The save gate of the missions (sections 6.10 and 6.12 of the M6b design).</summary>
public sealed class MissionSaveGateTests
{
    /// <summary>MSD section 5.3: a trigger without a condition blocks Save with the text of M01. The trigger check of
    /// M11 has the same text and place, so the gate gives one problem (decision D556).</summary>
    [Fact]
    public void ATriggerWithoutAConditionBlocksSaveWithTheTextOfM01()
    {
        using var m = Create();

        m.Apply(new AddTrigger(TestScript) { Nickname = "ol_new" });
        var result = m.Session.Save();

        Assert.False(result.Saved);
        var problem = Assert.Single(result.Problems, p => p.Message == TriggerRules.NoConditionMessage("ol_new"));
        Assert.Equal(("M01", DiagnosticSeverity.Error), (problem.Rule, problem.Severity));
        Assert.Equal("act", problem.Origin!.ModId);
        m.Session.Undo();
        Assert.True(m.Session.Save().Saved);
    }

    /// <summary>A problem that the base game has too is a warning (decision D551), so it does not block Save.</summary>
    [Fact]
    public void ABaseGameProblemIsAWarning()
    {
        using var m = Create(Text + "\n\n[Trigger]\nnickname = ol_bad\nAct_ActTrig = ol_end\n");

        m.Apply(new SetTriggerSystem(T("ol_wave")) { System = "ol_other" });

        var problem = Assert.Single(m.Missions.Model(Script).Problems, p => p.Rule == "M01");
        Assert.Equal((DiagnosticSeverity.Warning, MessageOrigin.BaseGame), (problem.Severity, problem.Origin));
        Assert.DoesNotContain(m.Session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
        Assert.True(m.Session.Save().Saved);
    }

    /// <summary>A delete never stops at a check (decision D81). Its "Missing" lines block Save until an Undo.</summary>
    [Fact]
    public void TheMissingLinesOfADeleteBlockSaveUntilAnUndo()
    {
        using var m = Create();

        m.Apply(new RemoveTrigger(T("ol_end")));

        var errors = m.Session.SaveProblems().Where(p => p.Rule == "M05").ToList();
        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.Equal(DiagnosticSeverity.Error, e.Severity));
        Assert.False(m.Session.Save().Saved);
        m.Session.Undo();
        Assert.True(m.Session.Save().Saved);
    }

    /// <summary>A line of a script that the active mod does not change names an object that the active mod deletes in
    /// the Scene. The line blocks Save (decision D81).</summary>
    [Fact]
    public void ALineOfAnUnchangedScriptThatNamesADeletedObjectBlocksSave()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(StoryScript, """
            [Mission]

            [Trigger]
            nickname = ol_lock
            InitState = ACTIVE
            Cnd_True = no_params
            Act_LockDock = Player, Li01_dock, lock
            Act_ChangeState = SUCCEED
            """);
        var session = ws.Session("act");
        Assert.DoesNotContain(session.SaveProblems(), p => p.Rule == "M06");

        session.Execute(ScenePlans.Delete(session, [SceneWorkspace.Object("Li01_dock")]).Command!);

        var problem = Assert.Single(session.SaveProblems(), p => p.Rule == "M06");
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("'Li01_dock'", problem.Message);
        session.Undo();
        Assert.DoesNotContain(session.SaveProblems(), p => p.Rule == "M06");
    }

    /// <summary>Two triggers of the active mod with one game id block Save (rule M27).</summary>
    [Fact]
    public void TwoTriggersWithOneGameIdBlockSave()
    {
        var (first, second) = DataListsTests.FlHashPair();
        using var m = Create(Text.Replace("ol_end", first),
            modFiles: new() { [@"patches\" + Script] = $"[Trigger]\nol.new = true\nnickname = {second}\nCnd_True = no_params\n" });

        var problem = Assert.Single(m.Session.SaveProblems(), p => p.Rule == "M27");

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal($"Triggers '{first}' and '{second}' have the same game id. The game keeps only the first of the two. " +
                     "Rename one of them.", problem.Message);
        Assert.False(m.Session.Save().Saved);
    }

    /// <summary>A target game without the section of a patch gives T01, an error (decision D415).</summary>
    [Fact]
    public void ATargetGameWithoutTheSectionGivesT01()
    {
        using var m = Create();
        using var game = Target(Text.Replace("nickname = ol_end", "nickname = ol_finish"));
        m.Session.TargetGame = id => id == ModTargets.Vanilla ? GameBase.Standard(game.Base) : null;

        m.Apply(new SetTriggerSystem(T("ol_end")) { System = "ol_system" });
        var result = m.Session.Save();

        Assert.False(result.Saved);
        var problem = Assert.Single(result.Problems, p => p.Rule == "T01");
        Assert.Equal($"[Trigger] nickname = ol_end is not in {Script} of Freelancer 1.1. The merge of this patch fails on " +
                     "Freelancer 1.1. Remove the edit, or remove Freelancer 1.1 from the targets of the mod.", problem.Message);
        Assert.Equal(new SectionRef(Script, new SectionIdentity("Trigger", "ol_end")), problem.Target);
        // A new trigger needs no section of the target.
        m.Session.Undo();
        m.Apply(new AddTrigger(TestScript) { Nickname = "ol_more", Condition = LineSpec.Of("Cnd_True") });
        Assert.True(m.Session.Save().Saved);
    }

    /// <summary>A target game whose section differs from the base of the edit gives T02, a warning (decision D429).
    /// The same game as the profile base gives none.</summary>
    [Fact]
    public void ATargetSectionThatDiffersFromTheBaseOfTheEditGivesT02()
    {
        using var m = Create();
        using var game = Target(Text.Replace("Act_ChangeState = SUCCEED", "Act_ChangeState = FAIL"));
        m.Session.TargetGame = id => id switch
        {
            ModTargets.HdEdition => GameBase.Standard(game.Base),
            ModTargets.Vanilla => GameBase.Standard(m.Ws.Base),
            _ => null,
        };

        m.Apply(new SetTriggerSystem(T("ol_end")) { System = "ol_system" });
        var result = m.Session.Save();

        Assert.True(result.Saved);
        var problem = Assert.Single(result.Problems, p => p.Rule == "T02");
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal($"[Trigger] nickname = ol_end of {Script} on HD Edition 0.7.1 differs from the base of your edit. The " +
                     "changes of HD Edition 0.7.1 in this section go away. Compare the two, then keep your lines or use the " +
                     "base of HD Edition 0.7.1.", problem.Message);
    }

    /// <summary>A retail target without a linked game gives one note in a session, and only when the active mod
    /// changes a mission file.</summary>
    [Fact]
    public void ATargetWithoutAGameGivesOneNote()
    {
        using var m = Create();
        Assert.Empty(m.Session.TakeTargetNotes());

        m.Apply(new SetTriggerSystem(T("ol_end")) { System = "ol_system" });

        Assert.Equal(["The editor cannot check HD Edition 0.7.1, because no game is linked for it.",
            "The editor cannot check Freelancer 1.1, because no game is linked for it."], m.Session.TakeTargetNotes());
        Assert.Empty(m.Session.TakeTargetNotes());
    }

    private const string StoryScript = @"DATA\MISSIONS\OL_REF\ol_ref.ini";

    /// <summary>The folder of a target game with another text of the script.</summary>
    private static TestWorkspace Target(string script)
    {
        var game = new TestWorkspace();
        game.WriteBase(Script, script);
        game.WriteBase(Ships, ShipsText);
        game.WriteBase(SharedShips, SharedText);
        return game;
    }
}

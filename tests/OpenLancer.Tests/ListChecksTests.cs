using System;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>The severities of the list checks, one test for each row of section 11.1 of the M21 design.</summary>
public sealed class ListChecksTests
{
    private static ListProblem Single(EditSession session, SectionRef section) =>
        Assert.Single(session.Lists.Problems(section));

    [Fact]
    public void AValueOfTheActiveModOutsideAClosedListIsAnError()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "loadout", ["lo_nothing"]));

        var problem = Single(session, Li01("Li01_01"));
        Assert.Equal((ListProblemKind.Missing, true, "loadout", "lo_nothing"),
            (problem.Kind, problem.IsError, problem.Diagnostic.Key, problem.Value));
        Assert.Equal("[Object] nickname = Li01_01: 'loadout = lo_nothing': no loadout has the name 'lo_nothing'. The " +
                     "object gets no equipment.", problem.Diagnostic.Message);
        Assert.Equal("loadout", problem.List!.Id);
        Assert.Equal(problem.Diagnostic, Assert.Single(session.CheckLists(Li01("Li01_01"))));
    }

    [Fact]
    public void AValueOfTheActiveModOutsideAnOpenListIsAWarning()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "voice", ["unknown_voice"]));

        var problem = Single(session, Li01("Li01_01"));
        Assert.Equal((ListProblemKind.NotListed, false), (problem.Kind, problem.IsError));
        Assert.EndsWith("Unlisted values are allowed.", problem.Diagnostic.Message);
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>Decision D108: a value of a lower layer outside a list is a warning, and the save goes on.</summary>
    [Fact]
    public void AValueOfALowerLayerOutsideAListIsAWarning()
    {
        using var ws = Create(lowerFiles: new()
        {
            [@"patches\DATA\UNIVERSE\SYSTEMS\LI01\li01.ini"] = "[Object]\nnickname = Li01_01\nloadout = lo_lower_typo\n",
        });
        var session = ws.Session("act");

        var problem = Single(session, Li01("Li01_01"));
        Assert.Equal((ListProblemKind.NotListed, false), (problem.Kind, problem.IsError));
        Assert.EndsWith("The value comes from mod 'lower'.", problem.Diagnostic.Message);
        // A change of another key of the section by the active mod does not make the lower value an error.
        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "pos", ["1, 2, 3"]));
        Assert.Empty(session.SaveProblems());
    }

    [Fact]
    public void ARequiredKeyWithoutALineIsAnErrorInASectionThatTheActiveModChanges()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.RemoveName(Li01("Li01_01"), "archetype"));

        var problem = Single(session, Li01("Li01_01"));
        Assert.Equal((ListProblemKind.RequiredMissing, true, "archetype"), (problem.Kind, problem.IsError, problem.Diagnostic.Key));
        Assert.EndsWith("the key 'archetype' is missing. The game drops this object.", problem.Diagnostic.Message);
        Assert.Contains(problem.Diagnostic, session.SaveProblems());
        session.Undo();
        Assert.Empty(session.Lists.Problems(Li01("Li01_01")));
    }

    [Fact]
    public void ARequiredKeyOfALowerSectionThatTheActiveModDoesNotChangeIsAWarning()
    {
        using var ws = Create(lowerFiles: new()
        {
            [@"patches\DATA\UNIVERSE\SYSTEMS\LI01\li01.ini"] = "[Object]\nnickname = Li01_01\narchetype =\n",
        });
        var session = ws.Session("act");

        var problem = Single(session, Li01("Li01_01"));
        Assert.Equal((ListProblemKind.RequiredMissing, false), (problem.Kind, problem.IsError));
    }

    /// <summary>A missile (a munition with a motor) needs an explosion: the engine stops the load without one.</summary>
    [Fact]
    public void ARequiredWithKeyIsRequiredWhenTheSectionHasTheOtherKey()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var missile = new SectionRef(EquipmentPath, new SectionIdentity("Munition", "missile1"));
        var ammo = new SectionRef(EquipmentPath, new SectionIdentity("Munition", "ammo1"));

        session.Execute(EditCommand.RemoveName(missile, "explosion_arch"));
        session.Execute(EditCommand.SetLinesFromText(ammo, "hull_damage", ["10"]));

        var problem = Single(session, missile);
        Assert.Equal((ListProblemKind.RequiredMissing, true), (problem.Kind, problem.IsError));
        Assert.Contains("A section with 'motor' needs it. The engine stops with an error", problem.Diagnostic.Message);
        Assert.Empty(session.Lists.Problems(ammo));
    }

    [Fact]
    public void AValueThatNamesAnUnusableEntryIsAnError()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(@"DATA\igraph.ini", "IGraph",
            [Line("nickname", "no_points"), Line("type", "FLOAT")]));

        session.Execute(EditCommand.SetLinesFromText(Li01Section("LightSource", "light1"), "atten_curve", ["no_points"]));

        var problem = Single(session, Li01Section("LightSource", "light1"));
        Assert.Equal((ListProblemKind.Unusable, true), (problem.Kind, problem.IsError));
        Assert.Contains("has no 'point' line.", problem.Diagnostic.Message);
    }

    /// <summary>A line of a key with more than one line gives its line number. Each line gets a check.</summary>
    [Fact]
    public void EachLineOfAMultilineKeyGetsACheck()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var loadout = Loadout(ShipLoadoutsPath, "lo_station");

        session.Execute(EditCommand.SetLinesFromText(loadout, "equip", ["gun1, HpWeapon01", "gun_typo, HpWeapon02"]));

        var problem = Single(session, loadout);
        Assert.Equal((ListProblemKind.Missing, 1, "gun_typo"), (problem.Kind, problem.Line, problem.Value));
        Assert.Contains(": line 2: 'equip = gun_typo, HpWeapon02': no equipment has the name 'gun_typo'.",
            problem.Diagnostic.Message);
    }

    /// <summary>A value of a scoped list follows the other values of the line: the exit object of a jump gate is an
    /// object of the destination system.</summary>
    [Fact]
    public void AScopedValueIsCheckedInTheSystemOfTheLine()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var gate = Li01("Li01_to_Li02");

        session.Execute(EditCommand.SetLinesFromText(gate, "goto", ["Li02, Li01_01, gate_tunnel_bretonia"]));
        var problem = Single(session, gate);
        Assert.Equal((ListProblemKind.Missing, 1), (problem.Kind, problem.Rule!.Index));

        // A system that does not exist: only value 0 reports.
        session.Execute(EditCommand.SetLinesFromText(gate, "goto", ["Li09, Li01_01, gate_tunnel_bretonia"]));
        problem = Single(session, gate);
        Assert.Equal((ListProblemKind.Missing, 0), (problem.Kind, problem.Rule!.Index));
    }

    /// <summary>Review finding R22: the editor cannot change a whole file of the active mod, so its values never stop
    /// the save. W of such a file has the lower data, and the gate skips it.</summary>
    [Fact]
    public void AWholeFileOfTheActiveModGivesNoError()
    {
        using var ws = Create(activeFiles: new()
        {
            [@"files\DATA\UNIVERSE\SYSTEMS\LI01\li01.ini"] = Li01Text.Replace("loadout = lo_station", "loadout = lo_typo"),
        });
        var session = ws.Session("act");

        Assert.NotNull(session.Get(Li01("Li01_01"))!.ReadOnlyReason);
        Assert.DoesNotContain(session.Lists.Problems(Li01("Li01_01")), p => p.IsError);
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>A whole file of the active mod without an object removes the object: a line of another file that
    /// names it is an error, for example the exit object of the partner jump gate (decision D81).</summary>
    [Fact]
    public void AWholeFileWithoutAJumpGateMakesThePartnerGateAnError()
    {
        using var ws = Create(activeFiles: new()
        {
            [@"files\DATA\UNIVERSE\SYSTEMS\LI01\li01.ini"] = "[Object]\nnickname = Li01_01\narchetype = station1\n",
        });
        var session = ws.Session("act");
        var partner = Object(Li02Path, "Li02_to_Li01");

        Assert.Contains(session.Lists.Removed(), r => r.Value == "Li01_to_Li02");
        var problem = Assert.Single(session.SaveProblems());
        Assert.Equal((partner, "goto"), (problem.Target, problem.Key));
        // The rings of the lower data of the file are not in the game, so their lines give no error.
        Assert.DoesNotContain(session.SaveProblems(), p => p.Target!.Path.Equals(Li01Path, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A list that is not available gives no result (section 8.3).</summary>
    [Fact]
    public void AListThatIsNotAvailableGivesNoResult()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var depot = SystemWorkspace.Li01Section("Object", "Li01_depot");

        session.Execute(EditCommand.SetLinesFromText(depot, "loadout", ["lo_typo"]));

        Assert.Null(session.Lists.List("loadout"));
        Assert.Empty(session.Lists.Problems(depot));
    }

    [Fact]
    public void ALayerOneErrorOfASectionThatTheActiveModChangesStopsTheSave()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "pos", ["1, two, 3"]));

        var problem = Assert.Single(session.SaveProblems());
        Assert.Contains("cannot read 'two'", problem.Message);
        Assert.Empty(session.Lists.Problems(Li01("Li01_01")));
    }
}

/// <summary>
/// Decision D81: a delete works the way Unity treats a delete (section 15 of the M21 design). A reference to a deleted
/// entry stays and shows "Missing". No check refuses the delete, and one undo heals each referrer.
/// </summary>
public sealed class DeleteUndoTests
{
    [Fact]
    public void ADeletedEntryMakesItsReferrerMissingAndAnUndoHealsIt()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(SolarLoadoutsPath, "Loadout", [Line("nickname", "lo_new")]));
        session.Execute(EditCommand.SetLinesFromText(Li01("ring1"), "loadout", ["lo_new"]));
        Assert.Empty(session.Lists.Problems(Li01("ring1")));

        session.Execute(EditCommand.DeleteSection(Loadout(SolarLoadoutsPath, "lo_new")));

        var problem = Assert.Single(session.Lists.Problems(Li01("ring1")));
        Assert.Equal((ListProblemKind.Missing, true), (problem.Kind, problem.IsError));
        Assert.Contains(problem.Diagnostic, session.SaveProblems());

        session.Undo();
        Assert.Empty(session.Lists.Problems(Li01("ring1")));
        Assert.Empty(session.SaveProblems());
        // No command other than the undo ran: the redo holds the delete, and the undo stack holds the edit before it.
        Assert.Equal("Delete [Loadout] nickname = lo_new", session.RedoLabel);
        Assert.Equal("Set loadout", session.UndoLabel);
    }

    [Fact]
    public void ADeletedLowerMotorMakesTheLowerMissileAnErrorUntilTheUndo()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var missile = new SectionRef(EquipmentPath, new SectionIdentity("Munition", "missile1"));

        session.Execute(EditCommand.DeleteSection(new SectionRef(EquipmentPath, new SectionIdentity("Motor", "motor1"))));

        var problem = Assert.Single(session.Lists.Problems(missile));
        Assert.Equal((ListProblemKind.Removed, true, "motor"), (problem.Kind, problem.IsError, problem.Diagnostic.Key));
        Assert.Contains("The engine stops with an error when it loads the game data.", problem.Diagnostic.Message);
        var result = session.Save();
        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Target == missile && p.Key == "motor");
        Assert.Empty(TestWorkspace.Files(Path.Combine(ws.ModFolder("act"), "patches")));

        session.Undo();
        Assert.True(session.Save().Saved);
    }
}

/// <summary>The save gate of <see cref="EditSession.Save"/> (section 11.3 of the M21 design).</summary>
public sealed class EditSessionSaveGateTests
{
    [Fact]
    public void TheGateStopsASaveWithAListErrorAndAFixMakesItPass()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "loadout", ["lo_nothing"]));

        var refused = session.Save();
        Assert.False(refused.Saved);
        Assert.Equal(Li01("Li01_01"), Assert.Single(refused.Problems, p => p.Severity == DiagnosticSeverity.Error).Target);
        Assert.Empty(TestWorkspace.Files(Path.Combine(ws.ModFolder("act"), "patches")));
        Assert.True(session.IsDirty);

        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "loadout", ["lo_solar"]));
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Single(saved.Written);
    }

    /// <summary>The gate reads the files of the patches of the active mod too, also when the session did not edit
    /// them: a delete of a loadout that a saved patch names stops the save.</summary>
    [Fact]
    public void ADeleteOfAnEntryThatASavedPatchNamesStopsTheSave()
    {
        using var ws = Create();
        var first = ws.Session("act");
        first.Execute(EditCommand.AddSection(SolarLoadoutsPath, "Loadout", [Line("nickname", "lo_new")]));
        first.Execute(EditCommand.SetLinesFromText(Li01("ring1"), "loadout", ["lo_new"]));
        Assert.True(first.Save().Saved);
        first.Dispose();

        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(Loadout(SolarLoadoutsPath, "lo_new")));

        Assert.DoesNotContain(Li01Path, session.EditedPaths, StringComparer.OrdinalIgnoreCase);
        var problem = Assert.Single(session.SaveProblems());
        Assert.Equal((Li01("ring1"), "loadout"), (problem.Target, problem.Key));
        Assert.False(session.Save().Saved);
    }

    /// <summary>The gate runs on each save, also when nothing changes, so that Play never starts with an error.</summary>
    [Fact]
    public void ASaveWithNothingToWriteStillRunsTheGate()
    {
        using var ws = Create(activeFiles: new()
        {
            [@"patches\DATA\UNIVERSE\SYSTEMS\LI01\li01.ini"] = "[Object]\nnickname = Li01_01\nloadout = lo_typo\n",
        });
        var session = ws.Session("act");

        Assert.False(session.IsDirty);
        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Key == "loadout" && p.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>An edit with an error stays a draft in W and in the recovery file (decision D8). It never reaches the
    /// mod files.</summary>
    [Fact]
    public void TheRecoveryFileKeepsADraftWithAListError()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Li01("Li01_01"), "loadout", ["lo_nothing"]));

        Assert.Empty(session.WriteRecovery());

        var recovery = RecoveryFile.Read(RecoveryFile.PathOf(ws.ModFolder("act")));
        Assert.Contains("loadout = lo_nothing", Assert.Single(recovery.Files).Text);
        Assert.False(session.Save().Saved);
    }
}

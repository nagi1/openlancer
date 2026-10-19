using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The origin of a mission problem that a mod causes outside the script (decision D551, section 0 item 2 of the M6a
/// design). A mod that removes or renames a name of the base game gives a problem in a script that it does not change.
/// The base-game run checks the script with the names of the base game. So the problem has the origin of the mod, and
/// it shows also while the base-game messages are hidden.
/// </summary>
public sealed class MissionOriginTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    private const string Names = Story + """

        [Trigger]
        nickname = t_names
        Cnd_BaseEnter = base_a
        Act_LockDock = Player, gate_a, lock
        Act_ActTrig = t_end

        [Trigger]
        nickname = t_more
        InitState = ACTIVE
        Cnd_BaseEnter = base_gone
        Act_ActTrig = t_names
        """;

    /// <summary>The world of the edit view: the mod "mod1" removed the base base_a and the object gate_a. The base game
    /// never had base_gone.</summary>
    private static (MissionWorld Edit, MissionWorld Base) Worlds(string mod = "mod1")
    {
        var edit = World(new()
        {
            [MissionValueKind.Base] = ["base_b"],
            [MissionValueKind.SystemObject] = ["gate_b"],
        });
        var removed = new Dictionary<MissionValueKind, IReadOnlyDictionary<string, string>>
        {
            [MissionValueKind.Base] = new Dictionary<string, string> { ["base_a"] = mod },
            [MissionValueKind.SystemObject] = new Dictionary<string, string> { ["gate_a"] = mod },
        };
        return (edit, edit.WithRemoved(removed));
    }

    [Fact]
    public void ANameThatAModRemovesGivesAProblemOfThatModInAScriptThatNoModChanges()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Names);
        var (edit, baseNames) = Worlds();

        var problems = MissionRun.Execute(folder.Inputs(activeMod: "mod1", world: edit, baseWorld: baseNames)).Problems.ToList();

        var baseEnter = problems.Single(p => p.Rule == "M23" && p.Message.Contains("'base_a'"));
        var gate = problems.Single(p => p.Rule == "M06" && p.Message.Contains("'gate_a'"));
        Assert.All(new[] { baseEnter, gate }, p =>
        {
            Assert.Equal(OriginKind.Mod, p.Origin!.Kind);
            Assert.Equal(("mod1", ModEvidence.Name), (p.Origin.ModId, p.Origin.Evidence));
            // A line of a name that the active mod removes has the severity of its rule.
            Assert.Equal(DiagnosticSeverity.Error, p.Severity);
            Assert.Contains("removes or renames a name that this line names", p.Origin.Text);
        });
        // A name that the base game misses too stays a base-game problem.
        Assert.True(problems.Single(p => p.Message.Contains("'base_gone'")).Origin!.IsBaseGame);
    }

    [Fact]
    public void ANameThatALowerModRemovesGivesAWarningOfThatMod()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Names);
        var (edit, baseNames) = Worlds("lower");

        var problem = MissionRun.Execute(folder.Inputs(activeMod: "mod1", world: edit, baseWorld: baseNames)).Problems
            .Single(p => p.Message.Contains("'base_a'"));

        Assert.Equal("lower", problem.Origin!.ModId);
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
    }

    [Fact]
    public void WithoutARemovedNameEachProblemOfAnUnchangedScriptIsBaseGame()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Names);
        var (edit, _) = Worlds();

        var run = MissionRun.Execute(folder.Inputs(activeMod: "mod1", world: edit));

        Assert.All(run.Problems, p => Assert.True(p.Origin!.IsBaseGame, p.Message));
        Assert.Null(run.Of(Script)!.BaseIndex);
    }

    [Fact]
    public void TheBaseGameRunOfAChangedScriptUsesTheNamesOfTheBaseGame()
    {
        using var baseFolder = new MissionFolder();
        baseFolder.Write(Script, Names);
        using var folder = new MissionFolder();
        folder.Write(Script, Names.Replace("Act_ActTrig = t_end", "Act_ActTrig = t_end\nAct_ActTrig = t_start"));
        var ledger = new TestLedger();
        ledger.Change(Script, "mod2", new SectionIdentity("Trigger", "t_names"));
        var (edit, baseNames) = Worlds();

        var problems = MissionRun.Execute(folder.Inputs(baseFolder, ledger, "mod1", world: edit, baseWorld: baseNames))
            .Problems.ToList();

        // The mod that changes the section of the line comes first, as for each other problem of a changed section.
        var problem = problems.Single(p => p.Message.Contains("'gate_a'"));
        Assert.Equal(("mod2", ModEvidence.Section), (problem.Origin!.ModId, problem.Origin.Evidence));
        Assert.True(problems.Single(p => p.Message.Contains("'base_gone'")).Origin!.IsBaseGame);
    }

    [Fact]
    public void AWorldWithRemovedNamesHasThemAndNamesTheirMod()
    {
        var (edit, baseNames) = Worlds();

        Assert.False(edit.Has(MissionValueKind.Base, "base_a"));
        Assert.True(baseNames.Has(MissionValueKind.Base, "BASE_A"));
        Assert.True(baseNames.Has(MissionValueKind.Base, "base_b"));
        Assert.Equal("mod1", baseNames.RemovedBy(MissionValueKind.Object, "gate_a"));
        Assert.Null(baseNames.RemovedBy(MissionValueKind.Base, "base_b"));
        Assert.True(baseNames.HasRemovedNames);
        Assert.False(edit.HasRemovedNames);
    }

    /// <summary>The snapshot of a session finds the names that a patch of a mod deletes, as the base folder gives
    /// them.</summary>
    [Fact]
    public void TheSnapshotFindsTheNamesThatAModDeletes()
    {
        using var ws = ListWorkspace.Create(activeFiles: new()
        {
            [@"patches\DATA\UNIVERSE\SYSTEMS\LI01\li01.ini"] = "[Object]\nnickname = Li01_to_Li02\nol.delete = true\n",
            [@"patches\DATA\UNIVERSE\universe.ini"] = "[Base]\nnickname = Li01_01_Base\nol.delete = true\n",
        });
        ws.WriteBase(Script, Story.Replace("Act_ActTrig = t_end",
            "Act_ActTrig = t_end\nAct_LockDock = Player, Li01_to_Li02, lock\nAct_LockDock = Player, Li02_to_Li01, lock"));
        using var session = ws.Session("act");

        var inputs = session.MissionInputs(FullCoverage.Instance);
        var problems = MissionRun.Execute(inputs).Problems.ToList();

        Assert.Equal("act", inputs.BaseWorld!.RemovedBy(MissionValueKind.SystemObject, "Li01_to_Li02"));
        Assert.Equal("act", inputs.BaseWorld.RemovedBy(MissionValueKind.Base, "Li01_01_Base"));
        Assert.Null(inputs.BaseWorld.RemovedBy(MissionValueKind.SystemObject, "Li02_to_Li01"));
        var problem = Assert.Single(problems, p => p.Message.Contains("'Li01_to_Li02'"));
        Assert.Equal(("act", DiagnosticSeverity.Error), (problem.Origin!.ModId, problem.Severity));
        Assert.True(BaseGameMessages.Shows(problem, showBaseGame: false));
        Assert.DoesNotContain(problems, p => p.Message.Contains("'Li02_to_Li01'"));
    }

    /// <summary>The session finds the system of an object by the system file that holds it. So a connector line to an
    /// object in another system ends at the chip of that system (section 7.3 of the M6a design).</summary>
    [Fact]
    public void TheSessionFindsTheSystemOfAnObject()
    {
        using var ws = ListWorkspace.Create();
        using var session = ws.Session("act");

        Assert.Equal("li01", session.SystemOfObject("LI01_TO_LI02")?.ToLowerInvariant());
        Assert.Null(session.SystemOfObject("no_such_object"));
    }

    [Fact]
    public void WithoutARemovedNameTheSnapshotHasNoBaseNames()
    {
        using var ws = ListWorkspace.Create();
        ws.WriteBase(Script, Story);
        using var session = ws.Session("act");

        Assert.Null(session.MissionInputs(FullCoverage.Instance).BaseWorld);
    }
}

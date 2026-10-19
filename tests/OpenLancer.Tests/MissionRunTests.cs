using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Runtime.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The run of the mission checks, the origin of the problems by comparison with the base game (decision D551), and the
/// rule that the run logs nothing (sections 12.2, 12.3 and 18.1 of the M6a design).
/// </summary>
public sealed class MissionRunTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";
    private const string Other = @"DATA\MISSIONS\TEST\test.ini";

    private const string Broken = Story + """

        [Trigger]
        nickname = t_x
        Cnd_True = no_params
        Act_ActTrig = t_end
        """;

    [Fact]
    public void ARunFindsTheSlotScriptsAndTheOtherScripts()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        folder.Write(Other, Story);
        folder.Write(@"DATA\MISSIONS\TEST\notes.ini", "[Notes]\ntext = 1\n");
        folder.Write(@"DATA\MISSIONS\ENCOUNTERS\enc.ini", "[Trigger]\nnickname = not_a_script\n");

        var result = MissionRun.Execute(folder.Inputs(generation: 7));

        Assert.Equal(7, result.Generation);
        Assert.Equal(15, result.Scripts.Count);
        Assert.Equal("M01a", result.Scripts[0].Slot?.Slot);
        Assert.Equal(MissionScriptState.BaseGame, result.Scripts[0].State);
        Assert.All(result.Scripts.Skip(1).Take(13), s => Assert.Equal(MissionScriptState.Missing, s.State));
        Assert.Equal(Other, result.Scripts[^1].Path, ignoreCase: true);
        Assert.Null(result.Scripts[^1].Slot);
        Assert.Equal(2, result.Analyses.Count);
        Assert.True(result.IsScript(Other));
        Assert.False(result.IsScript(@"DATA\MISSIONS\TEST\notes.ini"));
    }

    [Fact]
    public void AScriptThatNoModChangesGivesOnlyBaseGameProblems()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Broken);

        var problems = MissionRun.Execute(folder.Inputs(activeMod: "mod1")).Problems.ToList();

        var problem = Assert.Single(problems);
        Assert.Equal("G05|t_x", problem.Subject);
        Assert.True(problem.Origin!.IsBaseGame);
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
    }

    [Fact]
    public void AModThatRemovesTheOnlyStartOfATriggerCausesTheProblem()
    {
        using var baseFolder = new MissionFolder();
        baseFolder.Write(Script, Story);
        using var folder = new MissionFolder();
        // The mod changes the start trigger, so nothing starts t_end any more. The section of t_end does not change.
        folder.Write(Script, Story.Replace("Act_ActTrig = t_end", "Act_ActTrig = t_start"));
        var ledger = new TestLedger();
        ledger.Change(Script, "mod1", new SectionIdentity("Trigger", "t_start"));

        var problems = MissionRun.Execute(folder.Inputs(baseFolder, ledger, "mod1")).Problems.ToList();

        var problem = Assert.Single(problems, p => p.Rule == "G05");
        Assert.Equal("G05|t_end", problem.Subject);
        Assert.Equal(OriginKind.Mod, problem.Origin!.Kind);
        Assert.Equal("mod1", problem.Origin.ModId);
        Assert.Equal(ModEvidence.File, problem.Origin.Evidence);
    }

    [Fact]
    public void AWholeFileCopyKeepsTheBaseGameProblems()
    {
        using var baseFolder = new MissionFolder();
        baseFolder.Write(Script, Broken);
        using var folder = new MissionFolder();
        folder.Write(Script, Broken);
        var ledger = new TestLedger();
        ledger.Change(Script, "mod1");
        ledger.Replaced[Script] = "mod1";

        var result = MissionRun.Execute(folder.Inputs(baseFolder, ledger, "mod1"));

        Assert.Equal(MissionScriptState.Replaced, result.Scripts[0].State);
        Assert.True(Assert.Single(result.Problems).Origin!.IsBaseGame);
    }

    [Fact]
    public void AScriptThatOnlyAModHasGivesProblemsOfThatMod()
    {
        using var baseFolder = new MissionFolder();
        using var folder = new MissionFolder();
        folder.Write(Other, Broken + "\n[Trigger]\nnickname = t_y\nAct_ActTrig = t_end\n");
        var ledger = new TestLedger();
        ledger.Change(Other, "mod1");
        ledger.Replaced[Other] = "mod1";

        var problems = MissionRun.Execute(folder.Inputs(baseFolder, ledger, "mod1")).Problems.ToList();

        Assert.NotEmpty(problems);
        Assert.All(problems, p => Assert.Equal("mod1", p.Origin!.ModId));
        // A line of the active mod keeps the severity of its rule.
        Assert.Equal(DiagnosticSeverity.Error, problems.Single(p => p.Rule == "M01").Severity);
    }

    [Fact]
    public void AProblemOfALowerModIsAWarning()
    {
        using var baseFolder = new MissionFolder();
        using var folder = new MissionFolder();
        folder.Write(Other, Story + "\n[Trigger]\nnickname = t_y\nAct_ActTrig = t_end\n");
        var ledger = new TestLedger();
        ledger.Change(Other, "lower");
        ledger.Replaced[Other] = "lower";

        var problems = MissionRun.Execute(folder.Inputs(baseFolder, ledger, "active")).Problems.ToList();

        var problem = problems.Single(p => p.Rule == "M01");
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("lower", problem.Origin!.ModId);
    }

    [Fact]
    public void ANewerRunDropsTheResultOfAnOlderRun()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(Script, Story);
        ws.WriteProfile();
        var session = ws.Session(null);

        var first = session.MissionInputs(FullCoverage.Instance);
        var second = session.MissionInputs(FullCoverage.Instance);
        var older = MissionRun.Execute(first);
        var newer = MissionRun.Execute(second);

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.False(session.TakeMissionRun(older));
        Assert.True(session.TakeMissionRun(newer));
        // The roles of the lists follow the scripts of the run.
        Assert.Contains("mission", session.DataFiles.RolesOf(Script));
    }

    [Fact]
    public void ARunWritesNoFileAndLeavesTheSessionClean()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(Script, Broken);
        ws.WriteProfile();
        var session = ws.Session(null);
        var path = System.IO.Path.Combine(ws.Base, Script);
        var before = System.IO.File.ReadAllBytes(path);
        var time = System.IO.File.GetLastWriteTimeUtc(path);

        var result = MissionRun.Execute(session.MissionInputs(FullCoverage.Instance));
        session.TakeMissionRun(result);

        Assert.NotEmpty(result.Problems);
        Assert.False(session.IsDirty);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
        Assert.Equal(time, System.IO.File.GetLastWriteTimeUtc(path));
        Assert.True(result.IsScript(Script));
    }

    [Fact]
    public void ARunAddsNoEngineLogLine()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Broken + "\n[Trigger]\nnickname = t_mb\nCnd_CmpToPlane = inside, Player, 1, 0, 0, 0\nAct_GiveMB = Player, mb\n");
        var lines = new List<string>();
        var old = FLLog.AppendLine;
        FLLog.AppendLine = (line, severity) =>
        {
            lock (lines)
                lines.Add(line);
        };
        try
        {
            _ = EngineCoverage.Instance.All.Count;
            _ = MissionSchema.Retail.Actions.Count;
            _ = MissionPrefabs.Retail.Definitions.Count;
            _ = StoryStages.Names.Count;
            var result = MissionRun.Execute(folder.Inputs());
            Assert.NotEmpty(result.Problems);
            FLLog.Flush();
        }
        finally
        {
            FLLog.AppendLine = old;
        }

        Assert.Empty(lines);
    }
}

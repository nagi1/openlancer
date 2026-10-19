using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Runtime.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The queries of the mission studio in the data layer (sections 5 and 10.2 of the M6a design): the rows of the
/// Missions tab, the OpenLancer report, the search, the bundle, the news and rumors, the text ids and the comm lines.
/// The editor views and an agent server call the same queries (roadmap row MCP1).
/// </summary>
public sealed class MissionSummaryTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    private const string Broken = Story + """

        [Trigger]
        nickname = t_x
        Cnd_True = no_params
        Act_ActTrig = t_end
        """;

    [Fact]
    public void TheFourteenSlotRowsComeInSlotOrderWithTheirStagesAndNextRules()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        folder.Write(Path, Story);

        var rows = MissionSummary.Rows(MissionRun.Execute(folder.Inputs()), showBaseGame: false);

        Assert.Equal(15, rows.Count);
        Assert.Equal(MissionSchema.Retail.StorySlots.Select(s => s.Slot), rows.Take(14).Select(r => r.Slot));
        Assert.Equal(("M01a", "MISSIONS\\M01A\\m01a.ini", "1", "2", "At once", "Base game", 2),
            (rows[0].Slot, rows[0].ScriptText, rows[0].StartStage, rows[0].AcceptStage, rows[0].NextMission, rows[0].State,
                rows[0].Triggers));
        Assert.Equal(("4", "4", "Worth grows by 3,500", "Missing"),
            (rows[1].StartStage, rows[1].AcceptStage, rows[1].NextMission, rows[1].State));
        Assert.Equal("The story ends", rows[13].NextMission);
        Assert.Contains("mission_01a_loaded", rows[0].StartTooltip);
        Assert.Equal("No mod changes this script.", rows[0].StateTooltip);
        Assert.Equal(("No slot", "", "MISSIONS\\TEST\\test.ini"), (rows[14].Slot, rows[14].StartStage, rows[14].ScriptText));
    }

    [Fact]
    public void TheHiddenCountsFollowTheToggle()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Broken);
        var result = MissionRun.Execute(folder.Inputs());

        var hidden = MissionSummary.Rows(result, showBaseGame: false)[0];
        var shown = MissionSummary.Rows(result, showBaseGame: true)[0];

        Assert.Equal((0, 0, 1), (hidden.Errors, hidden.Warnings, hidden.Hidden));
        Assert.Equal((0, 1, 0), (shown.Errors, shown.Warnings, shown.Hidden));
    }

    [Fact]
    public void TheOpenLancerReportNamesTheLinesThatDoNothing()
    {
        var name = EngineCoverage.Instance.All.First(p => p.Value == EngineMark.NoEffect && p.Key.StartsWith("Act_")).Key;
        var index = Index(Story + $"\n[Trigger]\nnickname = t_y\nCnd_True = no_params\n{name} = x\n{name} = y\n",
            coverage: EngineCoverage.Instance);

        var report = MissionSummary.OpenLancer(index);

        Assert.True(report.HasGaps);
        Assert.Equal("2 lines do nothing", report.Text);
        Assert.EndsWith($"{name}: no effect (2 lines)", report.Tooltip);
        Assert.Equal((name, 2, EngineMark.NoEffect), (report.Gaps[0].Name, report.Gaps[0].Lines, report.Gaps[0].Mark));
        Assert.Equal(("Runs", false), (MissionSummary.OpenLancer(Index(Story)).Text,
            MissionSummary.OpenLancer(Index(Story)).HasGaps));
    }

    /// <summary>A condition with no effect never becomes true, so the actions of its trigger
    /// never run in OpenLancer, and the report counts them.</summary>
    [Fact]
    public void TheActionsAfterAConditionWithNoEffectNeverRun()
    {
        var condition = EngineCoverage.Instance.All.First(p => p.Value == EngineMark.NoEffect && p.Key.StartsWith("Cnd_")).Key;
        var index = Index(Story + $"\n[Trigger]\nnickname = t_y\n{condition} = x\nAct_ActTrig = t_end\nAct_ChangeState = FAIL\n",
            coverage: EngineCoverage.Instance);

        var trigger = index.Trigger("t_y")!;
        var report = MissionSummary.OpenLancer(index);

        Assert.Equal(EngineMark.NoEffect, trigger.Condition!.Mark);
        Assert.All(trigger.Actions, a => Assert.Equal(EngineMark.NeverRuns, a.Mark));
        Assert.Equal("3 lines do nothing", report.Text);
        var never = report.Gaps.Single(g => g.Mark == EngineMark.NeverRuns);
        Assert.Equal((MissionSummary.NeverRunsGroup, 2, "never runs (2 lines)"), (never.Name, never.Lines, never.Text));
        Assert.Equal(EngineMark.Runs, index.Trigger("t_end")!.Actions[0].Mark);
    }

    [Fact]
    public void TheSearchFindsTriggersByNicknameAndByValue()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        var result = MissionRun.Execute(folder.Inputs());
        var analysis = result.Of(Script)!;
        var row = MissionSummary.Rows(result, false)[0];

        Assert.Equal(["t_start"], MissionSummary.MatchingTriggers(analysis, "START").Select(t => t.Nickname));
        Assert.Equal(["t_end"], MissionSummary.MatchingTriggers(analysis, "succeed").Select(t => t.Nickname));
        Assert.True(MissionSummary.Matches(row, [], "m01a"));
        Assert.True(MissionSummary.Matches(row, MissionSummary.MatchingTriggers(analysis, "succeed"), "succeed"));
        Assert.False(MissionSummary.Matches(row, MissionSummary.MatchingTriggers(analysis, "nothing_here"), "nothing_here"));
    }

    [Fact]
    public void TheBundleCountsTheFilesTheVoiceLinesAndTheTexts()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story + """

            [Trigger]
            nickname = t_more
            Cnd_True = no_params
            Act_CallThorn = missions\m01a\clip_a.thn
            Act_CallThorn = missions\m01a\clip_a.thn
            Act_SetTitle = 1234

            [NNObjective]
            nickname = o1
            type = navmarker, sys_a, 2001, 2002, 0, 0, 0

            [NNObjective]
            nickname = o2
            type = ids, 3001
            """);
        var result = MissionRun.Execute(folder.Inputs());
        var analysis = result.Of(Script)!;

        var bundle = MissionSummary.Bundle(result, analysis).ToDictionary(r => r.Name);

        Assert.Equal(MissionBundleAction.OpenMission, bundle["Script"].Action);
        Assert.Equal("1 file", bundle["Space cutscenes"].Value);
        Assert.Equal(MissionBundleAction.OpenCutscene, bundle["Space cutscenes"].Action);
        Assert.Equal("open the file in the Cutscene tab", bundle["Space cutscenes"].ActionPhrase);
        Assert.Equal("0 ship builds", bundle["Mission ships"].Value);
        Assert.Equal(["1234", "2001", "2002", "3001"], analysis.Index.TextIds());
        Assert.Equal("4 text ids", bundle["Text"].Value);
        Assert.Equal((MissionBundleAction.ShowSection, "text"), (bundle["Text"].Action, bundle["Text"].Section));
        Assert.Equal((MissionBundleAction.ShowSection, "voice"), (bundle["Voice lines"].Action, bundle["Voice lines"].Section));
        Assert.Equal(MissionBundleAction.None, bundle["News and rumors"].Action);
        Assert.Null(bundle["News and rumors"].ActionPhrase);
        Assert.Equal(("0 files", "The script names no such file."), (bundle["Base scenes"].Value, bundle["Base scenes"].Tooltip));
        // A tooltip says what the row holds. A view adds its own gesture.
        Assert.DoesNotContain(bundle.Values, r => r.Tooltip.Contains("click", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheTextIdsComeInGroupsAndTheCommLinesOnce()
    {
        var index = Index(Story + """

            [Trigger]
            nickname = t_more
            Cnd_True = no_params
            Act_SetOffer = 1200
            Act_SetTitle = 1100
            Act_NNIds = 1500, OBJECTIVE
            Act_ChangeState = FAIL, 1400
            Act_SendComm = s1, Player, comm_b
            Act_SendComm = s1, Player, comm_a
            Act_SendComm = s1, Player, COMM_B

            [NNObjective]
            nickname = o1
            type = ids, 1300

            [MsnShip]
            nickname = s1
            """);

        Assert.Equal([("Title", "1100"), ("Offer", "1200"), ("Objective", "1300"), ("Fail text", "1400"),
            ("Log line", "1500")], MissionSummary.TextIds(index).Select(t => (t.Group, t.Id)));
        Assert.Equal(["comm_b", "comm_a"], MissionSummary.CommLines(index));
    }

    [Fact]
    public void TheNewsAndRumorsOfASlotFollowTheStageRanges()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        folder.Write(@"DATA\MISSIONS\news.ini", "[NewsItem]\nrank = mission_01a_loaded, mission_01b_loaded\n" +
                                                "[NewsItem]\nrank = mission_04_loaded, mission_end\n");
        folder.Write(@"DATA\MISSIONS\mBases.ini", "[GF_NPC]\nrumor = base_0_rank, mission_end, 1, 100\n");
        var result = MissionRun.Execute(folder.Inputs());
        var slots = MissionSchema.Retail.StorySlots;

        Assert.Equal(3, result.StoryItems.Count);
        Assert.Equal((1, 1), MissionSummary.StoryItems(result, slots[0]));
        Assert.Equal((0, 1), MissionSummary.StoryItems(result, slots[1]));
        Assert.Equal((1, 1), MissionSummary.StoryItems(result, slots[4]));
    }

    /// <summary>The system that the Scene opens with a mission (section 7.1 of the M6a design), and the menu "Mission
    /// systems" (section 7.4).</summary>
    [Fact]
    public void TheStartSystemIsTheSystemOfTheStartTriggerOrTheMostNamedSystem()
    {
        const string text = """
            [Trigger]
            nickname = t_start
            InitState = ACTIVE
            Cnd_True = no_params

            [Trigger]
            nickname = t_a
            system = sys_a
            Cnd_True = no_params

            [Trigger]
            nickname = t_b1
            system = sys_b
            Cnd_True = no_params

            [Trigger]
            nickname = t_b2
            system = SYS_B
            Cnd_True = no_params

            [Trigger]
            nickname = t_any
            system = ANY
            Cnd_True = no_params
            """;

        Assert.Equal([("sys_b", 2), ("sys_a", 1)], MissionSummary.TriggerSystems(Index(text)));
        Assert.Equal("sys_b", MissionSummary.StartSystem(Index(text)));
        Assert.Equal("sys_c", MissionSummary.StartSystem(Index(text.Replace("InitState = ACTIVE",
            "InitState = ACTIVE\nsystem = sys_c"))));
        Assert.Null(MissionSummary.StartSystem(Index("[Trigger]\nnickname = t\nCnd_True = no_params\n")));
    }

    [Fact]
    public void TheBaseLabelNamesTheGameAndItsVersion()
    {
        Assert.Equal("Base game: Unknown version", MissionSummary.BaseLabel(null));
    }

    [Fact]
    public void TheHiddenLineCountsBaseGameMessages()
    {
        Assert.Null(BaseGameMessages.HiddenLine(0));
        Assert.Equal("1 base-game message is hidden.", BaseGameMessages.HiddenLine(1));
        Assert.Equal("1,234 base-game messages are hidden.", BaseGameMessages.HiddenLine(1234));
    }
}

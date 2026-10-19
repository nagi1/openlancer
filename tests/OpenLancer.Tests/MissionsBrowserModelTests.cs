using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Games;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Missions;
using OpenLancer.Runtime.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The rows, counts and bundles of the Missions tab (section 5 of the M6a design), on synthetic scripts.
/// </summary>
public sealed class MissionsBrowserModelTests
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

        var rows = MissionsBrowserModel.Rows(MissionRun.Execute(folder.Inputs()), showBaseGame: false);

        Assert.Equal(15, rows.Count);
        Assert.Equal(MissionSchema.Retail.StorySlots.Select(s => s.Slot), rows.Take(14).Select(r => r.Slot));
        Assert.Equal(("M01a", "MISSIONS\\M01A\\m01a.ini", "1", "2", "At once", "Base game", 2),
            (rows[0].Slot, rows[0].ScriptText, rows[0].StartStage, rows[0].AcceptStage, rows[0].NextMission, rows[0].State,
                rows[0].Triggers));
        Assert.Equal(("4", "4", "Worth grows by 3,500", "Missing"),
            (rows[1].StartStage, rows[1].AcceptStage, rows[1].NextMission, rows[1].State));
        Assert.Equal("The story ends", rows[13].NextMission);
        Assert.Contains("mission_01a_loaded", rows[0].StartTooltip);
        Assert.Equal(("No slot", "", "MISSIONS\\TEST\\test.ini"), (rows[14].Slot, rows[14].StartStage, rows[14].ScriptText));
    }

    [Fact]
    public void TheHiddenCountsFollowTheToggle()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Broken);
        var result = MissionRun.Execute(folder.Inputs());

        var hidden = MissionsBrowserModel.Rows(result, showBaseGame: false)[0];
        var shown = MissionsBrowserModel.Rows(result, showBaseGame: true)[0];

        Assert.Equal((0, 0, 1), (hidden.Errors, hidden.Warnings, hidden.Hidden));
        Assert.Equal((0, 1, 0), (shown.Errors, shown.Warnings, shown.Hidden));
    }

    [Fact]
    public void TheOpenLancerColumnNamesTheLinesThatDoNothing()
    {
        var name = EngineCoverage.Instance.All.First(p => p.Value == EngineMark.NoEffect && p.Key.StartsWith("Act_")).Key;
        var index = Index(Story + $"\n[Trigger]\nnickname = t_y\nCnd_True = no_params\n{name} = x\n{name} = y\n",
            coverage: EngineCoverage.Instance);

        var (text, tooltip, gaps) = MissionsBrowserModel.OpenLancerOf(index);

        Assert.True(gaps);
        Assert.Equal("2 lines do nothing", text);
        Assert.EndsWith($"{name}: no effect (2 lines)", tooltip);
        Assert.Equal(("Runs", false), (MissionsBrowserModel.OpenLancerOf(Index(Story)).Text,
            MissionsBrowserModel.OpenLancerOf(Index(Story)).Gaps));
    }

    [Fact]
    public void TheOpenLancerColumnHidesForAModWhoseTargetsHaveNoOpenLancer()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(Script, Story);
        ws.Mod("data", []);
        System.IO.File.WriteAllText(System.IO.Path.Combine(ws.ModFolder("data"), ModManifest.FileName),
            """{ "id": "data", "formatVersion": 2, "tier": 1, "targets": [ "vanilla-1.1" ] }""");
        ws.Mod("all", []);
        ws.WriteProfile("data", "all");

        using (var session = ws.Session("data"))
        {
            Assert.True(session.HidesOpenLancer);
            Assert.False(session.MissionInputs(FullCoverage.Instance).Options.TargetsOpenLancer);
        }
        using (var session = ws.Session("all"))
        {
            Assert.False(session.HidesOpenLancer);
            Assert.True(session.MissionInputs(FullCoverage.Instance).Options.TargetsOpenLancer);
        }
        using (var session = ws.Session(null))
            Assert.False(session.HidesOpenLancer);
    }

    [Fact]
    public void TheSearchFindsTriggersByNicknameAndByValue()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        var result = MissionRun.Execute(folder.Inputs());
        var analysis = result.Of(Script)!;
        var row = MissionsBrowserModel.Rows(result, false)[0];

        Assert.Equal(["t_start"], MissionsBrowserModel.MatchingTriggers(analysis, "START").Select(t => t.Nickname));
        Assert.Equal(["t_end"], MissionsBrowserModel.MatchingTriggers(analysis, "succeed").Select(t => t.Nickname));
        Assert.True(MissionsBrowserModel.Matches(row, analysis, "m01a"));
        Assert.False(MissionsBrowserModel.Matches(row, analysis, "nothing_here"));
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

        var bundle = MissionsBrowserModel.Bundle(result, analysis).ToDictionary(r => r.Name);

        Assert.Equal(MissionBundleAction.OpenMission, bundle["Script"].Action);
        Assert.Equal("1 file", bundle["Space cutscenes"].Value);
        Assert.Equal(MissionBundleAction.OpenCutscene, bundle["Space cutscenes"].Action);
        Assert.Equal("0 ship builds", bundle["Mission ships"].Value);
        Assert.Equal(["1234", "2001", "2002", "3001"], analysis.Index.TextIds());
        Assert.Equal("4 text ids", bundle["Text"].Value);
        Assert.Equal(MissionBundleAction.None, bundle["News and rumors"].Action);
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
        Assert.Equal((1, 1), MissionsBrowserModel.StoryItems(result, slots[0]));
        Assert.Equal((0, 1), MissionsBrowserModel.StoryItems(result, slots[1]));
        Assert.Equal((1, 1), MissionsBrowserModel.StoryItems(result, slots[4]));
    }

    [Fact]
    public void TheBaseLabelNamesTheGameAndItsVersion()
    {
        Assert.Equal("Base game: Unknown version", MissionsBrowserModel.BaseLabel(null));
    }
}

using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The model of the Missions tab (section 5 of the M6a design): the columns, and the results that it keeps between
/// frames. <see cref="MissionSummaryTests"/> checks the queries that make them.
/// </summary>
public sealed class MissionsBrowserModelTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    [Fact]
    public void TheModelKeepsItsRowsUntilTheRunOrTheToggleChanges()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        var result = MissionRun.Execute(folder.Inputs());
        var model = new MissionsBrowserModel();

        var rows = model.Rows(result, showBaseGame: false);

        Assert.Same(rows, model.Rows(result, showBaseGame: false));
        Assert.NotSame(rows, model.Rows(result, showBaseGame: true));
        var again = MissionRun.Execute(folder.Inputs());
        Assert.NotSame(model.Rows(again, showBaseGame: true), model.Rows(result, showBaseGame: true));
    }

    [Fact]
    public void TheModelKeepsTheSearchMatchesAndTheBundleOfARun()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        var result = MissionRun.Execute(folder.Inputs());
        var analysis = result.Of(Script)!;
        var model = new MissionsBrowserModel();

        var matches = model.MatchingTriggers(result, analysis, "succeed");

        Assert.Equal(["t_end"], matches.Select(t => t.Nickname));
        Assert.Same(matches, model.MatchingTriggers(result, analysis, "succeed"));
        Assert.Empty(model.MatchingTriggers(result, analysis, "nothing_here"));
        Assert.True(model.Matches(result, model.Rows(result, false)[0], "succeed"));
        Assert.Same(model.Bundle(result, analysis), model.Bundle(result, analysis));
        Assert.Contains("M01a", model.Rows(result, false).Select(r => r.Slot));

        model.Reset();
        Assert.NotSame(matches, model.MatchingTriggers(result, analysis, "succeed"));
    }

    /// <summary>A base-game problem is a warning (decision D410), so only the Warnings column has a hidden count.</summary>
    [Fact]
    public void OnlyTheWarningsColumnNamesTheHiddenCount()
    {
        var columns = MissionsBrowserModel.Columns.ToDictionary(c => c.Name, c => c.Tooltip);

        Assert.DoesNotContain("brackets", columns["Errors"]);
        Assert.Contains("brackets", columns["Warnings"]);
        Assert.Equal("OpenLancer", MissionsBrowserModel.Columns[MissionsBrowserModel.OpenLancerColumn].Name);
    }

    [Fact]
    public void ABundleTooltipSaysWhatADoubleClickDoes()
    {
        var script = new MissionBundleRow("Script", "x.ini", "The mission script.", MissionBundleAction.OpenMission, ["x.ini"]);
        var clips = new MissionBundleRow("Space cutscenes", "2 files", "a.thn\nb.thn", MissionBundleAction.OpenCutscene,
            ["a.thn", "b.thn"]);
        var news = new MissionBundleRow("News and rumors", "0 news items, 0 rumors", "Where the items are.",
            MissionBundleAction.None, []);

        Assert.Equal("The mission script.\nDouble-click to open the mission.", MissionsBrowser.BundleTooltip(script));
        Assert.EndsWith("Double-click to open the first file in the Cutscene tab. Right-click to pick a file.",
            MissionsBrowser.BundleTooltip(clips));
        Assert.Equal("Where the items are.", MissionsBrowser.BundleTooltip(news));
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
}

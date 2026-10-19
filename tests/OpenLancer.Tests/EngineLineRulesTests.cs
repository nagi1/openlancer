using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Editor.Logs;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The kinds of engine lines (section 4.5 of the console filter design).</summary>
public sealed class EngineLineRulesTests
{
    private static EngineLine Line(string text) => EngineLine.Parse(text)!;

    /// <summary>Each kind matches its sample. So no sample of another kind gives that kind.</summary>
    [Fact]
    public void EachKindMatchesItsSampleAndNoOtherSample()
    {
        foreach (var kind in EngineLineRules.Kinds)
            Assert.Same(kind, EngineLineRules.Match(Line(kind.Sample)));
        Assert.Equal(EngineLineRules.Kinds.Count, EngineLineRules.Kinds.Select(k => k.Sample).Distinct().Count());
    }

    /// <summary>A specific kind comes before a general one: a duplicate animation line of a room is an engine gap, each
    /// other duplicate is a data problem.</summary>
    [Fact]
    public void ASpecificKindComesBeforeAFallback()
    {
        Assert.Equal("G08", EngineLineRules.Match(Line(
            @"[Warning: 12:00:00] Ini: Duplicate of animation at section Room_Info: data\a.ini, line 2"))!.Id);
        Assert.Equal("B04", EngineLineRules.Match(Line(
            @"[Warning: 12:00:00] Ini: Duplicate of animation at section Object: data\a.ini, line 2"))!.Id);
        Assert.Equal("G03", EngineLineRules.Match(Line(
            @"[Warning: 12:00:00] Ini: Too many components for at_t at section start_effect: data\a.ini, line 2"))!.Id);
        Assert.Equal("B07", EngineLineRules.Match(Line(
            @"[Warning: 12:00:00] Ini: Too many components for pos at section Object: data\a.ini, line 2"))!.Id);
        Assert.Equal("B06", EngineLineRules.Match(Line(@"[Warning: 12:00:00] Ini: Unknown section Foo at data\a.ini, line 2"))!.Id);
        Assert.Equal("G04", EngineLineRules.Match(Line(@"[Warning: 12:00:00] Ini: Unknown section tumble at data\a.ini, line 2"))!.Id);
    }

    [Fact]
    public void ALineOfAnotherFormHasNoKind()
    {
        Assert.Null(EngineLineRules.Match(Line("[Error: 12:00:00] Game: Something failed")));
        Assert.Null(EngineLineRules.Match(Line(@"[Error: 12:00:00] Ini: Missing required field nickname at section Ship: data\a.ini, line 2")));
    }

    [Fact]
    public void TheClassesAndTheDispositions()
    {
        Assert.Equal(EngineLineClass.DataProblem, EngineLineRules.Find("B01")!.Class);
        Assert.Equal(EngineLineClass.EngineGap, EngineLineRules.Find("G13")!.Class);
        Assert.Equal(EngineLineDisposition.Drop, EngineLineRules.Find("O01")!.Disposition);
        Assert.Equal(EngineLineDisposition.Notice, EngineLineRules.Find("O02")!.Disposition);
        Assert.True(EngineLineRules.Find("G15")!.OpenLancerOrigin);
        Assert.Equal("Engine gap. The game runs this fuse action. The OpenLancer engine does not run it yet.",
            EngineLineRules.Find("G04")!.DetailText);
        Assert.All(EngineLineRules.Kinds.Where(k => k.Id[0] is 'B' or 'G'),
            k => Assert.Equal(EngineLineDisposition.Entry, k.Disposition));
    }

    /// <summary>A line of a component without a file names its subject: the ship or the pilot.</summary>
    [Fact]
    public void TheSubjectOfAShipLineAndOfAPilotLine()
    {
        Assert.Equal((EngineLineSubject.Ship, "ship_a"),
            EngineLineRules.Subject(Line("[Error: 12:00:00] Ship: Unrecognised hp_type hp_x in ship_a")));
        Assert.Equal((EngineLineSubject.Pilot, "pilot_a"),
            EngineLineRules.Subject(Line("[Warning: 12:00:00] Pilot: pilot_a: Unable to find JobBlock 'j'")));
        Assert.Null(EngineLineRules.Subject(Line(@"[Warning: 12:00:00] Ini: Unknown entry k at section Ship: data\a.ini, line 2")));
    }

    /// <summary>The Console drops a process line (decision D506). It adds no entry, and it never counts.</summary>
    [Fact]
    public void AProcessLineGivesNoEntry()
    {
        var log = new ConsoleLog();
        log.AddEngineLine("[Warning: 12:00:00] MaterialMap: Switching global material map. TODO make instance",
            LogSeverity.Warning);
        log.AddGameLine(log.BeginGameRun(),
            ConsoleLog.ParseGameLine("[Warning: 12:00:00] MaterialMap: Switching global material map. TODO make instance")!,
            "Play");

        Assert.Empty(log.Entries);
    }

    /// <summary>The lines of a slow game server give one info entry for each Play run (decision D507).</summary>
    [Fact]
    public void ANoticeGivesOneInfoEntryForEachPlayRun()
    {
        var log = new ConsoleLog();
        var first = log.BeginGameRun();
        log.AddGameLine(first, ConsoleLog.ParseGameLine("[Warning: 12:00:00] Server: Running slow: update took 34.57ms")!, "Play");
        log.AddGameLine(first, ConsoleLog.ParseGameLine("[Warning: 12:00:01] Server: Running slow: update took 21.10ms")!, "Play");
        log.AddGameLine(first, ConsoleLog.ParseGameLine("[Warning: 12:00:09] Server: Still running slow: update took 40.00ms")!, "Play");
        var second = log.BeginGameRun();
        log.AddGameLine(second, ConsoleLog.ParseGameLine("[Warning: 12:10:00] Server: Running slow: update took 50.00ms")!, "Play");

        Assert.Equal(
        [
            "The game server was slow. An update took 34.57 ms. The Play log has each slow update.",
            "The game server was slow. An update took 50.00 ms. The Play log has each slow update.",
        ], log.Entries.Select(e => e.Message));
        Assert.All(log.Entries, e => Assert.Equal((ConsoleSeverity.Info, "Play"), (e.Severity, e.Source)));
        Assert.Equal((0, 0), (log.UnseenWarnings, log.UnseenErrors));
    }

    /// <summary>The new-character save of Play has the origin OpenLancer (G15), so it always shows.</summary>
    [Fact]
    public void AnOpenLancerKindHasTheOriginOpenLancer()
    {
        var log = new ConsoleLog();
        log.AddGameLine(log.BeginGameRun(), ConsoleLog.ParseGameLine(
            "[Error: 12:00:00] Ini: Failed to parse float '' for cargo in section Player: sample: new_player:16")!, "Play");

        var entry = Assert.Single(log.Entries);
        Assert.Equal((OriginKind.OpenLancer, "G15"), (entry.Origin.Kind, entry.Kind!.Id));
        Assert.Equal(1, log.UnseenErrors);
    }

    /// <summary>The pins are the counts of the base run of each data set before the fork commits OL-0039 to OL-0052:
    /// HD Edition 0.7.1 gives 120 lines (117 of the data load, 1 of the Scene, 2 of the story missions), and the retail
    /// game 261 (240, 1 and 20).</summary>
    [Fact]
    public void ThePinsAddUpToTheLinesOfTheBaseRuns()
    {
        Assert.Equal(120, EngineLineRules.Kinds.Sum(k => EngineLineRules.Pin(k, EngineLineDataSet.HdEdition071)));
        Assert.Equal(261, EngineLineRules.Kinds.Sum(k => EngineLineRules.Pin(k, EngineLineDataSet.Vanilla)));
        Assert.All(EngineLineRules.Kinds.Where(k => k.Disposition != EngineLineDisposition.Entry || k.OpenLancerOrigin),
            k => Assert.Empty(k.Pins));
    }
}

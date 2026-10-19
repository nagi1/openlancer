using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Logs;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The origins of the Console entries, the batches of engine lines and the counts of the tab badges and the status bar
/// (sections 3.4 and 4.3 of the console filter design).
/// </summary>
public sealed class ConsoleLogOriginTests
{
    private const string Known = @"[Warning: 12:00:00] Ini: Duplicate of reputation at section Object: data\base.ini, line 3";
    private const string KnownError = @"[Error: 12:00:00] Ini: Missing required field effect at section start_effect: data\fx.ini, line 9";
    private const string New = @"[Warning: 12:00:00] Ini: Unknown entry typo at section Object: data\other.ini, line 5";

    /// <summary>A Console with the origin rules of a profile with a mod file, so its batches wait for the base
    /// run.</summary>
    private static (ConsoleLog Log, TestWorkspace Workspace) Profile(bool modFiles = true)
    {
        var ws = ListWorkspace.Create(activeFiles: modFiles
            ? new() { [@"patches\" + ListWorkspace.Li01Path] = "[Object]\nnickname = Li01_01\nloadout = lo_shared\n" }
            : null);
        var log = new ConsoleLog { Origins = new EngineLineOrigins(ws.Session("act")) };
        return (log, ws);
    }

    /// <summary>The engine lines between the start line and the end line of a batch.</summary>
    private static void Batch(ConsoleLog log, params string[] lines)
    {
        log.AddEngineLine(ConsoleLog.BatchStartLine("b1"), LogSeverity.Info);
        foreach (var line in lines)
            log.AddEngineLine(line, line.StartsWith("[Error", System.StringComparison.Ordinal) ? LogSeverity.Error : LogSeverity.Warning);
        log.AddEngineLine(ConsoleLog.BatchEndLine("b1"), LogSeverity.Info);
    }

    private static IReadOnlyDictionary<string, int> Counts(params string[] lines) =>
        lines.Select(l => EngineLine.Parse(l)!).CountBy(l => l.Key).ToDictionary(c => c.Key, c => c.Value);

    /// <summary>Pending entries and base-game entries never change the unseen counts or the last entry, so the tab
    /// badges and the status bar never show them.</summary>
    [Fact]
    public void PendingAndBaseGameEntriesDoNotCount()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        var info = log.AddStatus(ConsoleSeverity.Info, "Editor", "Opened.");

        Batch(log, Known, KnownError);

        Assert.Equal(2, log.PendingCount);
        Assert.Equal((0, 0), (log.UnseenWarnings, log.UnseenErrors));
        Assert.Same(info, log.Last);
        Assert.Equal((0, 0), (log.Count(ConsoleSeverity.Warning, true), log.Count(ConsoleSeverity.Error, true)));

        log.BaseRunEnded(Counts(Known, KnownError));

        Assert.Equal((0, 2), (log.PendingCount, log.BaseGameCount));
        Assert.Equal((0, 0), (log.UnseenWarnings, log.UnseenErrors));
        Assert.Same(info, log.Last);
        Assert.All(log.Entries.Skip(1), e => Assert.Equal(MessageOrigin.BaseGame, e.Origin));
    }

    /// <summary>A pending entry that resolves to a mod counts as a new entry: it adds to the unseen counts and becomes
    /// the last entry.</summary>
    [Fact]
    public void AResolveToAModAddsToTheUnseenCounts()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        Batch(log, Known, New);

        log.BaseRunEnded(Counts(Known));

        var shown = Assert.Single(log.Entries, e => !e.Origin.IsBaseGame);
        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.OnlyMod), shown.Origin);
        Assert.Equal((1, 0), (log.UnseenWarnings, log.UnseenErrors));
        Assert.Same(shown, log.Last);
        Assert.Equal("B05", shown.Kind!.Id);
    }

    /// <summary>The toggle shows the base-game entries and counts them in the severity counts. The badges do not
    /// change.</summary>
    [Fact]
    public void TheToggleChangesTheVisibleListAndTheSeverityCountsNotTheBadges()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        Batch(log, Known, New);
        log.BaseRunEnded(Counts(Known));
        var badges = (log.UnseenWarnings, log.UnseenErrors);

        Assert.Equal(1, log.Count(ConsoleSeverity.Warning, false));
        Assert.Equal(2, log.Count(ConsoleSeverity.Warning, true));
        Assert.Single(log.Entries, e => ConsolePanel.IsShown(e.Origin, false));
        Assert.Equal(2, log.Entries.Count(e => ConsolePanel.IsShown(e.Origin, true)));
        Assert.Equal(badges, (log.UnseenWarnings, log.UnseenErrors));
    }

    /// <summary>A failed base run makes the pending entries unknown. They show and count (decision D517).</summary>
    [Fact]
    public void AFailedBaseRunShowsThePendingEntries()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        Batch(log, Known);

        log.BaseRunFailed("oltool did not start.");

        Assert.Equal(MessageOrigin.Unknown, Assert.Single(log.Entries).Origin);
        Assert.Equal(1, log.UnseenWarnings);
    }

    /// <summary>A profile without a mod file needs no base run, so each line of a batch is a base-game line at
    /// once.</summary>
    [Fact]
    public void WithoutAModFileTheLinesOfABatchAreBaseGameAtOnce()
    {
        var (log, ws) = Profile(modFiles: false);
        using var _ = ws;
        Batch(log, Known, New);

        Assert.Equal((0, 2), (log.PendingCount, log.BaseGameCount));
        Assert.Equal(0, log.UnseenWarnings);
    }

    /// <summary>A line outside a batch has the origin unknown and shows. Before a profile opens, a line has the origin
    /// OpenLancer.</summary>
    [Fact]
    public void ALineOutsideABatchIsUnknown()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        log.AddEngineLine(Known, LogSeverity.Warning);
        var none = new ConsoleLog();
        none.AddEngineLine(Known, LogSeverity.Warning);

        Assert.Equal(MessageOrigin.Unknown, Assert.Single(log.Entries).Origin);
        Assert.Equal(MessageOrigin.OpenLancer, Assert.Single(none.Entries).Origin);
        Assert.Equal(1, log.UnseenWarnings);
    }

    /// <summary>Clear removes the pending batches too: their entries do not come back when the base run ends.</summary>
    [Fact]
    public void ClearDropsThePendingBatches()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        Batch(log, New);

        log.Clear();
        log.BaseRunEnded(Counts());

        Assert.Empty(log.Entries);
        Assert.Equal((0, 0, 0), (log.PendingCount, log.UnseenWarnings, log.Count(ConsoleSeverity.Warning, true)));
    }

    /// <summary>A batch that did not end keeps its lines for the comparison after Clear, but not their entries.</summary>
    [Fact]
    public void ABatchThatDidNotEndKeepsCountingAfterClear()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        log.BaseRunEnded(Counts(Known));
        log.AddEngineLine(ConsoleLog.BatchStartLine("b2"), LogSeverity.Info);
        log.AddEngineLine(Known, LogSeverity.Warning);

        log.Clear();
        log.AddEngineLine(Known.Replace("line 3", "line 4"), LogSeverity.Warning);
        log.AddEngineLine(ConsoleLog.BatchEndLine("b2"), LogSeverity.Info);

        // The base run gives the line once, and the batch has it twice. The line after Clear is the second line.
        var entry = Assert.Single(log.Entries);
        Assert.False(entry.Origin.IsBaseGame);
    }

    /// <summary>The capacity drop corrects the count of the origin group of the oldest entry.</summary>
    [Fact]
    public void TheCapacityDropUpdatesTheOriginCounts()
    {
        var (log, ws) = Profile(modFiles: false);
        using var _ = ws;
        Batch(log, Known);
        Assert.Equal(1, log.BaseGameCount);
        for (var i = 0; i < ConsoleLog.Capacity; i++)
            log.Add(ConsoleSeverity.Info, "Editor", "info");

        Assert.Equal(0, log.BaseGameCount);
        Assert.Equal(ConsoleLog.Capacity, log.Count(ConsoleSeverity.Info));
    }

    /// <summary>A Play run is a batch whose lines resolve when they arrive (decision D514). The lines of the first load
    /// are base-game lines, and the dedupe of decision D57 stays.</summary>
    [Fact]
    public void APlayRunResolvesItsLinesWhenTheyArrive()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        log.BaseRunEnded(Counts(Known));
        var run = log.BeginGameRun();

        log.AddGameLine(run, EngineLine.Parse(Known)!, "Play");
        log.AddGameLine(run, EngineLine.Parse(New)!, "Play");

        Assert.Equal([MessageOrigin.BaseGame, MessageOrigin.Mod("act", ModEvidence.OnlyMod)],
            log.Entries.Select(e => e.Origin));
        Assert.Equal(["Play", "Play"], log.Entries.Select(e => e.Source));
        Assert.Equal(1, log.UnseenWarnings);
    }

    /// <summary>The lines of a Play run wait while the base run goes, and resolve when it ends.</summary>
    [Fact]
    public void APlayLineWaitsForTheBaseRun()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        var run = log.BeginGameRun();

        log.AddGameLine(run, EngineLine.Parse(New)!, "Play");
        Assert.Equal(1, log.PendingCount);
        log.BaseRunEnded(Counts());

        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.OnlyMod), Assert.Single(log.Entries).Origin);
    }

    /// <summary>A diagnostic keeps its origin. A diagnostic without one has the origin OpenLancer.</summary>
    [Fact]
    public void ADiagnosticKeepsItsOrigin()
    {
        var log = new ConsoleLog();
        var baseGame = log.Add(Diagnostic.Warning("s", "m") with { Origin = MessageOrigin.BaseGame });
        var other = log.Add(Diagnostic.Warning("s", "m"));

        Assert.Equal((MessageOrigin.BaseGame, MessageOrigin.OpenLancer), (baseGame.Origin, other.Origin));
        Assert.Equal(1, log.UnseenWarnings);
        Assert.Same(other, log.Last);
    }

    /// <summary>A change of the origin rules, for example at the close of a profile, shows the pending entries with the
    /// origin unknown.</summary>
    [Fact]
    public void ANewProfileShowsThePendingEntriesOfTheProfileBefore()
    {
        var (log, ws) = Profile();
        using var _ = ws;
        Batch(log, New);

        log.Origins = null;

        Assert.Equal(MessageOrigin.Unknown, Assert.Single(log.Entries).Origin);
        Assert.Equal(0, log.PendingCount);
    }

    /// <summary>The texts of the toggle, the banner and the pending row have a singular and a plural form (section 4.4
    /// of the console filter design).</summary>
    [Fact]
    public void TheTextsHaveASingularAndAPluralForm()
    {
        Assert.Equal(("The Console hides 1 base-game message.", "Show it"), ConsolePanel.BaseGameBanner(1));
        Assert.Equal(("The Console hides 117 base-game messages.", "Show them"), ConsolePanel.BaseGameBanner(117));
        Assert.Equal("The Console compares 1 engine message with the base game. Then it shows the message if a mod " +
                     "causes it.", ConsolePanel.PendingText(1));
        Assert.Equal("The Console compares 117 engine messages with the base game. Then it shows the messages that the " +
                     "mods cause.", ConsolePanel.PendingText(117));
        Assert.Equal("Hide the base-game messages (3).", ConsolePanel.BaseGameToggleTip(3, true));
        Assert.Equal("Show the base-game messages (3). The base game without mods gives these messages too. The mods do " +
                     "not cause them.", ConsolePanel.BaseGameToggleTip(3, false));
    }

    /// <summary>The source of a base-game row has the lock glyph, and an engine gap has the wrench glyph.</summary>
    [Fact]
    public void TheGlyphsOfTheSource()
    {
        var gap = new ConsoleEntry(ConsoleSeverity.Warning, "Ini", "m", default)
        {
            Kind = EngineLineRules.Find("G04"),
            Origin = MessageOrigin.BaseGame,
        };
        var data = new ConsoleEntry(ConsoleSeverity.Warning, "Ini", "m", default) { Kind = EngineLineRules.Find("B05") };

        Assert.Equal($"{Editor.Ui.EditorIcons.Lock} {Editor.Ui.EditorIcons.Wrench} Ini", ConsolePanel.SourceText(gap));
        Assert.Equal("Ini", ConsolePanel.SourceText(data));
    }
}

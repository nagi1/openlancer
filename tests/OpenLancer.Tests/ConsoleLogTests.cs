using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Logs;
using Xunit;

namespace OpenLancer.Tests;

public sealed class ConsoleLogTests
{
    [Fact]
    public void RemovesThePrefixOfAnEngineLineAndUsesTheComponentAsSource()
    {
        var (source, message) = ConsoleLog.ParseEngineLine(
            "[Warning: 22:48:12] Ini: Unknown entry x in section Ship: EXE\\..\\data\\ships\\shiparch.ini");
        Assert.Equal("Ini", source);
        Assert.Equal("Unknown entry x in section Ship: EXE\\..\\data\\ships\\shiparch.ini", message);
    }

    [Fact]
    public void KeepsAllLinesOfAMessage()
    {
        var (source, message) = ConsoleLog.ParseEngineLine("[Error: 22.48.12] Game: Failed\n  at A.B()\n  at C.D()");
        Assert.Equal("Game", source);
        Assert.Equal("Failed\n  at A.B()\n  at C.D()", message);
    }

    [Fact]
    public void GivesTheEngineSourceAndTheFullTextForALineInAnotherForm()
    {
        Assert.Equal((ConsoleLog.EngineSource, "no prefix here"), ConsoleLog.ParseEngineLine("no prefix here"));
        Assert.Equal((ConsoleLog.EngineSource, "[Warning: 22:48:12] no component"),
            ConsoleLog.ParseEngineLine("[Warning: 22:48:12] no component"));
    }

    /// <summary>A diagnostic keeps its place in a cutscene, so a click on the entry opens the element.</summary>
    [Fact]
    public void ADiagnosticKeepsItsPlaceInACutscene()
    {
        var log = new ConsoleLog();
        var place = new ThnLocation(@"DATA\x.thn", ThnElementKind.Event, 2, "props.duration");

        var entry = log.Add(Diagnostic.Error(@"DATA\x.thn", "m") with { Cutscene = place }, "Save 1: ");

        Assert.Equal((ConsoleSeverity.Error, @"DATA\x.thn", "Save 1: m", place),
            (entry.Severity, entry.Source, entry.Message, entry.Cutscene));
        Assert.Null(entry.Target);
        Assert.Null(log.Add(Diagnostic.Warning("s", "m")).Cutscene);
        Assert.Equal(1, log.Count(ConsoleSeverity.Error));
    }

    [Fact]
    public void AddsOnlyEngineWarningsAndErrors()
    {
        var log = new ConsoleLog();
        log.AddEngineLine("[Debug: 22:48:12] Game: debug", LogSeverity.Debug);
        log.AddEngineLine("[Info: 22:48:12] Game: info", LogSeverity.Info);
        log.AddEngineLine("[Warning: 22:48:12] Ini: warning", LogSeverity.Warning);
        log.AddEngineLine("[Error: 22:48:12] Ini: error", LogSeverity.Error);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(ConsoleSeverity.Warning, log.Entries[0].Severity);
        Assert.Equal("Ini", log.Entries[0].Source);
        Assert.Equal("warning", log.Entries[0].Message);
        Assert.Equal(ConsoleSeverity.Error, log.Entries[1].Severity);
    }

    [Fact]
    public void CountsEachSeverityAndTheUnseenWarningsAndErrors()
    {
        var log = new ConsoleLog();
        log.Add(ConsoleSeverity.Info, "Editor", "a");
        log.Add(ConsoleSeverity.Warning, "Editor", "b");
        log.Add(ConsoleSeverity.Error, "Editor", "c");
        log.Add(ConsoleSeverity.Error, "Editor", "d");
        Assert.Equal((1, 1, 2), (log.Count(ConsoleSeverity.Info), log.Count(ConsoleSeverity.Warning),
            log.Count(ConsoleSeverity.Error)));
        Assert.Equal((1, 2), (log.UnseenWarnings, log.UnseenErrors));

        log.MarkSeen();
        Assert.Equal((0, 0), (log.UnseenWarnings, log.UnseenErrors));
        Assert.Equal(4, log.Entries.Count);
    }

    [Fact]
    public void LastIsTheNewestWarningOrError()
    {
        var log = new ConsoleLog();
        Assert.Null(log.Last);
        var warning = log.Add(ConsoleSeverity.Warning, "Editor", "warning");
        log.Add(ConsoleSeverity.Info, "Editor", "info");
        Assert.Same(warning, log.Last);
        var error = log.Add(ConsoleSeverity.Error, "Editor", "error");
        Assert.Same(error, log.Last);

        log.Clear();
        Assert.Null(log.Last);
        Assert.Empty(log.Entries);
        Assert.Equal(0, log.Count(ConsoleSeverity.Error));
    }

    [Fact]
    public void DropsTheOldestEntryAtTheCapacityAndCorrectsTheCounts()
    {
        var log = new ConsoleLog();
        log.Add(ConsoleSeverity.Error, "Editor", "oldest");
        for (var i = 1; i < ConsoleLog.Capacity; i++)
        {
            log.Add(ConsoleSeverity.Info, "Editor", "info");
        }

        Assert.Equal(ConsoleLog.Capacity, log.Entries.Count);
        log.Add(ConsoleSeverity.Info, "Editor", "newest");
        Assert.Equal(ConsoleLog.Capacity, log.Entries.Count);
        Assert.Equal("info", log.Entries[0].Message);
        Assert.Equal("newest", log.Entries[^1].Message);
        Assert.Equal(0, log.Count(ConsoleSeverity.Error));
        Assert.Equal(ConsoleLog.Capacity, log.Count(ConsoleSeverity.Info));
    }

    [Fact]
    public void FormatsAnEntryAsOneLineAndKeepsTheFirstLineForTheList()
    {
        var entry = new ConsoleEntry(ConsoleSeverity.Error, "Mods", "first\r\nsecond",
            new System.DateTime(2026, 10, 6, 12, 34, 56));
        Assert.Equal("[Error 12:34:56] Mods: first\r\nsecond", entry.ToString());
        Assert.Equal("first", entry.FirstLine);
    }

    [Fact]
    public void DuringAReloadTheLogAddsOnlyTheEngineLinesThatItDidNotAdd()
    {
        var log = new ConsoleLog();
        log.AddEngineLine("[Warning: 22:48:12] Ini: first", LogSeverity.Warning);
        log.AddEngineLine("[Warning: 22:48:12] Ini: first", LogSeverity.Warning);
        Assert.Equal(2, log.Entries.Count);

        log.SkipKnownEngineLines = true;
        log.AddEngineLine("[Warning: 22:49:00] Ini: first", LogSeverity.Warning);
        log.AddEngineLine("[Error: 22:49:00] Ini: first", LogSeverity.Error);
        log.AddEngineLine("[Warning: 22:49:00] Ini: second", LogSeverity.Warning);
        log.AddEngineLine("[Warning: 22:49:00] Ini: second", LogSeverity.Warning);

        Assert.Equal(["first", "first", "first", "second"], log.Entries.Select(e => e.Message));
        Assert.Equal(ConsoleSeverity.Error, log.Entries[2].Severity);
    }

    /// <summary>
    /// The skip of an action covers only the lines of the action. FLLog gives its lines to the UI thread later, so the
    /// lines that are on the way when the action starts, for example the lines of a system load, show as before.
    /// </summary>
    [Fact]
    public void TheSkipOfAnActionCoversOnlyTheLinesOfTheAction()
    {
        var log = new ConsoleLog();
        var ui = new QueuedUIThread();
        log.AddEngineLine("[Warning: 22:48:12] Ini: load", LogSeverity.Warning);
        log.AddEngineLine("[Warning: 22:48:12] Ini: model", LogSeverity.Warning);
        // A line of the load that is on the way to the UI thread.
        ui.QueueUIThread(() => log.AddEngineLine("[Warning: 22:49:00] Ini: load", LogSeverity.Warning));

        log.SkipKnownEngineLinesOf(() =>
        {
            ui.QueueUIThread(() => log.AddEngineLine("[Warning: 22:49:00] Ini: model", LogSeverity.Warning));
            ui.QueueUIThread(() => log.AddEngineLine("[Warning: 22:49:00] Ini: new", LogSeverity.Warning));
        }, ui, () => false);
        ui.RunQueued();
        log.AddEngineLine("[Warning: 22:50:00] Ini: model", LogSeverity.Warning);

        Assert.Equal(["load", "model", "load", "new", "model"], log.Entries.Select(e => e.Message));
        Assert.False(log.SkipKnownEngineLines);
    }

    [Fact]
    public void AGameLineIsAWarningOrAnErrorOfFLLog()
    {
        Assert.Equal((ConsoleSeverity.Warning, "Ini", "a: b"), ConsoleLog.ParseGameLine("[Warning: 22:48:12] Ini: a: b"));
        Assert.Equal((ConsoleSeverity.Error, "Game", "e"), ConsoleLog.ParseGameLine("[Error: 22.48.12] Game: e"));
        Assert.Null(ConsoleLog.ParseGameLine("[Info: 22:48:12] Game: info"));
        Assert.Null(ConsoleLog.ParseGameLine("[Debug: 22:48:12] Game: debug"));
        Assert.Null(ConsoleLog.ParseGameLine("[Warning: 22:48:12] no component"));
        Assert.Null(ConsoleLog.ParseGameLine("    Channels       = 2"));
    }

    /// <summary>
    /// Decision D57: the game of Play loads the same data as the editor. The Console adds a line of the game only when
    /// it did not add the same engine line before, also when the line of the game came first.
    /// </summary>
    [Fact]
    public void AGameLineShowsOnlyWhenTheConsoleDidNotAddTheSameEngineLine()
    {
        var log = new ConsoleLog();
        log.AddEngineLine("[Warning: 22:48:12] Ini: known", LogSeverity.Warning);

        log.AddGameLine(ConsoleLog.ParseGameLine("[Warning: 23:00:00] Ini: known")!.Value, "Play");
        log.AddGameLine(ConsoleLog.ParseGameLine("[Error: 23:00:00] Ini: known")!.Value, "Play");
        log.AddGameLine(ConsoleLog.ParseGameLine("[Warning: 23:00:00] Ship: new")!.Value, "Play");
        log.AddGameLine(ConsoleLog.ParseGameLine("[Warning: 23:00:01] Ship: new")!.Value, "Play");
        log.SkipKnownEngineLines = true;
        log.AddEngineLine("[Warning: 23:00:02] Ship: new", LogSeverity.Warning);

        Assert.Equal([("Ini", "known"), ("Play", "Ini: known"), ("Play", "Ship: new")],
            log.Entries.Select(e => (e.Source, e.Message)));
        Assert.Equal(ConsoleSeverity.Error, log.Entries[1].Severity);
    }
}

using System.Linq;
using LibreLancer;
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
}

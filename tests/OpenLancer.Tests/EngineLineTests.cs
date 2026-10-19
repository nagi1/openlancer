using LibreLancer;
using OpenLancer.Data;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The parser of FLLog lines and the key that compares two loads (section 3.2 of the console filter
/// design).</summary>
public sealed class EngineLineTests
{
    private const string File = @"EXE\..\data\universe\systems\x01\x01.ini";

    [Theory]
    [InlineData("Unknown entry k at section Zone: " + File + ", line 12", "Unknown entry k", "Zone", 12)]
    [InlineData("Unknown entry k in section Zone: " + File + " (line not available)", "Unknown entry k", "Zone", null)]
    [InlineData("Unknown section tumble at " + File + ", line 6", "Unknown section tumble", null, 6)]
    [InlineData("Unknown section tumble in " + File + " (line not available)", "Unknown section tumble", null, null)]
    [InlineData("Failed to parse float 'x' for size in section Zone: " + File + ":9",
        "Failed to parse float 'x' for size", "Zone", 9)]
    [InlineData("Entry without object 'k'  at section Zone: " + File + ", line 3", "Entry without object 'k'", "Zone", 3)]
    [InlineData("navmarker needs 7 entries, got 2  at section NNObjective: " + File + ", line 4",
        "navmarker needs 7 entries, got 2", "NNObjective", 4)]
    [InlineData("Could not add section [CollisionGroup] to parent at " + File + ", line 8",
        "Could not add section [CollisionGroup] to parent", "CollisionGroup", 8)]
    public void ReadsEachFormOfThePlace(string message, string core, string? section, int? line)
    {
        var parsed = EngineLine.Parse($"[Warning: 12:34:56] Ini: {message}", LogSeverity.Warning);

        Assert.Equal(("Ini", message, core, File, section, line),
            (parsed.Component, parsed.Message, parsed.Core, parsed.File, parsed.Section, parsed.Line));
    }

    /// <summary>A line of a component without a file has no place, also when its message has " in ".</summary>
    [Fact]
    public void ALineWithoutAFileHasNoPlace()
    {
        var parsed = EngineLine.Parse("[Error: 12:34:56] Ship: Unrecognised hp_type hp_x in ship_a", LogSeverity.Error);

        Assert.Equal(("Ship", "Unrecognised hp_type hp_x in ship_a", null, null, null),
            (parsed.Component, parsed.Core, parsed.File, parsed.Section, parsed.Line));
    }

    /// <summary>The key has no time and no line number, so two loads of the same data give the same key, also when a
    /// patch moves the lines of a file. The ":N" form of the float parse gives the key of the other forms.</summary>
    [Fact]
    public void TheKeyHasNoTimeAndNoLineNumber()
    {
        var first = EngineLine.Parse("[Warning: 12:00:00] Ini: Duplicate of k at section Object: " + File + ", line 5",
            LogSeverity.Warning);
        var second = EngineLine.Parse("[Warning: 13.30.00] Ini: Duplicate of k in section Object: " + File +
                                      " (line not available)", LogSeverity.Warning);
        var floatAt = EngineLine.Parse("[Error: 12:00:00] Ini: Failed to parse float '' for x in section Object: " + File +
                                       ":7", LogSeverity.Error);
        var floatIn = EngineLine.Parse("[Error: 12:00:00] Ini: Failed to parse float '' for x in section Object: " + File +
                                       " (line not available)", LogSeverity.Error);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(floatAt.Key, floatIn.Key);
        Assert.Equal(floatAt.Normalized, floatIn.Normalized);
        Assert.DoesNotContain("7", floatAt.Normalized.Replace(File, ""));
    }

    /// <summary>The key has the install path of the file: another form of the same path and another letter case give
    /// the same key.</summary>
    [Fact]
    public void TheKeyNormalizesThePath()
    {
        var a = EngineLine.Parse(@"[Warning: 12:00:00] Ini: Unknown entry k at section Zone: EXE\..\DATA\X.ini, line 1",
            LogSeverity.Warning);
        var b = EngineLine.Parse(@"[Warning: 12:00:00] Ini: Unknown entry k at section zone: data/x.INI, line 9",
            LogSeverity.Warning);

        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void TheKeyTellsTheSeverityTheComponentAndTheFileApart()
    {
        var line = EngineLine.Parse(@"[Warning: 12:00:00] Ini: Unknown entry k at section Zone: data\a.ini, line 1",
            LogSeverity.Warning);

        Assert.NotEqual(line.Key, EngineLine.Parse(@"[Error: 12:00:00] Ini: Unknown entry k at section Zone: data\a.ini, line 1",
            LogSeverity.Error).Key);
        Assert.NotEqual(line.Key, EngineLine.Parse(@"[Warning: 12:00:00] Ini: Unknown entry k at section Zone: data\b.ini, line 1",
            LogSeverity.Warning).Key);
        Assert.NotEqual(line.Key, EngineLine.Parse(@"[Warning: 12:00:00] Ini: Unknown entry k at section Ship: data\a.ini, line 1",
            LogSeverity.Warning).Key);
    }

    /// <summary>The normal form of a line is the form that the validation compares (see
    /// <see cref="EngineLogCapture"/>).</summary>
    [Fact]
    public void TheNormalFormHasTheSeverityTheComponentAndThePlaceWithoutTheLine()
    {
        Assert.Equal(@"Warning Ini: Unknown entry k in section Zone: data\a.ini",
            EngineLine.Parse(@"[Warning: 12:00:00] Ini: Unknown entry k at section Zone: data\a.ini, line 4",
                LogSeverity.Warning).Normalized);
        Assert.Equal(@"Warning Ini: Unknown section k in data\a.ini",
            EngineLine.Parse(@"[Warning: 12:00:00] Ini: Unknown section k at data\a.ini, line 4", LogSeverity.Warning)
                .Normalized);
        Assert.Equal("Error Ship: Unrecognised hp_type x in s",
            EngineLine.Parse("[Error: 12:00:00] Ship: Unrecognised hp_type x in s", LogSeverity.Error).Normalized);
    }

    /// <summary>A line in an unknown form keeps its text as the message. It has no component and no place.</summary>
    [Fact]
    public void ALineInAnUnknownFormKeepsItsText()
    {
        var parsed = EngineLine.Parse("something else", LogSeverity.Warning);

        Assert.Equal((null, "something else", null, EngineLine.NoComponent),
            (parsed.Component, parsed.Message, parsed.File, parsed.Source));
        Assert.Null(EngineLine.Parse("something else"));
        Assert.Null(EngineLine.Parse("[Trace: 12:00:00] Ini: x"));
    }

    [Fact]
    public void ALogTextLineGivesItsSeverity()
    {
        Assert.Equal(LogSeverity.Error, EngineLine.Parse("[Error: 12:00:00] Ini: x")!.Severity);
        Assert.Equal(LogSeverity.Info, EngineLine.Parse("[Info: 12:00:00] Game: x")!.Severity);
    }
}

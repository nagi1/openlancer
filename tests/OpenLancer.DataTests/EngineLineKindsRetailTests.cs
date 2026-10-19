using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Games;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The checks of a base run of a retail data set (test 8 of the console filter design): no load fails, each engine line
/// has a kind of the rules table, each kind is a data problem or an engine gap, and the count of each kind equals its
/// pin. The pins apply only to a data set that the detector knows and that has no changed INI file. Else the test
/// writes the counts to the test output and does not compare them.
/// </summary>
internal static class EngineLineKindsRetail
{
    public static void AssertKinds(BaseRun run, string folder, ITestOutputHelper output)
    {
        var lines = run.All.ToList();
        output.WriteLine($"Lines: {run.Load.Count} of the data load, {run.Systems.Count} of the systems, " +
                         $"{run.Missions.Count} of the missions.");
        foreach (var failure in run.Failures)
            output.WriteLine("failure: " + failure);
        var counts = lines.GroupBy(l => EngineLineRules.Match(l)?.Id ?? "-").ToDictionary(g => g.Key, g => g.Count());
        foreach (var (id, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
            output.WriteLine($"{id}: {count}");
        foreach (var line in lines.Where(l => EngineLineRules.Match(l) == null))
            output.WriteLine("no kind: " + line.Normalized);

        Assert.Empty(run.Failures);
        Assert.All(lines, l => Assert.NotNull(EngineLineRules.Match(l)));
        Assert.All(lines, l => Assert.Contains(EngineLineRules.Match(l)!.Class,
            new[] { EngineLineClass.DataProblem, EngineLineClass.EngineGap }));
        if (DataSetOf(folder) is not { } set)
        {
            output.WriteLine("The detector does not know the data set, so the test does not compare the pins.");
            return;
        }

        var expected = EngineLineRules.Kinds.Where(k => EngineLineRules.Pin(k, set) > 0)
            .ToDictionary(k => k.Id, k => EngineLineRules.Pin(k, set));
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal),
            counts.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    /// <summary>The data set of the pins of a folder, or null when the folder holds another game or changed INI
    /// files.</summary>
    public static EngineLineDataSet? DataSetOf(string folder)
    {
        var report = GameDetector.Detect(folder);
        if (report.Data != DataState.NotChanged)
            return null;
        return report.Build switch
        {
            "vanilla" or "vanilla-1.0" or "vanilla-1.1" => EngineLineDataSet.Vanilla,
            "hde-0.7.1" => EngineLineDataSet.HdEdition071,
            _ => null,
        };
    }
}

/// <summary>The kinds and the pins of the base run of HD Edition. The test needs <c>OPENLANCER_HDE</c>, a copy of an HD
/// Edition install. It only reads it, and it skips without it. <see cref="VanillaLoadTests"/> checks the retail
/// game.</summary>
public sealed class EngineLineKindsRetailTests(ITestOutputHelper output)
{
    static EngineLineKindsRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheHdEditionBaseRunGivesTheKnownKinds()
    {
        var folder = GameDataPaths.RequireHde();

        EngineLineKindsRetail.AssertKinds(BaseEngineLines.Run(folder), folder, output);
    }
}

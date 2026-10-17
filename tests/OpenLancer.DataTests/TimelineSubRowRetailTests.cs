using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The sub-rows of the Timeline on the retail cutscenes (decision D131, retail test 1 of the M8b design): in each of
/// the 1,506 files, no two bars of one sub-row overlap. The test writes the count of the rows with more than one sub-row
/// and the largest count, and the time of the packing for the largest retail script.
/// </summary>
public sealed class TimelineSubRowRetailTests(ITestOutputHelper output)
{
    /// <summary>The script with the most events of the retail data: 292 entities and 1,437 events.</summary>
    private const string S020x = @"DATA\SCRIPTS\STORY\s020x_nrml_VanPelt_li_09_Bar_01.thn";

    [Fact]
    public void NoTwoBarsOfOneSubRowOverlapInTheRetailCutscenes()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var (files, rows, withSubRows, largest) = (0, 0, 0, 1);
        var largestRow = "";
        var overlaps = new List<string>();
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            files++;
            var model = Model(view, file);
            foreach (var row in model.Groups.SelectMany(g => g.Rows))
            {
                rows++;
                if (row.SubRows > 1)
                    withSubRows++;
                if (row.SubRows > largest)
                    (largest, largestRow) = (row.SubRows, $"{file} {row.Name}");
                var bars = row.Events.Where(e => e.IsBar).ToList();
                overlaps.AddRange(bars.Where(a => bars.Any(b => !ReferenceEquals(a, b) && a.SubRow == b.SubRow &&
                                                                a.Time < b.End && b.Time < a.End))
                    .Select(a => $"{file} {row.Name} {a.Type} at {a.Time}"));
                Assert.All(row.Events.Where(e => !e.IsBar), e => Assert.Equal(0, e.SubRow));
                Assert.InRange(row.Events.Select(e => e.SubRow).DefaultIfEmpty().Max(), 0, row.SubRows - 1);
            }
        }

        output.WriteLine($"{files} files, {rows} rows: {withSubRows} rows with more than one sub-row. The largest " +
                         $"count is {largest} ({largestRow}).");
        foreach (var overlap in overlaps.Take(20))
            output.WriteLine($"Overlap: {overlap}");
        Assert.Equal(1506, files);
        Assert.Empty(overlaps);
        Assert.True(withSubRows > 0);
    }

    /// <summary>The packing of the largest retail script, with a model before it as after an edit, takes less than
    /// 5 ms. The test also writes the time of the whole build.</summary>
    [Fact]
    public void ThePackingOfTheLargestRetailScriptTakesLessThanFiveMilliseconds()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var working = ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(view.ReadAllBytes(S020x), S020x),
            ThnElementId.Counter());
        var symbols = ThornEvaluator.Instance.Symbols;
        var previous = TimelineModel.Build(S020x, working, symbols, ScriptPlay.Normal);
        var (packs, builds) = (new List<double>(), new List<double>());
        for (var i = 0; i < 20; i++)
        {
            var clock = Stopwatch.StartNew();
            previous = TimelineModel.Build(S020x, working, symbols, ScriptPlay.Normal, previous);
            builds.Add(clock.Elapsed.TotalMilliseconds);
            packs.Add(previous.PackTime.TotalMilliseconds);
        }

        double Median(List<double> times) => times.Order().ElementAt(times.Count / 2);
        output.WriteLine($"{S020x}: {working.Entities.Count} entities, {working.Events.Count} events. Packing: " +
                         $"median {Median(packs):0.000} ms, largest {packs.Max():0.000} ms. Whole build: median " +
                         $"{Median(builds):0.00} ms. {packs.Count} runs.");
        Assert.Equal((292, 1437), (working.Entities.Count, working.Events.Count));
        Assert.True(Median(packs) < 5, $"The packing took {Median(packs):0.000} ms.");
    }

    private static TimelineModel Model(FileSystem view, string file) =>
        TimelineModel.Build(file, ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(view.ReadAllBytes(file), file),
            ThnElementId.Counter()), ThornEvaluator.Instance.Symbols, ScriptPlay.Normal);
}

using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The key schema on the retail corpus (test 13.2 item 3 of the M8 design): each key of each entity and of the props of
/// each event of the 1,506 retail cutscenes has a row in <see cref="ThnSchema"/> for its type, and its value fits the
/// kind of the row.
/// </summary>
public sealed class ThnSchemaRetailTests(ITestOutputHelper output)
{
    static ThnSchemaRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EveryKeyOfTheCorpusIsInThnSchemaAndFitsTheKindOfItsRow()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var symbols = evaluator.Symbols;
        var missing = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var misfit = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<ThnKeyRow>();
        var (files, values, entities, events) = (0, 0, 0, 0);

        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            files++;
            var model = ThnScriptModel.From(evaluator.Evaluate(view.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var entity in model.Entities)
            {
                entities++;
                Walk(ThnElementKind.Entity, ThnSchema.TypeOf(ThnElementKind.Entity, entity.Table, symbols), entity.Table, "");
            }

            foreach (var ev in model.Events)
            {
                events++;
                if (ev.Table.List.Length > 3 && ev.Table.List[3] is ThnTable { IsRecord: true } props)
                    Walk(ThnElementKind.Event, ThnSchema.TypeOf(ThnElementKind.Event, ev.Table, symbols), props, "props");
            }
        }

        output.WriteLine($"{files} files, {entities} entities, {events} events, {values} keys checked, " +
                         $"{used.Count} of {ThnSchema.Rows.Length} rows used.");
        foreach (var (key, count) in missing)
            output.WriteLine($"Missing: {key} ({count})");
        foreach (var (key, count) in misfit)
            output.WriteLine($"Misfit: {key} ({count})");
        Assert.Equal(1506, files);
        Assert.Empty(missing);
        Assert.Empty(misfit);
        Assert.Equal(41_250, entities);
        Assert.Equal(50_785, events);

        void Walk(ThnElementKind kind, string? type, ThnTable record, string prefix)
        {
            foreach (var (key, value) in record.Record)
            {
                values++;
                var path = new ThnPath(prefix.Length == 0 ? key : $"{prefix}.{key}");
                var row = ThnSchema.Find(kind, type, path);
                if (row == null)
                {
                    Count(missing, $"{kind} {type} {path}");
                    continue;
                }

                used.Add(row);
                if (!ThnSchema.Fits(row, value, symbols))
                    Count(misfit, $"{kind} {type} {path} {row.Kind} {ThnWriter.Value(value)}");
                if (row.Kind is ThnKeyKind.Record or ThnKeyKind.Curve && value is ThnTable { IsRecord: true } child)
                    Walk(kind, type, child, path.Text);
            }
        }

        static void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    }
}

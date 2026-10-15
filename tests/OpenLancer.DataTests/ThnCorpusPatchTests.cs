using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The THN patch format on all retail cutscene scripts (design section 6.3), with the seeded random edits of the
/// prototype <c>m8\Delta.cs</c>:
/// <list type="number">
/// <item>Pass 1: about 10 random edits for each script. They change times, props, targets, positions and the duration,
/// remove keys, and delete and add events and entities. Make the patch, write it, evaluate the text, apply it to the
/// lower script, and compare with the working script exactly. Then write the merged script, evaluate it again, and
/// compare again.</item>
/// <item>Pass 2: the same, but the edits go first to the elements whose key repeats.</item>
/// <item>Stacked: a second patch, made on the merged script, applies after the first one.</item>
/// <item>Conflicts: a patch made against the lower script changes an event that the first patch retimed or deleted.
/// The merge must stop it with an error.</item>
/// </list>
/// </summary>
public sealed class ThnCorpusPatchTests(ITestOutputHelper output)
{
    private const int RetailScripts = 1506;

    static ThnCorpusPatchTests() => AppHandler.ConsoleInit(silent: true);

    private static IReadOnlyDictionary<string, ThnSymbolInfo> Symbols => ThornEvaluator.Instance.Symbols;

    [Fact]
    public void RandomEditSetsStackedPatchesAndConflictsGiveExactScriptsAndErrors()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var files = Directory.EnumerateFiles(Path.Combine(baseFolder, "DATA"), "*.thn", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var ids = ThnElementId.Counter();
        var random = new Random(1234);
        var counts = new Counts();
        var problems = new List<string>();

        foreach (var file in files)
        {
            var name = Path.GetRelativePath(baseFolder, file);
            var lower = ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(File.ReadAllBytes(file), name), ids);
            for (var pass = 0; pass < 2; pass++)
            {
                try
                {
                    Run(name, lower, pass, random, ids, counts, problems);
                }
                catch (Exception e) when (e is ArgumentException or ThnPatchException or ThnEvaluationException)
                {
                    problems.Add($"{name}, pass {pass + 1}: {e.Message}");
                }
            }
        }

        output.WriteLine($"Scripts: {files.Count}. Pass 1 exact: {counts.Exact[0]}. Pass 2 exact: {counts.Exact[1]}. " +
                         $"Stacked exact: {counts.Stacked}. Conflict cases: {counts.Conflicts}, errors: {counts.ConflictErrors}.");
        output.WriteLine($"Pass 1 patches: {counts.PatchBytes} bytes, whole files: {counts.FileBytes} bytes " +
                         $"({100.0 * counts.PatchBytes / counts.FileBytes:F1}%). Largest patch: {counts.LargestPatch} bytes.");
        Assert.True(problems.Count == 0,
            $"{problems.Count} problems:{Environment.NewLine}{string.Join(Environment.NewLine, problems.Take(20))}");
        Assert.Equal(RetailScripts, files.Count);
        Assert.Equal((RetailScripts, RetailScripts, 2 * RetailScripts), (counts.Exact[0], counts.Exact[1], counts.Stacked));
        // The seeded edits are the edits of the prototype, so they give its 2,728 conflict cases. All are errors.
        Assert.Equal((2728, 2728), (counts.Conflicts, counts.ConflictErrors));
    }

    private static void Run(string name, ThnScriptModel lower, int pass, Random random, Func<ThnElementId> ids, Counts counts,
        List<string> problems)
    {
        var working = Edit(lower, random, hitDuplicates: pass == 1, ids);
        var text = ThnPatch.Make(lower, working, Symbols).Write();
        var patch = ThnPatch.Read(Encoding.ASCII.GetBytes(text), name, ThornEvaluator.Instance);
        var merged = Applied(patch.Apply(lower, Symbols, ids), name, problems);
        if (merged == null)
            return;
        var again = ThornEvaluator.Instance.Evaluate(Encoding.ASCII.GetBytes(ThnWriter.Write(merged.ToGlobals())), name);
        if ((Difference(working.ToGlobals(), merged.ToGlobals()) ?? Difference(working.ToGlobals(), again)) is { } difference)
            problems.Add($"{name}, pass {pass + 1}: the merged script differs at {difference}.");
        else
            counts.Exact[pass]++;
        if (pass == 0)
        {
            counts.PatchBytes += text.Length;
            counts.FileBytes += ThnWriter.Write(lower.ToGlobals()).Length;
            counts.LargestPatch = Math.Max(counts.LargestPatch, text.Length);
        }

        // Stacked: a second mod edits the merged script.
        var lower2 = ThnScriptModel.From(merged.ToGlobals(), ids);
        var working2 = Edit(lower2, random, hitDuplicates: false, ids);
        var patch2 = ThnPatch.Read(Encoding.ASCII.GetBytes(ThnPatch.Make(lower2, working2, Symbols).Write()), name,
            ThornEvaluator.Instance);
        if (Applied(patch.Apply(lower, Symbols, ids), name, problems) is { } first &&
            Applied(patch2.Apply(first, Symbols, ids), name, problems) is { } stacked)
        {
            if (Difference(working2.ToGlobals(), stacked.ToGlobals()) is { } stackedDifference)
                problems.Add($"{name}, pass {pass + 1}: the stacked script differs at {stackedDifference}.");
            else
                counts.Stacked++;
        }

        // A conflict: a third mod, made against the lower script, changes an event that the first mod retimed or
        // deleted.
        var touched = Enumerable.Range(0, lower.Events.Count).FirstOrDefault(i =>
            working.Events.FirstOrDefault(e => e.Origin == i) is not { } kept ||
            !kept.Table.List[0].Equals(lower.Events[i].Table.List[0]), -1);
        if (touched < 0)
            return;
        counts.Conflicts++;
        var conflicting = lower with
        {
            Events = lower.Events.SetItem(touched, lower.Events[touched] with
            {
                Table = SetProps(lower.Events[touched].Table, ["ol_conflict"], new ThnNumber(1)),
            }),
        };
        var patch3 = ThnPatch.Read(Encoding.ASCII.GetBytes(ThnPatch.Make(lower, conflicting, Symbols).Write()), name,
            ThornEvaluator.Instance);
        var conflict = patch3.Apply(merged, Symbols, ids);
        if (conflict.Model == null && conflict.Errors.Count > 0)
            counts.ConflictErrors++;
        else
            problems.Add($"{name}, pass {pass + 1}: the conflict on event {touched + 1} applied.");
    }

    private static ThnScriptModel? Applied(ThnPatchResult result, string name, List<string> problems)
    {
        if (result.Model == null)
            problems.Add($"{name}: {string.Join(" ", result.Errors)}");
        return result.Model;
    }

    private static string? Difference(ThnTable expected, ThnTable actual) =>
        ThnValues.FirstDifference(expected, actual) is { } path ? (path.Length == 0 ? "the globals" : path) : null;

    /// <summary>The random edits of the prototype, with the same use of the random numbers. An edit replaces values, so
    /// the elements keep their origins, and new elements go at the end.</summary>
    private static ThnScriptModel Edit(ThnScriptModel lower, Random random, bool hitDuplicates, Func<ThnElementId> ids)
    {
        var duration = lower.Duration;
        var entities = lower.Entities.ToList();
        var events = lower.Events.ToList();
        if (random.Next(4) == 0)
            duration += 1;
        var eventKeys = ThnKeys.Of(ThnElementKind.Event, lower.Events.Select(e => e.Table), Symbols);
        var entityKeys = ThnKeys.Of(ThnElementKind.Entity, lower.Entities.Select(e => e.Table), Symbols);

        // Picks n indices at random. In pass 2, the indices of repeated keys come first.
        IEnumerable<int> Pick(int n, int max, Func<int, bool> prefer) =>
            Enumerable.Range(0, max).OrderBy(i => (hitDuplicates && prefer(i) ? 0 : 1) * 1000000 + random.Next(1000000)).Take(n);
        bool RepeatedEvent(int i) => eventKeys[i] is { Count: > 1 };
        bool RepeatedEntity(int i) => entityKeys[i] is { Count: > 1 };

        if (events.Count > 0)
        {
            foreach (var i in Pick(4, events.Count, RepeatedEvent).ToList())
            {
                var ev = events[i].Table;
                var props = ev.List.Length > 3 ? (ThnTable)ev.List[3] : null;
                switch (random.Next(5))
                {
                    case 0:
                        ev = WithItem(ev, 0, Bump(ev.List[0], random));
                        break;
                    case 1 when props is { Record.Length: > 0 }:
                        var j = random.Next(props.Record.Length);
                        ev = WithItem(ev, 3, new ThnTable(props.List,
                            props.Record.SetItem(j, new ThnField(props.Record[j].Key, Bump(props.Record[j].Value, random)))));
                        break;
                    case 2 when props is { Record.Length: > 0 }:
                        ev = WithItem(ev, 3, new ThnTable(props.List, props.Record.RemoveAt(random.Next(props.Record.Length))));
                        break;
                    case 3:
                        ev = SetProps(ev, ["ol_test", "inner"], new ThnNumber(random.Next(100)));
                        break;
                    default:
                        var targets = (ThnTable)ev.List[2];
                        ev = WithItem(ev, 2, new ThnTable(targets.List.Add(new ThnText("Extra_Target")), targets.Record));
                        break;
                }
                events[i] = events[i] with { Table = ev };
            }
            events.RemoveAt(Pick(1, events.Count, RepeatedEvent).First());
            for (var k = 0; k < 2 && events.Count > 0; k++)
            {
                var source = events[random.Next(events.Count)].Table;
                events.Add(new ThnElement(ids(), -1, WithItem(source, 0, Bump(source.List[0], random))));
            }
        }
        if (entities.Count > 0)
        {
            foreach (var i in Pick(3, entities.Count, RepeatedEntity).ToList())
            {
                var entity = entities[i].Table;
                switch (random.Next(3))
                {
                    case 0:
                        entity = SetIn(entity, ["spatialprops", "pos"],
                            ThnTable.FromList(new ThnNumber(1), new ThnNumber(2), new ThnNumber(random.Next(9))));
                        break;
                    case 1:
                        entity = SetIn(entity, ["userprops", "ol_note"], new ThnText("edited"));
                        break;
                    default:
                        if (entity.Record.Length > 2)
                            entity = new ThnTable(entity.List, entity.Record.RemoveAt(entity.Record.Length - 1));
                        break;
                }
                entities[i] = entities[i] with { Table = entity };
            }
            if (entities.Count > 1)
                entities.RemoveAt(Pick(1, entities.Count, RepeatedEntity).First());
            var source = entities[random.Next(entities.Count)].Table;
            entities.Add(new ThnElement(ids(), -1, SetIn(source, ["entity_name"], new ThnText("OL_New_" + random.Next(1000)))));
        }
        return lower with { Duration = duration, Entities = [.. entities], Events = [.. events] };
    }

    private static ThnValue Bump(ThnValue value, Random random) => value switch
    {
        ThnNumber number => new ThnNumber(number.Value + 0.5f + random.Next(3)),
        ThnText text => new ThnText(text.Value + "_x"),
        ThnBool flag => new ThnBool(!flag.Value),
        ThnTable { List: [ThnNumber, ..] } table => new ThnTable(table.List.SetItem(0, Bump(table.List[0], random)), table.Record),
        _ => value,
    };

    private static ThnTable WithItem(ThnTable list, int index, ThnValue value) =>
        new(index < list.List.Length ? list.List.SetItem(index, value) : list.List.Add(value), list.Record);

    /// <summary>Sets a path in the props of an event, and makes the props when the event has none.</summary>
    private static ThnTable SetProps(ThnTable ev, string[] keys, ThnValue value) =>
        WithItem(ev, 3, SetIn(ev.List.Length > 3 ? (ThnTable)ev.List[3] : ThnTable.Empty, keys, value));

    /// <summary>Sets a path in a record, and makes each missing record. A new key goes at the end of its record.</summary>
    private static ThnTable SetIn(ThnTable record, string[] keys, ThnValue value)
    {
        var index = record.Record.Select(f => f.Key).ToList().IndexOf(keys[0]);
        if (keys.Length > 1)
            value = SetIn(index >= 0 ? (ThnTable)record.Record[index].Value : ThnTable.Empty, keys[1..], value);
        var field = new ThnField(keys[0], value);
        return new ThnTable(record.List, index >= 0 ? record.Record.SetItem(index, field) : record.Record.Add(field));
    }

    private sealed class Counts
    {
        public readonly int[] Exact = new int[2];
        public int Stacked;
        public int Conflicts;
        public int ConflictErrors;
        public long PatchBytes;
        public long FileBytes;
        public int LargestPatch;
    }
}

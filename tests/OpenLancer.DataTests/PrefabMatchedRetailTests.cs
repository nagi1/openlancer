using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the write rules of the matched prefab instances (sections 5.8 and 11.2 of the M6b design): for each
/// matched instance of the vanilla scripts, a write of the value that a field reads changes nothing, and a field write
/// and its inverse give the same W. The tests read the scripts at run time and hold no text of them.
/// </summary>
public sealed class PrefabMatchedRetailTests(ITestOutputHelper output)
{
    /// <summary>The instances of one prefab in one script that the test writes. The others repeat the same rules.</summary>
    private const int InstancesOfAPrefab = 3;

    [Fact]
    public void AFieldWriteAndItsInverseGiveTheSameW()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bmatched");
        var session = workspace.Open();
        session.Missions.Coverage = EngineCoverage.Instance;
        var missions = session.Missions;
        var watch = Stopwatch.StartNew();
        var writes = 0;
        var same = 0;
        var skipped = 0;
        foreach (var script in MissionSchema.Retail.StorySlots.Select(s => session.DataPath(s.Path)!)
                     .Where(p => session.OpenDocument(p) is { HoldsScript: true }))
        {
            var instances = missions.Model(script).Prefabs!.Instances.Where(i => i.Linked == null)
                .GroupBy(i => i.Definition.Id).SelectMany(g => g.Take(InstancesOfAPrefab)).Select(i => i.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var id in instances)
            {
                var reference = new InstanceRef(script, id);
                foreach (var field in Instance(missions, reference).Definition.Fields.Where(f => f.Write != PrefabWrite.None))
                {
                    var instance = Instance(missions, reference);
                    if (Rows(instance, field) is not { } rows)
                    {
                        skipped++;
                        continue;
                    }
                    var before = State(session, script);
                    var unchanged = missions.Apply(new SetPrefabField(reference, field.Id, rows));
                    // A field of several triggers, and a section that a patch cannot find (decision D20), refuse.
                    if (unchanged.Status == EditStatus.Refused && unchanged.Errors[0].Detail is MissionErrors.FieldNotWritable or
                            MissionErrors.RepeatedNickname or MissionErrors.ReadOnlySection)
                    {
                        skipped++;
                        continue;
                    }
                    Assert.True(unchanged.Status == EditStatus.NoChange,
                        $"{script} {id} {field.Id}: {unchanged.Status} {string.Join(" ", unchanged.Errors.Select(e => e.Message))}");
                    same++;
                    if (Changed(instance, field, rows) is not { } changed)
                        continue;
                    var result = missions.Apply(new SetPrefabField(reference, field.Id, changed));
                    if (result.Status == EditStatus.Refused)
                    {
                        // A value that the checks of the line refuse, for example a retail word in a number field.
                        Assert.Equal(EditErrorKind.InvalidValue, result.Errors[0].Kind);
                        continue;
                    }
                    Assert.True(result.Status == EditStatus.Applied, $"{script} {id} {field.Id}: {result.Status}");
                    var back = missions.Apply(result.Inverse!);
                    Assert.True(back.Status == EditStatus.Applied,
                        $"{script} {id} {field.Id}: {string.Join(" ", back.Errors.Select(e => e.Message))}");
                    Assert.Equal(before, State(session, script));
                    writes++;
                }
            }
        }
        output.WriteLine($"{same} writes of the same value, {writes} writes and inverses, {skipped} fields skipped: " +
                         $"{watch.ElapsedMilliseconds} ms");
        Assert.True(writes > 100, $"Only {writes} fields changed.");
    }

    /// <summary>"Save as prefab" of the first instance of each prefab in each vanilla script keeps the lines of the
    /// members: the compile of the new asset gives the lines of W, so the linked instance has no hand edit (rule
    /// P01).</summary>
    [Fact]
    public void SaveAsPrefabKeepsTheLinesOfEachMatchedInstance()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6bsaved");
        var session = workspace.Open();
        var missions = session.Missions;
        var saved = 0;
        foreach (var script in MissionSchema.Retail.StorySlots.Select(s => session.DataPath(s.Path)!)
                     .Where(p => session.OpenDocument(p) is { HoldsScript: true }))
        {
            var ids = missions.Model(script).Prefabs!.Instances.Where(i => i.Linked == null).GroupBy(i => i.Definition.Id)
                .Select(g => g.First().Id).ToList();
            foreach (var id in ids)
            {
                var before = State(session, script);
                var result = missions.Apply(new CreatePrefabAsset($"ol_saved_{saved}") { Source = new InstanceRef(script, id) });
                if (result.Status == EditStatus.Refused && result.Errors[0].Detail is MissionErrors.RepeatedNickname or
                        MissionErrors.ReadOnlySection or MissionErrors.PrefabProblems)
                    continue;
                Assert.True(result.Status == EditStatus.Applied,
                    $"{script} {id}: {string.Join(" ", result.Errors.Select(e => e.Message))}");
                Assert.Equal(before, State(session, script));
                Assert.DoesNotContain(missions.Model(script).Problems, p => p.Rule is "P01" or "P02" or "P03");
                session.Undo();
                saved++;
            }
        }
        output.WriteLine($"{saved} instances saved as prefabs.");
        Assert.True(saved > 50, $"Only {saved} instances saved.");
    }

    private static MissionPrefabInstance Instance(MissionEditor missions, InstanceRef reference) =>
        // Two sections with one nickname give two instances with one id. The reference names the first, as the layer
        // finds it.
        missions.Model(reference.Script).Prefabs!.Instances.First(i => i.Linked == null && i.Id == reference.Id);

    /// <summary>The rows that a field reads now, in the text form of a write, or null when the instance has no value
    /// for the field.</summary>
    private static IReadOnlyList<string>? Rows(MissionPrefabInstance instance, PrefabField field)
    {
        var from = field.From;
        var schema = MissionSchema.Retail;
        static string Line(MissionLine line) => $"{line.Key} = {string.Join(", ", MissionValues.TextsOf(line.Line))}";
        switch (field.Write)
        {
            case PrefabWrite.Condition:
                var conditions = instance.Role(from.Role).Select(t => t.Condition).ToList();
                return conditions.Count == 0 || conditions.Any(c => c == null) ? null : conditions.Select(c => Line(c!)).ToList();
            case PrefabWrite.Value when field.List == null:
            {
                var line = instance.Role(from.Role).SelectMany(t => t.Actions)
                    .FirstOrDefault(a => from.Actions.Any(k => MissionIndex.Is(a.Key, k)));
                if (line?.Name?.ValueAt(from.Value) is not { } spec || spec.Index + spec.Width > line.Count)
                    return null;
                return [string.Join(", ", MissionValues.TextsOf(line.Line).Skip(spec.Index).Take(spec.Width))];
            }
            case PrefabWrite.Key:
            {
                var entry = instance.CoreSection?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, from.Key!));
                return entry == null || entry.Count <= from.Value ? null : [MissionValues.TextsOf(entry)[from.Value]];
            }
            default:
            {
                bool Matches(string key) => from.Keys.Count > 0 ? from.Keys.Any(k => MissionIndex.Is(key, k))
                    : from.AllActions ? TriggerRules.IsAction(key)
                    : from.Group is { } group ? schema.Name(key) is { Kind: MissionNameKind.Action } name && name.Group == group
                    : field.LineKeys(schema).Any(k => MissionIndex.Is(key, k));
                if (from.Keys.Count > 0)
                    return instance.CoreSection is { } order
                        ? order.Section.Where(e => Matches(e.Name))
                            .Select(e => $"{e.Name} = {string.Join(", ", MissionValues.TextsOf(e))}").ToList()
                        : null;
                return instance.Role(from.Role) is [var trigger] ? trigger.Actions.Where(a => Matches(a.Key)).Select(Line).ToList() : null;
            }
        }
    }

    /// <summary>A changed value of a field: the condition "At once" or a delay, a number plus one, or a list without its
    /// last row.
    /// Null when the test has no change for the field.</summary>
    private static IReadOnlyList<string>? Changed(MissionPrefabInstance instance, PrefabField field, IReadOnlyList<string> rows)
    {
        switch (field.Write)
        {
            case PrefabWrite.Condition:
                return rows.Select(r => r.StartsWith("Cnd_True", StringComparison.OrdinalIgnoreCase) ? "Cnd_Timer = 1"
                    : "Cnd_True = no_params").ToList();
            case PrefabWrite.Value when field.List == null:
            case PrefabWrite.Key:
                return double.TryParse(rows[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                       number == Math.Floor(number) && Math.Abs(number) < 1e9
                    ? [(number + 1).ToString(CultureInfo.InvariantCulture)]
                    : null;
            default:
                return rows.Count > 0 ? rows.Take(rows.Count - 1).ToList() : null;
        }
    }

    private static string State(EditSession session, string script) =>
        string.Join("\n", session.OpenDocument(script)!.Working.Select(s => $"[{s.Name}]\n" + string.Join("\n",
            s.Select(e => e.Name.ToLowerInvariant() + " = " + string.Join(", ", MissionValues.TextsOf(e))))));
}

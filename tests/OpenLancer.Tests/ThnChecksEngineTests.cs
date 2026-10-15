using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The tests that run many scripts through the native compiler, one after the other. The compiler lets one thread at a
/// time compile (ThornCompiler.cs:30-48), so the collection runs alone. Else the other tests wait on the lock, and a
/// test with a time limit, for example of <c>PlayViewTests</c>, can fail.
/// </summary>
[CollectionDefinition(nameof(CompilerCollection), DisableParallelization = true)]
public sealed class CompilerCollection;

/// <summary>
/// The read checks of <see cref="ThnChecks"/> against the engine: for each key of <see cref="ThnSchema"/> and many
/// values of each kind, the checks find an error exactly when <see cref="ThnScript"/> throws while it reads the script.
/// The script has a MARKER and events whose targets name no entity, so no check of the build or of the targets
/// applies.
/// </summary>
[Collection(nameof(CompilerCollection))]
public sealed class ThnChecksEngineTests(ITestOutputHelper output)
{
    private const string Path = "differential.thn";

    private const string Curve = "FreeFormPCurve";

    /// <summary>Values of each kind, and values that are close to a kind.</summary>
    private static readonly ThnValue[] Values =
    [
        N(1), N(-2.5f), T("1.5"), T("abc"), T(""), new ThnBool(true), T("Y"), S("CAMERA"), T("CAMERA"), S("L_POINT"),
        S("X_AXIS"), T("X_AXIS"), S("LOOP"), S("POSITION"), S("REFERENCE"), S("HARDPOINT"), S("F_EXP"), ThnTable.Empty,
        List(N(1), N(2), N(3)), List(N(1), N(2)), List(N(1), N(2), N(3), N(4)), List(T("a"), T("b"), T("c")),
        List(List(N(1), N(0), N(0)), List(N(0), N(1), N(0)), List(N(0), N(0), N(1))), List(List(N(1), N(0)), List(N(0), N(1))),
        Record(F("a", N(1))), List(N(90), S("Y_AXIS")), List(N(90)), List(N(90), T("abc")), List(T("90")),
        T("OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}"), T("CLOSED, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}"),
        T("OPEN, {0,0,0}, {1,1,1}"), T("1, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}"), T("OPEN, {0,0,0}, {1,0,0,0}"),
        T("OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}"), T("OPEN, {0,0,0}, {1,0,0}, {1,1,1}, {1,0,0,0}"),
        Record(F("CLSID", T(Curve)), F("points", List(List(N(0), N(0), N(0), N(0)), List(N(1), N(1), N(0), N(0))))),
        Record(F("CLSID", N(5)), F("points", ThnTable.Empty)), Record(F("points", ThnTable.Empty)), Record(F("CLSID", T("x"))),
        Record(F("CLSID", T("x")), F("points", List(List(N(1), N(2), N(3))))), List(List(N(0), N(0), N(0), N(0))),
        List(N(1)),
    ];

    [Fact]
    public void TheChecksFindAnErrorExactlyWhenTheEngineCannotReadTheScript()
    {
        var (scripts, refused) = (0, 0);
        var mismatches = new List<string>();
        foreach (var (name, globals) in Scripts())
        {
            scripts++;
            var text = ThnWriter.Write(globals);
            string? engine = null;
            try
            {
                _ = new ThnScript(Encoding.ASCII.GetBytes(text), include => throw new FileNotFoundException(include), Path);
            }
            catch (Exception e) when (e is not FileNotFoundException)
            {
                engine = $"{e.GetType().Name}: {e.Message}";
                refused++;
            }
            var errors = ThnChecks.Errors(Path, Evaluate(text), Evaluator);
            if ((engine != null) != (errors.Count > 0))
                mismatches.Add($"{name}: the engine {engine ?? "reads it"}, the checks {(errors.Count > 0 ? errors[0].Message : "find no error")}");
        }

        output.WriteLine($"{scripts} scripts, {refused} that the engine cannot read, {mismatches.Count} mismatches.");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(40)));
        Assert.True(scripts > 5000 && refused > 1000, $"{scripts} scripts, {refused} refused");
    }

    /// <summary>
    /// The flags of an entity as the engine reads them (ThnScript.cs:293-302), for each THN name and some other values,
    /// on a LIGHT and on a SOUND: <see cref="ThnFlags.CanRead"/> is true exactly when the engine reads the value, and
    /// <see cref="ThnFlags.IsReference"/> and <see cref="ThnFlags.On"/> give the flags of the engine. The cast takes a
    /// name of each group with the integer type of the flags, so <c>CAMERA</c> gives REFERENCE and LIT_DYNAMIC. The
    /// REFERENCE exemption of the checks reads the flags with <see cref="ThnFlags.IsReference"/>.
    /// </summary>
    [Fact]
    public void TheFlagsOfAnEntityAreTheFlagsOfTheEngine()
    {
        var symbols = Evaluator.Symbols;
        string[] values = [.. symbols.Keys.Order(StringComparer.Ordinal), "0", "1", "2", "3", "6", "1048577", "Y", "\"REFERENCE\""];
        var (read, refused, references) = (0, 0, 0);
        foreach (var type in new[] { "LIGHT", "SOUND" })
        {
            var sound = type == "SOUND";
            foreach (var value in values)
            {
                var text = $"duration = 1\nentities = {{ {{ entity_name = \"E\", type = {type}, flags = {value} }} }}\nevents = {{}}\n";
                var entity = (ThnTable)((ThnTable)Evaluate(text)[ThnScriptModel.EntitiesGlobal]!).List[0];
                var stored = entity[ThnFlags.Key]!;
                ThnObjectFlags flags;
                try
                {
                    flags = new ThnScript(Encoding.ASCII.GetBytes(text), include => throw new FileNotFoundException(include), Path)
                        .Entities["E"].ObjectFlags;
                }
                catch (InvalidCastException)
                {
                    Assert.False(ThnFlags.CanRead(stored, symbols), $"{type}, flags = {value}: the engine cannot read it.");
                    refused++;
                    continue;
                }
                var name = $"{type}, flags = {value}: the engine reads {flags}.";
                Assert.True(ThnFlags.CanRead(stored, symbols), name);
                Assert.True(((flags & ThnObjectFlags.Reference) != 0) == ThnFlags.IsReference(entity, symbols), name);
                Assert.True(ThnFlags.Shown(sound).Where(n => (flags & (ThnObjectFlags)ThnScript.ThnEnv[n]) != 0).Order()
                    .SequenceEqual(ThnFlags.On(stored, sound, symbols).Order()), name);
                read++;
                references += (flags & ThnObjectFlags.Reference) != 0 ? 1 : 0;
            }
        }

        output.WriteLine($"{read} values that the engine reads, {references} of them with REFERENCE, {refused} that it refuses.");
        Assert.True(read > 100 && references > 40 && refused > 4, $"{read} read, {references} with REFERENCE, {refused} refused");
    }

    /// <summary>The scripts of the comparison, each with a name for a message.</summary>
    private static IEnumerable<(string Name, ThnTable Globals)> Scripts()
    {
        var entity = Record(F("entity_name", T("E")), F("type", S("MARKER")),
            F("spatialprops", Record(F("pos", List(N(0), N(0), N(0))))));
        foreach (var row in ThnSchema.Rows.Where(r => r.Element == ThnElementKind.Entity).DistinctBy(r => r.Path))
        {
            // The light type and the text of a display text are there, so that a value of another key is the only change.
            var start = row.Path.Text.StartsWith("lightprops.", StringComparison.Ordinal)
                ? Set(ThnElementKind.Entity, entity, "lightprops.type", S("L_POINT"))
                : row.Path.Text == "userprops.TextStart"
                    ? Set(ThnElementKind.Entity, entity, "userprops.TextString", N(1))
                    : entity;
            foreach (var (value, index) in Values.Select((v, i) => (v, i)))
                yield return ($"entity {row.Path} = value {index} {ThnWriter.Value(value)}", Globals([Set(ThnElementKind.Entity, start, row.Path.Text, value)], []));
            if (row.Required)
                yield return ($"entity without {row.Path}", Globals([Remove(ThnElementKind.Entity, start, row.Path.Text)], []));
        }

        foreach (var row in ThnSchema.Rows.Where(r => r.Element == ThnElementKind.Event))
        {
            foreach (var type in row.Types ?? ["START_MOTION", "SET_CAMERA"])
            {
                var props = row.Path.Text.StartsWith("props.param_curve.", StringComparison.Ordinal) || row.Path.Text == "props.pcurve_period"
                    ? Record(F("param_curve", Record(F("CLSID", T(Curve)), F("points", ThnTable.Empty))))
                    : ThnTable.Empty;
                var ev = List(N(0), S(type), List(T("X"), T("Y")), props);
                foreach (var (value, index) in Values.Select((v, i) => (v, i)))
                    yield return ($"{type} {row.Path} = value {index} {ThnWriter.Value(value)}", Globals([], [Set(ThnElementKind.Event, ev, row.Path.Text, value)]));
                if (row.Required)
                    yield return ($"{type} without {row.Path}", Globals([], [Remove(ThnElementKind.Event, ev, row.Path.Text)]));
            }
        }

        // The slots of an event, and the globals. The event has two targets, and START_SPATIAL_PROP_ANIM reads no
        // target, so no check of the targets applies.
        ThnValue[] slots = [N(0), T("0"), N(13), N(99), N(0.5f), N(1), S("START_PSYS"), T("START_PSYS"), T("CAMERA"), S("CAMERA"),
            new ThnBool(false), List(T("X")), List(), List(N(1)), Record(F("a", T("X"))), T("X"), Record(), N(2), N(-1),
            T("F_EXP")];
        foreach (var slot in Enumerable.Range(0, 4))
        {
            foreach (var value in slots)
            {
                var items = new List<ThnValue> { N(1), S("START_SPATIAL_PROP_ANIM"), List(T("X"), T("Y")), Record() };
                items[slot] = value;
                yield return ($"event slot {slot} = {ThnWriter.Value(value)}", Globals([], [List([.. items])]));
                yield return ($"event of {slot + 1} items", Globals([], [List([.. items.Take(slot + 1)])]));
            }
        }
        foreach (var value in Values)
        {
            yield return ($"duration = {ThnWriter.Value(value)}", Record(F("duration", value), F("entities", List()), F("events", List())));
            yield return ($"entities = {ThnWriter.Value(value)}", Record(F("duration", N(1)), F("entities", value), F("events", List())));
            yield return ($"events = {ThnWriter.Value(value)}", Record(F("duration", N(1)), F("entities", List()), F("events", value)));
        }
        yield return ("entities in the record part", Record(F("duration", N(1)), F("entities", Record(F("odd", entity))), F("events", List())));
    }

    private static ThnTable Globals(ThnTable[] entities, ThnTable[] events) =>
        Record(F("duration", N(10)), F("entities", List([.. entities])), F("events", List([.. events])));

    private static ThnTable Set(ThnElementKind kind, ThnTable element, string path, ThnValue value) =>
        ThnElementPaths.Set(kind, element, new ThnPath(path), value);

    private static ThnTable Remove(ThnElementKind kind, ThnTable element, string path) =>
        ThnElementPaths.Remove(kind, element, new ThnPath(path)) ?? element;
}

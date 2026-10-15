using System.Linq;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>The keys that a THN patch uses to find an element: the entity name without regard to letter case, and the
/// time, type and targets of an event, with nth and count when a key repeats.</summary>
public sealed class ThnKeysTests
{
    /// <summary>An entity without a text name has the empty name as its key, so a patch can still name it after a lower
    /// patch removed the name.</summary>
    [Fact]
    public void RepeatedNamesGetNthAndCountWithoutRegardToLetterCase()
    {
        var entities = ((ThnTable)Evaluate("""
            entities = {
                { entity_name = "Mark" },
                { entity_name = "Cam" },
                { entity_name = "MARK" },
                { type = MARKER },
                { entity_name = "mark" },
                { entity_name = "" }
            }
            """)["entities"]!).List.Cast<ThnTable>().ToList();

        var keys = ThnKeys.Of(ThnElementKind.Entity, entities, Evaluator.Symbols);

        Assert.Equal([(1, 3), (1, 1), (2, 3), (1, 2), (3, 3), (2, 2)], keys.Select(k => (k!.Value.Nth, k.Value.Count)));
        Assert.Equal(keys[0]!.Value.Signature, keys[4]!.Value.Signature);
        Assert.NotEqual(keys[0]!.Value.Signature, keys[1]!.Value.Signature);
        Assert.Equal(T(""), ThnKeys.Match(ThnElementKind.Entity, entities[3]));
    }

    /// <summary>The type compares by its number (ThnTypes.cs:41-78), so SET_CAMERA and its number are one key. The time
    /// compares by its float bits, and the targets compare by their characters and their order.</summary>
    [Fact]
    public void AnEventKeyIsTheTimeTheTypeAsANumberAndTheTargets()
    {
        var setCamera = Evaluator.Symbols["SET_CAMERA"].Value;
        var events = ((ThnTable)Evaluate($$"""
            events = {
                { 5, SET_CAMERA, { "Monitor", "Cam" } },
                { 5, {{setCamera}}, { "Monitor", "Cam" }, { duration = 1 } },
                { 5, SET_CAMERA, { "Cam", "Monitor" } },
                { 5, SET_CAMERA, { "Monitor", "cam" } },
                { 5.0001, SET_CAMERA, { "Monitor", "Cam" } },
                { 5, START_SOUND, { "Monitor", "Cam" } },
                { 5, "SET_CAMERA", { "Monitor", "Cam" } }
            }
            """)["events"]!).List.Cast<ThnTable>();

        var keys = ThnKeys.Of(ThnElementKind.Event, events, Evaluator.Symbols);

        Assert.Equal([(1, 2), (2, 2), (1, 1), (1, 1), (1, 1), (1, 1), (0, 0)], keys.Select(k => (k?.Nth ?? 0, k?.Count ?? 0)));
        Assert.Equal(keys[0]!.Value.Signature, keys[1]!.Value.Signature);
        Assert.Equal(5, keys.Take(6).Select(k => k!.Value.Signature).Distinct().Count());
        // A text is not a type, so the event has no key.
        Assert.Null(keys[6]);
    }

    /// <summary>A length before each target keeps two lists apart that give the same text when joined.</summary>
    [Fact]
    public void TargetListsThatJoinToTheSameTextAreDifferentKeys()
    {
        var a = ThnKeys.Signature(ThnElementKind.Event, List(N(1), S("SET_CAMERA"), List(T("a b"), T("c"))), Evaluator.Symbols);
        var b = ThnKeys.Signature(ThnElementKind.Event, List(N(1), S("SET_CAMERA"), List(T("a"), T("b c"))), Evaluator.Symbols);

        Assert.NotNull(a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TheKeyValueIsTheNameOrTheFirstThreeItems()
    {
        var entity = Record(F("type", S("CAMERA")), F("entity_name", T("Cam")));
        var ev = List(N(2), S("SET_CAMERA"), List(T("Monitor"), T("Cam")), Record(F("duration", N(1))));

        Assert.Equal(T("Cam"), ThnKeys.Match(ThnElementKind.Entity, entity));
        Assert.Equal(List(N(2), S("SET_CAMERA"), List(T("Monitor"), T("Cam"))), ThnKeys.Match(ThnElementKind.Event, ev));
        Assert.Equal("{ 2, SET_CAMERA, { \"Monitor\", \"Cam\" } }", ThnKeys.Text(ThnKeys.Match(ThnElementKind.Event, ev)!));
        Assert.Null(ThnKeys.Match(ThnElementKind.Event, List(N(2), S("SET_CAMERA"))));
    }
}

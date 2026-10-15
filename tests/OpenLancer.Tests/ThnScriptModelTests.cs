using System;
using System.Linq;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>The model of a script: its elements with ids and origins, and the globals in their order.</summary>
public sealed class ThnScriptModelTests
{
    [Fact]
    public void TheGlobalsKeepTheirOrder()
    {
        var globals = Evaluate("""
            note = "first"
            events = { { 1, SET_CAMERA, { "M", "C" } } }
            duration = 4
            entities = { { entity_name = "C" }, { entity_name = "M" } }
            extra = { 1, 2 }
            """);

        var model = ThnScriptModel.From(globals, ThnElementId.Counter());

        Assert.Equal(globals, model.ToGlobals());
        Assert.Equal(4, model.Duration);
        Assert.Equal([0, 1], model.Entities.Select(e => e.Origin));
        Assert.Equal([0], model.Events.Select(e => e.Origin));
        Assert.Equal(3, model.Entities.Concat(model.Events).Select(e => e.Id).Distinct().Count());
        // The model globals hold empty tables in the other globals. They only keep the places.
        Assert.Equal(ThnTable.Empty, model.OtherGlobals["entities"]);
    }

    [Fact]
    public void AModelWithoutTheGlobalsAddsThemAtTheEnd()
    {
        var model = new ThnScriptModel(ThnTable.Empty, 2, [], []);

        Assert.Equal("duration = 2\nentities = {}\nevents = {}\n", ThnWriter.Write(model.ToGlobals()));
    }

    [Theory]
    [InlineData("entities = {}\nevents = {}", "The global 'duration' must be a number.")]
    [InlineData("duration = \"5\"\nentities = {}\nevents = {}", "The global 'duration' must be a number.")]
    [InlineData("duration = 5\nevents = {}", "The global 'entities' must be a list of tables.")]
    [InlineData("duration = 5\nentities = { a = 1 }\nevents = {}", "The global 'entities' must be a list of tables.")]
    [InlineData("duration = 5\nentities = {}\nevents = { 3 }", "The value at events[1] must be a table.")]
    public void AScriptThatTheEngineCannotReadHasNoModel(string text, string message)
    {
        var e = Assert.Throws<FormatException>(() => ThnScriptModel.From(Evaluate(text), ThnElementId.Counter()));

        Assert.Equal(message, e.Message);
    }

    /// <summary>Equality compares the elements by value, also when the lists are other objects.</summary>
    [Fact]
    public void EqualModelsCompareEqual()
    {
        var globals = Evaluate("duration = 1\nentities = { { entity_name = \"a\" } }\nevents = {}");
        var a = ThnScriptModel.From(globals, ThnElementId.Counter());
        var b = ThnScriptModel.From(globals, ThnElementId.Counter());

        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Duration = 2 });
        Assert.NotEqual(a, a with { Entities = [a.Entities[0] with { Origin = -1 }] });
        Assert.Equal(0, BitConverter.SingleToInt32Bits((a with { Duration = -0f }).Duration));
    }
}

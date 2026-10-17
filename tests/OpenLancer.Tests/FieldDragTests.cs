using System;
using System.Globalization;
using System.Numerics;
using LibreLancer;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The pure rules of a label drag (section 6.4 and 6.5 of the M20 design): the kind of each key, the step of one unit,
/// and the exact value after a change.
/// </summary>
public sealed class FieldDragTests
{
    [Fact]
    public void EachVectorKeyHasTheKindOfItsDrag()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        KeySchema? Key(string type, string name) =>
            session.Schema(SystemWorkspace.Li01Section(type, type == "Zone" ? "Zone_death" : "Li01_depot"), name);

        Assert.Equal(DragKind.Position, FieldDrag.KindOf(Key("Object", "pos"), "Object"));
        Assert.Equal(DragKind.Rotation, FieldDrag.KindOf(Key("Object", "rotate"), "Object"));
        Assert.Equal(DragKind.Size, FieldDrag.KindOf(Key("Zone", "size"), "Zone"));
        // Bw05 and other retail files write the section type in lower case.
        Assert.Equal(DragKind.Size, FieldDrag.KindOf(Key("Zone", "size"), "zone"));
        Assert.Equal(DragKind.Scaled, FieldDrag.KindOf(Key("Object", "spin"), "Object"));
        Assert.Equal(DragKind.Scaled, FieldDrag.KindOf(Vector("size"), "Object"));
        Assert.Equal(DragKind.Coefficient, FieldDrag.KindOf(Vector("attenuation"), "LightSource"));
        Assert.Equal(DragKind.Coefficient, FieldDrag.KindOf(Vector("emit_attenuation"), "Light"));

        Assert.Null(FieldDrag.KindOf(Key("Zone", "edge_fraction"), "Zone"));
        Assert.Null(FieldDrag.KindOf(new KeySchema("color", typeof(Color4), false, false, 3, false, null), "LightSource"));
        Assert.Null(FieldDrag.KindOf(Vector("pos") with { Multiline = true }, "Object"));
        Assert.Null(FieldDrag.KindOf(null, "Object"));
        // The pos of a [system] in universe.ini is a Vector2.
        var system = session.Schema(SystemWorkspace.System("Li01"), "pos");
        Assert.Equal(typeof(Vector2), system!.ClrType);
        Assert.Null(FieldDrag.KindOf(system, "system"));
    }

    [Fact]
    public void ACutsceneRowHasTheKindOfItsValue()
    {
        Assert.Equal(DragKind.Scaled, FieldDrag.KindOf(ThnKeyKind.Vector, "spatialprops.pos"));
        Assert.Equal(DragKind.Scaled, FieldDrag.KindOf(ThnKeyKind.Vector, "pathprops.path_data"));
        Assert.Equal(DragKind.Coefficient, FieldDrag.KindOf(ThnKeyKind.Vector, "lightprops.atten"));
        Assert.Equal(DragKind.Coefficient, FieldDrag.KindOf(ThnKeyKind.Vector, "props.lightprops.atten"));
        Assert.Equal(DragKind.Rotation, FieldDrag.KindOf(ThnKeyKind.Orient, "spatialprops.orient"));
        Assert.Null(FieldDrag.KindOf(ThnKeyKind.Quaternion, "spatialprops.q_orient"));
        Assert.Null(FieldDrag.KindOf(ThnKeyKind.Number, "cameraprops.fovh"));
    }

    /// <summary>The steps of the table of section 6.4. The exponent of a value comes from its exact text, so a power of
    /// 10 gives its own exponent also when the float is a little below it.</summary>
    [Theory]
    [InlineData("Position", new[] { -38675f, 0, -27705 }, 0, "10")]
    [InlineData("Size", new[] { 5000f }, 0, "10")]
    [InlineData("Rotation", new[] { 0f, 105, 0 }, 1, "0.5")]
    [InlineData("Scaled", new[] { 0f, -1, 0 }, 1, "0.01")]
    [InlineData("Scaled", new[] { 0f, 0.002f, 0 }, 0, "0.00001")]
    [InlineData("Scaled", new[] { 2800f, 2800, 1800 }, 2, "10")]
    [InlineData("Scaled", new[] { 12.5f, 3.2f, -40 }, 0, "0.1")]
    [InlineData("Coefficient", new[] { 1f, 0, 0.0000056f }, 0, "0.01")]
    [InlineData("Coefficient", new[] { 1f, 0, 0.0000056f }, 1, "0.01")]
    [InlineData("Coefficient", new[] { 1f, 0, 0.0000056f }, 2, "0.00000001")]
    [InlineData("Scaled", new[] { 1000f }, 0, "10")]
    [InlineData("Scaled", new[] { 999.99994f }, 0, "1")]
    [InlineData("Scaled", new[] { 0.001f }, 0, "0.00001")]
    [InlineData("Scaled", new[] { 0.1f }, 0, "0.001")]
    // The float of 0.01 is 0.0099999998, so floor(log10) would give -3.
    [InlineData("Scaled", new[] { 0.01f }, 0, "0.0001")]
    public void TheStepFitsTheKindAndTheValues(string kind, float[] start, int index, string step)
    {
        Assert.Equal(decimal.Parse(step, CultureInfo.InvariantCulture),
            FieldDrag.BaseStep(Enum.Parse<DragKind>(kind), start, index));
    }

    [Fact]
    public void TheScaledStepStaysInsideItsRange()
    {
        Assert.Equal(0.01m, FieldDrag.BaseStep(DragKind.Scaled, [0, 0, 0], 0));
        Assert.Equal(0.01m, FieldDrag.BaseStep(DragKind.Scaled, [-0f, 0, 0], 0));
        Assert.Equal(10_000m, FieldDrag.BaseStep(DragKind.Scaled, [1e30f, 0, 0], 1));
        Assert.Equal(10_000m, FieldDrag.BaseStep(DragKind.Scaled, [float.MaxValue], 0));
        Assert.Equal(0.000001m, FieldDrag.BaseStep(DragKind.Scaled, [1e-30f, 0, 0], 0));
        Assert.Equal(0.000001m, FieldDrag.BaseStep(DragKind.Scaled, [0.0000001f], 0));
        Assert.Equal(0.000000001m, FieldDrag.BaseStep(DragKind.Coefficient, [1e-20f], 0));
        // A coefficient of 0 takes the step of its line.
        Assert.Equal(0.1m, FieldDrag.BaseStep(DragKind.Coefficient, [0, 12, 0], 0));
    }

    [Theory]
    [InlineData("Rotation", -180f)]
    [InlineData("Rotation", 270f)]
    [InlineData("Rotation", 123.4567f)]
    [InlineData("Position", 1e-30f)]
    [InlineData("Position", 1e30f)]
    [InlineData("Size", 0.5f)]
    public void AChangeOf0GivesTheStartBitForBit(string kind, float start)
    {
        Assert.Equal(Bits(start), Bits(FieldDrag.Value(Enum.Parse<DragKind>(kind), start, 0)));
    }

    [Fact]
    public void AChangeAddsToTheExactTextOfTheStart()
    {
        var sum = FieldDrag.Value(DragKind.Scaled, 0.3f, 0.1m);
        Assert.Equal("0.4", sum.ToString("R", CultureInfo.InvariantCulture));
        Assert.Equal(-37675f, FieldDrag.Value(DragKind.Position, -38675, 100 * 10m));
        Assert.Equal(210f, FieldDrag.Value(DragKind.Position, 200, 10));
        Assert.Equal(0.0000057f, FieldDrag.Value(DragKind.Coefficient, 0.0000056f, 10 * 0.00000001m));
    }

    [Fact]
    public void AnAngleWrapsAndRoundsAsAGizmoTurnWritesIt()
    {
        Assert.Equal(-170f, FieldDrag.Value(DragKind.Rotation, 170, 20));
        Assert.Equal(120f, FieldDrag.Value(DragKind.Rotation, 105, 30 * 0.5m));
        Assert.Equal(180f, FieldDrag.Value(DragKind.Rotation, 179.9f, 0.0996m));
        Assert.Equal(-180f, FieldDrag.Value(DragKind.Rotation, -179.9f, -0.0996m));
        Assert.Equal(180f, FieldDrag.Value(DragKind.Rotation, -180, 360));
        Assert.Equal(Bits(0f), Bits(FieldDrag.Value(DragKind.Rotation, 0.5f, -0.5004m)));
        Assert.Equal(12.346f, FieldDrag.Value(DragKind.Rotation, 12, 0.3456m));
    }

    [Fact]
    public void AChangedSizeIs1OrMore()
    {
        Assert.Equal(1f, FieldDrag.Value(DragKind.Size, 5000, -6000));
        Assert.Equal(1f, FieldDrag.Value(DragKind.Size, 0.5f, 0.1m));
        Assert.Equal(4400f, FieldDrag.Value(DragKind.Size, 5000, -600));
    }

    /// <summary>A decimal holds a change up to 7.9e28, and the float of the largest start plus that change is still
    /// the largest float. A start outside the range of a decimal adds in a double.</summary>
    [Fact]
    public void TheResultIsAlwaysAFiniteFloat()
    {
        Assert.Equal(float.MaxValue, FieldDrag.Value(DragKind.Position, float.MaxValue, decimal.MaxValue));
        Assert.Equal(-float.MaxValue, FieldDrag.Value(DragKind.Scaled, -float.MaxValue, decimal.MinValue));
        // 7.9e28 is below half the float step at 3e38.
        Assert.Equal(3e38f, FieldDrag.Value(DragKind.Coefficient, 3e38f, decimal.MaxValue));
        Assert.Equal(1e30f, FieldDrag.Value(DragKind.Position, 1e30f, 1));
        Assert.Null(FieldDrag.Exact(1e30f));
        Assert.Null(FieldDrag.Exact(1e-30f));
        Assert.Equal(0m, FieldDrag.Exact(-0f));
        // A decimal reads 1e-30 as 0, so the change adds in a double.
        Assert.Equal(1e-9f, FieldDrag.Value(DragKind.Coefficient, 1e-30f, 0.000000001m));
    }

    [Fact]
    public void TheTextOfALineIsTheShortestTextOfEachNumber()
    {
        Assert.Equal("255, 128, 0", FieldDrag.Text([255, 128, -0f]));
        Assert.Equal("0.784314, 1E-05, -37675", FieldDrag.Text([0.784314f, 0.00001f, -37675]));
        Assert.True(FieldDrag.TryParse(" 1, 2.5,-3 ", 3, out var numbers));
        Assert.Equal([1f, 2.5f, -3f], numbers);
        Assert.False(FieldDrag.TryParse("1, 2", 3, out _));
        Assert.False(FieldDrag.TryParse("1, 2, NaN", 3, out _));
        Assert.False(FieldDrag.TryParse("1, 2, 1e40", 3, out _));
        Assert.False(FieldDrag.TryParse("1, 2, x", 3, out _));
    }

    /// <summary>The text of a drag changes only the dragged value. A change of 0 units gives the start text back, so a
    /// drag back to its start is no edit.</summary>
    [Fact]
    public void ADragTextChangesOnlyTheDraggedValue()
    {
        Assert.Equal("-37675, 0, -27705", FieldDrag.DragText(DragKind.Position, "-38675, 0, -27705", 3, 0, 100));
        Assert.Equal("0, -170, 0", FieldDrag.DragText(DragKind.Rotation, "0, 170, 0", 3, 1, 40));
        Assert.Equal("12.5, 3.2, -40", FieldDrag.DragText(DragKind.Scaled, "12.5, 3.2, -40", 3, 1, 0));
        Assert.Equal("12.5, 3.3, -40", FieldDrag.DragText(DragKind.Scaled, "12.5, 3.2, -40", 3, 1, 1));
        Assert.Equal("1", FieldDrag.DragText(DragKind.Size, "5000", 1, 0, -600));
        Assert.Equal("not numbers", FieldDrag.DragText(DragKind.Size, "not numbers", 1, 0, 3));
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static KeySchema Vector(string name) => new(name, typeof(Vector3), false, false, 3, false, null);
}

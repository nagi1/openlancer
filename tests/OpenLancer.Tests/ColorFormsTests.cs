using System;
using System.Globalization;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The color forms of the picker without ImGui (section 8 of the M20 design): the form of a key, the picker color of a
/// line, and the numbers of a picker color.
/// </summary>
public sealed class ColorFormsTests
{
    [Fact]
    public void TheFormOfAColorKeyComesFromItsType()
    {
        Assert.Equal(ColorForm.Byte, ColorForms.Of(Key(typeof(Color4))));
        Assert.Equal(ColorForm.Byte, ColorForms.Of(Key(typeof(Color3f))));
        Assert.Equal(ColorForm.Unit, ColorForms.Of(Key(typeof(Color3f)) with { FloatColor = true }));
        // color_shift is a Vector3 color factor, not a color type.
        Assert.Null(ColorForms.Of(Key(typeof(Vector3))));
        Assert.Null(ColorForms.Of(Key(typeof(Color4)) with { Multiline = true }));
        Assert.Null(ColorForms.Of(null));
    }

    [Fact]
    public void EachByteValueComesBackAsItself()
    {
        for (var b = 0; b <= 255; b++)
        {
            float[] start = [b, b, b];
            var color = Picker(start, ColorForm.Byte);
            // From another start, the rounding alone gives the value. From its own start, the keep rule gives it.
            Assert.Equal(start, ColorForms.FromPicker(color, Vector3.Zero, [0, 0, 0], ColorForm.Byte));
            Assert.Equal(start, ColorForms.FromPicker(color, color, start, ColorForm.Byte));
        }
    }

    [Fact]
    public void EachValueWith3DecimalsComesBackAsItselfInTheUnitForm()
    {
        for (var k = 0; k <= 1000; k++)
        {
            var value = float.Parse((k / 1000m).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            float[] start = [value, value, value];
            var color = Picker(start, ColorForm.Unit);
            Assert.Equal(start, ColorForms.FromPicker(color, Vector3.Zero, [0, 0, 0], ColorForm.Unit));
            Assert.Equal(start, ColorForms.FromPicker(color, color, start, ColorForm.Unit));
        }
    }

    [Fact]
    public void APickerColorOutsideTheRangeClamps()
    {
        var color = new Vector3(1.2f, -0.1f, 0.5f);

        var other = new Vector3(0.25f);

        Assert.Equal([255f, 0, 128], ColorForms.FromPicker(color, other, [7, 7, 7], ColorForm.Byte));
        Assert.Equal([1f, 0, 0.5f], ColorForms.FromPicker(color, other, [7, 7, 7], ColorForm.Unit));
    }

    [Fact]
    public void ALineWithoutAPickerHasAReason()
    {
        Assert.Contains("fewer than 3 values", Reason([new Int32Value(1), new Int32Value(2)], ColorForm.Byte));
        Assert.Equal("Value 2 is not a number. Correct it in the field first.",
            Reason([new Int32Value(1), new StringValue("red"), new Int32Value(2)], ColorForm.Byte));
        Assert.Contains("Value 3 is not a number",
            Reason([new Int32Value(1), new Int32Value(2), new StringValue("NaN")], ColorForm.Byte));
        Assert.Contains("Value 1 is outside the range from 0 to 255",
            Reason([new Int32Value(300), new Int32Value(0), new Int32Value(0)], ColorForm.Byte));
        Assert.Contains("Value 2 is outside the range from 0 to 1",
            Reason([new SingleValue(1, null), new SingleValue(1.5f, null), new SingleValue(0, null)], ColorForm.Unit));
    }

    /// <summary>A line of 4 values gives its picker color from the first 3. The fourth value can be any value, because
    /// the picker does not change it.</summary>
    [Fact]
    public void ALineWith4ValuesGivesThePickerColorOfTheFirst3()
    {
        var (color, reason) = ColorForms.ToPicker(
            [new Int32Value(255), new Int32Value(0), new Int32Value(51), new StringValue("any")], ColorForm.Byte);

        Assert.Null(reason);
        Assert.Equal(new Vector3(1, 0, 0.2f), color);
    }

    /// <summary>A channel whose rounded value does not change keeps its start value, also a value that is not on the
    /// grid of the form. So the start color gives the start numbers back.</summary>
    [Fact]
    public void AChannelThatTheUserDoesNotChangeKeepsItsStartValue()
    {
        float[] light = [0.784314f, 0.784314f, 0.784314f];
        var start = Picker(light, ColorForm.Unit);
        // The float noise of the picker on G and B.
        var moved = new Vector3(0.9f, start.Y + 0.0001f, start.Z - 0.0002f);
        Assert.Equal([0.9f, 0.784314f, 0.784314f], ColorForms.FromPicker(moved, start, light, ColorForm.Unit));
        Assert.Equal(light, ColorForms.FromPicker(start, start, light, ColorForm.Unit));

        float[] half = [120.5f, 0, 0];
        var startByte = Picker(half, ColorForm.Byte);
        Assert.Equal([120.5f, 128, 0], ColorForms.FromPicker(startByte with { Y = 0.5f }, startByte, half, ColorForm.Byte));
        Assert.Equal(half, ColorForms.FromPicker(startByte, startByte, half, ColorForm.Byte));
    }

    private static KeySchema Key(Type type) => new("color", type, false, false, 3, false, null);

    private static Vector3 Picker(float[] numbers, ColorForm form)
    {
        ValueBase[] line = [new SingleValue(numbers[0], null), new SingleValue(numbers[1], null), new SingleValue(numbers[2], null)];
        var (color, reason) = ColorForms.ToPicker(line, form);
        Assert.Null(reason);
        return color!.Value;
    }

    private static string? Reason(ValueBase[] line, ColorForm form) => ColorForms.ToPicker(line, form).Reason;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The number rules of a drag (rule 4 of the design): an unchanged value keeps its object, a changed integer that
/// replaces an Int32 or no value is an Int32, and each other changed value is a Single.
/// </summary>
public sealed class TransformValuesTests
{
    [Fact]
    public void AnIntegralMoveWritesInt32AndKeepsTheUnchangedValues()
    {
        ValueBase[] start = [new Int32Value(-38675), new Int32Value(0), new Int32Value(-27705)];

        var line = TransformValues.Line(start, [-37675, 0, -27705]);

        Assert.Equal("pos = -37675, 0, -27705", Text("pos", line));
        Assert.IsType<Int32Value>(line[0]);
        Assert.NotSame(start[0], line[0]);
        Assert.Same(start[1], line[1]);
        Assert.Same(start[2], line[2]);
    }

    [Fact]
    public void AFractionWritesASingle()
    {
        ValueBase[] start = [new Int32Value(-38675), new Int32Value(0), new Int32Value(-27705)];

        var line = TransformValues.Line(start, [-37612.457f, 0, -27705]);

        Assert.Equal("pos = -37612.457, 0, -27705", Text("pos", line));
        Assert.IsType<SingleValue>(line[0]);
    }

    /// <summary>After a save and a reload of a binary file, W holds a Single where the patch had an integer. A later
    /// drag must keep that Single, so the text of the line stays the same across saves.</summary>
    [Fact]
    public void AnIntegerThatReplacesASingleStaysASingle()
    {
        ValueBase[] start = [new SingleValue(150.5f, null), new SingleValue(200, null)];

        var line = TransformValues.Line(start, [100, 200]);

        Assert.Equal("size = 100.0, 200.0", Text("size", line));
        Assert.IsType<SingleValue>(line[0]);
        Assert.Same(start[1], line[1]);
    }

    [Fact]
    public void MinusZeroBecomesZero()
    {
        var single = TransformValues.Line([new SingleValue(5, null)], [-0f]);
        var integer = TransformValues.Line([new Int32Value(5)], [-0f]);
        var none = TransformValues.Line(null, [-0f]);
        ValueBase zero = new Int32Value(0);
        var unchanged = TransformValues.Line([zero], [-0f]);

        Assert.Equal(0, BitConverter.SingleToInt32Bits((float)Assert.IsType<SingleValue>(single[0])));
        Assert.Equal("rotate = 0.0", Text("rotate", single));
        Assert.Equal("rotate = 0", Text("rotate", integer));
        Assert.Equal("rotate = 0", Text("rotate", none));
        Assert.Same(zero, unchanged[0]);
    }

    [Fact]
    public void AnIntegerAbove2To24IsASingle()
    {
        var line = TransformValues.Line([new Int32Value(0), new Int32Value(0), new Int32Value(0), new Int32Value(0)],
            [16_777_216, -16_777_216, 16_777_218, -16_777_218]);

        Assert.IsType<Int32Value>(line[0]);
        Assert.IsType<Int32Value>(line[1]);
        Assert.IsType<SingleValue>(line[2]);
        Assert.IsType<SingleValue>(line[3]);
        Assert.Equal("v = 16777216, -16777216, 16777218.0, -16777218.0", Text("v", line));
    }

    /// <summary>The engine reads the one value 0 as the zero vector. A move then writes all three values.</summary>
    [Fact]
    public void TheOneValueZeroFormIsWrittenAsThreeValues()
    {
        ValueBase[] start = [new Int32Value(0)];

        var line = TransformValues.Line(start, [100, 0, 0]);

        Assert.Equal("pos = 100, 0, 0", Text("pos", line));
        Assert.All(line, v => Assert.IsType<Int32Value>(v));
    }

    /// <summary>
    /// A move or a rotate that the engine reads as the start vector keeps the start line, also a line without three
    /// values: no line is (0, 0, 0), the one value 0 is the zero vector, and the parser ignores a line with two values.
    /// So a drag that changes nothing makes no line, for example no <c>rotate = 0, 0, 0</c> on an object without a
    /// rotate line.
    /// </summary>
    [Fact]
    public void AnUnchangedVectorKeepsTheStartLine()
    {
        Assert.Empty(TransformValues.Line(null, Vector3.Zero));
        Assert.Empty(TransformValues.Line(null, new Vector3(-0f, 0, 0)));
        ValueBase[] zero = [new Int32Value(0)];
        Assert.Same(zero, TransformValues.Line(zero, Vector3.Zero));
        ValueBase[] ignored = [new Int32Value(1), new Int32Value(2)];
        Assert.Same(ignored, TransformValues.Line(ignored, Vector3.Zero));
        ValueBase[] four = [new Int32Value(0), new Int32Value(105), new SingleValue(0, null), new Int32Value(7)];
        Assert.Same(four, TransformValues.Line(four, new Vector3(0, 105, 0)));
    }

    [Fact]
    public void AChangedVectorWritesThreeValues()
    {
        Assert.Equal("rotate = 0, 15, 0", Text("rotate", TransformValues.Line(null, new Vector3(0, 15, 0))));
        Assert.Equal("pos = 100, 0, 0", Text("pos", TransformValues.Line([new Int32Value(0)], new Vector3(100, 0, 0))));
        ValueBase[] start = [new Int32Value(-38675), new Int32Value(0), new Int32Value(-27705)];
        var line = TransformValues.Line(start, new Vector3(-37675, 0, -27705));
        Assert.Equal("pos = -37675, 0, -27705", Text("pos", line));
        Assert.Same(start[1], line[1]);
        Assert.Same(start[2], line[2]);
    }

    [Fact]
    public void AStartValueAfterTheNewNumbersStays()
    {
        ValueBase[] start = [new Int32Value(4100), new Int32Value(50), new Int32Value(60)];

        var line = TransformValues.Line(start, [6150]);

        Assert.Equal("size = 6150, 50, 60", Text("size", line));
        Assert.Same(start[1], line[1]);
        Assert.Same(start[2], line[2]);
    }

    [Fact]
    public void AStringIsUnchangedOnlyWhenTheEngineReadsTheSameNumber()
    {
        ValueBase[] start = [new StringValue("100"), new StringValue("abc")];

        var line = TransformValues.Line(start, [100, 0]);

        Assert.Same(start[0], line[0]);
        Assert.Equal(0f, (float)Assert.IsType<SingleValue>(line[1]));
    }

    [Fact]
    public void ANumberThatIsNotFiniteThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransformValues.Line(null, [float.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransformValues.Line(null, [1, float.PositiveInfinity]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransformValues.Line(null, new Vector3(float.NaN, 0, 0)));
    }

    /// <summary>The patch text of a line in a binary file, as a save writes it.</summary>
    private static string Text(string name, IReadOnlyList<ValueBase> values)
    {
        var entry = new Entry(new Section("Object"), name);
        foreach (var value in values)
            entry.Add(value);
        Assert.True(PatchFormatter.TryFormatLine(entry, null, binary: true, out var line, out var error), error);
        return line;
    }
}

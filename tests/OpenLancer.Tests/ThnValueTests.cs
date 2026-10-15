using System;
using System.Collections.Immutable;
using System.Linq;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>The THN value tree: exact equality, the table rules and the path of the first difference.</summary>
public sealed class ThnValueTests
{
    private static ThnTable Camera(float fovh, string typeName = "CAMERA") => Record(
        F("entity_name", T("Cam_A")),
        F("type", S(typeName)),
        F("cameraprops", Record(F("fovh", N(fovh)), F("hvaspect", N(1.5f)))));

    [Fact]
    public void EqualTablesAreEqualInAllLevels()
    {
        var a = Record(F("entities", List(Camera(10))));
        var b = Record(F("entities", List(Camera(10))));

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Null(ThnValues.FirstDifference(a, b));
    }

    /// <summary>The engine reads <c>type = 3</c> and <c>type = CAMERA</c> the same way, but they are different
    /// values. Only this rule gives identical tables after a round trip.</summary>
    [Fact]
    public void ANumberAndANameAreDifferentValues()
    {
        var name = Record(F("type", S("CAMERA")));
        var number = Record(F("type", N(3)));

        Assert.NotEqual<ThnValue>(name, number);
        Assert.Equal("type", ThnValues.FirstDifference(name, number));
        Assert.NotEqual<ThnValue>(T("CAMERA"), S("CAMERA"));
        Assert.NotEqual<ThnValue>(new ThnBool(true), S("Y"));
    }

    [Fact]
    public void TheFirstDifferenceNamesThePathOfTheValue()
    {
        var a = Record(F("duration", N(5)), F("entities", List(Camera(10), Camera(20))));
        var b = Record(F("duration", N(5)), F("entities", List(Camera(10), Camera(25))));

        Assert.Equal("entities[2].cameraprops.fovh", ThnValues.FirstDifference(a, b));
        Assert.Equal("entities[2].cameraprops.fovh", ThnValues.FirstDifference(b, a));
    }

    /// <summary>A record with the same keys in another order is a different table. The path names the table.</summary>
    [Fact]
    public void KeyOrderIsPartOfEquality()
    {
        var a = Record(F("entities", List(Record(F("x", N(1)), F("y", N(2))))));
        var b = Record(F("entities", List(Record(F("y", N(2)), F("x", N(1))))));

        Assert.NotEqual(a, b);
        Assert.Equal("entities[1]", ThnValues.FirstDifference(a, b));
        Assert.Equal("entities", ThnValues.FirstDifference(a, Record(F("entities", List()))));
        Assert.Equal("", ThnValues.FirstDifference(a, Record(F("events", List()))));
    }

    [Fact]
    public void TheIndexerFindsARecordKeyWithItsCase()
    {
        var table = Record(F("Actor", T("player")), F("speaker", T("Char_A")));

        Assert.Equal(T("player"), table["Actor"]);
        Assert.Null(table["actor"]);
        Assert.Null(table["missing"]);
    }

    /// <summary>An empty table is a record, so a path can go into an empty <c>psysprops</c>.</summary>
    [Fact]
    public void AnEmptyTableIsARecord()
    {
        Assert.True(ThnTable.Empty.IsRecord);
        Assert.True(Record(F("a", N(1))).IsRecord);
        Assert.False(List(N(1)).IsRecord);
        Assert.Equal(ThnTable.Empty, List());
    }

    /// <summary>A Lua table constructor with a key twice does not run (ThornRuntime.cs:390), so a tree cannot hold
    /// one. The check works for small and for large records.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(40)]
    public void ATableRefusesAKeyTwice(int fieldCount)
    {
        var fields = Enumerable.Range(0, fieldCount).Select(i => F($"k{i}", N(i))).Append(F("k1", N(0))).ToArray();

        var e = Assert.Throws<ArgumentException>(() => Record(fields));
        Assert.Contains("'k1' occurs two times", e.Message);
    }

    [Fact]
    public void ATableRefusesNullAndDefaultParts()
    {
        Assert.Throws<ArgumentException>(() => new ThnTable(default, []));
        Assert.Throws<ArgumentException>(() => new ThnTable([], default));
        Assert.Throws<ArgumentException>(() => new ThnTable([null!], []));
        Assert.Throws<ArgumentException>(() => new ThnTable([], [default]));
        Assert.Throws<ArgumentException>(() => new ThnTable([], ImmutableArray.Create(new ThnField("a", null!))));
    }

    /// <summary>A tree never holds a null text or name, so the writer never meets one.</summary>
    [Fact]
    public void ATextAndANameRefuseNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ThnText(null!));
        Assert.Throws<ArgumentNullException>(() => new ThnSymbol(null!));
    }
}

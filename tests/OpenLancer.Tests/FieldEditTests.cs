using System;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The text of an Inspector field: values to text, and text to the line texts of an edit.</summary>
public sealed class FieldEditTests
{
    private enum Kind
    {
        Fighter,
        Freighter,
    }

    private static KeySchema Key(Type? type, bool multiline = false, int minComponents = 1) =>
        new("k", type, multiline, false, minComponents, false, null);

    [Fact]
    public void AValueGetsTheTextThatAPatchLineHolds()
    {
        Assert.Equal("true", FieldEdit.FormatValue(new BooleanValue(true)));
        Assert.Equal("-1980013182", FieldEdit.FormatValue(new Int32Value(-1980013182)));
        Assert.Equal("2314954114", FieldEdit.FormatValue(new Int32Value(unchecked((int)2314954114u), unsigned: true)));
        Assert.Equal("0.1", FieldEdit.FormatValue(new SingleValue(0.1f, null)));
        Assert.Equal("100", FieldEdit.FormatValue(new SingleValue(100, null)));
        Assert.Equal("4000000000", FieldEdit.FormatValue(new SingleValue(4e9f, 4000000000)));
        Assert.Equal("li_gun01", FieldEdit.FormatValue(new StringValue("li_gun01")));
    }

    [Fact]
    public void AFieldShowsEachLineOnItsOwnTextLine()
    {
        EntryView[] lines =
        [
            new("addon", [new StringValue("gun1"), new StringValue("HpWeapon01"), new Int32Value(1)]),
            new("addon", [new StringValue("eng1"), new StringValue("internal"), new Int32Value(1)]),
            new("separable", []),
        ];

        Assert.Equal("gun1, HpWeapon01, 1\neng1, internal, 1\n", FieldEdit.Format(lines));
        Assert.Equal("addon = gun1, HpWeapon01, 1\naddon = eng1, internal, 1", FieldEdit.IniText("addon", lines[..2]));
        Assert.Equal("separable =", FieldEdit.IniText("separable", lines[2..]));
    }

    [Fact]
    public void TextGivesLineTextsWithTheValuesSeparatedByACommaAndASpace()
    {
        var parse = FieldEdit.Parse(Key(null, multiline: true), " gun1 ,HpWeapon01,  1 \r\n\n eng1,internal,1");

        Assert.Null(parse.Error);
        Assert.Equal(["gun1, HpWeapon01, 1", "eng1, internal, 1"], parse.Lines);
    }

    [Fact]
    public void EmptyTextRemovesTheKey()
    {
        Assert.Equal([], FieldEdit.Parse(Key(typeof(float)), "  ").Lines);
    }

    [Theory]
    [InlineData(typeof(float), "1.5", null)]
    [InlineData(typeof(float), "-0.0", null)]
    [InlineData(typeof(float), "abc", "'abc' is not a number")]
    [InlineData(typeof(float), "1,000", "one value")]
    [InlineData(typeof(float), "NaN", "is not a number")]
    [InlineData(typeof(int), "7", null)]
    [InlineData(typeof(int), "2314954114", null)]
    [InlineData(typeof(int), "7.5", "is not a whole number")]
    [InlineData(typeof(bool), "TRUE", null)]
    [InlineData(typeof(bool), "yes", "is not true or false")]
    [InlineData(typeof(Kind), "FREIGHTER", null)]
    [InlineData(typeof(Kind), "Tanker", "is not one of Fighter, Freighter")]
    [InlineData(typeof(Vector3), "1, 2, 3", null)]
    [InlineData(typeof(Vector3), "0", null)]
    [InlineData(typeof(Vector3), "1, 2", "needs 3 values")]
    [InlineData(typeof(Vector3), "1, 2, 3, 4", "at most 3 values")]
    [InlineData(typeof(Vector3), "1, x, 3", "'x' is not a number")]
    [InlineData(typeof(float[]), "0, 75, 150, 1300", null)]
    [InlineData(typeof(Color4), "255, 128, 0", null)]
    [InlineData(typeof(string), "ships\\li_elite.cmp", null)]
    [InlineData(typeof(string), "a, b", "one value")]
    public void TheSchemaChecksEachValue(Type type, string text, string? error)
    {
        var minComponents = type == typeof(Vector3) || type == typeof(Color4) ? 3 : 1;
        var parse = FieldEdit.Parse(Key(type, minComponents: minComponents), text);

        if (error == null)
        {
            Assert.Null(parse.Error);
            Assert.Single(parse.Lines!);
        }
        else
        {
            Assert.Null(parse.Lines);
            Assert.Contains(error, parse.Error);
        }
    }

    [Fact]
    public void AKeyThatTheEngineReadsOneTimeTakesOneLine()
    {
        Assert.Contains("one line", FieldEdit.Parse(Key(typeof(float)), "1\n2").Error);
        Assert.Equal(["1", "2"], FieldEdit.Parse(Key(typeof(float), multiline: true), "1\n2").Lines);
    }

    [Fact]
    public void AKeyWithoutASchemaTakesAnyValues()
    {
        Assert.Equal(["a, 1, 2.5"], FieldEdit.Parse(null, "a,1,2.5").Lines);
    }

    [Fact]
    public void AVectorFieldReplacesOneValueOfTheLine()
    {
        Assert.Equal("1, 9, 3", FieldEdit.WithValue("1, 2, 3", 1, " 9 "));
        Assert.Equal("1, , 5", FieldEdit.WithValue("1", 2, "5"));
        Assert.Equal(["1", "2", "3"], FieldEdit.Values(" 1,2 , 3"));
        Assert.Empty(FieldEdit.Values("  "));
    }
}

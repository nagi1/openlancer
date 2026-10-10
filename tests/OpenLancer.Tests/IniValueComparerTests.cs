using System;
using System.Collections.Generic;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The two comparer modes. Strict: the file stores the same bytes. Engine-equal: the values that the engine reads
/// back from the file give the same result for every accessor.
/// </summary>
public sealed class IniValueComparerTests
{
    private static readonly bool[] BothFormats = [true, false];

    [Fact]
    public void AnInt32AndAFloatWithTheSameNumberAreEngineEqual()
    {
        foreach (var binary in BothFormats)
        {
            Assert.True(IniValueComparer.EngineEqual(new Int32Value(100), new SingleValue(100f, null), binary));
            Assert.False(IniValueComparer.EngineEqual(new Int32Value(1), new SingleValue(1.5f, null), binary));
        }
        // A binary file stores the value kind. A text file stores "100" for both.
        Assert.False(IniValueComparer.StrictEqual(new Int32Value(100), new SingleValue(100f, null), binary: true));
        Assert.True(IniValueComparer.StrictEqual(new Int32Value(100), new SingleValue(100f, null), binary: false));
    }

    [Fact]
    public void ABoolStringIsABooleanOnlyInATextFile()
    {
        // In a binary file the string stays a string, and BooleanValue.ToString gives "True".
        Assert.False(IniValueComparer.EngineEqual(new StringValue("true"), new BooleanValue(true), binary: true));
        Assert.False(IniValueComparer.EngineEqual(new StringValue("True"), new BooleanValue(true), binary: true));
        // The text parser reads "true" as a boolean.
        Assert.True(IniValueComparer.EngineEqual(new StringValue("true"), new BooleanValue(true), binary: false));
        Assert.False(IniValueComparer.StrictEqual(new StringValue("true"), new BooleanValue(true), binary: false));
    }

    [Fact]
    public void StringsWithTheSameTextAreStrictEqualInBothFormats()
    {
        foreach (var binary in BothFormats)
        {
            Assert.True(IniValueComparer.StrictEqual(new StringValue("li_gun"), new LancerStringValue("li_gun"), binary));
            Assert.True(IniValueComparer.StrictEqual(new StringValue("yes"), new LancerStringValue("yes"), binary));
            Assert.False(IniValueComparer.EngineEqual(new StringValue("li_gun"), new StringValue("LI_GUN"), binary));
        }
    }

    [Fact]
    public void ABinaryFileStoresOnlyTheBitsOfAnInt32()
    {
        var unsigned = new Int32Value(unchecked((int)2314954114u), unsigned: true);
        var signed = new Int32Value(-1980013182);

        Assert.True(IniValueComparer.StrictEqual(unsigned, signed, binary: true));
        Assert.True(IniValueComparer.EngineEqual(unsigned, signed, binary: true));
        // A text file stores "2314954114" and "-1980013182".
        Assert.False(IniValueComparer.EngineEqual(unsigned, signed, binary: false));
    }

    [Fact]
    public void ABinaryFileStoresOnlyTheFloatOfALargeInteger()
    {
        var withLong = new SingleValue(4e9f, 4_000_000_000);
        var floatOnly = new SingleValue(4e9f, null);

        Assert.True(IniValueComparer.StrictEqual(withLong, floatOnly, binary: true));
        // A text file stores "4000000000" and "4E+09". Only the first reads back with the exact long.
        Assert.False(IniValueComparer.EngineEqual(withLong, floatOnly, binary: false));
    }

    [Fact]
    public void ATextFileStoresAHashAsItsExactNumber()
    {
        var unsigned = new Int32Value(unchecked((int)2314954114u), unsigned: true);
        var parsed = new SingleValue(2314954114f, 2_314_954_114);

        Assert.True(IniValueComparer.StrictEqual(unsigned, parsed, binary: false));
        Assert.False(IniValueComparer.EngineEqual(unsigned, parsed, binary: true));
    }

    [Fact]
    public void ANumberStringInABinaryFileIsEngineEqualToTheNumber()
    {
        // Every accessor of the string "100" gives the result of the Int32 100. The file still changes.
        Assert.True(IniValueComparer.EngineEqual(new StringValue("100"), new Int32Value(100), binary: true));
        Assert.False(IniValueComparer.StrictEqual(new StringValue("100"), new Int32Value(100), binary: true));
        // The Int32 0 is false. The string "0" is true, because it is not empty.
        Assert.False(IniValueComparer.EngineEqual(new StringValue("0"), new Int32Value(0), binary: true));
        // TryToInt32 fails for the string "1.5".
        Assert.False(IniValueComparer.EngineEqual(new StringValue("1.5"), new SingleValue(1.5f, null), binary: true));
    }

    [Fact]
    public void NegativeZeroIsZeroOnlyInATextFile()
    {
        var negative = new SingleValue(-0f, null);
        var zero = new SingleValue(0f, null);

        Assert.False(IniValueComparer.EngineEqual(negative, zero, binary: true));
        // A text file stores "-0", which the text parser reads as the Int32 0.
        Assert.True(IniValueComparer.EngineEqual(negative, zero, binary: false));
        Assert.False(IniValueComparer.StrictEqual(negative, zero, binary: false));
    }

    [Fact]
    public void StrictEqualFloatsInABinaryFileHaveTheSameBits()
    {
        var nan = new SingleValue(float.NaN, null);
        var otherNan = new SingleValue(BitConverter.Int32BitsToSingle(0x7FC00001), null);

        Assert.True(IniValueComparer.EngineEqual(nan, otherNan, binary: true));
        Assert.False(IniValueComparer.StrictEqual(nan, otherNan, binary: true));
        Assert.True(IniValueComparer.StrictEqual(nan, new SingleValue(float.NaN, null), binary: true));
        // A text file stores "NaN" for both.
        Assert.True(IniValueComparer.StrictEqual(nan, otherNan, binary: false));
    }

    [Fact]
    public void ABinaryFileReadsTextOutsideAsciiAsQuestionMarks()
    {
        Assert.True(IniValueComparer.EngineEqual(new StringValue("café"), new StringValue("cafè"), binary: true));
        Assert.False(IniValueComparer.StrictEqual(new StringValue("café"), new StringValue("cafè"), binary: true));
    }

    [Fact]
    public void ATextFileStoresTextInItsCodePage()
    {
        // The text writer uses code page 1252. It has 'é', but it writes 'O' for 'Ω'.
        Assert.True(IniValueComparer.StrictEqual(new StringValue("Ω"), new StringValue("O"), binary: false));
        Assert.False(IniValueComparer.StrictEqual(new StringValue("é"), new StringValue("e"), binary: false));
    }

    [Fact]
    public void ABinaryFileEndsAStringAtItsFirstNul()
    {
        Assert.True(IniValueComparer.EngineEqual(new StringValue("a\0b"), new StringValue("a"), binary: true));
        Assert.False(IniValueComparer.StrictEqual(new StringValue("a\0b"), new StringValue("a"), binary: true));
        Assert.False(IniValueComparer.EngineEqual(new StringValue("a\0b"), new StringValue("a"), binary: false));
    }

    [Fact]
    public void TheComparerLogsNoParseErrors()
    {
        // StringValue.TryToSingle logs an engine error when the text is not a number.
        const string probe = "ol_comparer_probe";
        IReadOnlyList<string> lines;
        using (var capture = EngineLogCapture.Start())
        {
            foreach (var binary in BothFormats)
            {
                IniValueComparer.EngineEqual(new StringValue(probe), new StringValue(probe), binary);
                IniValueComparer.EngineEqual(new StringValue(probe), new LancerStringValue(probe + "x"), binary);
                IniValueComparer.EngineEqual(new StringValue(probe), new Int32Value(5), binary);
                IniValueComparer.EngineEqual(new SingleValue(2.5f, null), new LancerStringValue(probe), binary);
                IniValueComparer.EngineEqual(new BooleanValue(true), new StringValue(probe), binary);
            }
            lines = capture.Stop();
        }

        Assert.DoesNotContain(lines, l => l.Contains(probe));
    }
}

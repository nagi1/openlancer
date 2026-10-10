using System;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The value text rules of the patch writer: each value gets a text that the merge reads back as the same value.
/// </summary>
public sealed class PatchFormatterTests
{
    private static readonly bool[] BothFormats = [true, false];

    [Theory]
    [InlineData(-0f, "-0.0")]
    [InlineData(100f, "100.0")]
    [InlineData(16777216f, "16777216.0")]
    [InlineData(0.1f, "0.1")]
    [InlineData(1f / 3, "0.33333334")]
    [InlineData(-2.5e-7f, "-2.5E-07")]
    [InlineData(1e10f, "1E+10")]
    public void AFloatGetsTheShortestTextThatReadsBackAsAFloat(float number, string text)
    {
        foreach (var binary in BothFormats)
        {
            var line = Format(Line("x", new SingleValue(number, null)), binary);

            Assert.Equal("x = " + text, line);
            var read = Assert.IsType<SingleValue>(ReadBack(line).Single());
            Assert.Equal(BitConverter.SingleToInt32Bits(number), BitConverter.SingleToInt32Bits(read));
        }
    }

    [Fact]
    public void NaNAndInfinityAreRefused()
    {
        foreach (var binary in BothFormats)
        {
            Assert.Contains("cannot hold the number NaN", Error(Line("x", new SingleValue(float.NaN, null)), binary));
            Assert.Contains("cannot hold the number", Error(Line("x", new SingleValue(float.NegativeInfinity, null)), binary));
        }
    }

    [Fact]
    public void ABinaryFileGetsEveryInt32InSignedForm()
    {
        // A binary file stores only the 32 bits. The signed form fits a new line too, where an unsigned number
        // above 2147483647 has no replaced value that gives its type.
        var hash = new Int32Value(unchecked((int)2314954114u), unsigned: true);

        Assert.Equal("x = -1980013182", Format(Line("x", hash), binary: true));
        Assert.Equal("x = 2314954114", Format(Line("x", hash), binary: false));
        Assert.Equal("x = -5, 7", Format(Line("x", new Int32Value(-5), new Int32Value(7)), binary: true));
    }

    [Fact]
    public void AFloatThatHoldsALargeIntegerWritesTheIntegerOnlyInATextFile()
    {
        // The text parser keeps the exact long of 4000000000. A binary file stores only the float.
        var large = new SingleValue(4e9f, 4_000_000_000);

        Assert.Equal("x = 4000000000", Format(Line("x", large), binary: false));
        Assert.Equal("x = 4E+09", Format(Line("x", large), binary: true));
    }

    [Fact]
    public void ABooleanIsTrueOrFalse()
    {
        foreach (var binary in BothFormats)
            Assert.Equal("x = true, false", Format(Line("x", new BooleanValue(true), new BooleanValue(false)), binary));
    }

    [Fact]
    public void ABoolStringKeepsItsText()
    {
        // In a binary file, the merge keeps a boolean that replaces a string as a string with the same text.
        // In a text file, the parser reads the file's own "TRUE" as the same boolean.
        foreach (var binary in BothFormats)
            Assert.Equal("x = TRUE", Format(Line("x", new StringValue("TRUE")), binary));
    }

    [Fact]
    public void ANewLineOfABinaryFileHoldsABooleanOnlyAsLowercaseText()
    {
        // The merge stores a boolean that replaces no value as the string "true" or "false", as retail files do.
        Assert.Equal("x = true, false", Format(Line("x", "true", "false"), replaced: null, binary: true));
        Assert.Contains("as the string 'true', not as the string 'TRUE'", Error(Line("x", "TRUE"), replaced: null, binary: true));
        Assert.Contains("as the string 'true', not as the boolean True",
            Error(Line("x", new BooleanValue(true)), replaced: null, binary: true));
        // A value after the end of the replaced line also replaces no value.
        Assert.Contains("value 2 as the string 'false', not as the string 'False'",
            Error(Line("x", "TRUE", "False"), replaced: Line("x", "TRUE"), binary: true));
        // A text file has no type rules.
        Assert.Equal("x = TRUE", Format(Line("x", "TRUE"), replaced: null, binary: false));
    }

    [Fact]
    public void AValueThatTheMergeCannotStoreIsRefused()
    {
        // The parser reads 4000000000 as a large integer. Over a string or no value, the merge reports an error.
        foreach (var replaced in new[] { Line("x", "old"), null })
            Assert.Contains("the merge cannot store value 1 exactly", Error(Line("x", "4000000000"), replaced, binary: true));
    }

    [Theory]
    [InlineData("café", "must be ASCII")]
    [InlineData("a,b", "cannot contain ','")]
    [InlineData("a;b", "cannot contain ','")]
    [InlineData("a\rb", "control character")]
    [InlineData("a\nb", "control character")]
    [InlineData("a\0b", "control character")]
    [InlineData("a\tb", "control character")]
    [InlineData("a\u001Fb", "control character")]
    [InlineData("a\u007Fb", "control character")]
    [InlineData(" a", "white space")]
    [InlineData("a\t", "white space")]
    [InlineData("a\v", "white space")]
    [InlineData("", "an empty text cannot be the only value")]
    public void AStringThatAPatchLineCannotHoldIsRefused(string text, string reason)
    {
        foreach (var binary in BothFormats)
            Assert.Contains(reason, Error(Line("x", new StringValue(text)), binary));
    }

    [Fact]
    public void ALineHoldsAtMost255ValuesForABinaryFileAnd256ForATextFile()
    {
        ValueBase[] Values(int count) => Enumerable.Range(0, count).Select(i => (ValueBase)new Int32Value(i)).ToArray();

        Assert.Equal(255, ReadBack(Format(Line("x", Values(255)), binary: true)).Count);
        Assert.Contains("holds at most 255 values, not 256", Error(Line("x", Values(256)), binary: true));
        // A text file stores no value count, but the text parser reads at most 256 values in a line.
        Assert.Equal(256, ReadBack(Format(Line("x", Values(256)), binary: false)).Count);
        Assert.Contains("the merge does not read back 257 values", Error(Line("x", Values(257)), binary: false));
    }

    [Theory]
    [InlineData("0.50", "the merge reads value 1 as the float 0.5, not as the string '0.50'")]
    [InlineData("4000000000", "the merge cannot store value 1 exactly")]
    public void ANumberStringIsRefusedForABinaryFile(string text, string reason)
    {
        Assert.Contains(reason, Error(Line("x", new StringValue(text)), binary: true));
        // A text file stores only the text. The parser reads the file's own text as the same number.
        Assert.Equal("x = " + text, Format(Line("x", new StringValue(text)), binary: false));
    }

    [Fact]
    public void AnEmptyStringBetweenOtherValuesIsAllowed()
    {
        foreach (var binary in BothFormats)
            Assert.Equal("x = a, , b", Format(Line("x", "a", "", "b"), binary));
    }

    [Fact]
    public void AnEntryWithoutValuesIsWrittenWithAnEqualsSign()
    {
        foreach (var binary in BothFormats)
        {
            var line = Format(Line("separable"), binary);

            Assert.Equal("separable =", line);
            Assert.Empty(ReadBack(line));
        }
    }

    [Theory]
    [InlineData("ol.new")]
    [InlineData("OL.delete")]
    [InlineData(";x")]
    [InlineData("@x")]
    [InlineData("[x")]
    [InlineData("a=b")]
    [InlineData("a;b")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    [InlineData(" a")]
    [InlineData("a\t")]
    [InlineData("näme")]
    [InlineData("")]
    public void ANameThatAPatchLineCannotHoldIsRefused(string name)
    {
        foreach (var binary in BothFormats)
            Error(Line(name, "v"), binary);
    }

    [Fact]
    public void ADirectiveLineHoldsTextsThatTheMergeReadsBack()
    {
        Assert.True(PatchFormatter.TryFormatDirective("ol.remove", ["addon", "ids.name", "data path"], out var line, out var error), error);
        Assert.Equal("ol.remove = addon, ids.name, data path", line);
        // The merge compares the text of the value without letter case, so a key "true" reads back as "True".
        Assert.True(PatchFormatter.TryFormatDirective("ol.parent", ["true"], out line, out error), error);
        Assert.Equal("ol.parent = true", line);
    }

    [Theory]
    [InlineData("0.50", "the merge does not read back the same text")]
    [InlineData("a,b", "cannot contain ','")]
    [InlineData(" a", "cannot start or end with white space")]
    [InlineData("", "cannot be empty")]
    public void ADirectiveTextThatTheMergeCannotReadBackIsRefused(string text, string reason)
    {
        Assert.False(PatchFormatter.TryFormatDirective("ol.parent", [text], out var line, out var error), line);
        Assert.Contains(reason, error);
    }

    [Theory]
    [InlineData("Ship", true)]
    [InlineData("Exclusion Zones", true)]
    [InlineData("a]b", false)]
    [InlineData("a[b", false)]
    [InlineData(" Ship", false)]
    [InlineData("Ship\t", false)]
    [InlineData("Schiffé", false)]
    [InlineData("", false)]
    public void ASectionHeaderHoldsOnlyANameThatTheParserReadsBack(string name, bool accepted)
    {
        Assert.Equal(accepted, PatchFormatter.TryFormatHeader(name, out var line, out _));
        if (accepted)
            Assert.Equal($"[{name}]", line);
    }

    [Fact]
    public void ANameCanHoldInnerSpacesAndDots()
    {
        Assert.Equal("data path = ..\\data", Format(Line("data path", "..\\data"), binary: false));
        Assert.Equal("ids.name = 5", Format(Line("ids.name", new Int32Value(5)), binary: true));
    }

    /// <summary>
    /// Each formatted line goes through the real merge over the line that it was made from. A binary file must
    /// then store the same values, with the same types and bits.
    /// </summary>
    [Fact]
    public void ALineThatReplacesItsOwnValuesGivesThemBackThroughTheMerge()
    {
        using var ws = new TestWorkspace();
        var original = new Section("Ship")
        {
            Line("nickname", "s1"),
            Line("flags", "TRUE", "false", "yes"),
            Line("numbers", new SingleValue(100f, null), new SingleValue(-0f, null), new SingleValue(0.1f, null)),
            Line("hash", new Int32Value(-1980013182), new Int32Value(7)),
            Line("separable"),
            Line("names", "a", "", "b"),
        };
        ws.WriteBaseBini(@"DATA\ships.ini", original);
        var patch = "[Ship]\r\nnickname = s1\r\n" +
                    string.Join("\r\n", original.Skip(1).Select(e => Format(e, binary: true)));

        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\ships.ini"] = patch }));

        Assert.Empty(overlay.Diagnostics);
        AssertStrictEqual(original, TestWorkspace.Sections(overlay, @"DATA\ships.ini").Single());
    }

    /// <summary>
    /// Each line of a new section is formatted with no replaced line and goes through the real merge. A binary file
    /// must then store the same values, with the same types and bits. This is how a copy of a retail item works.
    /// </summary>
    [Fact]
    public void ANewSectionGivesBackItsValuesThroughTheMerge()
    {
        using var ws = new TestWorkspace();
        ws.WriteBaseBini(@"DATA\ships.ini", new Section("Ship") { Line("nickname", "s1") });
        var copy = new Section("Ship")
        {
            Line("nickname", "s2"),
            Line("flags", "true", "false"),
            Line("numbers", new SingleValue(100f, null), new SingleValue(-0f, null), new SingleValue(0.1f, null)),
            Line("hash", new Int32Value(unchecked((int)2314954114u), unsigned: true), new Int32Value(-1980013182)),
            Line("separable"),
            Line("names", "a", "", "b"),
        };
        var patch = "[Ship]\r\nnickname = s2\r\nol.new = true\r\n" +
                    string.Join("\r\n", copy.Skip(1).Select(e => Format(e, replaced: null, binary: true)));

        var overlay = ws.Build(ws.Mod("m", new() { [@"patches\DATA\ships.ini"] = patch }));

        Assert.Empty(overlay.Diagnostics);
        AssertStrictEqual(copy, TestWorkspace.Sections(overlay, @"DATA\ships.ini")[1]);
    }

    private static void AssertStrictEqual(Section expected, Section merged)
    {
        Assert.Equal(expected.Select(e => e.Name), merged.Select(e => e.Name));
        foreach (var (before, after) in expected.Zip(merged))
        {
            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++)
                Assert.True(IniValueComparer.StrictEqual(before[i], after[i], binary: true), $"{before} became {after}");
        }
    }

    private static Entry Line(string name, params ValueBase[] values)
    {
        var entry = new Entry(new Section("Test"), name);
        foreach (var value in values)
            entry.Add(value);
        return entry;
    }

    /// <summary>Formats a line that replaces a target line with the same values.</summary>
    private static string Format(Entry entry, bool binary) => Format(entry, entry, binary);

    private static string Format(Entry entry, Entry? replaced, bool binary)
    {
        Assert.True(PatchFormatter.TryFormatLine(entry, replaced, binary, out var line, out var error), error);
        return line;
    }

    /// <summary>The refusal for a line that replaces a target line with the same values.</summary>
    private static string Error(Entry entry, bool binary) => Error(entry, entry, binary);

    private static string Error(Entry entry, Entry? replaced, bool binary)
    {
        Assert.False(PatchFormatter.TryFormatLine(entry, replaced, binary, out var line, out var error), line);
        return error;
    }

    /// <summary>Parses one patch line with the engine's text parser.</summary>
    private static Entry ReadBack(string line) =>
        IniFile.ParseFile("test.ini", new MemoryStream(Encoding.ASCII.GetBytes("[Test]\r\n" + line))).Single().Single();

}

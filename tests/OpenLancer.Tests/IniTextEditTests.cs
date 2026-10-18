using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>
/// The text-preserving writer of the export (section 7.3 of the M11 design): only the changed lines change. The other
/// lines keep their text, their comments and their line ends.
/// </summary>
public sealed class IniTextEditTests
{
    private const string Base = """
        ; The goods.
        [Good]
        nickname = a   ; the first good
          price = 100 ; old price
        volume = 1

        ; The second good.
        [Good]
        nickname = b
        price = 200

        """;

    /// <summary>Writes the merged text on the raw text of the base. The base text keeps its line ends.</summary>
    private static IniTextEdit.Result Edit(string before, IniDocument merged) =>
        Edit(IniDocument.TextEncoding.GetBytes(before), merged);

    private static IniTextEdit.Result Edit(byte[] bytes, IniDocument merged)
    {
        var document = IniDocument.Parse("t.ini", bytes);
        return IniTextEdit.Write(bytes, document, merged, IniSectionDiff.Compare(document.Sections, merged.Sections));
    }

    private static string Edited(string before, string after, string lineEnd = "\r\n")
    {
        var result = Edit(before.Replace("\n", lineEnd), Parse(after));
        Assert.Null(result.Fallback);
        return Text(result.Bytes);
    }

    private static string Crlf(string text) => text.Replace("\n", "\r\n");

    [Fact]
    public void AChangedValueKeepsTheCommentsAndTheOtherLines()
    {
        var text = Edited(Base, Base.Replace("price = 100", "price = 150"));

        Assert.Equal(Crlf(Base.Replace("  price = 100 ; old price", "  price = 150 ; old price")), text);
    }

    [Fact]
    public void TheLineEndsOfTheFileStay()
    {
        Assert.Equal(Base.Replace("price = 200", "price = 250"), Edited(Base, Base.Replace("price = 200", "price = 250"), "\n"));
        Assert.Equal(Crlf(Base.Replace("price = 200", "price = 250")), Edited(Base, Base.Replace("price = 200", "price = 250")));
    }

    [Fact]
    public void AKeyWithoutEqualsKeepsItsPlace()
    {
        const string before = "[Mission]\nnickname = m1\nautoselect\nlevel = 1\n";

        var text = Edited(before, before.Replace("level = 1", "level = 2"));

        Assert.Equal(Crlf("[Mission]\nnickname = m1\nautoselect\nlevel = 2\n"), text);
    }

    [Fact]
    public void ALineAfterALastLineWithoutALineEnd()
    {
        const string before = "[Good]\nnickname = a\nprice = 1";

        var text = Edited(before, before + "\nvolume = 2");

        Assert.Equal(Crlf("[Good]\nnickname = a\nprice = 1\nvolume = 2\n"), text);
    }

    [Fact]
    public void AnInsertedLineGoesAfterTheLineBeforeItWithItsIndent()
    {
        var text = Edited(Base, Base.Replace("volume = 1", "volume = 1\nids_name = 7"));

        Assert.Contains(Crlf("volume = 1\nids_name = 7\n\n; The second good."), text);
    }

    [Fact]
    public void ANewSectionGoesAfterTheSectionBeforeIt()
    {
        var after = Base.Replace("; The second good.", "[Good]\nnickname = n\nprice = 5\n\n; The second good.");

        var text = Edited(Base, after);

        Assert.Equal(Crlf(Base.Replace("volume = 1\n", "volume = 1\n\n[Good]\nnickname = n\nprice = 5\n")), text);
    }

    [Fact]
    public void ARemovedSectionLosesItsLinesAndKeepsTheNextComment()
    {
        const string before = "[Good]\nnickname = a\nprice = 1\n\n; The good b.\n[Good]\nnickname = b\n";

        var text = Edited(before, "[Good]\nnickname = b\n");

        Assert.Equal(Crlf("; The good b.\n[Good]\nnickname = b\n"), text);
    }

    [Fact]
    public void AByteOrderMarkGivesCanonicalText()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(Base)];
        var result = Edit(bytes, Parse(Base.Replace("price = 200", "price = 250")));

        Assert.Equal("the file starts with a byte order mark", result.Fallback);
        Assert.DoesNotContain("; The goods.", Text(result.Bytes));
        Assert.Contains("price = 250", Text(result.Bytes));
    }

    [Fact]
    public void AFileWithMixedLineEndsGivesCanonicalText()
    {
        var result = Edit("[Good]\r\nnickname = a\nprice = 1\r\n", Parse("[Good]\nnickname = a\nprice = 2\n"));

        Assert.Equal("the file mixes line ends", result.Fallback);
        Assert.Equal(Crlf("[Good]\nnickname = a\nprice = 2\n"), Text(result.Bytes));
    }

    [Fact]
    public void AnEditThatDoesNotParseBackFallsBackToCanonicalText()
    {
        // A value with ';' reads back without the text after ';', so the edited text gives other sections.
        var merged = Parse(Base);
        var section = merged.Sections[1];
        var line = new Entry(section, "price", 1);
        line.Add(new StringValue("1;2") { Entry = line });
        section.Remove(section.First(e => e.Name == "price"));
        section.Add(line);

        var result = Edit(Base.Replace("\n", "\r\n"), merged);

        Assert.Equal("the edited text does not give the merged sections", result.Fallback);
    }

    [Fact]
    public void AHashKeepsItsUnsignedNumber()
    {
        const string before = "[locked_gates]\nlocked_gate = 1\n";
        var merged = Parse(before);
        var section = merged.Sections[0];
        var line = new Entry(section, "locked_gate", 1);
        line.Add(new SingleValue(2926089285f, 2926089285) { Entry = line });
        section.Clear();
        section.Add(line);

        var result = Edit(before, merged);

        Assert.Null(result.Fallback);
        Assert.Equal("[locked_gates]\nlocked_gate = 2926089285\n", Text(result.Bytes));
    }

    [Fact]
    public void ACodePage1252CommentStays()
    {
        // 0x92 is a right single quote in code page 1252.
        var before = "[Good]\nnickname = a ; the hull’s good\nprice = 1\n";

        var text = Edited(before, before.Replace("price = 1", "price = 2"), "\n");

        Assert.Equal(before.Replace("price = 1", "price = 2"), text);
        Assert.Contains((byte)0x92, IniDocument.TextEncoding.GetBytes(text));
    }
}

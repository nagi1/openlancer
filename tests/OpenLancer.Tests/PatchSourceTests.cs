using System.Linq;
using System.Text;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The loaded patch file: how it splits into lines and parts, and which loaded sections the writer finds for a
/// section.
/// </summary>
public sealed class PatchSourceTests
{
    [Fact]
    public void EachLineKeepsItsTextAndBelongsToAPart()
    {
        // CR, LF and CRLF line ends, and bytes outside ASCII in comments. Code page 1252 does not define the byte 0x81.
        var data = Bytes("; file ~\r\n\r\n; about a\r[Good] ; ~\n; above nickname\nnickname = a\r\n@note\nprice = 5 ; ~\r\n" +
                         "\r\n; end of a\r\n\r\n; about b\r\n[Good]\r\nnickname = b\r\n\r\n; end ~", 0x81, 0x89, 0xE9, 0xE9);

        var source = PatchSource.Parse("patch.ini", data);

        Assert.Equal(1252, source.Encoding.CodePage);
        Assert.Equal([Text("; file ~", 0x81), ""], source.Header);
        var a = source.Existing("good", "A")!;
        Assert.Equal(["; about a"], a.Leading);
        Assert.Equal(Text("[Good] ; ~", 0x89), a.Header);
        Assert.Equal(["nickname", "price"], a.Lines.Select(l => l.Parsed.Name));
        Assert.Equal(["; above nickname"], a.Lines[0].Above);
        Assert.Equal(["@note"], a.Lines[1].Above);
        Assert.Equal(Text("price = 5 ; ~", 0xE9), a.Lines[1].Text);
        // The blank line directly above the leading lines of b ends the tail of a.
        Assert.Equal(["", "; end of a", ""], a.Tail);
        var b = source.Existing("Good", "b")!;
        Assert.Equal(["; about b"], b.Leading);
        Assert.Empty(b.Tail);
        Assert.Equal(["", Text("; end ~", 0xE9)], source.Footer);
        Assert.Equal(source.Encoding.GetString(data).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'),
            source.Header.Concat(a.AllLines).Concat(b.AllLines).Concat(source.Footer));
    }

    [Fact]
    public void AByteOrderMarkGivesTheEncodingThatTheParserReadsTheFileWith()
    {
        const string text = "[Good]\r\nnickname = a\r\nicon = café ; ü\r\n";
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

        foreach (var (data, encoding) in new[] { (utf8, Encoding.UTF8), (utf16, Encoding.Unicode) })
        {
            var source = PatchSource.Parse("patch.ini", data);

            Assert.Equal(encoding.WebName, source.Encoding.WebName);
            Assert.Equal(encoding.GetPreamble(), source.Encoding.GetPreamble());
            Assert.Equal("café", source.Sections[0]["icon"]![0].ToString());
            Assert.Equal("icon = café ; ü", source.Existing("Good", "a")!.Lines[1].Text);
        }
    }

    [Fact]
    public void AnEntryWithoutAnEqualsSignGetsItsLine()
    {
        var source = PatchSource.Parse("patch.ini", Encoding.ASCII.GetBytes("[Good]\nnickname = a\nseparable ; x\nprice = 1\n"));

        var lines = source.Existing("Good", "a")!.Lines;
        Assert.Equal(["nickname", "separable", "price"], lines.Select(l => l.Parsed.Name));
        Assert.Equal("separable ; x", lines[1].Text);
    }

    [Fact]
    public void TheLoadedSectionsThatChangeOneSectionJoin()
    {
        var source = PatchSource.Parse("patch.ini", Encoding.ASCII.GetBytes("""
            [Good]
            nickname = a
            price = 1 ; first

            ; second part
            [Good] ; dropped
            NICKNAME = A ; dropped
            price = 2 ; wins
            ids_name = 3
            """));

        var a = source.Existing("Good", "a")!;

        // The header line and the key line of the second part go. Its other lines stay with their parts.
        Assert.Equal("[Good]", a.Header);
        Assert.Equal(["nickname = a", "price = 1 ; first", "price = 2 ; wins", "ids_name = 3"], a.Lines.Select(l => l.Text));
        Assert.Equal([0, 0, 1, 1], a.Lines.Select(l => l.Part));
        Assert.Equal(["", "; second part"], a.Lines[2].Above);
        Assert.Null(source.Added("Good", "a", 0));
    }

    [Fact]
    public void ADeleteEndsAGroupAndAnAddStartsOne()
    {
        var source = PatchSource.Parse("patch.ini", Encoding.ASCII.GetBytes("""
            [Good]
            nickname = b
            ol.delete = true
            [Good]
            nickname = b
            ol.new = true
            price = 9
            [Good]
            nickname = c
            ol.new = true
            [Good]
            nickname = c
            ol.delete = true
            """));

        Assert.Equal(["nickname", "ol.delete"], source.Existing("Good", "b")!.Lines.Select(l => l.Parsed.Name));
        Assert.Equal(["nickname", "ol.new", "price"], source.Added("Good", "b", 0)!.Lines.Select(l => l.Parsed.Name));
        Assert.Null(source.Added("Good", "b", 1));
        // A section that the patch adds and deletes again gives nothing.
        Assert.Null(source.Added("Good", "c", 0));
        Assert.Null(source.Existing("Good", "c"));
    }

    [Fact]
    public void ASectionWithoutAKeyMatchesByItsPositionAndAChildOfANewParentIsAdded()
    {
        var source = PatchSource.Parse("patch.ini", Encoding.ASCII.GetBytes("""
            [SystemInfo]
            local_faction = x
            [BaseGood]
            base = first
            [BaseGood]
            base = second
            [MBase]
            nickname = m
            ol.new = true
            [GF_NPC]
            nickname = n
            [Ship]
            nickname = s
            ol.new = true
            [CollisionGroup]
            obj = x
            """));

        Assert.Equal("local_faction = x", Assert.Single(source.Existing("systeminfo", null)!.Lines).Text);
        Assert.Equal("base = second", Assert.Single(source.Added("BaseGood", null, 1)!.Lines).Text);
        Assert.Null(source.Added("BaseGood", null, 2));
        Assert.Equal("nickname = n", Assert.Single(source.Added("GF_NPC", "n", 0)!.Lines).Text);
        Assert.Null(source.Existing("GF_NPC", "n"));
        Assert.Equal("obj = x", Assert.Single(source.Added("CollisionGroup", null, 0)!.Lines).Text);
        Assert.Null(source.Existing("Ship", "s"));
    }

    /// <summary>ASCII text in which each '~' stands for the next of the bytes.</summary>
    internal static byte[] Bytes(string text, params byte[] bytes)
    {
        var next = 0;
        return text.SelectMany(c => c == '~' ? [bytes[next++]] : Encoding.ASCII.GetBytes(c.ToString())).ToArray();
    }

    /// <summary>The text of <see cref="Bytes"/> in code page 1252.</summary>
    private static string Text(string text, params byte[] bytes) => IniDocument.TextEncoding.GetString(Bytes(text, bytes));
}

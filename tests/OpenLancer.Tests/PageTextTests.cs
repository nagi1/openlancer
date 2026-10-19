using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The text format of the description (decision D564 and section 8.4 of the M11c design).</summary>
public sealed class PageTextTests
{
    private const string Source = """
        # What it changes
        - New ship at the **Manhattan** dealer.
        * New text for the ship.

        ## Install
        1. Pick the package.
        2. Read the [forum thread](https://example.org/thread).
        """;

    [Fact]
    public void HeadingsListsBoldAndLinksGiveNexusBBCode()
    {
        Assert.Equal("""
            [size=5][b]What it changes[/b][/size]
            [list]
            [*]New ship at the [b]Manhattan[/b] dealer.
            [*]New text for the ship.
            [/list]

            [size=4][b]Install[/b][/size]
            [list=1]
            [*]Pick the package.
            [*]Read the [url=https://example.org/thread]forum thread[/url].
            [/list]
            """.ReplaceLineEndings("\n"), PageText.BBCode(Source));
    }

    [Fact]
    public void PlainTextKeepsTheListsAndDropsTheMarks()
    {
        Assert.Equal("""
            What it changes

            - New ship at the Manhattan dealer.
            - New text for the ship.

            Install
            1. Pick the package.
            2. Read the forum thread (https://example.org/thread).
            """.ReplaceLineEndings("\n"), PageText.PlainText(Source));
    }

    [Fact]
    public void PlainTextDropsAPassedThroughTagAndKeepsItsText()
    {
        Assert.Equal("A red word here.", PageText.PlainText("A [color=red]red[/color] word [u]here[/u]."));
        // A text in brackets that is no known tag stays.
        Assert.Equal("Note [1] and [todo].", PageText.PlainText("Note [1] and [todo]."));
    }

    [Fact]
    public void AsciiChangesQuotesDashesAndTheEllipsis()
    {
        var text = $"{(char)0x201C}Fast{(char)0x201D} {(char)0x2013} it{(char)0x2019}s quick{(char)0x2026}" +
                   $"{(char)0x00A0}{(char)0x2014}\tend";
        var lost = new List<string>();

        Assert.Equal("\"Fast\" - it's quick... -    end", PageText.ToAscii(text, lost));
        Assert.Empty(lost);
    }

    [Fact]
    public void AnotherCharacterGivesAWarningAndAQuestionMark()
    {
        var lost = new List<string>();
        var text = $"Caf{(char)0xE9} {(char)0xE9} {char.ConvertFromUtf32(0x1F680)}";

        Assert.Equal("Caf? ? ?", PageText.ToAscii(text, lost));
        Assert.Equal([((char)0xE9).ToString(), char.ConvertFromUtf32(0x1F680)], lost);
        Assert.Equal("The FLMM package cannot show the character 'x' in the summary. FLMM shows '?'.",
            PublishMessages.FlmmCharacter("x", "the summary"));
    }

    [Fact]
    public void ALinkThatIsNotHttpStaysText()
    {
        var blocks = PageText.Parse("See [the file](file:///c/x) now.");

        Assert.Equal("See [the file](file:///c/x) now.", PageText.BBCode("See [the file](file:///c/x) now."));
        Assert.Equal("See [the file](file:///c/x) now.", PageText.PlainText("See [the file](file:///c/x) now."));
        Assert.Contains(blocks[0].Spans, s => s.BadLink && s.Kind == PageSpanKind.Text);
    }

    [Fact]
    public void AnUnpairedBoldStaysText()
    {
        Assert.Equal("A ** star and **** two.", PageText.PlainText("A ** star and **** two."));
        Assert.Equal("[b]bold[/b] and 2 ** 3", PageText.BBCode("**bold** and 2 ** 3"));
    }

    [Fact]
    public void ABBCodeTagPassesThroughToBBCode()
    {
        var blocks = PageText.Parse("A [color=red]red[/color] word.");

        Assert.Equal("A [color=red]red[/color] word.", PageText.BBCode("A [color=red]red[/color] word."));
        Assert.Equal(2, blocks[0].Spans.Count(s => s.Kind == PageSpanKind.Tag));
    }

    [Fact]
    public void ControlCharactersGoAndEmptyEdgeLinesGo()
    {
        var text = $"\n\nLine{(char)7} one\r\n\r\nLine two\n\n";

        Assert.Equal("Line one\n\nLine two", PageText.PlainText(text));
        Assert.Empty(PageText.Parse("  \n "));
    }
}

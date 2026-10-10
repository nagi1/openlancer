using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The Console report of a save.</summary>
public sealed class SaveReportTests
{
    private static readonly string ModFolder = Path.Combine(Path.GetTempPath(), "mods", "proof");

    private static string File(string relative) => Path.Combine(ModFolder, relative);

    [Fact]
    public void ASaveListsTheFilesThatItWroteAndDeletedWithTheNumberOfTheSave()
    {
        var target = new SectionRef(@"DATA\EQUIPMENT\goods.ini", new SectionIdentity("Good", "g1"));
        var result = new SaveResult(true, [File(@"patches\DATA\EQUIPMENT\goods.ini"), File("strings.json")],
            [File(@"patches\DATA\SHIPS\shiparch.ini")],
            [Diagnostic.Warning("goods.ini", "a warning") with { Target = target }], []);

        var lines = SaveReport.Lines(result, "proof", ModFolder, 3);

        Assert.Equal(
        [
            "Saved mod 'proof' (save 3): 2 files written, 1 file deleted.",
            @"Wrote patches\DATA\EQUIPMENT\goods.ini",
            "Wrote strings.json",
            @"Deleted patches\DATA\SHIPS\shiparch.ini",
            "a warning",
        ], lines.Select(l => l.Message));
        Assert.Equal(ConsoleSeverity.Warning, lines[^1].Severity);
        Assert.Equal(target, lines[^1].Target);
        Assert.All(lines.SkipLast(1), l => Assert.Equal(ConsoleSeverity.Info, l.Severity));
    }

    [Fact]
    public void ASaveThatWroteNothingSaysSoAndListsItsErrors()
    {
        var result = new SaveResult(false, [], [],
            [Diagnostic.Error("goods.ini", "first"), Diagnostic.Error("shiparch.ini", "second")], []);

        var lines = SaveReport.Lines(result, "proof", ModFolder, 0);

        Assert.Equal("The save of mod 'proof' wrote nothing: 2 errors. The edits are not saved.", lines[0].Message);
        Assert.All(lines, l => Assert.Equal(ConsoleSeverity.Error, l.Severity));
        Assert.Equal(["goods.ini", "shiparch.ini"], lines.Skip(1).Select(l => l.Source));
    }

    [Fact]
    public void ASaveThatChangedNoFileHasNoNumber()
    {
        var lines = SaveReport.Lines(new SaveResult(true, [], [], [], []), "proof", ModFolder, 0);

        Assert.Equal("Saved mod 'proof'. No file changed.", Assert.Single(lines).Message);
    }
}

using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The Console report of a save, and the summary that the status bar shows after each save.</summary>
public sealed class SaveReportTests
{
    private static readonly string ModFolder = Path.Combine(Path.GetTempPath(), "mods", "proof");

    private static string File(string relative) => Path.Combine(ModFolder, relative);

    [Fact]
    public void ASaveListsTheFilesThatItWroteAndDeletedAndThenTheSummaryWithTheNumberOfTheSave()
    {
        var target = new SectionRef(@"DATA\EQUIPMENT\goods.ini", new SectionIdentity("Good", "g1"));
        var result = new SaveResult(true, [File(@"patches\DATA\EQUIPMENT\goods.ini"), File("strings.json")],
            [File(@"patches\DATA\SHIPS\shiparch.ini")],
            [Diagnostic.Warning("goods.ini", "a warning") with { Target = target }], []);

        var lines = SaveReport.Lines(result, ModFolder);
        var summary = SaveReport.Summary(result, "proof", 3, hadEdits: true);

        Assert.Equal(
        [
            @"Wrote patches\DATA\EQUIPMENT\goods.ini",
            "Wrote strings.json",
            @"Deleted patches\DATA\SHIPS\shiparch.ini",
            "a warning",
        ], lines.Select(l => l.Message));
        Assert.Equal(ConsoleSeverity.Warning, lines[^1].Severity);
        Assert.Equal(target, lines[^1].Target);
        Assert.All(lines.SkipLast(1), l => Assert.Equal(ConsoleSeverity.Info, l.Severity));
        Assert.Equal((ConsoleSeverity.Info, SaveReport.Source, "Saved mod 'proof' (save 3): 2 files written, 1 file deleted."),
            (summary.Severity, summary.Source, summary.Message));
    }

    [Fact]
    public void ASaveThatWroteNothingListsItsErrorsAndTheSummaryCountsThem()
    {
        var result = new SaveResult(false, [], [],
            [Diagnostic.Error("goods.ini", "first"), Diagnostic.Error("shiparch.ini", "second")], []);

        var lines = SaveReport.Lines(result, ModFolder);
        var summary = SaveReport.Summary(result, "proof", 0, hadEdits: true);

        Assert.All(lines, l => Assert.Equal(ConsoleSeverity.Error, l.Severity));
        Assert.Equal(["goods.ini", "shiparch.ini"], lines.Select(l => l.Source));
        Assert.Equal(ConsoleSeverity.Error, summary.Severity);
        Assert.Equal("The editor did not save mod 'proof' because of 2 errors. The Console lists them.", summary.Message);
    }

    /// <summary>The Overwrite dialog follows a save that stops at files that changed outside the editor.</summary>
    [Fact]
    public void ASaveThatStopsAtChangedFilesPointsToTheOverwriteDialog()
    {
        var path = File(@"patches\DATA\EQUIPMENT\goods.ini");
        var result = new SaveResult(false, [], [], [Diagnostic.Error(path, "changed")], [path]);

        var summary = SaveReport.Summary(result, "proof", 0, hadEdits: true);

        Assert.Equal(ConsoleSeverity.Error, summary.Severity);
        Assert.Equal("The editor did not save mod 'proof'. Something outside the editor changed 1 file of the mod. " +
                     "Select Overwrite or Cancel in the dialog.", summary.Message);
    }

    [Fact]
    public void ASaveThatChangedNoFileHasNoNumber()
    {
        var result = new SaveResult(true, [], [], [], []);

        Assert.Empty(SaveReport.Lines(result, ModFolder));
        Assert.Equal("Saved mod 'proof'. No file changed.", SaveReport.Summary(result, "proof", 0, hadEdits: true).Message);
    }

    /// <summary>Ctrl+S without unsaved edits tells so, because the user can expect a change.</summary>
    [Fact]
    public void ASaveWithoutUnsavedEditsSaysSo()
    {
        var summary = SaveReport.Summary(new SaveResult(true, [], [], [], []), "proof", 0, hadEdits: false);

        Assert.Equal((ConsoleSeverity.Info, "Mod 'proof' has no unsaved edits. No file changed."),
            (summary.Severity, summary.Message));
    }

    /// <summary>A save that does not start tells why: no profile, Mod > None, or a session that takes no edits.</summary>
    [Fact]
    public void ASaveThatDoesNotStartTellsWhy()
    {
        Assert.Equal("No profile is open, so there is nothing to save.", SaveReport.NoProfile);
        Assert.Equal("The editor did not save. Mod > None is read-only. Select a mod in the Mod menu, then save again.",
            SaveReport.NoActiveMod);
        Assert.Equal("The editor did not save mod 'proof'. A transaction is open.",
            SaveReport.NotStarted("proof", "A transaction is open."));
    }

    /// <summary>The write guard makes a session with its active mod in the game folder read-only. The summary of a save
    /// gives the reason of the guard.</summary>
    [Fact]
    public void ASaveOfAModInTheGameFolderGivesTheReasonOfTheWriteGuard()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"mods\act\mod.json", """{ "id": "act", "formatVersion": 1 }""");
        Directory.CreateDirectory(Path.GetDirectoryName(ws.ProfilePath)!);
        System.IO.File.WriteAllText(ws.ProfilePath, """{ "base": "../base", "mods": [ "../base/mods/act" ] }""");
        using var session = EditSession.Open(ModProfile.Load(ws.ProfilePath), "act", TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator);

        var summary = SaveReport.ReadOnly(session.ActiveMod!.Id, session.ReadOnlyReason!);

        Assert.Equal($"The editor did not save mod 'act' because it is read-only. {GameTexts.ActiveModInGame}", summary);
    }
}

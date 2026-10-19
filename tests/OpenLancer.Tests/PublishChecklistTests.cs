using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The publish checklist (section 11 of the M11c design). Each test starts from a mod with each step complete
/// and takes one part away.</summary>
public sealed class PublishChecklistTests
{
    private const string Content = "content";
    private const string Page = "page";
    private const string Base = @"C:\Games\Freelancer|vanilla-1.0";
    private static readonly DateTime Monday = new(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc);

    private static ModManifest Manifest(string name = "Starrunner", string version = "1.0.0", params string[] targets) =>
        new("ol-star", name, version, [], [])
        {
            Targets = targets.Length == 0 ? [ModTargets.Vanilla, ModTargets.OpenLancer] : targets,
        };

    private static PublishSettings Settings => PublishSettings.Empty with
    {
        Summary = "A fast light fighter.",
        Description = "# The ship\nA fast ship.",
        Authors = ["Example Modder"],
        Screenshots = [new PublishScreenshot("publish/screenshots/a.png", "")],
        Changelog = [new ChangelogEntry("1.0.0", ["First release."])],
    };

    /// <summary>A history with a check of each target, a test play and an export of each target.</summary>
    private static PublishHistory History(ModManifest manifest)
    {
        var history = new PublishHistory();
        foreach (var target in manifest.Targets)
            history.SetCheck(new HistoryCheck(target, Monday, Content, target == ModTargets.OpenLancer ? "" : Base, 0, 1));
        history.AddPlay(new HistoryPlay(Monday, ModTargets.Vanilla, Content, true, 120, false));
        history.RecordExport(new HistoryExport(manifest.Version, Monday, "test", Content, Page, @"C:\out",
            [.. manifest.Targets.Select(t => new HistoryPackage(t, PackageForms.DropIn, $"{t}.zip", "s-" + t, "g"))]));
        return history;
    }

    private static PublishChecklist Of(ModManifest? manifest = null, PublishSettings? settings = null,
        PublishHistory? history = null, Func<ExportTarget, string?>? baseOf = null, string content = Content)
    {
        manifest ??= Manifest();
        return PublishChecklist.Of(new ChecklistInput(manifest, settings ?? Settings, history ?? History(manifest), content,
            Page, baseOf ?? (_ => Base), [ExportTarget.Vanilla]));
    }

    private static ChecklistStep Step(PublishChecklist checklist, string name) => checklist.Steps.Single(s => s.Name == name);

    [Fact]
    public void AFullModHasSixOfSixSteps()
    {
        var checklist = Of();

        Assert.Equal(["Display name", "Summary", "Version", "Checks for each target", "Test play in the game", "Export made"],
            checklist.Steps.Select(s => s.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6], checklist.Steps.Select(s => s.Number));
        Assert.True(checklist.AllComplete, string.Join("\n", checklist.Steps.SelectMany(s => s.Missing)));
        Assert.Equal("6 of 6 steps complete", checklist.Summary);
        Assert.All(checklist.Steps, s => Assert.Empty(s.Buttons));
    }

    [Fact]
    public void AnOpenLancerOnlyModHasFiveSteps()
    {
        var checklist = Of(Manifest(targets: ModTargets.OpenLancer));

        Assert.DoesNotContain(checklist.Steps, s => s.Name == "Test play in the game");
        Assert.Equal("5 of 5 steps complete", checklist.Summary);
    }

    [Fact]
    public void TheStarterNameAndAnEmptyNameAreMissing()
    {
        var starter = Step(Of(Manifest(ModWorkspace.StarterModName)), "Display name");
        var empty = Step(Of(Manifest("")), "Display name");

        Assert.Equal([PublishMessages.StarterName], starter.Missing);
        Assert.Equal("Change the display name. OpenLancer gives the name \"My mod\" to the first mod of each new profile.",
            starter.Missing[0]);
        Assert.Equal([PublishMessages.DisplayNameEmpty], empty.Missing);
        Assert.Equal(ChecklistAction.GoTo, Assert.Single(starter.Buttons).Action);
        Assert.Equal(PublishField.DisplayName, starter.Buttons[0].Field);
    }

    [Fact]
    public void TheIdAsTheNameIsMissing()
    {
        var step = Step(Of(Manifest("ol-star")), "Display name");

        Assert.Equal(["Give the mod a name that players can read. The display name is now the mod id ol-star."], step.Missing);
    }

    [Fact]
    public void AnEmptySummaryIsMissing()
    {
        var step = Step(Of(settings: Settings with { Summary = " " }), "Summary");

        Assert.Equal(["Write a summary of one or two sentences. Nexus shows the summary below the name of the mod."], step.Missing);
        Assert.Equal(PublishField.Summary, Assert.Single(step.Buttons).Field);
    }

    [Fact]
    public void AVersionOutsideTheRuleOffersOnePointZero()
    {
        var step = Step(Of(Manifest(version: "1.0")), "Version");

        Assert.Equal(["Use the form 1.2.3. Players and Nexus compare versions in this form."], step.Missing);
        Assert.Equal(new ChecklistButton(ChecklistAction.SetVersion, "Use 1.0.0", Argument: "1.0.0"), step.Buttons[0]);
        Assert.Equal(ChecklistAction.GoTo, step.Buttons[1].Action);
    }

    [Fact]
    public void APublishedVersionNeedsAHigherVersion()
    {
        var manifest = Manifest();
        var history = History(manifest);
        history.Mark("1.0.0", Monday, "https://example.org/mods/1");
        history.RecordExport(new HistoryExport("1.0.1", Monday, "test", "other", Page, @"C:\out",
            [new HistoryPackage(ModTargets.Vanilla, PackageForms.DropIn, "x.zip", "s", "g")]));

        var step = Step(Of(manifest, history: history), "Version");

        Assert.Equal([PublishMessages.VersionPublished("1.0.0")], step.Missing);
        Assert.Equal(new ChecklistButton(ChecklistAction.SetVersion, "Increase to 1.0.2", Argument: "1.0.2"), step.Buttons[0]);
        Assert.True(Step(Of(Manifest(version: "1.0.3"), history: history), "Version").Complete);
    }

    [Fact]
    public void ACheckWithErrorsIsMissing()
    {
        var manifest = Manifest();
        var history = History(manifest);
        history.SetCheck(new HistoryCheck(ModTargets.Vanilla, Monday, Content, Base, 3, 0));

        var step = Step(Of(manifest, history: history), "Checks for each target");

        Assert.Equal(["The check of Freelancer 1.1 found 3 errors. The export stops with these errors."], step.Missing);
        Assert.Equal([ChecklistAction.ShowInConsole, ChecklistAction.CheckNow], step.Buttons.Select(b => b.Action));
    }

    [Fact]
    public void AnOldCheckDoesNotCount()
    {
        var step = Step(Of(content: "new content"), "Checks for each target");

        Assert.Equal(["The content changed after the last check of Freelancer 1.1. Check the current content.",
                      "The content changed after the last check of OpenLancer. Check the current content."], step.Missing);
        Assert.Equal(ChecklistAction.CheckNow, Assert.Single(step.Buttons).Action);
        var none = Step(Of(history: new PublishHistory()), "Checks for each target");
        Assert.Contains("OpenLancer did not check Freelancer 1.1 yet. Check the current content.", none.Missing);
    }

    [Fact]
    public void ACheckOfAnotherBaseDoesNotCount()
    {
        var step = Step(Of(baseOf: _ => @"D:\Other|vanilla-1.1"), "Checks for each target");

        Assert.Equal(["The linked game of Freelancer 1.1 changed after the last check. Check the current content."], step.Missing);
        Assert.True(Step(Of(baseOf: _ => Base.ToUpperInvariant()), "Checks for each target").Complete);
    }

    [Fact]
    public void ATargetWithoutABaseGivesGameLinkAndRemoveTarget()
    {
        var step = Step(Of(baseOf: _ => null), "Checks for each target");

        Assert.Equal(["Link a game folder of Freelancer 1.0 or 1.1 in File > Game link. The check needs the files of the game."],
            step.Missing);
        Assert.Equal(new ChecklistButton(ChecklistAction.GameLink, "Game link..."), step.Buttons[0]);
        Assert.Equal(new ChecklistButton(ChecklistAction.RemoveTarget, "Remove the target Freelancer 1.1",
            Argument: ModTargets.Vanilla), step.Buttons[1]);
    }

    [Fact]
    public void AMissingTestPlayOffersPlayAndMark()
    {
        var manifest = Manifest();
        var history = History(manifest);
        history.Plays.Clear();

        var step = Step(Of(manifest, history: history), "Test play in the game");

        Assert.Equal(["Play the current content in the real game once. OpenLancer cannot prove that the retail game " +
                      "accepts all data."], step.Missing);
        Assert.Equal([new ChecklistButton(ChecklistAction.Play, "Play in Freelancer 1.1", Argument: ModTargets.Vanilla),
                      new ChecklistButton(ChecklistAction.MarkAsTested, "Mark as tested")], step.Buttons);
    }

    [Fact]
    public void ATestPlayOfOlderContentDoesNotCount()
    {
        var manifest = Manifest();
        var history = History(manifest);
        history.Plays.Clear();
        history.AddPlay(new HistoryPlay(Monday, ModTargets.Vanilla, "older content", true, 300, false));
        history.AddPlay(new HistoryPlay(Monday, ModTargets.OpenLancer, Content, true, 300, false));

        Assert.False(Step(Of(manifest, history: history), "Test play in the game").Complete);
    }

    [Fact]
    public void AManualMarkCounts()
    {
        var manifest = Manifest();
        var history = History(manifest);
        history.Plays.Clear();
        history.AddPlay(new HistoryPlay(Monday, null, Content, false, 0, true));

        Assert.True(Step(Of(manifest, history: history), "Test play in the game").Complete);
    }

    [Fact]
    public void AnExportOfOlderTextsIsMissing()
    {
        var manifest = Manifest();
        var history = History(manifest);
        history.Exports[0] = history.Exports[0] with { Page = "older page" };

        var step = Step(Of(manifest, history: history), "Export made");

        Assert.Equal(["Export version 1.0.0. The step counts an export of the current content and texts, with a package " +
                      "for each target."], step.Missing);
        Assert.Equal(ChecklistAction.Export, Assert.Single(step.Buttons).Action);
    }

    [Fact]
    public void TwoExportsOfOneVersionTogetherCompleteStepSix()
    {
        var manifest = Manifest();
        var history = History(manifest);
        history.Exports.Clear();
        history.RecordExport(new HistoryExport("1.0.0", Monday, "test", Content, Page, @"C:\out",
            [new HistoryPackage(ModTargets.Vanilla, PackageForms.Flmm, "a.zip", "s1", "g1")]));
        var one = Step(Of(manifest, history: history), "Export made");
        history.RecordExport(new HistoryExport("1.0.0", Monday.AddHours(1), "test", Content, Page, @"C:\out",
            [new HistoryPackage(ModTargets.OpenLancer, PackageForms.ModFolder, "b.zip", "s2", "g2")]));

        Assert.False(one.Complete);
        Assert.True(Step(Of(manifest, history: history), "Export made").Complete);
        Assert.Equal(2, history.ValidExports("1.0.0").Count());
    }

    [Fact]
    public void RecommendedItemsDoNotCount()
    {
        var name = $"Fast {(char)0x2013} light {(char)0x263A}";
        var manifest = Manifest(name);
        var history = History(manifest);
        history.Mark("0.9.0", Monday, "");
        history.Exports[0] = history.Exports[0] with
        {
            Packages = [.. history.Exports[0].Packages, new HistoryPackage(ModTargets.Vanilla, PackageForms.Flmm, "f.zip", "s", "g")],
        };

        var checklist = Of(manifest, PublishSettings.Empty with { Summary = "A summary." }, history);

        Assert.True(checklist.AllComplete);
        Assert.Equal(
        [
            "Write a description.", "Name the authors.", "Write the changes of version 1.0.0.",
            "Add one screenshot or more for the Nexus page.", $"The FLMM package cannot show '{(char)0x263A}'. FLMM shows '?'.",
            "Add the page link after the upload.",
            "The FLMM package is a preview. Check it with FLMM before you publish it.",
        ], checklist.Recommended.Select(r => r.Text));
        Assert.Equal(ChecklistAction.AddImages, checklist.Recommended[3].Button!.Action);
        Assert.Null(checklist.Recommended[^1].Button);
        Assert.Empty(Of().Recommended);
    }

    [Fact]
    public void EachStepThatIsMissingCountsOnce()
    {
        var checklist = Of(Manifest(""), Settings with { Summary = "" }, new PublishHistory());

        Assert.Equal("1 of 6 steps complete", checklist.Summary);
        Assert.True(Step(checklist, "Version").Complete);
        Assert.Equal(new List<string> { "Display name", "Summary", "Checks for each target", "Test play in the game", "Export made" },
            checklist.Steps.Where(s => !s.Complete).Select(s => s.Name).ToList());
    }
}

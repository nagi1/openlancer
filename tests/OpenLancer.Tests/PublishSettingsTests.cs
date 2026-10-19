using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The file <c>publish.json</c> (decision D560 and section 8.1 of the M11c design).</summary>
public sealed class PublishSettingsTests
{
    private static string PublishPath(string folder) => Path.Combine(folder, PublishSettings.FileName);

    [Fact]
    public void AMissingFileGivesEmptySettings()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");

        var load = PublishSettings.Load(mod);

        Assert.Equal(PublishSettings.Empty, load.Settings);
        Assert.Empty(load.Problems);
        Assert.Null(load.ReadOnly);
        Assert.Null(load.Hash);
        Assert.Equal(PublishSettings.DefaultCategory, load.Settings.Category);
    }

    [Fact]
    public void UnknownFieldsStay()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(PublishPath(mod), """
            {
              "formatVersion": 1,
              "summary": "Old.",
              "later": { "kept": true }
            }
            """);

        PublishSettings.Write(mod, PublishSettings.Load(mod).Settings with { Summary = "New." });

        var root = JsonNode.Parse(File.ReadAllText(PublishPath(mod)))!.AsObject();
        Assert.True(root["later"]!["kept"]!.GetValue<bool>());
        Assert.Equal("New.", root["summary"]!.GetValue<string>());
        Assert.Equal("New.", PublishSettings.Load(mod).Settings.Summary);
    }

    [Fact]
    public void AFileWithACommentIsReadOnly()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(PublishPath(mod), "{ // a comment\n \"formatVersion\": 1, \"summary\": \"x\" }");

        var load = PublishSettings.Load(mod);

        Assert.Equal("publish.json: The file has a comment. Remove the comments, then try again.", load.ReadOnly);
        Assert.NotNull(load.Hash);
        // The read skips the comment, as the read of mod.json does, so the export keeps the texts.
        Assert.Equal("x", load.Settings.Summary);
        Assert.Empty(load.Problems);
        Assert.Throws<InvalidDataException>(() => PublishSettings.Write(mod, PublishSettings.Empty));
    }

    [Fact]
    public void AnotherFormatVersionIsReadOnly()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(PublishPath(mod), """{ "formatVersion": 2, "summary": "Newer." }""");

        var load = PublishSettings.Load(mod);

        Assert.Equal(PublishMessages.PublishJsonVersion(2), load.ReadOnly);
        Assert.Equal("OpenLancer cannot read publish.json version 2. It reads version 1. The window shows the settings " +
                     "read-only.", Assert.Single(load.Problems));
        Assert.Equal("Newer.", load.Settings.Summary);
    }

    [Fact]
    public void TheListsKeepTheirOrder()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var settings = PublishSettings.Empty with
        {
            Authors = ["Zed", "Ann", "Mia"],
            Tags = ["Ships", "Gameplay"],
            Credits = [new PublishCredit("Bo", "Tests"), new PublishCredit("Al", "Art")],
            Screenshots = [new PublishScreenshot("publish/screenshots/b.png", "B"), new PublishScreenshot("publish/screenshots/a.png", "A")],
            Changelog = [new ChangelogEntry("1.0.1", ["Two", "One"]), new ChangelogEntry("1.0.0", ["First release."])],
        };

        PublishSettings.Write(mod, settings);
        var load = PublishSettings.Load(mod).Settings;

        Assert.Equal(["Zed", "Ann", "Mia"], load.Authors);
        Assert.Equal(settings.Credits, load.Credits);
        Assert.Equal(settings.Screenshots, load.Screenshots);
        Assert.Equal(["1.0.1", "1.0.0"], load.Changelog.Select(e => e.Version));
        Assert.Equal(["Two", "One"], load.Changelog[0].Changes);
        Assert.True(load.SameField(settings, PublishField.Changelog));
    }

    [Fact]
    public void ABadFieldIsAProblemAndTheModStillLoads()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(PublishPath(mod), """{ "formatVersion": 1, "summary": 5, "tags": "Ships", "authors": [ "Ann" ] }""");

        var load = PublishSettings.Load(mod);

        Assert.Equal(2, load.Problems.Count);
        Assert.Contains("'summary' must be a text", load.Problems[0]);
        Assert.Contains("'tags' must be a list of texts", load.Problems[1]);
        Assert.Null(load.ReadOnly);
        Assert.Equal(["Ann"], load.Settings.Authors);
        Assert.Equal("", load.Settings.Summary);
        Assert.Equal("m", Mod.Load(mod).Id);
    }

    [Fact]
    public void ControlCharactersAreRemoved()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        var bell = (char)7;
        PublishWorkspace.Write(PublishPath(mod), JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            summary = $"One{bell}\nline.",
            description = $"First\tline\r\nSecond{bell} line",
            authors = new[] { $"A{bell}nn" },
            changelog = new[] { new { version = "1.0.0", changes = new[] { "Two\r\nlines" } } },
        }));

        var settings = PublishSettings.Load(mod).Settings;

        Assert.Equal("Oneline.", settings.Summary);
        Assert.Equal("Firstline\nSecond line", settings.Description);
        Assert.Equal(["Ann"], settings.Authors);
        Assert.Equal(["Two\nlines"], settings.Changelog[0].Changes);
    }

    [Fact]
    public void ModLoadIgnoresPublishJsonAndThePublishFolder()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(PublishPath(mod), "{ this is not JSON");
        PublishWorkspace.Screenshot(mod, "shot.png");

        var loaded = Mod.Load(mod);

        Assert.Empty(loaded.Files);
        Assert.Single(loaded.Patches);
        Assert.Contains(PublishSettings.FileName, PublishSettings.Load(mod).ReadOnly);
    }

    [Fact]
    public void TheChecksFollowTheRulesOfEachField()
    {
        var settings = PublishSettings.Empty with
        {
            Summary = new string('s', PublishLimits.SummaryLength + 1),
            Authors = [" "],
            Tags = ["Ships, Gameplay"],
            PageUrl = "http://example.org",
            Screenshots = [new PublishScreenshot("publish/screenshots/../../x.png", "")],
            Changelog = [new ChangelogEntry("1.0.0", []), new ChangelogEntry("1.0.0", []), new ChangelogEntry("1.0", [])],
        };

        var problems = settings.Check();

        Assert.Contains(problems, p => p is { Field: PublishField.Summary, Severity: DiagnosticSeverity.Warning } &&
                                       p.Message == PublishMessages.SummaryLong(PublishLimits.SummaryLength + 1));
        Assert.Contains(problems, p => p.Field == PublishField.Authors && p.Index == 0);
        Assert.Contains(problems, p => p.Field == PublishField.Tags && p.Message == PublishMessages.TagWithComma);
        Assert.Contains(problems, p => p.Field == PublishField.PageUrl && p.Message == PublishMessages.PageLink);
        Assert.Contains(problems, p => p.Field == PublishField.Screenshots &&
                                       p.Message == PublishMessages.ImageOutsideFolder("publish/screenshots/../../x.png"));
        Assert.Contains(problems, p => p is { Field: PublishField.Changelog, Index: 1 } && p.Message.Contains("two changelog entries"));
        Assert.Contains(problems, p => p is { Field: PublishField.Changelog, Index: 2, Severity: DiagnosticSeverity.Warning });
        Assert.Empty((PublishSettings.Empty with { PageUrl = "https://example.org/m" }).Check());
    }
}

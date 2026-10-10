using System;
using System.Linq;
using System.Text.Json;
using OpenLancer.Editor;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

public sealed class RecentProfilesTests
{
    private static readonly DateTime Saved = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MovesTheLastProfileOfAnOlderEditorIntoTheRecentList()
    {
        var settings = EditorSettings.Parse("""{ "LastProfile": "C:\\data\\sample.json", "UiScale": 1.5 }""", Saved);
        Assert.Equal([new RecentProfile("C:\\data\\sample.json", Saved)], settings.RecentProfiles);
        Assert.Equal(1.5f, settings.UiScale);
        // The settings do not write the old field again.
        Assert.DoesNotContain("LastProfile", settings.ToJson());
    }

    [Fact]
    public void KeepsAnExistingRecentListWhenTheFileAlsoHasALastProfile()
    {
        var settings = EditorSettings.Parse("""
            {
              "LastProfile": "C:\\data\\old.json",
              "RecentProfiles": [ { "Path": "C:\\data\\new.json", "LastOpenedUtc": "2026-10-05T10:00:00Z" } ]
            }
            """, Saved);
        Assert.Equal(["C:\\data\\new.json"], settings.RecentProfiles.Select(r => r.Path));
    }

    [Fact]
    public void ReadsAndWritesTheRecentList()
    {
        var settings = new EditorSettings();
        settings.AddRecentProfile("C:\\data\\sample.json", Saved);
        var copy = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);
        Assert.Equal(settings.RecentProfiles, copy.RecentProfiles);
    }

    [Fact]
    public void PutsTheNewestProfileFirstAndKeepsEachPathOnce()
    {
        var settings = new EditorSettings();
        settings.AddRecentProfile("C:\\a.json", Saved);
        settings.AddRecentProfile("C:\\b.json", Saved.AddHours(1));
        settings.AddRecentProfile("C:\\a.json", Saved.AddHours(2));
        Assert.Equal(["C:\\a.json", "C:\\b.json"], settings.RecentProfiles.Select(r => r.Path));
        Assert.Equal(Saved.AddHours(2), settings.RecentProfiles[0].LastOpenedUtc);
    }

    [Fact]
    public void KeepsAtMostTenProfiles()
    {
        var settings = new EditorSettings();
        for (var i = 0; i < 12; i++)
        {
            settings.AddRecentProfile($"C:\\{i}.json", Saved.AddMinutes(i));
        }

        Assert.Equal(EditorSettings.MaxRecentProfiles, settings.RecentProfiles.Count);
        Assert.Equal("C:\\11.json", settings.RecentProfiles[0].Path);
        Assert.Equal("C:\\2.json", settings.RecentProfiles[^1].Path);
    }

    [Fact]
    public void RemovesAProfile()
    {
        var settings = new EditorSettings();
        settings.AddRecentProfile("C:\\a.json", Saved);
        settings.AddRecentProfile("C:\\b.json", Saved);
        settings.RemoveRecentProfile("C:\\a.json");
        Assert.Equal(["C:\\b.json"], settings.RecentProfiles.Select(r => r.Path));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(45 * 60, "45 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(5 * 3600, "5 hours ago")]
    [InlineData(2 * 86400, "2 days ago")]
    [InlineData(90 * 86400, "3 months ago")]
    public void GivesTheAgeInWords(int seconds, string expected)
    {
        Assert.Equal(expected, WelcomeScreen.Age(TimeSpan.FromSeconds(seconds)));
    }

    // Load turns a JsonException into the default settings. Without these checks, a null value would go into a
    // setting that cannot be null, and the editor would stop at the start.
    [Theory]
    [InlineData("""{ "RecentProfiles": null }""")]
    [InlineData("""{ "InspectorFoldouts": null }""")]
    [InlineData("""{ "DatabaseCategory": null }""")]
    [InlineData("""{ "RecentProfiles": [ null ] }""")]
    [InlineData("""{ "RecentProfiles": [ { "Path": null, "LastOpenedUtc": "2026-10-05T10:00:00Z" } ] }""")]
    [InlineData("""{ "RecentProfiles": [ { "Path": "", "LastOpenedUtc": "2026-10-05T10:00:00Z" } ] }""")]
    [InlineData("""{ "RecentProfiles": [ { "LastOpenedUtc": "2026-10-05T10:00:00Z" } ] }""")]
    public void RejectsSettingsWithANullOrMissingValue(string json)
    {
        Assert.Throws<JsonException>(() => EditorSettings.Parse(json, Saved));
    }

    [Fact]
    public void AcceptsAnEmptyImGuiState()
    {
        // The ImGui state is null until ImGui saves it for the first time.
        Assert.Null(EditorSettings.Parse("""{ "ImGuiState": null }""", Saved).ImGuiState);
    }
}

using System;
using System.Text.Json;
using OpenLancer.Editor;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The ship package of Play for each profile (decision D15, section 9.6 of the M4 design).</summary>
public sealed class PlaySettingsTests
{
    private const string Profile = @"C:\p\sample.json";

    [Fact]
    public void AProfilePlaysWithTheDefaultPackageUntilAPackageIsSet()
    {
        var settings = new EditorSettings();

        Assert.Null(settings.PlayPackageOf(Profile));
        settings.SetPlayPackage(Profile, "ol_starrunner_package");
        Assert.Equal("ol_starrunner_package", settings.PlayPackageOf(Profile));
        Assert.Null(settings.PlayPackageOf(@"C:\p\other.json"));

        // Use the default ship removes the entry, so the file keeps no empty value.
        settings.SetPlayPackage(Profile, null);
        Assert.Null(settings.PlayPackageOf(Profile));
        Assert.Empty(settings.PlayPackages);
    }

    [Fact]
    public void ThePackagesStayAfterASaveAndAReadOfTheSettings()
    {
        var settings = new EditorSettings();
        settings.SetPlayPackage(Profile, "ol_starrunner_package");
        settings.SetPlayPackage(@"C:\p\other.json", "le_package");

        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        Assert.Equal("ol_starrunner_package", read.PlayPackageOf(Profile));
        Assert.Equal("le_package", read.PlayPackageOf(@"C:\p\other.json"));
        // A path compares as the file system compares it, also after a read.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("ol_starrunner_package", read.PlayPackageOf(@"c:\P\SAMPLE.json"));
        }
    }

    /// <summary>The serializer does not check the values of a dictionary for null, so a null package is the default
    /// too.</summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("null")]
    public void AnEmptyOrNullValueIsTheDefaultPackage(string value)
    {
        var read = EditorSettings.Parse($$"""{ "PlayPackages": { "C:\\p\\sample.json": {{value}} } }""",
            DateTime.UtcNow);

        Assert.Null(read.PlayPackageOf(Profile));
    }

    [Fact]
    public void SettingsWithoutPackagesGiveTheDefaultPackage()
    {
        Assert.Null(EditorSettings.Parse("{}", DateTime.UtcNow).PlayPackageOf(Profile));
    }

    /// <summary>A null list throws, as for the other settings. So a damaged file gives the defaults.</summary>
    [Fact]
    public void ANullListIsAnError()
    {
        Assert.ThrowsAny<JsonException>(() => EditorSettings.Parse("""{ "PlayPackages": null }""", DateTime.UtcNow));
    }
}

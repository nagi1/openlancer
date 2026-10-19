using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The version rules of a mod (decisions D563 and D585 of the M11c design).</summary>
public sealed class ModVersionTests
{
    [Fact]
    public void ParsesTheCoreAndAPreRelease()
    {
        var release = ModVersion.Parse("1.2.3")!;
        var pre = ModVersion.Parse("1.0.0-beta.1")!;

        Assert.Equal((1, 2, 3), (release.Major, release.Minor, release.Patch));
        Assert.False(release.IsPreRelease);
        Assert.Equal(["beta", "1"], pre.PreRelease);
        Assert.Equal("1.0.0-beta.1", pre.ToString());
        Assert.Equal("0.0.0", ModVersion.Parse("0.0.0")!.ToString());
    }

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("1.00.0")]
    [InlineData("1.0.0+build.5")]
    [InlineData("1.0.0-Beta")]
    [InlineData("1.0.0-beta.01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1234567890.0.0")]
    public void RefusesLeadingZerosBuildMetadataAndUppercasePreReleases(string text)
    {
        Assert.Null(ModVersion.Parse(text));
    }

    [Fact]
    public void ComparesByPrecedence()
    {
        string[] order =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0",
        ];

        for (var i = 0; i + 1 < order.Length; i++)
        {
            Assert.True(ModVersion.Parse(order[i])!.CompareTo(ModVersion.Parse(order[i + 1])) < 0, $"{order[i]} < {order[i + 1]}");
            Assert.True(ModVersion.Compare(order[i + 1], order[i]) > 0);
        }
        Assert.True(ModVersion.Compare("1.0", "0.0.1") < 0, "A version outside the rule is lower.");
    }

    [Fact]
    public void IncreaseGivesTheNextPatchMinorAndMajor()
    {
        var version = ModVersion.Parse("1.2.3")!;

        Assert.Equal("1.2.4", version.Increase(VersionPart.Patch).ToString());
        Assert.Equal("1.3.0", version.Increase(VersionPart.Minor).ToString());
        Assert.Equal("2.0.0", version.Increase(VersionPart.Major).ToString());
    }

    [Fact]
    public void APatchIncreaseOfAPreReleaseGivesItsRelease()
    {
        var version = ModVersion.Parse("1.3.0-beta.2")!;

        Assert.Equal("1.3.0", version.Increase(VersionPart.Patch).ToString());
        Assert.Equal("1.3.0", version.Increase(VersionPart.Minor).ToString());
        Assert.Equal("2.0.0", version.Increase(VersionPart.Major).ToString());
    }

    [Fact]
    public void IncreaseSkipsAVersionOfTheHistory()
    {
        Assert.Equal("1.0.2", ModVersion.Next("1.0.0", VersionPart.Patch, ["1.0.1"]));
        Assert.Equal("1.0.3", ModVersion.Next("1.0.0", VersionPart.Patch, ["1.0.1", "1.0.2"]));
        Assert.Equal("1.2.0", ModVersion.Next("1.0.0", VersionPart.Minor, ["1.1.0"]));
        Assert.Null(ModVersion.Next("1.0", VersionPart.Patch, []));
    }

    [Theory]
    [InlineData(@"..\..\x")]
    [InlineData("../x")]
    [InlineData(@"C:\x")]
    [InlineData("/x")]
    [InlineData("1.0/2")]
    [InlineData(@"1.0\2")]
    [InlineData("1..0")]
    [InlineData(".1")]
    [InlineData("-1")]
    [InlineData("1 0")]
    [InlineData("")]
    [InlineData("1.0.0+build")]
    public void IsSafeRefusesRootedPathsDotsAndSeparators(string text)
    {
        Assert.False(ModVersion.IsSafe(text));
        Assert.Equal(PublishMessages.VersionUnsafe(text), ModVersion.SafeProblem(text));
        Assert.Equal(DiagnosticSeverity.Error, ModVersion.Problem(text)!.Value.Severity);
        Assert.False(ModVersion.IsSafe(new string('1', ModVersion.MaxSafeLength + 1)));
    }

    [Fact]
    public void IsSafeAcceptsAnOldVersionSuchAsOnePointZero()
    {
        Assert.True(ModVersion.IsSafe("1.0"));
        Assert.True(ModVersion.IsSafe("1.0.0-beta.1"));
        Assert.True(ModVersion.IsSafe("v2-Final"));
        Assert.Equal((DiagnosticSeverity.Warning, PublishMessages.VersionRule), ModVersion.Problem("1.0"));
        Assert.Equal((DiagnosticSeverity.Warning, PublishMessages.VersionLetterCase), ModVersion.Problem("1.0.0-Beta"));
        Assert.Null(ModVersion.Problem("1.0.0"));
        Assert.True(Enumerable.Range(0, 1).All(_ => ModVersion.TextComparer.Equals("1.0.0-BETA", "1.0.0-beta")));
    }
}

using System.Linq;
using OpenLancer.Data.Export;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The difference of the sections of a base file and its merged file (section 7.3 of the M11 design).</summary>
public sealed class IniSectionDiffTests
{
    private const string Base = """
        [Good]
        nickname = a
        price = 100
        volume = 1

        [Good]
        nickname = b
        price = 200

        [SystemInfo]
        space_color = 0, 0, 0
        """;

    private static FileDiff Diff(string before, string after) =>
        IniSectionDiff.Compare(Parse(before).Sections, Parse(after).Sections);

    [Fact]
    public void AChangedValueIsAChangedLine()
    {
        var diff = Diff(Base, Base.Replace("price = 100", "price = 150"));

        var pair = Assert.Single(diff.Changed);
        Assert.Equal(0, pair.Base);
        Assert.Equal([1], pair.Changed);
        Assert.Empty(pair.Removed);
        Assert.Empty(pair.Inserted);
        Assert.Equal(1, pair.MergedOf[1]);
    }

    [Fact]
    public void AnInsertedLineIsInserted()
    {
        var diff = Diff(Base, Base.Replace("price = 200", "price = 200\nids_name = 5"));

        var pair = Assert.Single(diff.Changed);
        Assert.Equal(1, pair.Base);
        Assert.Equal([2], pair.Inserted);
        Assert.Equal([0, 1, -1], pair.BaseOf);
    }

    [Fact]
    public void ARemovedLineIsRemoved()
    {
        var diff = Diff(Base, Base.Replace("volume = 1\n", ""));

        var pair = Assert.Single(diff.Changed);
        Assert.Equal([2], pair.Removed);
        Assert.Empty(pair.Changed);
    }

    [Fact]
    public void ANewSectionComesAfterTheSectionBeforeIt()
    {
        var diff = Diff(Base, Base.Replace("[SystemInfo]", "[Good]\nnickname = c\nprice = 1\n\n[SystemInfo]"));

        Assert.Equal([2], diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
        // The base sections keep their pairs, with the new index of the section after the new one.
        Assert.Equal([(0, 0), (1, 1), (2, 3)], diff.Matched.Select(m => (m.Base, m.Merged)));
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void ARemovedSectionIsRemoved()
    {
        var diff = Diff(Base, Base.Replace("[Good]\nnickname = b\nprice = 200\n", ""));

        Assert.Equal([1], diff.Removed);
        Assert.Empty(diff.Added);
    }

    [Fact]
    public void AKeylessSingletonMatchesByItsType()
    {
        var diff = Diff(Base, Base.Replace("space_color = 0, 0, 0", "space_color = 1, 2, 3"));

        var pair = Assert.Single(diff.Changed);
        Assert.Equal(2, pair.Base);
        Assert.Equal([0], pair.Changed);
    }

    [Fact]
    public void ABaseGoodWithoutAKeyMatchesByItsPlace()
    {
        const string market = """
            [BaseGood]
            base = b1
            marketgood = g1, 0, -1, 1, 1, 0, 1, 1

            [BaseGood]
            base = b2
            marketgood = g2, 0, -1, 1, 1, 0, 1, 1
            """;

        var diff = Diff(market, market + "\n[BaseGood]\nbase = b1\nmarketgood = g3, 0, -1, 1, 1, 0, 1, 1\n");

        Assert.Equal([2], diff.Added);
        Assert.Equal(2, diff.Matched.Count);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void LinesThatBelongTogetherCompareInOrder()
    {
        const string zone = """
            [Zone]
            nickname = z1
            encounter = area_a, 5, 0.4
            faction = grp_a, 1
            encounter = area_b, 5, 0.4
            faction = grp_b, 1
            """;

        var diff = Diff(zone, zone.Replace("faction = grp_b, 1", "faction = grp_c, 1"));

        var pair = Assert.Single(diff.Changed);
        Assert.Equal([4], pair.Changed);
        Assert.Empty(pair.Inserted);
    }

    [Fact]
    public void AnUnchangedFileGivesAnEmptyDiff()
    {
        // Comments, blank lines, the letter case of a key and another form of a number are no change.
        var other = "; a comment\n" + Base.Replace("price = 100", "PRICE = 100.0").Replace("\n\n", "\n");

        var diff = Diff(Base, other);

        Assert.True(diff.IsEmpty);
        Assert.Equal(3, diff.Matched.Count);
    }
}

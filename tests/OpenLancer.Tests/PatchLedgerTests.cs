using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>Which mod changes which entries: for conflict warnings, and for the origin of a value in the editor.</summary>
public sealed class PatchLedgerTests
{
    private const string FilePath = @"DATA\EQUIPMENT\goods.ini";

    private static readonly SectionIdentity G1 = new("Good", "g1");
    private static readonly SectionIdentity G2 = new("Good", "g2");

    [Fact]
    public void RecordReturnsTheModThatMadeTheSameChangeLast()
    {
        var ledger = new PatchLedger();

        Assert.Null(ledger.Record(FilePath, G1, "price", "a"));
        Assert.Equal("a", ledger.Record(FilePath, G1, "PRICE", "b"));
        Assert.Null(ledger.Record(FilePath, G1, "volume", "c"));
        Assert.Null(ledger.Record(FilePath, G1, null, "c"));
        ledger.RecordFile(FilePath, "d");
        // The whole file discards the earlier changes.
        Assert.Null(ledger.Record(FilePath, G1, "price", "e"));

        Assert.Equal([(G1, (string?)"price")], ledger.Changes(FilePath, "e"));
        Assert.Empty(ledger.Changes(FilePath, "b"));
    }

    [Fact]
    public void OwnerIsTheLastModThatGivesTheLines()
    {
        var ledger = new PatchLedger();
        var g3 = new SectionIdentity("Good", "g3");

        ledger.Record(FilePath, G1, "price", "a");
        ledger.RecordAdd(FilePath, g3, "a");
        ledger.Record(FilePath, g3, "price", "b");

        Assert.Equal("a", ledger.Owner(FilePath, G1, "price"));
        Assert.Null(ledger.Owner(FilePath, G1, "volume"));
        Assert.Null(ledger.Owner(FilePath, G2, "price"));
        Assert.Equal("a", ledger.Owner(FilePath, g3, "volume"));
        Assert.Equal("b", ledger.Owner(FilePath, g3, "price"));

        ledger.RecordFile(FilePath, "c");
        Assert.Equal("c", ledger.Owner(FilePath, G2, "price"));
        ledger.Record(FilePath, G1, "price", "d");
        Assert.Equal("d", ledger.Owner(FilePath, G1, "price"));
        Assert.Equal("c", ledger.Owner(FilePath, G1, "volume"));

        Assert.Equal(["a", "c", "d"], ledger.ModsThatChange(FilePath, G1, "price"));
        Assert.Equal(["c"], ledger.ModsThatChange(FilePath, G1, "volume"));
        Assert.Equal(["a", "b", "c"], ledger.ModsThatChange(FilePath, g3, "price"));
        Assert.Empty(ledger.ModsThatChange(@"DATA\other.ini", G1, "price"));
    }

    /// <summary>The merge records the sections that a mod adds: new sections, new child sections and [BaseGood].</summary>
    [Fact]
    public void TheMergeRecordsAddedSections()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(FilePath, "[Good]\nnickname = g1\n[BaseGood]\nbase = b1\n[Ship]\nnickname = s1");
        var mod = ws.Mod("m", new()
        {
            [@"patches\" + FilePath] = """
                [Good]
                nickname = g2
                ol.new = true
                price = 5

                [BaseGood]
                base = b2

                [CollisionGroup]
                ol.new = true
                ol.parent = s1
                obj = a
                """,
        });

        var ledger = ws.Build(mod).Ledger;

        Assert.Equal("m", ledger.Owner(FilePath, G2, "price"));
        Assert.Equal("m", ledger.Owner(FilePath, new SectionIdentity("BaseGood", null, null, 1), "base"));
        Assert.Null(ledger.Owner(FilePath, new SectionIdentity("BaseGood", null), "base"));
        Assert.Equal("m", ledger.Owner(FilePath, new SectionIdentity("CollisionGroup", null, new SectionIdentity("Ship", "s1")), "obj"));
        Assert.Null(ledger.Owner(FilePath, G1, "nickname"));
    }
}

using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// How the editor identifies a section, so that it can compare two versions of a file section by section.
/// </summary>
public sealed class SectionIdentityTests
{
    [Fact]
    public void ASectionWithAKeyIsItsTypeAndKey()
    {
        var ids = Identify("[Good]\nnickname = le_hull\nprice = 5\n[Ship]\nnickname = le_hull");

        Assert.Equal(new SectionIdentity("good", "LE_HULL"), ids[0]);
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(new SectionIdentity("Ship", "le_hull").GetHashCode(), ids[1].GetHashCode());
        Assert.Equal("[Good] nickname = le_hull", ids[0].ToString());
    }

    [Fact]
    public void TheTypeDecidesTheKeyEntry()
    {
        var ids = Identify("[fuse]\nname = f1\nnickname = x\n[FactionProps]\naffiliation = li_n_grp");

        Assert.Equal([new SectionIdentity("fuse", "f1"), new SectionIdentity("FactionProps", "li_n_grp")], ids);
    }

    [Fact]
    public void ARepeatedIdentityGetsTheNextOrdinal()
    {
        var ids = Identify("[Good]\nnickname = a\n[Good]\nnickname = A\n[SystemInfo]\n[SystemInfo]");

        Assert.Equal(
        [
            new SectionIdentity("Good", "a"), new SectionIdentity("Good", "a", Ordinal: 1),
            new SectionIdentity("SystemInfo", null), new SectionIdentity("SystemInfo", null, Ordinal: 1),
        ], ids);
        // The text keeps the letter case of the section.
        Assert.Equal("[Good] nickname = A (#2)", ids[1].ToString());
        Assert.Equal("[SystemInfo] #2", ids[3].ToString());
    }

    [Fact]
    public void AChildWithoutAKeyIsTheNthChildOfItsTypeUnderItsParent()
    {
        var ids = Identify("""
            [Ship]
            nickname = a
            [CollisionGroup]
            obj = x
            [CollisionGroup]
            obj = y
            [Ship]
            nickname = b
            [CollisionGroup]
            obj = z
            """);

        var a = new SectionIdentity("Ship", "a");
        var b = new SectionIdentity("Ship", "b");
        Assert.Equal(
        [
            a, new SectionIdentity("CollisionGroup", null, a), new SectionIdentity("CollisionGroup", null, a, 1),
            b, new SectionIdentity("CollisionGroup", null, b),
        ], ids);
        Assert.Equal("[CollisionGroup] #2 of [Ship] nickname = a", ids[2].ToString());
    }

    [Fact]
    public void AChildWithAKeyBelongsToItsParent()
    {
        const string b2 = "[MBase]\nnickname = b2\n[MRoom]\nnickname = bar\n[MRoom]\nnickname = deck";
        var ids = Identify("[MBase]\nnickname = b1\n[MRoom]\nnickname = bar\n" + b2);
        var withoutB1 = Identify(b2);

        var parent = new SectionIdentity("MBase", "b2");
        Assert.Equal(new SectionIdentity("MRoom", "bar", new SectionIdentity("MBase", "b1")), ids[1]);
        Assert.Equal(new SectionIdentity("MRoom", "bar", parent), ids[3]);
        Assert.Equal("[MRoom] nickname = bar of [MBase] nickname = b2", ids[3].ToString());
        // A repeated child key keeps its identity when another parent goes.
        Assert.Equal(ids[2..], withoutB1);
    }

    [Fact]
    public void ASectionWithoutASingleKeyValueHasNoKey()
    {
        var ids = Identify("[Good]\nprice = 1\n[Good]\nnickname = a\nnickname = b\n[Good]\nnickname = a, b\n[BaseGood]\nbase = x\n[BaseGood]\nbase = x");

        Assert.Equal(
        [
            new SectionIdentity("Good", null), new SectionIdentity("Good", null, Ordinal: 1),
            new SectionIdentity("Good", null, Ordinal: 2),
            new SectionIdentity("BaseGood", null), new SectionIdentity("BaseGood", null, Ordinal: 1),
        ], ids);
        Assert.Equal("[BaseGood] #2", ids[4].ToString());
    }

    /// <summary>The identity of one section is the identity that the whole file gives it.</summary>
    [Fact]
    public void OneSectionGetsTheIdentityThatTheFileGivesIt()
    {
        var sections = IniFile.ParseFile("test.ini", new MemoryStream(Encoding.ASCII.GetBytes("""
            [CollisionGroup]
            obj = orphan
            [Ship]
            nickname = a
            [CollisionGroup]
            obj = x
            [CollisionGroup]
            obj = y
            [Ship]
            nickname = A
            [CollisionGroup]
            obj = z
            [MBase]
            nickname = b
            [MRoom]
            nickname = bar
            [MRoom]
            nickname = BAR
            [BaseGood]
            base = x
            [Good]
            price = 1
            [BaseGood]
            base = x
            [SystemInfo]
            [SystemInfo]
            """))).ToList();

        var all = SectionIdentity.Identify(sections);

        Assert.Equal(all, sections.Select((_, i) => SectionIdentity.Of(sections, i)));
        // The repeated identities: the second ship a, the second room bar, the second [BaseGood], the second [SystemInfo].
        Assert.Equal([1, 1, 1, 1], new[] { all[4], all[8], all[11], all[13] }.Select(id => id.Ordinal));
        Assert.Equal(all[4], all[5].Parent);
        Assert.Null(all[0].Parent);
    }

    [Fact]
    public void ASectionRefComparesThePathWithoutLetterCase()
    {
        var id = new SectionIdentity("Good", "a");

        Assert.Equal(new SectionRef(@"DATA\EQUIPMENT\goods.ini", id), new SectionRef(@"data\equipment\GOODS.INI", id));
        Assert.Equal(new SectionRef(@"DATA\goods.ini", id).GetHashCode(), new SectionRef(@"data\GOODS.ini", id).GetHashCode());
        Assert.NotEqual(new SectionRef(@"DATA\goods.ini", id), new SectionRef(@"DATA\goods.ini", id with { Ordinal = 1 }));
    }

    private static SectionIdentity[] Identify(string text) =>
        SectionIdentity.Identify(IniFile.ParseFile("test.ini", new MemoryStream(Encoding.ASCII.GetBytes(text))).ToList());
}

using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The lists of the Play menu: the bases of Play at base (section 9.7 of the M4 design) and the ship packages
/// (section 11.2).</summary>
public sealed class PlayMenuTests
{
    private static readonly StarSystem[] Systems =
    [
        new() { Nickname = "Li01", SourceFile = "" },
        new() { Nickname = "Li02", SourceFile = "" },
        new() { Nickname = "Br01", SourceFile = "" },
        new() { Nickname = "Ew01", SourceFile = "" },
    ];

    private static readonly Base[] Bases =
    [
        new() { Nickname = "Br01_01_Base", System = "br01" },
        new() { Nickname = "Li01_01_Base", System = "Li01" },
        new() { Nickname = "Li02_01_Base", System = "Li02" },
        new() { Nickname = "Li01_02_Base", System = "LI01" },
        // A base that no system of the data has, and a base without a system.
        new() { Nickname = "Xx01_01_Base", System = "xx01" },
        new() { Nickname = "intro_base" },
    ];

    [Fact]
    public void TheBasesOfTheOpenSystemComeFirstThenTheOtherSystemsInTheOrderOfTheData()
    {
        var groups = PlayMenu.BaseGroups(Systems, Bases, "br01");

        // A system without bases is not in the list.
        Assert.Equal(["Br01", "Li01", "Li02"], groups.Select(g => g.System.Nickname));
        Assert.Equal(["Br01_01_Base"], groups[0].Bases.Select(b => b.Nickname));
        // The bases of a system keep the order of the data.
        Assert.Equal(["Li01_01_Base", "Li01_02_Base"], groups[1].Bases.Select(b => b.Nickname));
    }

    /// <summary>No open system, an open system without bases, and an open system that is the first system of the
    /// data.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("ew01")]
    [InlineData("li01")]
    public void ElseTheListHasTheOrderOfTheData(string? open)
    {
        var groups = PlayMenu.BaseGroups(Systems, Bases, open);

        Assert.Equal(["Li01", "Li02", "Br01"], groups.Select(g => g.System.Nickname));
        Assert.DoesNotContain(groups.SelectMany(g => g.Bases), b => b.Nickname is "Xx01_01_Base" or "intro_base");
    }

    [Fact]
    public void AShipPackageIsAGoodOfCategoryShip()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var package = ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_package");

        Assert.Equal(package, PlayShips.Find(session, "S1_PACKAGE"));
        Assert.True(PlayShips.IsPackage(package, session.Get(package)!));
        // A hull good, an equipment good and an unknown nickname are no packages.
        Assert.Null(PlayShips.Find(session, "s1_hull"));
        Assert.Null(PlayShips.Find(session, "eng1"));
        Assert.Null(PlayShips.Find(session, "no_such_package"));
        var hull = ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_hull");
        Assert.False(PlayShips.IsPackage(hull, session.Get(hull)!));
        // The menu names the ship of the package.
        Assert.Equal([ItemWorkspace.Ship("s1")], session.Related(package, ItemRelation.Ship));
    }
}

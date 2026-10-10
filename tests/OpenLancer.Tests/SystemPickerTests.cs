using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

public sealed class SystemPickerTests
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "New York",
        [2] = "New London",
        [3] = "Colorado",
        [4] = "New Berlin",
    };

    private static FilterEntry<StarSystem>[] Entries() => SystemPicker.Entries(
    [
        System("Li01", 1), System("Br01", 2), System("Li03", 3), System("Rh01", 4), System("Iw03", 0),
    ], ids => Names.GetValueOrDefault(ids, ""));

    private static StarSystem System(string nickname, int ids) =>
        new() { Nickname = nickname, IdsName = ids, SourceFile = "" };

    [Fact]
    public void SortsByNameAndUsesTheNicknameForASystemWithoutName()
    {
        Assert.Equal(["Colorado", "Iw03", "New Berlin", "New London", "New York"],
            Entries().Select(e => e.Name));
    }

    [Fact]
    public void MatchesTheNameWithoutCase()
    {
        Assert.Equal(["New Berlin", "New London", "New York"],
            FilterList<StarSystem>.Filter(Entries(), "new").Select(e => e.Name));
    }

    [Fact]
    public void MatchesTheNicknameAndKeepsTheNameOrder()
    {
        Assert.Equal(["Li03", "Li01"], FilterList<StarSystem>.Filter(Entries(), "LI0").Select(e => e.Nickname));
    }

    [Fact]
    public void ShowsAllSystemsForAnEmptyFilterAndNoneForNoMatch()
    {
        Assert.Equal(5, FilterList<StarSystem>.Filter(Entries(), "").Count);
        Assert.Empty(FilterList<StarSystem>.Filter(Entries(), "xyz"));
    }
}

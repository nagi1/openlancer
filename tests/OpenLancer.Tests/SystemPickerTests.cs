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
    ], s => Names.GetValueOrDefault(s.IdsName, ""));

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

    /// <summary>The name of a system comes from the name function, which reads the <c>strid_name</c> of the system in W.
    /// So a new name before the save sorts and matches.</summary>
    [Fact]
    public void TheNamesComeFromTheNameFunction()
    {
        var entries = SystemPicker.Entries([System("Li01", 1), System("Br01", 2)],
            s => s.Nickname == "Li01" ? "Albany" : Names.GetValueOrDefault(s.IdsName, ""));

        Assert.Equal(["Albany", "New London"], entries.Select(e => e.Name));
        Assert.Equal(["Li01"], FilterList<StarSystem>.Filter(entries, "alb").Select(e => e.Nickname));
    }

    /// <summary>
    /// The list and the preview build again for other game data or another system, and for each new version of the
    /// session, because an edit can give a system a new name.
    /// </summary>
    [Fact]
    public void TheListBuildsAgainForOtherDataAndForANewVersion()
    {
        var li01 = System("Li01", 1);

        Assert.False(SystemPicker.IsStale((li01, 3), li01, 3));
        Assert.True(SystemPicker.IsStale((li01, 3), li01, 4));
        // Another system with the same values.
        Assert.True(SystemPicker.IsStale((li01, 3), System("Li01", 1), 3));
        Assert.True(SystemPicker.IsStale(((StarSystem?)null, 3), li01, 3));
    }
}

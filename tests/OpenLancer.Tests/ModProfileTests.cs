using System;
using System.Linq;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The place of a mod in a profile, and the mods of the edit view, which the editor, oltool and Play
/// share.</summary>
public sealed class ModProfileTests
{
    private static ModProfile Profile(TestWorkspace ws)
    {
        ws.Mod("first", new());
        ws.Mod("middle", new());
        ws.Mod("last", new());
        ws.WriteProfile("first", "middle", "last");
        return ModProfile.Load(ws.ProfilePath);
    }

    [Theory]
    [InlineData("first", 0)]
    [InlineData("middle", 1)]
    [InlineData("last", 2)]
    [InlineData("other", -1)]
    [InlineData("Middle", -1)]
    public void IndexOfGivesThePlaceOfTheMod(string id, int expected)
    {
        using var ws = new TestWorkspace();

        Assert.Equal(expected, Profile(ws).IndexOf(id));
    }

    [Theory]
    [InlineData("first", new[] { "first" })]
    [InlineData("middle", new[] { "first", "middle" })]
    [InlineData("last", new[] { "first", "middle", "last" })]
    [InlineData(null, new[] { "first", "middle", "last" })]
    public void ModsThroughGivesTheModsUpToTheActiveMod(string? activeId, string[] expected)
    {
        using var ws = new TestWorkspace();

        Assert.Equal(expected, Profile(ws).ModsThrough(activeId).Select(m => m.Id));
    }

    [Fact]
    public void ModsThroughRefusesAnUnknownMod()
    {
        using var ws = new TestWorkspace();

        var error = Assert.Throws<ArgumentException>(() => Profile(ws).ModsThrough("other"));
        Assert.Contains("no mod 'other'", error.Message);
    }
}

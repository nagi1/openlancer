using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

public sealed class ModOrderTests
{
    [Fact]
    public void KeepsTheRequestedOrderWithoutConstraints()
    {
        using var ws = new TestWorkspace();
        var mods = new[] { ws.Mod("c", new()), ws.Mod("a", new()), ws.Mod("b", new()) };

        Assert.Equal(["c", "a", "b"], ModProfile.ResolveOrder(mods, "test").Select(m => m.Id));
    }

    [Fact]
    public void LoadsDependenciesAndLoadAfterFirst()
    {
        using var ws = new TestWorkspace();
        var mods = new[]
        {
            ws.Mod("ui", new(), """, "dependsOn": ["core"]"""),
            ws.Mod("patch", new(), """, "loadAfter": ["ui", "absent"]"""),
            ws.Mod("core", new()),
        };

        Assert.Equal(["core", "ui", "patch"], ModProfile.ResolveOrder(mods, "test").Select(m => m.Id));
    }

    [Theory]
    [InlineData("""{ "dependsOn": ["nothere"] }""", "not in the profile")]
    [InlineData("""{ "dependsOn": ["x"] }""", "cycle")]
    public void RejectsBadOrders(string manifestB, string expected)
    {
        using var ws = new TestWorkspace();
        var extra = manifestB.Trim('{', '}', ' ');
        var mods = new List<Mod>
        {
            ws.Mod("x", new(), """, "dependsOn": ["y"]"""),
            ws.Mod("y", new(), ", " + extra),
        };

        var error = Assert.Throws<ModLoadException>(() => ModProfile.ResolveOrder(mods, "test"));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void RejectsADuplicateId()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("same", new());

        var error = Assert.Throws<ModLoadException>(() => ModProfile.ResolveOrder([mod, mod], "test"));
        Assert.Contains("two times", error.Message);
    }

    [Theory]
    [InlineData(@"EXE\..\data\ships\shiparch.ini", @"data\ships\shiparch.ini")]
    [InlineData("DATA/Ships//x.ini", @"DATA\Ships\x.ini")]
    [InlineData(@".\EXE\.\a.dll", @"EXE\a.dll")]
    [InlineData(@"..\outside.ini", null)]
    [InlineData(@"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini", @"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini")]
    [InlineData(@"\DATA\x.ini\", @"DATA\x.ini")]
    [InlineData(@"DATA\..\x.ini", "x.ini")]
    [InlineData(@"DATA\.cache\..x\x.", @"DATA\.cache\..x\x.")]
    [InlineData("", "")]
    public void NormalizesEnginePaths(string path, string? expected)
    {
        Assert.Equal(expected, VfsPath.Normalize(path));
    }
}

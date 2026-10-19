using System;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The space lines of the retail data (test 5.3 item 2 of the cutscene fixes plan). The test reads the local game data
/// at run time. It names retail paths and nicknames only to find them.
/// </summary>
public sealed class SpaceScenesRetailTests(ITestOutputHelper output)
{
    private const string SpaceKey = "Act_CallThorn";
    private const string Rev201 = @"DATA\MISSIONS\M01A\m01a_rev2_01.thn";
    private const string M01aIni = @"DATA\MISSIONS\M01A\m01a.ini";

    private static readonly string[] Freeport7 =
    [
        @"DATA\MISSIONS\M01A\m01a_fp7_01.thn", @"DATA\MISSIONS\M01A\m01a_fp7_02.thn",
        @"DATA\MISSIONS\M01A\m01a_fp7_02b.thn", @"DATA\MISSIONS\M01A\m01a_fp7_03.thn",
    ];

    static SpaceScenesRetailTests() => AppHandler.ConsoleInit(silent: true);

    private static (SpaceScenes Scenes, ThnReferences References) Load()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        var references = ThnReferences.Scan(items.VFS);
        return (new SpaceScenes(items.VFS, references, SpaceFacts.From(items)), references);
    }

    /// <summary>Each <c>Act_CallThorn</c> line of the retail data gets a system of the data. At least 63 lines get a
    /// start pose of the main object.</summary>
    [Fact]
    public void EachRetailSpaceLineGetsASystem()
    {
        var (scenes, references) = Load();
        var systems = RetailGame.Instance.Data.Items.Systems;
        var lines = references.All.Where(r => r.Key.Equals(SpaceKey, StringComparison.OrdinalIgnoreCase)).ToList();
        var scripts = lines.Select(l => ThnReferences.PathOf(l.Value)!).Distinct(VfsPath.Comparer).ToList();
        var uses = scripts.SelectMany(scenes.Of).ToList();

        Assert.Equal(70, lines.Count);
        Assert.Equal(60, scripts.Count);
        foreach (var line in lines)
        {
            var own = uses.Where(u => ReferenceEquals(u.Line, line)).ToList();
            Assert.NotEmpty(own);
            Assert.All(own, u => Assert.NotNull(systems.Get(u.System!)));
        }

        var firstUses = lines.Select(l => uses.First(u => ReferenceEquals(u.Line, l))).ToList();
        Assert.True(firstUses.Count(u => u.Start != null) >= 63);
        foreach (var rule in firstUses.GroupBy(u => u.SystemRule))
        {
            output.WriteLine($"System rule {rule.Key}: {rule.Count()} lines.");
        }

        foreach (var rule in firstUses.GroupBy(u => u.StartRule))
        {
            output.WriteLine($"Start rule {rule.Key?.ToString() ?? "none"}: {rule.Count()} lines.");
        }
    }

    /// <summary>The four Freeport 7 scenes of the game opening play in their system by the key of their trigger, and
    /// the context list suggests that system first.</summary>
    [Fact]
    public void TheFreeport7ScenesPlayInTheirSystem()
    {
        var (scenes, references) = Load();
        var items = RetailGame.Instance.Data.Items;

        foreach (var path in Freeport7)
        {
            var use = Assert.Single(scenes.Of(path));
            Assert.Equal("FP7_system", use.System, ignoreCase: true);
            Assert.Equal(SpaceSystemRule.TriggerKey, use.SystemRule);

            var first = PreviewContext.Choices(path, references, items, scenes)[0];
            Assert.Equal(PreviewContextKind.Space, first.Kind);
            Assert.Equal("FP7_system", first.System, ignoreCase: true);
        }
    }

    /// <summary>The first scene of the rescue plays in two missions: Li01 in its own mission folder first, then Br05.
    /// Its start pose in the first mission is the position of the mission ship of the line, which the test reads from
    /// the data.</summary>
    [Fact]
    public void TheRescueScenePlaysInTwoSystems()
    {
        var (scenes, _) = Load();
        var uses = scenes.Of(Rev201);

        Assert.Equal(["Li01", "Br05"], uses.Select(u => u.System).Distinct(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var first = uses[0];
        Assert.Equal(SpaceStartRule.MissionObject, first.StartRule);

        var view = RetailGame.Instance.Data.Items.VFS;
        var ship = IniDocument.Parse(M01aIni, view.ReadAllBytes(M01aIni)).Sections.Single(s =>
            s.Name.Equals("MsnShip", StringComparison.OrdinalIgnoreCase) &&
            s.Any(e => e.Name.Equals("nickname", StringComparison.OrdinalIgnoreCase) &&
                       e[0].ToString()!.Equals(first.MainObject, StringComparison.OrdinalIgnoreCase)));
        var position = ship.First(e => e.Name.Equals("position", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new Vector3(position[0].ToSingle(), position[1].ToSingle(), position[2].ToSingle()),
            first.Start!.Position);
        output.WriteLine($"{Rev201}: main object {first.MainObject} at {first.Start.Position}.");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.IO;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The space lines of <see cref="SpaceScenes"/> on small text INI files (test 5.2 item 3 of the cutscene fixes plan):
/// the system rules of decision D641, the order of D642, the start rules of D644 and the values of D650. The names are
/// synthetic.
/// </summary>
public sealed class SpaceScenesTests : IDisposable
{
    private readonly List<TestWorkspace> workspaces = [];

    private const string Script = @"DATA\MISSIONS\MX01\a.thn";
    private const string Value = @"missions\mx01\a.thn";

    private static readonly SpaceFacts Facts = new(
        ["Zz01", "Zz02"],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Zz02_01_Base"] = "zz02" },
        new Dictionary<string, SpaceObjectFact>(StringComparer.OrdinalIgnoreCase)
        {
            ["zz02_ring_1"] = new("zz02_ring_1", "Zz02", new Vector3(100, 200, 300),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1)),
        });

    /// <summary>The space lines of the script with one mission file in the folder of the script.</summary>
    private IReadOnlyList<SpaceUse> Uses(string ini, string script = Script) =>
        Scenes((@"DATA\MISSIONS\MX01\mx01.ini", ini)).Of(script);

    private SpaceScenes Scenes(params (string Path, string Text)[] files)
    {
        var ws = new TestWorkspace();
        workspaces.Add(ws);
        foreach (var (path, text) in files)
        {
            ws.WriteBase(path, text);
        }

        var view = FileSystem.FromPath(ws.Base);
        return new SpaceScenes(view, ThnReferences.Scan(view), Facts);
    }

    [Fact]
    public void TheSystemKeyOfTheTriggerGivesTheSystem()
    {
        var use = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = zz01\nAct_CallThorn = {Value}\n"));

        Assert.Equal("Zz01", use.System);
        Assert.Equal(SpaceSystemRule.TriggerKey, use.SystemRule);
        Assert.Equal("t1", use.Trigger);
        Assert.Equal(SpaceUse.Player, use.MainObject);
        Assert.Null(use.Problem);
    }

    /// <summary>A condition names a system, a base or a system object. <c>no_params</c> and <c>ANY</c> name
    /// nothing.</summary>
    [Theory]
    [InlineData("Cnd_SpaceEnter = zz02", "Zz02", "the system Zz02")]
    [InlineData("Cnd_JumpInComplete = Zz02", "Zz02", "the system Zz02")]
    [InlineData("Cnd_LocEnter = bar, Zz02_01_Base", "Zz02", "the base Zz02_01_Base")]
    [InlineData("Cnd_BaseEnter = zz02_01_base", "Zz02", "the base zz02_01_base")]
    [InlineData("Cnd_LaunchComplete = Zz02_01_Base", "Zz02", "the base Zz02_01_Base")]
    [InlineData("Cnd_DistShip = inside, Player, zz02_ring_1, 3000", "Zz02", "the object zz02_ring_1")]
    [InlineData("Cnd_DistCircle = inside, Player, ZZ02_RING_1, 3000", "Zz02", "the object zz02_ring_1")]
    [InlineData("Cnd_SpaceEnter = no_params", null, null)]
    [InlineData("Cnd_SystemEnter = ANY", null, null)]
    public void AConditionGivesTheSystemThatItNames(string condition, string? system, string? source)
    {
        var use = Assert.Single(Uses($"[Trigger]\nnickname = t1\n{condition}\nAct_CallThorn = {Value}\n"));

        Assert.Equal(system, use.System);
        Assert.Equal(system == null ? null : SpaceSystemRule.Condition, use.SystemRule);
        Assert.Equal(source, use.SystemSource);
    }

    /// <summary>A condition with two systems gives both, in the order of the values (rule S-9).</summary>
    [Fact]
    public void AConditionWithTwoSystemsGivesBothInValueOrder()
    {
        var uses = Uses($"[Trigger]\nnickname = t1\nCnd_SystemEnter = Zz02, Zz01\nAct_CallThorn = {Value}\n");

        Assert.Equal(["Zz02", "Zz01"], uses.Select(u => u.System));
    }

    /// <summary>The triggers that start the trigger give the system, level by level. The walk stops at the first level
    /// that gives a system.</summary>
    [Fact]
    public void TheActivatingTriggersGiveTheSystemLevelByLevel()
    {
        var uses = Uses(
            "[Trigger]\nnickname = c\nsystem = Zz01\nAct_ActTrig = b\n" +
            "[Trigger]\nnickname = b\nAct_ActTrig = a\n" +
            $"[Trigger]\nnickname = a\nAct_CallThorn = {Value}\n");

        var use = Assert.Single(uses);
        Assert.Equal("Zz01", use.System);
        Assert.Equal(SpaceSystemRule.ActivatingTrigger, use.SystemRule);
        Assert.Equal("c", use.SystemSource);

        var nearest = Uses(
            "[Trigger]\nnickname = far\nsystem = Zz01\nAct_ActTrig = near\n" +
            "[Trigger]\nnickname = near\nCnd_SpaceEnter = Zz02\nAct_ActTrig = a\n" +
            $"[Trigger]\nnickname = a\nAct_CallThorn = {Value}\n");
        Assert.Equal("Zz02", Assert.Single(nearest).System);
        Assert.Equal("near", nearest[0].SystemSource);
    }

    [Fact]
    public void ACycleOfActivatingTriggersEnds()
    {
        var use = Assert.Single(Uses(
            "[Trigger]\nnickname = b\nAct_ActTrig = a\n" +
            $"[Trigger]\nnickname = a\nAct_ActTrig = b\nAct_CallThorn = {Value}\n"));

        Assert.Null(use.System);
        Assert.Null(use.SystemRule);
    }

    /// <summary>Two parents at one level with two systems give two uses, in file order.</summary>
    [Fact]
    public void TwoParentsAtOneLevelGiveTwoUsesInFileOrder()
    {
        var uses = Uses(
            "[Trigger]\nnickname = p2\nsystem = Zz02\nAct_ActTrig = a\n" +
            "[Trigger]\nnickname = p1\nsystem = Zz01\nAct_ActTrig = a\n" +
            $"[Trigger]\nnickname = a\nAct_CallThorn = {Value}\n");

        Assert.Equal(["Zz02", "Zz01"], uses.Select(u => u.System));
        Assert.Equal(["p2", "p1"], uses.Select(u => u.SystemSource));
    }

    /// <summary>A system key that names no system of the data gives no system, and the problem tells why (decision
    /// D641).</summary>
    [Fact]
    public void ALineWithoutASystemGivesAUseWithoutASystem()
    {
        var use = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = Qq99\nAct_CallThorn = {Value}\n"));

        Assert.Null(use.System);
        Assert.Equal("t1", use.Trigger);
        Assert.Equal("The system key of the trigger names Qq99, but the game data has no such system. So the game " +
                     "never runs the trigger.", use.Problem);
        Assert.Equal($"No rule finds the system of the mission line of the trigger t1. {use.Problem}", use.NoSystemText);
    }

    /// <summary>A condition still gives the system when the system key names no system. The problem stays, for the
    /// tooltip of the row.</summary>
    [Fact]
    public void AnUnknownSystemKeyKeepsItsProblemWhenAConditionGivesTheSystem()
    {
        var use = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = Qq99\nCnd_SpaceEnter = Zz02\nAct_CallThorn = {Value}\n"));

        Assert.Equal("Zz02", use.System);
        Assert.Equal(SpaceSystemRule.Condition, use.SystemRule);
        Assert.Contains("Qq99", use.Problem);
    }

    /// <summary>The lines of a file in the folder of the script come first (decision D642), also when the scan reads
    /// another file first.</summary>
    [Fact]
    public void TheLinesOfTheFolderOfTheScriptComeFirst()
    {
        const string script = @"DATA\MISSIONS\MX02\b.thn";
        var scenes = Scenes(
            (@"DATA\MISSIONS\MX01\mx01.ini", "[Trigger]\nnickname = early\nsystem = Zz01\nAct_CallThorn = missions\\mx02\\b.thn\n"),
            (@"DATA\MISSIONS\MX02\mx02.ini", "[Trigger]\nnickname = own\nsystem = Zz02\nAct_CallThorn = missions\\mx02\\b.thn\n"));

        Assert.Equal(["Zz02", "Zz01"], scenes.Of(script).Select(u => u.System));
    }

    /// <summary>The start rules of decision D644, one case for each rule, and a main object without a start.</summary>
    [Theory]
    [InlineData("Act_SetInitialPlayerPos = 1, 2, 3, 1, 0, 0, 0", "", SpaceStartRule.PlayerPosition, 1f, 2f, 3f)]
    [InlineData("Act_MovePlayer = 4, 5, 6, 1", "", SpaceStartRule.PlayerPosition, 4f, 5f, 6f)]
    [InlineData("Act_RelocateShip = Player, 7, 8, 9", "", SpaceStartRule.Relocate, 7f, 8f, 9f)]
    [InlineData("Act_RelocateShip = zz_ship, 7, 8, 9", ", zz_ship", SpaceStartRule.Relocate, 7f, 8f, 9f)]
    [InlineData("Act_SpawnShip = zz_ship, zz_orders, 10, 11, 12", ", zz_ship", SpaceStartRule.Spawn, 10f, 11f, 12f)]
    public void AnActionOfTheTriggerGivesTheStart(string action, string main, SpaceStartRule rule, float x, float y,
        float z)
    {
        var use = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = Zz01\n{action}\nAct_CallThorn = {Value}{main}\n"));

        Assert.Equal(rule, use.StartRule);
        Assert.Equal(new SpacePose(new Vector3(x, y, z), Quaternion.Identity), use.Start);
        Assert.Equal("t1", use.StartSource);
    }

    [Fact]
    public void TheMissionShipTheFormationAndTheSystemObjectGiveTheStart()
    {
        var ship = Assert.Single(Uses(
            "[MsnShip]\nnickname = zz_ship\nposition = 1, 2, 3\norientation = 0, 1, 0, 0\n" +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_ship\n"));
        Assert.Equal(SpaceStartRule.MissionObject, ship.StartRule);
        Assert.Equal(new SpacePose(new Vector3(1, 2, 3), new Quaternion(1, 0, 0, 0)), ship.Start);

        var solar = Assert.Single(Uses(
            "[MsnSolar]\nnickname = zz_solar\nposition = 4, 5, 6\n" +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_solar\n"));
        Assert.Equal(SpaceStartRule.MissionObject, solar.StartRule);
        Assert.Equal(new Vector3(4, 5, 6), solar.Start!.Position);

        var formation = Assert.Single(Uses(
            "[MsnFormation]\nnickname = zz_group\nposition = 7, 8, 9\nship = zz_lead\nship = zz_wing\n" +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_wing\n"));
        Assert.Equal(SpaceStartRule.Formation, formation.StartRule);
        Assert.Equal(new Vector3(7, 8, 9), formation.Start!.Position);
        Assert.Equal("zz_group", formation.StartSource);

        var ring = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = Zz02\nAct_CallThorn = {Value}, ZZ02_RING_1\n"));
        Assert.Equal(SpaceStartRule.SystemObject, ring.StartRule);
        Assert.Equal(Facts.Objects["zz02_ring_1"].Position, ring.Start!.Position);

        // The system object is in another system than the line.
        var elsewhere = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz02_ring_1\n"));
        Assert.Null(elsewhere.Start);
        Assert.Null(elsewhere.StartRule);

        var none = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_nobody\n"));
        Assert.Equal("zz_nobody", none.MainObject);
        Assert.Null(none.Start);
    }

    // A formation of two ships. The mission ship of the wing has a position, and the formation has its own.
    private const string Group =
        "[MsnShip]\nnickname = zz_wing\nposition = 1, 1, 1\n" +
        "[MsnFormation]\nnickname = zz_group\nposition = 7, 8, 9\norientation = 0, 1, 0, 0\nship = zz_lead\nship = zz_wing\n";

    /// <summary>
    /// The game puts a ship that its formation spawns at the position of the spawn line, else at the position of the
    /// formation. The orientation of the formation applies when the line has none. The spawn wins over the position of
    /// the mission ship.
    /// </summary>
    [Fact]
    public void TheSpawnOfTheFormationGivesTheStart()
    {
        var placed = Assert.Single(Uses(Group +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_SpawnFormation = zz_group, 10, 11, 12\nAct_CallThorn = {Value}, zz_wing\n"));
        Assert.Equal(SpaceStartRule.Spawn, placed.StartRule);
        Assert.Equal(new SpacePose(new Vector3(10, 11, 12), new Quaternion(1, 0, 0, 0)), placed.Start);
        Assert.Equal("t1", placed.StartSource);

        var turned = Assert.Single(Uses(Group +
            "[Trigger]\nnickname = t1\nsystem = Zz01\nAct_SpawnFormation = zz_group, 10, 11, 12, 0.6, 0, 0.8, 0\n" +
            $"Act_CallThorn = {Value}, zz_wing\n"));
        Assert.Equal(new Quaternion(0, 0.8f, 0, 0.6f), turned.Start!.Orientation);

        var grouped = Assert.Single(Uses(Group +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_SpawnFormation = zz_group\nAct_CallThorn = {Value}, zz_wing\n"));
        Assert.Equal(SpaceStartRule.Formation, grouped.StartRule);
        Assert.Equal(new Vector3(7, 8, 9), grouped.Start!.Position);
        Assert.Equal("zz_group", grouped.StartSource);
    }

    /// <summary>A trigger outside the chain of the line can spawn the main object before the line runs. Then the first
    /// spawn line of the file gives the start. A spawn in the chain wins.</summary>
    [Fact]
    public void TheFirstSpawnLineOfTheFileGivesTheStart()
    {
        const string spawns =
            "[Trigger]\nnickname = early\nsystem = Zz01\nAct_SpawnFormation = zz_group, 20, 21, 22\n" +
            "[Trigger]\nnickname = again\nsystem = Zz01\nAct_SpawnFormation = zz_group, 30, 31, 32\n";

        var early = Assert.Single(Uses(Group + spawns +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_wing\n"));
        Assert.Equal(SpaceStartRule.Spawn, early.StartRule);
        Assert.Equal(new Vector3(20, 21, 22), early.Start!.Position);
        Assert.Equal("early", early.StartSource);

        var chain = Assert.Single(Uses(Group + spawns +
            "[Trigger]\nnickname = parent\nsystem = Zz01\nAct_SpawnFormation = zz_group, 40, 41, 42\nAct_ActTrig = t1\n" +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_wing\n"));
        Assert.Equal(new Vector3(40, 41, 42), chain.Start!.Position);
        Assert.Equal("parent", chain.StartSource);

        // A ship spawn without a position puts the ship at its mission ship. The ship spawn with a position takes the
        // orientation of the mission ship.
        var ship = Assert.Single(Uses(
            "[MsnShip]\nnickname = zz_ship\nposition = 1, 2, 3\norientation = 0, 1, 0, 0\n" +
            "[Trigger]\nnickname = early\nsystem = Zz01\nAct_SpawnShip = zz_ship, zz_orders\n" +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_ship\n"));
        Assert.Equal(SpaceStartRule.MissionObject, ship.StartRule);
        Assert.Equal("zz_ship", ship.StartSource);
        var moved = Assert.Single(Uses(
            "[MsnShip]\nnickname = zz_ship\nposition = 1, 2, 3\norientation = 0, 1, 0, 0\n" +
            "[Trigger]\nnickname = early\nsystem = Zz01\nAct_SpawnShip = zz_ship, zz_orders, 4, 5, 6\n" +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_ship\n"));
        Assert.Equal(new SpacePose(new Vector3(4, 5, 6), new Quaternion(1, 0, 0, 0)), moved.Start);
    }

    /// <summary>A rule on the trigger wins over the same rule on a trigger that starts it. An orientation reads with w
    /// first.</summary>
    [Fact]
    public void TheTriggerWinsOverItsParentAndAnOrientationReadsWFirst()
    {
        var use = Assert.Single(Uses(
            "[Trigger]\nnickname = parent\nsystem = Zz01\nAct_RelocateShip = zz_ship, 2, 2, 2\nAct_ActTrig = t1\n" +
            "[Trigger]\nnickname = t1\nAct_RelocateShip = zz_ship, 1, 1, 1, 0.5, 0.5, 0.5, 0.5\n" +
            $"Act_CallThorn = {Value}, zz_ship\n"));

        Assert.Equal(new Vector3(1, 1, 1), use.Start!.Position);
        Assert.Equal(new Quaternion(0.5f, 0.5f, 0.5f, 0.5f), use.Start.Orientation);
        Assert.Equal("t1", use.StartSource);

        var turned = Assert.Single(Uses(
            "[Trigger]\nnickname = t1\nsystem = Zz01\nAct_SetInitialPlayerPos = 0, 0, 0, 0.6, 0, 0.8, 0\n" +
            $"Act_CallThorn = {Value}\n"));
        Assert.Equal(new Quaternion(0, 0.8f, 0, 0.6f), turned.Start!.Orientation);

        var parent = Assert.Single(Uses(
            "[Trigger]\nnickname = parent\nAct_RelocateShip = zz_ship, 2, 2, 2\nAct_ActTrig = t1\n" +
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, zz_ship\n"));
        Assert.Equal(new Vector3(2, 2, 2), parent.Start!.Position);
        Assert.Equal("parent", parent.StartSource);
    }

    /// <summary>A doubled backslash names the script. A line without value 2 has the main object Player. Three numbers
    /// after the script give the position of decision D650, and the main object is the player.</summary>
    [Fact]
    public void TheValuesOfTheLineGiveTheScriptTheMainObjectAndThePosition()
    {
        var doubled = Assert.Single(Uses("[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = missions\\\\mx01\\\\a.thn\n"));
        Assert.Equal(SpaceUse.Player, doubled.MainObject);
        Assert.Null(doubled.Position);

        var placed = Assert.Single(Uses($"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}, 1, -2, 3\n"));
        Assert.Equal(new Vector3(1, -2, 3), placed.Position);
        Assert.Equal(SpaceUse.Player, placed.MainObject);
    }

    /// <summary>A bad line never throws: it gives a use without a system, and the problem tells why.</summary>
    [Fact]
    public void ABadLineDoesNotThrow()
    {
        var outside = Assert.Single(Uses($"[Mission]\nAct_CallThorn = {Value}\n"));
        Assert.Null(outside.System);
        Assert.Contains("[Trigger]", outside.Problem);

        var noName = Assert.Single(Uses($"[Trigger]\nsystem = Zz01\nAct_CallThorn = {Value}, zz_ship\n" +
                                        "Act_RelocateShip = zz_ship, x, y, z\n"));
        Assert.Equal("", noName.Trigger);
        Assert.Null(noName.Start);
    }

    /// <summary>The class reads each mission file once, also for two scripts of that file and for a second
    /// call.</summary>
    [Fact]
    public void EachMissionFileIsReadOnce()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\MISSIONS\MX01\mx01.ini",
            $"[Trigger]\nnickname = t1\nsystem = Zz01\nAct_CallThorn = {Value}\n" +
            "[Trigger]\nnickname = t2\nsystem = Zz02\nAct_CallThorn = missions\\mx01\\b.thn\n");
        var view = FileSystem.FromPath(ws.Base);
        var reads = new List<string>();
        var scenes = new SpaceScenes(path =>
        {
            reads.Add(path);
            return view.ReadAllBytes(path);
        }, ThnReferences.Scan(view), Facts);

        Assert.Equal("Zz01", Assert.Single(scenes.Of(Script)).System);
        Assert.Equal("Zz02", Assert.Single(scenes.Of(@"DATA\MISSIONS\MX01\b.thn")).System);
        Assert.Same(scenes.Of(Script), scenes.Of(Script.ToLowerInvariant()));
        Assert.Single(reads);
    }

    public void Dispose()
    {
        foreach (var ws in workspaces)
        {
            ws.Dispose();
        }
    }

    [Fact]
    public void AScriptWithoutALineHasNoUse()
    {
        Assert.Empty(Uses("[Trigger]\nnickname = t1\nsystem = Zz01\n"));
    }
}

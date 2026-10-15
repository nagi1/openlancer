using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Save;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Resources;
using LibreLancer.Server;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the start of a play (sections 9.5 to 9.7 and 10.3 of the M4 design): <see cref="PlaySave"/> makes
/// the save of the start, a headless server loads it, and <see cref="PlaySpawn"/> turns the ship. Each test waits for
/// the spawn packet or the base enter packet. The server sends the spawn packet from its spawn action, before it spawns
/// the ship. So a world action that the test queues after the packet runs after the spawn.
/// </summary>
/// <remarks>
/// The new character has its home at Planet Manhattan in Li01. The starts of the tests are in Br01 and at Planet New
/// London in Br01. So a save that keeps the home system or the home base of the new character fails the tests.
/// </remarks>
public sealed class PlayStartRetailTests(ITestOutputHelper output)
{
    /// <summary>The limit from the start of the server to the packet. The first load of a system is the slow part.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(30);

    /// <summary>A point in open space in Br01, about 9,000 units west of a trade lane.</summary>
    private static readonly Vector3 Br01Point = new(-33270, 0, -25039);

    private static readonly PlayStart InBr01 = new PlayStart.InSpace("br01", Br01Point, Quaternion.Identity);

    /// <summary>Planet New London in Br01.</summary>
    private const string NewLondon = "Br01_01_Base";

    /// <summary>The new-character data, which <c>GameServer.NewCharacter</c> reads.</summary>
    private const string NewCharacterFile = @"DATA\CHARACTERS\newcharacter.ini";

    static PlayStartRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>(1) A start in space with the package of a new multiplayer character.</summary>
    [Fact]
    public void ASpaceStartSpawnsTheNewCharacterShipAtThePoint()
    {
        GameDataPaths.RequireBase();
        var data = RetailGame.Instance.Data;
        Assert.NotEqual("br01", Home(data.Items).System!.ToLowerInvariant());
        using var headless = new HeadlessServer(data);

        Start(headless, InBr01, null, HeadlessServer.SpawnPacket);

        var player = headless.Player;
        var ship = headless.OnWorld(w => w.Players[player].LocalTransform);
        output.WriteLine($"System {player.System}, base {player.Base ?? "none"}, ship at {ship.Position}, " +
                         $"{Vector3.Distance(ship.Position, Br01Point)} from the start.");
        Assert.NotNull(player.Space);
        Assert.Null(player.Base);
        Assert.Equal("br01", player.System, ignoreCase: true);
        Assert.InRange(Vector3.Distance(ship.Position, Br01Point), 0, 0.01f);

        var character = player.Character!;
        Assert.Equal("ge_fighter", character.Ship!.Nickname, ignoreCase: true);
        // The Play menu of the editor names the same ship as the default ship.
        Assert.Equal("ge_fighter", PlaySave.DefaultShip(data.Items), ignoreCase: true);
        Assert.Equal(2000, character.Credits);
        var expected = NewCharacterMounts(data.Items);
        var mounted = Mounted(character);
        output.WriteLine($"MP_ge_fighter: {expected.Count} equip lines, {expected.Count(e => e.Item2 != "internal")} " +
                         $"with a hardpoint. The character has {mounted.Count} mounted items, " +
                         $"{character.Credits} credits and the ship {character.Ship.Nickname}.");
        Assert.Equal(16, expected.Count);
        Assert.Equal(expected, mounted);
        // The cargo lines of the loadout go into the hold.
        Assert.Equal([("ge_s_battery_01", 3), ("ge_s_repair_01", 3)], Hold(character));
    }

    /// <summary>(2) After the spawn, <see cref="PlaySpawn.Orient"/> turns the ship to the heading of the start.</summary>
    [Fact]
    public void OrientTurnsTheShipToTheHeadingOfTheStart()
    {
        GameDataPaths.RequireBase();
        using var headless = new HeadlessServer(RetailGame.Instance.Data);
        // Yaw 90 and pitch -30, as the Scene camera turns: RotationX(pitch) * RotationY(yaw).
        var heading = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, -MathF.PI / 6, 0);
        var start = new PlayStart.InSpace("br01", Br01Point, heading);
        Start(headless, start, null, HeadlessServer.SpawnPacket);
        var player = headless.Player;
        var before = headless.OnWorld(w => w.Players[player].LocalTransform);

        PlaySpawn.Orient(player, start);
        // The world runs its actions in order, so this read runs after the turn.
        var after = headless.OnWorld(w => w.Players[player].LocalTransform);
        // Some world updates later, the ship still has the heading.
        Thread.Sleep(500);
        var later = headless.OnWorld(w => w.Players[player].LocalTransform);

        output.WriteLine($"Dot product with the heading: {Dot(before, heading)} before the turn, {Dot(after, heading)} " +
                         $"after it, {Dot(later, heading)} 500 ms later. The ship is " +
                         $"{Vector3.Distance(after.Position, Br01Point)} from the start after the turn.");
        Assert.True(Dot(before, heading) < 0.9999f, "The ship had the heading before the turn.");
        Assert.True(Dot(after, heading) > 0.9999f, $"The dot product after the turn is {Dot(after, heading)}.");
        Assert.True(Dot(later, heading) > 0.9999f, $"The dot product 500 ms after the turn is {Dot(later, heading)}.");
        Assert.InRange(Vector3.Distance(after.Position, Br01Point), 0, 0.01f);
    }

    /// <summary>(3) A start at a base enters the base, in the system of the base.</summary>
    [Fact]
    public void ABaseStartEntersTheBase()
    {
        GameDataPaths.RequireBase();
        var data = RetailGame.Instance.Data;
        var home = Home(data.Items);
        Assert.NotEqual(NewLondon.ToLowerInvariant(), home.Nickname.ToLowerInvariant());
        Assert.NotEqual("br01", home.System!.ToLowerInvariant());
        using var headless = new HeadlessServer(data);

        Start(headless, new PlayStart.AtBase(NewLondon), null, HeadlessServer.BaseEnterPacket);

        var player = headless.Player;
        output.WriteLine($"Base {player.Base}, system {player.System}, space {(player.Space == null ? "none" : "set")}.");
        Assert.NotNull(player.Baseside);
        Assert.Equal("br01_01_base", player.Base, ignoreCase: true);
        Assert.Equal("br01", player.System, ignoreCase: true);
        Assert.Null(player.Space);
    }

    /// <summary>(4) A ship package that only a mod has. The data comes from the play view of a copy of the sample
    /// mod.</summary>
    [Fact]
    public void AModPackageGivesItsShipAndItsAddons()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "play-package",
            lowerMods: [GameDataPaths.SampleMod("ol-sample")]);
        var data = Load(PlayView.Build(ModProfile.Load(workspace.ProfilePath), workspace.ModId));
        // The convex shapes go after the server, which uses them.
        using var shapes = data.Resources.ConvexCollection;
        using var headless = new HeadlessServer(data);

        Start(headless, InBr01, "ol_starrunner_package", HeadlessServer.SpawnPacket);

        var character = headless.Player.Character!;
        var mounted = Mounted(character);
        output.WriteLine($"Ship {character.Ship!.Nickname}, {mounted.Count} mounted items, " +
                         $"{character.Items.Count - mounted.Count} items in the hold, {character.Credits} credits.");
        Assert.Equal("ol_starrunner", character.Ship.Nickname, ignoreCase: true);
        Assert.Equal(Sorted([
            ("ge_le_engine_01", "internal"),
            ("li_elite_power01", "internal"),
            ("ge_s_scanner_01", "internal"),
            ("ge_s_tractor_01", "internal"),
            ("shield01_mark02_hf", "HpShield01"),
            ("li_gun01_mark01", "HpWeapon01"),
            ("li_gun01_mark01", "HpWeapon02"),
        ]), mounted);
        Assert.Equal(7, mounted.Count);
        Assert.Equal(character.Items.Count, mounted.Count);
        Assert.Equal(2000, character.Credits);
    }

    /// <summary>(5) A retail ship package, at a base start.</summary>
    [Fact]
    public void ARetailPackageGivesItsShipAndItsAddons()
    {
        GameDataPaths.RequireBase();
        using var headless = new HeadlessServer(RetailGame.Instance.Data);

        Start(headless, new PlayStart.AtBase(NewLondon), "le_package", HeadlessServer.BaseEnterPacket);

        var player = headless.Player;
        var character = player.Character!;
        var mounted = Mounted(character);
        output.WriteLine($"Base {player.Base}, system {player.System}. Ship {character.Ship!.Nickname}, " +
                         $"{mounted.Count} mounted items, {character.Items.Count - mounted.Count} items in the hold.");
        Assert.Equal("br01_01_base", player.Base, ignoreCase: true);
        Assert.Equal("br01", player.System, ignoreCase: true);
        Assert.Equal("li_elite", character.Ship.Nickname, ignoreCase: true);
        Assert.Equal(Sorted([
            ("ge_le_engine_01", "internal"),
            ("li_elite_power01", "internal"),
            ("ge_s_scanner_01", "internal"),
            ("ge_s_tractor_01", "internal"),
            ("shield01_mark02_hf", "HpShield01"),
            ("LargeWhiteSpecial", "HpHeadlight"),
            ("SlowSmallBlue", "HpRunningLight01"),
            ("SlowSmallBlue", "HpRunningLight02"),
            ("SlowSmallBlue", "HpRunningLight03"),
            ("SlowSmallBlue", "HpRunningLight04"),
            ("contrail01", "HpContrail01"),
            ("contrail01", "HpContrail02"),
            ("DockingLightRedSmall", "HpDockLight01"),
            ("DockingLightRedSmall", "HpDockLight02"),
        ]), mounted);
        // The batteries and the nanobots of the new-character loadout are not in the save.
        Assert.Equal(character.Items.Count, mounted.Count);
    }

    /// <summary>(6) <see cref="PlaySave.Check"/> gives the reason of a start that the data cannot make, and
    /// <see cref="PlaySave.Create"/> throws with the same reason, so the caller gets no save.</summary>
    [Fact]
    public void CheckGivesTheReasonOfAnUnknownSystemBaseOrPackage()
    {
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        using var headless = new HeadlessServer(RetailGame.Instance.Data);
        (PlayStart Start, string? Package, string Reason)[] cases =
        [
            (new PlayStart.InSpace("no_such_system", Br01Point, Quaternion.Identity), null,
                "The system 'no_such_system' is not in the game data."),
            (new PlayStart.AtBase("no_such_base"), null, "The base 'no_such_base' is not in the game data."),
            (InBr01, "no_such_package", "The ship package 'no_such_package' is not in the game data."),
        ];

        foreach (var (start, package, reason) in cases)
        {
            Assert.Equal(reason, PlaySave.Check(items, start, package));
            var stop = Assert.Throws<ArgumentException>(() => PlaySave.Create(headless.Server, start, package));
            Assert.Equal(reason, stop.Message);
        }
        Assert.Null(PlaySave.Check(items, InBr01, null));
        Assert.Null(PlaySave.Check(items, new PlayStart.AtBase("BR01_01_BASE"), "LE_PACKAGE"));
    }

    /// <summary>(6) The new-character data must make a character. A temp mod patches the new-character faction or
    /// package, and <see cref="PlaySave.Check"/> names the problem. No row covers the template file
    /// <c>EXE\mpnewcharacter.fl</c>: a mod can add or replace a file, but it cannot hide a file of the base data.</summary>
    [Theory]
    [InlineData("[Faction]\r\nnickname = new_player\r\nol.delete = true\r\n",
        "The new-character data has no faction.")]
    [InlineData("[Faction]\r\nnickname = new_player\r\npilot = no_such_pilot\r\n",
        "The new-character faction 'new_player' names the pilot 'no_such_pilot', which the new-character data " +
        "does not have.")]
    [InlineData("[Faction]\r\nnickname = new_player\r\npackage = no_such_package\r\n",
        "The new-character faction 'new_player' names the package 'no_such_package', which the new-character data " +
        "does not have.")]
    [InlineData("[Faction]\r\nnickname = new_player\r\nbase = no_such_base\r\n",
        "The new-character faction 'new_player' names the base 'no_such_base', which is not in the game data.")]
    [InlineData("[Package]\r\nnickname = ge_fighter\r\nloadout = no_such_loadout\r\n",
        "The new-character package 'ge_fighter' names the loadout 'no_such_loadout', which is not in the game data.")]
    public void CheckGivesTheReasonOfNewCharacterDataThatCannotMakeACharacter(string patch, string reason)
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "no-new-character");
        var items = EngineDataLoader.Load(Patched(workspace, NewCharacterFile, patch).CreateFileSystem())
            .RequireComplete();

        Assert.Equal(reason, PlaySave.Check(items, InBr01, null));
    }

    /// <summary>(6) The ship of the new-character package counts only when the play uses that package. With a ship
    /// package, the save gets the ship of that package.</summary>
    [Fact]
    public void CheckNeedsTheShipOfTheNewCharacterPackageOnlyWithoutAShipPackage()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "no-new-character-ship");
        var patch = "[Package]\r\nnickname = ge_fighter\r\nship = no_such_ship\r\n";
        var items = EngineDataLoader.Load(Patched(workspace, NewCharacterFile, patch).CreateFileSystem())
            .RequireComplete();

        Assert.Equal("The new-character package 'ge_fighter' names the ship 'no_such_ship', which is not in the game " +
                     "data.", PlaySave.Check(items, InBr01, null));
        Assert.Null(PlaySave.Check(items, InBr01, "le_package"));
    }

    /// <summary>(7) The save has no story, so the play starts in free roam.</summary>
    [Fact]
    public void ThePlayStartsInFreeRoam()
    {
        GameDataPaths.RequireBase();
        using var headless = new HeadlessServer(RetailGame.Instance.Data);

        var save = Start(headless, InBr01, null, HeadlessServer.SpawnPacket);

        Assert.Null(save.StoryInfo);
        Assert.NotNull(headless.Player.Story);
        Assert.Null(headless.Player.Story.CurrentMission);
    }

    /// <summary>(8) An equip line of a combinable good in the loadout of the new-character package goes into the hold
    /// (decision D56). The server cannot mount such a good. Here a temp mod adds a battery to the equip lines of
    /// <c>MP_ge_fighter</c>.</summary>
    [Fact]
    public void ACombinableGoodOfTheNewCharacterLoadoutGoesIntoTheHold()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var expected = NewCharacterMounts(RetailGame.Instance.Data.Items);
        var lines = RetailGame.Instance.Data.Items.Ini.Loadouts.Loadouts.Single(IsNewCharacterLoadout).Equip
            .Select(e => e.Hardpoint == null ? $"equip = {e.Nickname}" : $"equip = {e.Nickname}, {e.Hardpoint}");
        using var workspace = new RetailWorkspace(baseFolder, "combinable-equip");
        var patch = $"[Loadout]\r\nnickname = MP_ge_fighter\r\n{string.Join("\r\n", lines)}\r\nequip = ge_s_battery_01\r\n";
        var data = Load(Patched(workspace, @"DATA\SHIPS\loadouts.ini", patch));
        // The convex shapes go after the server, which uses them.
        using var shapes = data.Resources.ConvexCollection;
        using var headless = new HeadlessServer(data);

        var save = Start(headless, InBr01, null, HeadlessServer.SpawnPacket);

        var character = headless.Player.Character!;
        output.WriteLine($"The save has {save.Player!.Equip.Count} equip lines and {save.Player.Cargo.Count} cargo " +
                         $"lines. The hold has {string.Join(", ", Hold(character))}.");
        Assert.Equal(expected, Mounted(character));
        // The server adds the battery of the equip line to the 3 batteries of the cargo line.
        Assert.Equal([("ge_s_battery_01", 4), ("ge_s_repair_01", 3)], Hold(character));
    }

    /// <summary>Makes the save of the start, starts the server with it and waits for <paramref name="packet"/>.</summary>
    private SaveGame Start(HeadlessServer headless, PlayStart start, string? package, string packet)
    {
        var save = PlaySave.Create(headless.Server, start, package);
        var clock = Stopwatch.StartNew();
        headless.Start(save);
        headless.WaitForPacket(packet, StartLimit);
        output.WriteLine($"{packet} after {clock.ElapsedMilliseconds} ms.");
        return save;
    }

    /// <summary>The play view of the temp mod of <paramref name="workspace"/> with one patch file.</summary>
    /// <param name="path">The install path of the file that the patch changes.</param>
    private static ModOverlay Patched(RetailWorkspace workspace, string path, string patch)
    {
        var file = Path.Combine(workspace.ModFolder, Mod.PatchesFolder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, patch);
        var overlay = PlayView.Build(ModProfile.Load(workspace.ProfilePath), workspace.ModId);
        Assert.False(overlay.HasErrors, string.Join("\n", overlay.Diagnostics));
        return overlay;
    }

    /// <summary>
    /// The game data of a play view, as the play process loads it, with a resource manager without a GPU. The caller
    /// disposes the convex shapes of the resource manager. In a Debug build, the buffer pool of the shapes fails an
    /// assert when the collector finds it not cleared, and the test host ends.
    /// </summary>
    private static GameDataManager Load(ModOverlay overlay)
    {
        var fs = overlay.CreateFileSystem();
        var data = new GameDataManager(new GameItemDb(fs), new ServerResourceManager(null, fs));
        data.LoadData(null);
        return data;
    }

    /// <summary>The home base of the new character: the base of the first new-character faction.</summary>
    private static Base Home(GameItemDb items) => items.Bases.Get(items.Ini.NewCharDB.Factions[0].Base)!;

    private static bool IsNewCharacterLoadout(Loadout loadout) =>
        loadout.Nickname.Equals("MP_ge_fighter", StringComparison.OrdinalIgnoreCase);

    /// <summary>The mounted items that the equip lines of <c>MP_ge_fighter</c> give, sorted. The server mounts an
    /// equip line without a hardpoint at "internal".</summary>
    private static List<(string, string)> NewCharacterMounts(GameItemDb items) =>
        Sorted(items.Ini.Loadouts.Loadouts.Single(IsNewCharacterLoadout).Equip
            .Select(e => (e.Nickname, e.Hardpoint ?? "internal")));

    /// <summary>The mounted items of a character: the equipment and the hardpoint, sorted.</summary>
    private static List<(string, string)> Mounted(NetCharacter character) =>
        Sorted(character.Items.Where(i => i.Hardpoint != null).Select(i => (i.Equipment!.Nickname, i.Hardpoint!)));

    /// <summary>The items in the hold of a character: the equipment and the count, sorted.</summary>
    private static List<(string, int)> Hold(NetCharacter character) =>
        character.Items.Where(i => i.Hardpoint == null)
            .Select(i => (i.Equipment!.Nickname.ToLowerInvariant(), i.Count)).Order().ToList();

    private static List<(string, string)> Sorted(IEnumerable<(string Item, string Hardpoint)> items) =>
        items.Select(i => (i.Item.ToLowerInvariant(), i.Hardpoint.ToLowerInvariant())).Order().ToList();

    /// <summary>The absolute dot product of two orientations: 1 for the same turn.</summary>
    private static float Dot(Transform3D transform, Quaternion orientation) =>
        MathF.Abs(Quaternion.Dot(Quaternion.Normalize(transform.Orientation), orientation));
}

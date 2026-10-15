using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.Market;
using LibreLancer.Data.Schema.Goods;
using LibreLancer.Data.Schema.Save;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rules of the equipment and the cargo of a play save (decisions D47 and D56), on synthetic data. The server
/// mounts an <c>equip</c> line at its hardpoint and puts a <c>cargo</c> line into the hold.
/// </summary>
public sealed class PlaySaveTests
{
    [Fact]
    public void AnAddonWithTheAmountOneGoesOntoItsHardpoint()
    {
        var player = Equip(new PackageAddon(Item("test_gun"), "HpWeapon01", 1));

        var line = Assert.Single(player.Equip);
        Assert.Equal(FLHash.CreateID("test_gun"), (uint)line.Item);
        Assert.Equal("HpWeapon01", line.Hardpoint);
        Assert.Empty(player.Cargo);
    }

    [Fact]
    public void AnAddonWithTheAmountOneAndNoHardpointGivesAnEquipLineWithoutOne()
    {
        var player = Equip(new PackageAddon(Item("test_engine"), null, 1));

        var line = Assert.Single(player.Equip);
        Assert.Equal(FLHash.CreateID("test_engine"), (uint)line.Item);
        Assert.True(string.IsNullOrEmpty(line.Hardpoint), $"The line has the hardpoint '{line.Hardpoint}'.");
        Assert.Empty(player.Cargo);
    }

    [Fact]
    public void AnAddonWithALargerAmountGoesIntoTheHold()
    {
        var player = Equip(
            new PackageAddon(Item("test_mine"), "HpMine01", 2),
            new PackageAddon(Item("test_missile"), "internal", 3));

        Assert.Empty(player.Equip);
        Assert.Equal([(FLHash.CreateID("test_mine"), 2), (FLHash.CreateID("test_missile"), 3)], Cargo(player));
    }

    [Fact]
    public void ACombinableGoodGoesIntoTheHoldAlsoWithTheAmountOne()
    {
        var player = Equip(new PackageAddon(Battery(), "internal", 1));

        Assert.Empty(player.Equip);
        Assert.Equal([(FLHash.CreateID("test_battery"), 1)], Cargo(player));
    }

    [Fact]
    public void ACombinableGoodOfTheNewCharacterLoadoutGoesIntoTheHold()
    {
        var player = OldSave();
        player.Equip.Add(new PlayerEquipment { Item = new HashValue("test_battery"), Hardpoint = "" });

        PlaySave.Equip(player, null, [Item("old_gun"), Item("old_battery"), Battery()]);

        // Without a package, the save keeps its ship, its other equipment and its cargo.
        Assert.Equal(FLHash.CreateID("old_ship"), (uint)player.ShipArchetype);
        var line = Assert.Single(player.Equip);
        Assert.Equal(FLHash.CreateID("old_gun"), (uint)line.Item);
        Assert.Equal([(FLHash.CreateID("old_battery"), 3), (FLHash.CreateID("test_battery"), 1)], Cargo(player));
    }

    [Fact]
    public void AnAddonWithAnAmountBelowOneGivesNoLine()
    {
        var player = Equip(new PackageAddon(Item("test_gun"), "HpWeapon01", 0),
            new PackageAddon(Item("test_mine"), "internal", -2));

        Assert.Empty(player.Equip);
        Assert.Empty(player.Cargo);
    }

    [Fact]
    public void ThePackageReplacesTheShipTheEquipmentAndTheCargoOfTheSave()
    {
        var player = Equip(new PackageAddon(Item("test_gun"), "HpWeapon01", 1));

        Assert.Equal(FLHash.CreateID("test_ship"), (uint)player.ShipArchetype);
        Assert.DoesNotContain(player.Equip, e => e.Item == new HashValue("old_gun"));
        Assert.DoesNotContain(player.Cargo, c => c.Item == new HashValue("old_battery"));
    }

    /// <summary>A save with an old ship, an old gun and old cargo, after <see cref="PlaySave.Equip"/> with a package of
    /// the ship <c>test_ship</c>. The equipment of the data is the equipment of the addons.</summary>
    private static SavePlayer Equip(params PackageAddon[] addons)
    {
        var player = OldSave();
        var ship = new Ship { Nickname = "test_ship", CRC = FLHash.CreateID("test_ship") };
        var package = new ShipPackage("test_package", ship);
        package.Addons.AddRange(addons);
        var equipment = new GameItemCollection<Equipment>();
        foreach (var addon in addons)
            equipment.Add(addon.Equipment);

        PlaySave.Equip(player, package, equipment);
        return player;
    }

    /// <summary>A save with the ship <c>old_ship</c>, the gun <c>old_gun</c> and 3 of <c>old_battery</c>.</summary>
    private static SavePlayer OldSave()
    {
        var player = new SavePlayer { ShipArchetype = new HashValue("old_ship") };
        player.Equip.Add(new PlayerEquipment { Item = new HashValue("old_gun"), Hardpoint = "HpWeapon03" });
        player.Cargo.Add(new PlayerCargo { Item = new HashValue("old_battery"), Count = 3 });
        return player;
    }

    private static Equipment Item(string nickname) => new() { Nickname = nickname, CRC = FLHash.CreateID(nickname) };

    /// <summary>The equipment <c>test_battery</c>, with a combinable good.</summary>
    private static Equipment Battery()
    {
        var battery = Item("test_battery");
        battery.Good = new ResolvedGood
        {
            Nickname = battery.Nickname,
            CRC = battery.CRC,
            Equipment = battery,
            Ini = new Good { Nickname = battery.Nickname, Combinable = true },
        };
        return battery;
    }

    private static (uint, int)[] Cargo(SavePlayer player) => player.Cargo.Select(c => ((uint)c.Item, c.Count)).ToArray();
}

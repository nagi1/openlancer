using System;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.Market;
using LibreLancer.Data.Schema.NewCharDB;
using LibreLancer.Data.Schema.Save;
using LibreLancer.Server;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The save that starts a play (section 10.3 of the M4 design). It is the multiplayer new-character save of the server,
/// with the start point of the editor. When the user selected a ship package, the save has the ship of that package
/// (decision D15). The save has no <c>[StoryInfo]</c>, so the game starts in free roam.
/// </summary>
public static class PlaySave
{
    /// <summary>The name of the character of a play.</summary>
    private const string CharacterName = "OpenLancer";

    /// <summary>The faction of the new-character data that the save uses. The first faction gives the package of a new
    /// multiplayer character.</summary>
    private const int Faction = 0;

    /// <summary>The template of the new-character save. <c>GameServer.NewCharacter</c> reads it.</summary>
    private const string TemplateFile = @"EXE\mpnewcharacter.fl";

    /// <summary>Tells if the data can start the play. <see cref="Create"/> runs this check first.</summary>
    /// <param name="package">The nickname of the ship package, or null for the package of a new multiplayer
    /// character.</param>
    /// <returns>Null when the data can start the play. Else the reason: an unknown system, base or package, or
    /// new-character data that cannot make a character.</returns>
    public static string? Check(GameItemDb items, PlayStart start, string? package) =>
        StartProblem(items, start) ?? PackageProblem(items, package) ?? NewCharacterProblem(items, package == null);

    /// <summary>
    /// The nickname of the ship of a play without a package: the ship of the package of the new-character faction. Null
    /// when the new-character data has no such faction or package. The Play menu of the editor names this ship.
    /// </summary>
    public static string? DefaultShip(GameItemDb items) => DefaultPackage(items)?.Ship;

    /// <summary>The package of the new-character faction, or null when the new-character data has no such faction or
    /// package. The player ship of the cutscene preview uses it too.</summary>
    internal static NewCharPackage? DefaultPackage(GameItemDb items)
    {
        var data = items.Ini.NewCharDB;
        return data.Factions.Count > Faction ? NewCharacterPackage(data, data.Factions[Faction]) : null;
    }

    /// <summary>
    /// The multiplayer new-character save of the server, with the start point. With a package, the save gets the ship,
    /// the equipment and the cargo of that package. The money stays the money of the new character. See
    /// <see cref="Equip"/> for the rules of the equipment and the cargo.
    /// </summary>
    /// <exception cref="ArgumentException">The data cannot start the play. The message is the reason of
    /// <see cref="Check"/>.</exception>
    public static SaveGame Create(GameServer server, PlayStart start, string? package)
    {
        var items = server.GameData.Items;
        if (Check(items, start, package) is { } problem)
            throw new ArgumentException(problem);
        var save = server.NewCharacter(CharacterName, Faction);
        var player = save.Player!;
        switch (start)
        {
            case PlayStart.InSpace space:
                // The new character starts at its home base. Without a base, the server spawns the ship in space.
                player.Base = null;
                player.System = items.Systems.Get(space.System)!.Nickname;
                player.Position = space.Position;
                break;
            case PlayStart.AtBase atBase:
                var dock = items.Bases.Get(atBase.Base)!;
                player.Base = dock.Nickname;
                player.System = dock.System;
                break;
        }
        Equip(player, package == null ? null : items.GetShipPackage(FLHash.CreateID(package))!, items.Equipment);
        return save;
    }

    /// <summary>
    /// Gives the save the equipment and the cargo that the server can load (decisions D47 and D56). The server mounts
    /// each <c>equip</c> line at its hardpoint, at <c>internal</c> when the line has none, and puts each <c>cargo</c>
    /// line into the hold.
    /// </summary>
    /// <remarks>
    /// With a package, the save gets the ship of the package, and the addons of the package replace the equipment and
    /// the cargo of the save:
    /// <list type="bullet">
    /// <item>An addon with the amount 1 becomes an <c>equip</c> line at its hardpoint.</item>
    /// <item>An addon with a larger amount becomes a <c>cargo</c> line with that amount.</item>
    /// <item>An addon with an amount below 1 gives no line.</item>
    /// </list>
    /// Then each <c>equip</c> line of a combinable good becomes a <c>cargo</c> line with the amount 1, with or without
    /// a package. The line can come from an addon, from the loadout of the new-character package, or from the autosave
    /// of a reload when the new data makes a mounted good combinable. The server cannot mount such a good: it throws on
    /// its thread, and the game ends.
    /// </remarks>
    /// <param name="package">The ship package, or null to keep the ship and the lines of the save.</param>
    /// <param name="equipment">The equipment of the game data, which tells the good of each line.</param>
    internal static void Equip(SavePlayer player, ShipPackage? package, GameItemCollection<Equipment> equipment)
    {
        if (package != null)
        {
            player.ShipArchetype = new HashValue(package.Ship.Nickname);
            player.Equip.Clear();
            player.Cargo.Clear();
            foreach (var addon in package.Addons.Where(a => a.Amount > 0))
            {
                var item = new HashValue(addon.Equipment.Nickname);
                if (addon.Amount == 1)
                    player.Equip.Add(new PlayerEquipment { Item = item, Hardpoint = addon.Hardpoint });
                else
                    player.Cargo.Add(new PlayerCargo { Item = item, Count = addon.Amount });
            }
        }

        // The rule of the server for a good that it adds to a stack in the hold.
        bool Combinable(PlayerEquipment line) => equipment.Get(line.Item)?.Good?.Ini.Combinable ?? false;
        player.Cargo.AddRange(player.Equip.Where(Combinable).Select(l => new PlayerCargo { Item = l.Item, Count = 1 }));
        player.Equip.RemoveAll(Combinable);
    }

    /// <summary>The reason when the items do not have the system. The check of a reload uses it too.</summary>
    internal static string? SystemProblem(GameItemDb items, string system) =>
        items.Systems.Get(system) == null ? $"The system '{system}' is not in the game data." : null;

    /// <summary>The reason when the items do not have the base. The check of a reload uses it too.</summary>
    internal static string? BaseProblem(GameItemDb items, string dock) =>
        items.Bases.Get(dock) == null ? $"The base '{dock}' is not in the game data." : null;

    private static string? StartProblem(GameItemDb items, PlayStart start) => start switch
    {
        PlayStart.InSpace space => SystemProblem(items, space.System),
        PlayStart.AtBase atBase => BaseProblem(items, atBase.Base),
        _ => null,
    };

    private static string? PackageProblem(GameItemDb items, string? package) =>
        package != null && items.GetShipPackage(FLHash.CreateID(package)) == null
            ? $"The ship package '{package}' is not in the game data."
            : null;

    /// <summary>
    /// Finds what <c>GameServer.NewCharacter</c> reads and does not find. The server would throw, or make a character
    /// without a ship.
    /// </summary>
    /// <param name="usesItsShip">True when the save keeps the ship of the new-character package.</param>
    private static string? NewCharacterProblem(GameItemDb items, bool usesItsShip)
    {
        var data = items.Ini.NewCharDB;
        if (data.Factions.Count <= Faction)
            return "The new-character data has no faction.";
        var faction = data.Factions[Faction];
        if (!data.Pilots.Any(p => Same(p.Nickname, faction.Pilot)))
            return $"The new-character faction '{faction.Nickname}' names the pilot '{faction.Pilot}', which the " +
                   "new-character data does not have.";
        var package = NewCharacterPackage(data, faction);
        if (package == null)
            return $"The new-character faction '{faction.Nickname}' names the package '{faction.Package}', which the " +
                   "new-character data does not have.";
        if (items.Bases.Get(faction.Base) == null)
            return $"The new-character faction '{faction.Nickname}' names the base '{faction.Base}', which is not in " +
                   "the game data.";
        if (!items.Ini.Loadouts.Loadouts.Any(l => Same(l.Nickname, package.Loadout)))
            return $"The new-character package '{package.Nickname}' names the loadout '{package.Loadout}', which is " +
                   "not in the game data.";
        if (usesItsShip && items.Ships.Get(package.Ship) == null)
            return $"The new-character package '{package.Nickname}' names the ship '{package.Ship}', which is not in " +
                   "the game data.";
        return items.VFS.FileExists(TemplateFile) ? null : $"The game data has no {TemplateFile}.";
    }

    /// <summary>The package that a new-character faction names, or null.</summary>
    private static NewCharPackage? NewCharacterPackage(NewCharDBIni data, NewCharFaction faction) =>
        data.Packages.FirstOrDefault(p => Same(p.Nickname, faction.Package));

    private static bool Same(string a, string? b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

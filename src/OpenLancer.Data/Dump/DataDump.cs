using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Dump;

/// <summary>
/// A canonical JSON view of the game data as Librelancer resolves it: ships, equipment, goods, what each base
/// sells, factions, and the objects of each star system.
/// </summary>
/// <remarks>
/// Every list is sorted, and the load is checked for completeness (see <see cref="EngineDataLoader"/>), so two
/// dumps of the same data are byte-identical. Use it to compare two ways of loading one mod stack, or to see what a
/// mod changes. A dump of retail data is retail-derived. Keep it outside the repo.
/// </remarks>
public static class DataDump
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <exception cref="EngineLoadException">Librelancer could not load the data completely.</exception>
    public static string Create(FileSystem fs) => Create(EngineDataLoader.Load(fs).RequireComplete());

    public static string Create(GameItemDb db)
    {
        var dump = new
        {
            ships = Sorted(db.Ships, s => new
            {
                s.Nickname,
                s.IdsName,
                s.IdsInfo,
                model = s.ModelFile?.ModelFile,
                s.Mass,
                s.Hitpoints,
                s.HoldSize,
                hardpoints = s.HardpointTypes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            }),
            equipment = Sorted(db.Equipment, e => new
            {
                e.Nickname,
                type = e.GetType().Name,
                e.IdsName,
                e.IdsInfo,
                e.HpType,
                model = e.ModelFile?.ModelFile,
            }),
            goods = Sorted(db.Goods, g => new { g.Nickname, equipment = g.Equipment.Nickname, g.Ini.Price }),
            bases = Sorted(db.Bases, b => new
            {
                b.Nickname,
                b.System,
                soldShips = b.SoldShips
                    .OrderBy(s => s.Package.Nickname, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new
                    {
                        package = s.Package.Nickname,
                        ship = s.Package.Ship.Nickname,
                        s.Package.BasePrice,
                        s.Rank,
                        addons = s.Package.Addons
                            .Select(a => $"{a.Equipment.Nickname}, {a.Hardpoint}, {a.Amount}")
                            .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    }).ToArray(),
                soldGoods = b.SoldGoods
                    .OrderBy(g => g.Good.Nickname, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.Price)
                    .Select(g => new { good = g.Good.Nickname, g.Price, g.Rank, g.Rep, g.ForSale })
                    .ToArray(),
            }),
            factions = Sorted(db.Factions, f => new
            {
                f.Nickname,
                f.IdsName,
                reputations = f.Reputations
                    .OrderBy(r => r.Key.Nickname, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(r => r.Key.Nickname, r => r.Value),
            }),
            systems = Sorted(db.Systems, s => new
            {
                s.Nickname,
                s.IdsName,
                objects = s.Objects
                    .OrderBy(o => o.Nickname, StringComparer.OrdinalIgnoreCase)
                    .Select(o => new
                    {
                        o.Nickname,
                        o.IdsName,
                        archetype = o.Archetype?.Nickname,
                        position = Vector(o.Position),
                        rotation = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W },
                        @base = o.Base?.Nickname,
                        dock = o.Dock == null ? null : $"{o.Dock.Kind} {o.Dock.Target}",
                        loadout = o.Loadout?.Nickname,
                    }).ToArray(),
            }),
        };
        return JsonSerializer.Serialize(dump, JsonOptions);
    }

    private static TOut[] Sorted<TIn, TOut>(IEnumerable<TIn> items, Func<TIn, TOut> select) where TIn : NicknameItem =>
        items.OrderBy(i => i.Nickname, StringComparer.OrdinalIgnoreCase).Select(select).ToArray();

    private static float[] Vector(Vector3 v) => [v.X, v.Y, v.Z];
}

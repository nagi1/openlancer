using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.Market;
using LibreLancer.Data.IO;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Data;

namespace OpenLancer.Runtime.Validation;

/// <summary>
/// Checks the resolved ship packages against the ship models and the server's purchase rules.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each addon that is not internal must use a hardpoint that the ship model has. The engine attaches an item
/// with an unknown hardpoint at the ship's origin and logs nothing.</item>
/// <item>Each gun and shield addon must fit its hardpoint by the server's rule
/// (<see cref="CargoUtilities.SupportsHardpoint"/>). Otherwise the server rejects every purchase of the package.</item>
/// </list>
/// The check loads the ship models without a GPU.
/// </remarks>
public static class PackageCheck
{
    public const string Source = "packages";

    public static IReadOnlyList<Diagnostic> Check(GameItemDb db, IEnumerable<string> packageNicknames, FileSystem fs)
    {
        var resources = new ServerResourceManager(null, fs);
        using var shapes = resources.ConvexCollection;
        var problems = new List<Diagnostic>();
        var hardpointsByModel = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        using (EngineLogCapture.Start())
        {
            // Model load messages are not data problems of the packages. A model that does not load is reported below.
            foreach (var nickname in packageNicknames.Order(StringComparer.Ordinal))
            {
                if (db.GetShipPackage(FLHash.CreateID(nickname)) is { } package)
                    CheckPackage(package, db, resources, hardpointsByModel, problems);
            }
        }
        return problems;
    }

    private static void CheckPackage(ShipPackage package, GameItemDb db, ResourceManager resources,
        Dictionary<string, HashSet<string>?> cache, List<Diagnostic> problems)
    {
        var label = $"Ship package '{package.Nickname}'";
        var model = package.Ship.ModelFile;
        if (model?.ModelFile == null)
        {
            problems.Add(Diagnostic.Error(Source, $"{label}: ship '{package.Ship.Nickname}' has no model."));
            return;
        }
        if (!cache.TryGetValue(model.ModelFile, out var hardpoints))
        {
            try
            {
                var file = model.LoadFile(resources)?.Drawable as IRigidModelFile;
                hardpoints = file?.CreateRigidModel(false, resources).AllParts
                    .SelectMany(p => p.Hardpoints)
                    .Select(h => h.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (hardpoints == null)
                    problems.Add(Diagnostic.Error(Source, $"{label}: model {model.ModelFile} did not load."));
            }
            catch (Exception e)
            {
                hardpoints = null;
                problems.Add(Diagnostic.Error(Source,
                    $"{label}: model {model.ModelFile} did not load: {e.GetType().Name}: {e.Message}"));
            }
            cache[model.ModelFile] = hardpoints;
        }

        foreach (var addon in package.Addons)
        {
            var equipment = addon.Equipment;
            var isInternal = addon.Hardpoint == null ||
                             addon.Hardpoint.Equals("internal", StringComparison.OrdinalIgnoreCase);
            if (!isInternal && hardpoints != null && !hardpoints.Contains(addon.Hardpoint!))
            {
                problems.Add(Diagnostic.Error(Source,
                    $"{label}: addon '{equipment.Nickname}' uses hardpoint '{addon.Hardpoint}', which model " +
                    $"{model.ModelFile} does not have."));
                continue;
            }
            // The server checks every gun and shield of a package, also one on 'internal'.
            if (equipment is GunEquipment or ShieldEquipment &&
                !CargoUtilities.SupportsHardpoint(package.Ship, db.Ini.HpTypes, equipment.HpType, addon.Hardpoint))
            {
                problems.Add(Diagnostic.Error(Source,
                    $"{label}: addon '{equipment.Nickname}' (type {equipment.HpType}) does not fit hardpoint " +
                    $"'{addon.Hardpoint ?? "internal"}' of ship '{package.Ship.Nickname}'. The server rejects the purchase."));
            }
        }
    }
}

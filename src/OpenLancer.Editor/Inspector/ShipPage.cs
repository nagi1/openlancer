using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.GameData;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a ship: identity, hull and cargo, physics, camera, model, the hardpoints, the goods of the
/// ship and the bases that sell it.
/// </summary>
/// <remarks>
/// The rows bind to the <c>[Ship]</c> section of the edit view. The price row binds to the last hull good of the ship,
/// because the engine takes the price from it. The hardpoint rows show the game data, because their key holds more
/// than one hardpoint type.
/// </remarks>
internal sealed class ShipPage(EditorWindow window, PropertyGrid grid)
{
    private readonly MarketSection market = new(window, grid);

    // The hardpoint rows of the last ship: each hardpoint type, sorted, and its hardpoints. They are made again only
    // for a different ship.
    private Ship? hardpointShip;
    private (string Type, string Hardpoints)[] hardpointRows = [];

    /// <summary>Forgets the last ship. Call it when the profile closes.</summary>
    public void Reset()
    {
        hardpointShip = null;
        hardpointRows = [];
    }

    public void Draw(Ship ship)
    {
        var edits = window.Session?.Edits;
        var section = edits?.Find(ItemFileKind.Ships, ship.Nickname);
        // The engine takes the price and the icon of a ship from its last hull good.
        var hulls = section == null ? [] : edits!.Related(section, ItemRelation.Hull);
        using (grid.Bind(section))
        {
            Identity(ship);
            HullAndCargo(ship, hulls.Count > 0 ? hulls[^1] : null);
            Physics(ship);
            Camera(ship);
            Model(ship);
        }

        Hardpoints(ship);
        if (edits != null && section != null)
        {
            Goods(edits, section, hulls);
        }
    }

    /// <summary>The sections of a ship in W, for the raw block of the page: the <c>[Ship]</c>, its hull goods and its
    /// packages.</summary>
    public IReadOnlyList<SectionRef> RawSections(Ship ship)
    {
        if (window.Session?.Edits is not { } edits || edits.Find(ItemFileKind.Ships, ship.Nickname) is not { } section)
        {
            return [];
        }

        return [section, .. edits.Related(section, ItemRelation.Hull), .. edits.Related(section, ItemRelation.Package)];
    }

    /// <summary>The hull goods and the packages of the ship, as links, and the bases that sell each package.</summary>
    private void Goods(EditSession edits, SectionRef section, IReadOnlyList<SectionRef> hulls)
    {
        var packages = edits.Related(section, ItemRelation.Package);
        if (grid.BeginSection("Ship.Goods", EditorIcons.Tags, "Goods"))
        {
            foreach (var good in hulls.Select(h => ("Hull good", h)).Concat(packages.Select(p => ("Package", p))))
            {
                grid.Reference(good.Item1, ObjectKinds.Good, good.Item2.Id.Key, window.Session!.SectionName(good.Item2),
                    good.Item2);
            }

            if (hulls.Count + packages.Count == 0)
            {
                grid.Text("Goods", null);
            }

            grid.EndSection();
        }

        // A base sells a ship as a package.
        for (var i = 0; i < packages.Count; i++)
        {
            if (packages[i].Id.Key is { } package)
            {
                market.Draw(package, i == 0 ? "Ship.SoldAt" : $"Ship.SoldAt{i + 1}",
                    packages.Count == 1 ? "Markets" : $"Markets: {package}");
            }
        }
    }

    private void Identity(Ship ship)
    {
        if (!grid.BeginSection("Ship.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        grid.Text("Nickname", ship.Nickname, "nickname");
        grid.Ids("Name", ship.IdsName, "ids_name");
        grid.Info("Info", ship.IdsInfo, "ids_info");
        grid.SetNextNote("The CRC of the nickname. The game uses it to find the ship in saved games and packets.");
        grid.Text("CRC", string.Create(CultureInfo.InvariantCulture, $"0x{ship.CRC:X8}"));
        grid.Enum("Type", ship.ShipType, "type");
        grid.Number("Class", ship.Class, iniKey: "ship_class");
        grid.Enum("Mission property", ship.MissionProperty, "mission_property");
        grid.EndSection();
    }

    /// <param name="hull">The last hull good of the ship, or null.</param>
    private void HullAndCargo(Ship ship, SectionRef? hull)
    {
        if (!grid.BeginSection("Ship.Hull", EditorIcons.ShieldAlt, "Hull and cargo"))
        {
            return;
        }

        grid.Number("Hit points", ship.Hitpoints, iniKey: "hit_pts");
        grid.Number("Hold size", ship.HoldSize, iniKey: "hold_size");
        grid.Number("Max shield batteries", ship.MaxShieldBatteries, iniKey: "shield_battery_limit");
        grid.Number("Max repair kits", ship.MaxRepairKits, iniKey: "nanobot_limit");
        if (window.Session?.GameData?.Items is { } items)
        {
            using (grid.Bind(hull))
            {
                grid.SetNextNote("The price of the last hull good of the ship in the goods files. The engine takes the " +
                                 "price of the ship from it.");
                grid.Number("Price", items.GetShipPrice(ship), "#,0", "price");
            }
        }

        grid.Text("Explosion", ship.Explosion?.Nickname, "explosion_arch");
        grid.EndSection();
    }

    private void Physics(Ship ship)
    {
        if (!grid.BeginSection("Ship.Physics", EditorIcons.Rocket, "Physics"))
        {
            return;
        }

        grid.Number("Mass", ship.Mass, iniKey: "mass");
        grid.Number("Linear drag", ship.LinearDrag, iniKey: "linear_drag");
        grid.Vector("Angular drag", ship.AngularDrag, "angular_drag");
        grid.Vector("Steering torque", ship.SteeringTorque, "steering_torque");
        grid.Vector("Rotation inertia", ship.RotationInertia, "rotation_inertia");
        grid.Number("Strafe force", ship.StrafeForce, iniKey: "strafe_force");
        grid.EndSection();
    }

    private void Camera(Ship ship)
    {
        if (!grid.BeginSection("Ship.Camera", EditorIcons.Video, "Camera"))
        {
            return;
        }

        grid.Vector("Offset", ship.ChaseOffset, "camera_offset");
        grid.Number("Max bank angle", ship.MaxBankAngle, iniKey: "max_bank_angle");
        grid.Number("Horizontal turn angle", ship.CameraHorizontalTurnAngle, iniKey: "camera_horizontal_turn_angle");
        grid.Number("Vertical turn up angle", ship.CameraVerticalTurnUpAngle,
            iniKey: "camera_vertical_turn_up_angle");
        grid.Number("Vertical turn down angle", ship.CameraVerticalTurnDownAngle,
            iniKey: "camera_vertical_turn_down_angle");
        grid.EndSection();
    }

    private void Model(Ship ship)
    {
        if (!grid.BeginSection("Ship.Model", EditorIcons.CubeSolid, "Model"))
        {
            return;
        }

        grid.Text("Model", ship.ModelFile?.ModelFile, "DA_archetype");
        grid.Text("Material libraries", Join(ship.ModelFile?.LibraryFiles), "material_library");
        grid.Text("LOD ranges", Join(ship.LODRanges?.Select(r => r.ToString("0.###", CultureInfo.InvariantCulture))),
            "LODranges");
        grid.Text("Environment map material", ship.EnvMapMaterial, "envmap_material");
        grid.EndSection();
    }

    /// <summary>Each hardpoint type that the ship has, and the hardpoints that take it.</summary>
    private void Hardpoints(Ship ship)
    {
        if (!grid.BeginSection("Ship.Hardpoints", EditorIcons.Crosshairs, "Hardpoints"))
        {
            return;
        }

        if (ship.PossibleHardpoints.Count == 0)
        {
            grid.Text("Hardpoints", null, "hp_type");
        }

        if (!ReferenceEquals(ship, hardpointShip))
        {
            hardpointShip = ship;
            hardpointRows = ship.PossibleHardpoints.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .Select(h => (h.Key, string.Join(", ", h.Value)))
                .ToArray();
        }

        foreach (var (type, hardpoints) in hardpointRows)
        {
            grid.Text(type, hardpoints, "hp_type");
        }

        grid.EndSection();
    }

    private static string? Join(IEnumerable<string>? values) =>
        values?.ToArray() is { Length: > 0 } array ? string.Join(", ", array) : null;
}

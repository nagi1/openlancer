using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.GameData.Items;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;
using Schema = LibreLancer.Data.Schema.Equipment;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of an equipment item: identity, the values that all equipment has, its good, a section for its
/// type, its INI definition as a closed list, and the bases that sell it.
/// </summary>
/// <remarks>
/// The rows bind to the equipment section of the edit view, and the rows of the good to its <c>[Good]</c>. The motor
/// and the explosion of a missile bind to their own sections.
/// </remarks>
internal sealed class EquipmentPage(EditorWindow window, PropertyGrid grid)
{
    private readonly RawFields definition = new(grid, "Definition", EditorIcons.Th, "Loaded definition (read-only)");
    private readonly MarketSection market = new(window, grid);

    /// <summary>Forgets the last definition. Call it when the profile closes.</summary>
    public void Reset() => definition.Reset();

    public void Draw(Equipment equipment)
    {
        var kind = ObjectKinds.Of(equipment);
        var section = Find(equipment.Nickname);
        var good = section == null ? null : window.Session!.Edits.Related(section, ItemRelation.Good) is [var first, ..]
            ? first
            : null;
        using (grid.Bind(section))
        {
            Identity(equipment, kind);
            Common(equipment);
            using (grid.Bind(good))
            {
                Good(equipment);
            }

            TypeSection(equipment, kind);
        }

        if (Definition(equipment) is { } def)
        {
            definition.Draw(def);
        }

        if (good?.Id.Key is { } nickname)
        {
            market.Draw(nickname, "Equipment.SoldAt");
        }
    }

    /// <summary>The sections of an equipment item in W, for the raw block of the page: the equipment section, its good,
    /// its munition and the good of the munition.</summary>
    public IReadOnlyList<SectionRef> RawSections(Equipment equipment)
    {
        if (window.Session?.Edits is not { } edits || Find(equipment.Nickname) is not { } section)
        {
            return [];
        }

        var munitions = edits.Related(section, ItemRelation.Munition);
        return new[] { section }.Concat(edits.Related(section, ItemRelation.Good)).Concat(munitions)
            .Concat(munitions.SelectMany(m => edits.Related(m, ItemRelation.Good))).Distinct().ToList();
    }

    /// <summary>The equipment section of the edit view with a nickname, or null.</summary>
    private SectionRef? Find(string nickname) => window.Session?.Edits.Find(ItemFileKind.Equipment, nickname);

    private void Identity(Equipment equipment, ObjectKind kind)
    {
        if (!grid.BeginSection("Equipment.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        grid.Text("Nickname", equipment.Nickname, "nickname");
        grid.Ids("Name", equipment.IdsName, "ids_name");
        grid.Info("Info", equipment.IdsInfo, "ids_info");
        grid.SetNextNote($"The INI section name of the item gives the type. The game data class is " +
                         $"{equipment.GetType().Name}.");
        grid.Text("Type", kind.Label);
        grid.Text("Hardpoint type", equipment.HpType, "hp_type");
        grid.EndSection();
    }

    private void Common(Equipment equipment)
    {
        if (!grid.BeginSection("Equipment.Common", EditorIcons.Cog, "Common"))
        {
            return;
        }

        grid.Number("Hit points", equipment.Hitpoints, iniKey: "hit_pts");
        grid.Number("Volume", equipment.Volume, iniKey: "volume");
        grid.Number("Units per container", equipment.UnitsPerContainer, iniKey: "units_per_container");
        grid.Text("Model", equipment.ModelFile?.ModelFile, "DA_archetype");
        grid.Text("LOD ranges", equipment.LODRanges is { Length: > 0 } ranges
            ? string.Join(", ", ranges.Select(r => r.ToString("0.###", CultureInfo.InvariantCulture)))
            : null, "lodranges");
        grid.Text("Hardpoint child", equipment.HPChild, "hp_child");
        if (equipment.LootAppearance is { } loot)
        {
            grid.Reference("Loot appearance", ObjectKinds.Of(loot), loot.Nickname, Name(loot.IdsName), loot,
                "loot_appearance");
        }

        grid.EndSection();
    }

    /// <summary>The good of the item in goods.ini. The section shows only when the item has a good.</summary>
    private void Good(Equipment equipment)
    {
        if (equipment.Good is not { } good || !grid.BeginSection("Equipment.Good", EditorIcons.Box, "Good"))
        {
            return;
        }

        grid.Text("Good", good.Nickname, "nickname");
        grid.Number("Price", good.Ini.Price, "#,0", "price");
        grid.Enum("Category", good.Ini.Category, "category");
        grid.Bool("Combinable", good.Ini.Combinable, "combinable");
        grid.Text("Item icon", good.Ini.ItemIcon, "item_icon");
        grid.EndSection();
    }

    /// <summary>The main values of the equipment type. Types with no main values have no section.</summary>
    private void TypeSection(Equipment equipment, ObjectKind kind)
    {
        if (!HasTypeSection(equipment) ||
            !grid.BeginSection($"{equipment.GetType().Name}.Type", kind.Icon, kind.Label))
        {
            return;
        }

        switch (equipment)
        {
            case GunEquipment gun:
                Gun(gun.Def);
                Projectile(gun.Munition);
                break;
            case MissileLauncherEquipment launcher:
                Gun(launcher.Def);
                Projectile(launcher.Munition);
                break;
            case MunitionEquip munition:
                Munition(munition.Def);
                grid.Text("Explosion", munition.Explosion?.Nickname, "explosion_arch");
                break;
            case MissileEquip missile:
                Munition(missile.Def);
                Missile(missile);
                break;
            case ShieldEquipment shield:
                Shield(shield.Def);
                break;
            case EngineEquipment engine:
                Engine(engine);
                break;
            case PowerEquipment power:
                grid.Number("Capacity", power.Def.Capacity, iniKey: "capacity");
                grid.Number("Charge rate", power.Def.ChargeRate, iniKey: "charge_rate");
                grid.Number("Thrust capacity", power.Def.ThrustCapacity, iniKey: "thrust_capacity");
                grid.Number("Thrust charge rate", power.Def.ThrustChargeRate, iniKey: "thrust_charge_rate");
                break;
            case ThrusterEquipment thruster:
                grid.Number("Force", thruster.Force, iniKey: "max_force");
                grid.Number("Power usage", thruster.Drain, iniKey: "power_usage");
                break;
            case CountermeasureEquipment countermeasure:
                grid.Number("AI range", countermeasure.Def.AiRange, iniKey: "ai_range");
                Projectile(countermeasure.Munition);
                break;
            case MineDropperEquipment dropper:
                grid.Number("Refire delay", dropper.Def.RefireDelay, iniKey: "refire_delay");
                grid.Number("Muzzle velocity", dropper.Def.MuzzleVelocity, iniKey: "muzzle_velocity");
                grid.Number("Power usage", dropper.Def.PowerUsage, iniKey: "power_usage");
                Projectile(dropper.Mine);
                break;
            case CloakEquipment cloak:
                grid.Number("Cloak in time", cloak.CloakInTime, iniKey: "cloakin_time");
                grid.Number("Cloak out time", cloak.CloakOutTime, iniKey: "cloakout_time");
                break;
            case LightEquipment light:
                Light(light);
                break;
            case ScannerEquipment scanner:
                grid.Number("Range", scanner.Def.Range, iniKey: "range");
                grid.Number("Cargo scan range", scanner.Def.CargoScanRange, iniKey: "cargo_scan_range");
                break;
            case TractorEquipment tractor:
                grid.Number("Max length", tractor.Def.MaxLength, iniKey: "max_length");
                grid.Number("Reach speed", tractor.Def.ReachSpeed, iniKey: "reach_speed");
                grid.Color("Color", tractor.Def.Color.ToColor4(), "color");
                break;
        }

        grid.EndSection();
    }

    private static bool HasTypeSection(Equipment equipment) => equipment is GunEquipment or MissileLauncherEquipment
        or MunitionEquip or MissileEquip or ShieldEquipment or EngineEquipment or PowerEquipment or ThrusterEquipment
        or CountermeasureEquipment or MineDropperEquipment or CloakEquipment or LightEquipment or ScannerEquipment
        or TractorEquipment;

    private void Gun(Schema.Gun def)
    {
        grid.Number("Refire delay", def.RefireDelay, iniKey: "refire_delay");
        grid.Number("Muzzle velocity", def.MuzzleVelocity, iniKey: "muzzle_velocity");
        grid.Number("Power usage", def.PowerUsage, iniKey: "power_usage");
        grid.Number("Turn rate", def.TurnRate, iniKey: "turn_rate");
        grid.Number("Damage per fire", def.DamagePerFire, iniKey: "damage_per_fire");
        grid.Number("Dispersion angle", def.DispersionAngle, iniKey: "dispersion_angle");
        grid.Bool("Auto turret", def.AutoTurret, "auto_turret");
    }

    private void Munition(Schema.Munition def)
    {
        grid.Number("Hull damage", def.HullDamage, iniKey: "hull_damage");
        grid.Number("Energy damage", def.EnergyDamage, iniKey: "energy_damage");
        grid.Number("Lifetime", def.Lifetime, iniKey: "lifetime");
        grid.Text("Weapon type", def.WeaponType, "weapon_type");
        grid.Bool("Requires ammo", def.RequiresAmmo, "requires_ammo");
        grid.Bool("Cruise disruptor", def.CruiseDisruptor, "cruise_disruptor");
    }

    private void Missile(MissileEquip missile)
    {
        var def = missile.Def;
        grid.Text("Seeker", def.Seeker, "seeker");
        grid.Number("Seeker range", def.SeekerRange, iniKey: "seeker_range");
        grid.Number("Time to lock", def.TimeToLock, iniKey: "time_to_lock");
        grid.Number("Max angular velocity", def.MaxAngularVelocity, iniKey: "max_angular_velocity");
        if (missile.Motor is { } motor)
        {
            grid.SubHeading("Motor");
            grid.Text("Motor", motor.Nickname, "motor");
            using (grid.Bind(Find(motor.Nickname)))
            {
                grid.Number("Acceleration", motor.Accel, iniKey: "accel");
                grid.Number("Delay", motor.Delay, iniKey: "delay");
                grid.Number("Lifetime", motor.Lifetime, iniKey: "lifetime");
            }
        }

        grid.SubHeading("Explosion");
        grid.Text("Explosion", missile.Explosion.Nickname, "explosion_arch");
        using (grid.Bind(Find(missile.Explosion.Nickname)))
        {
            grid.Number("Radius", missile.Explosion.Radius, iniKey: "radius");
            grid.Number("Hull damage", missile.Explosion.HullDamage, iniKey: "hull_damage");
            grid.Number("Energy damage", missile.Explosion.EnergyDamage, iniKey: "energy_damage");
        }
    }

    private void Shield(Schema.ShieldGenerator def)
    {
        grid.Number("Capacity", def.MaxCapacity, iniKey: "max_capacity");
        grid.Number("Regeneration rate", def.RegenerationRate, iniKey: "regeneration_rate");
        grid.Number("Offline threshold", def.OfflineThreshold, iniKey: "offline_threshold");
        grid.Number("Offline rebuild time", def.OfflineRebuildTime, iniKey: "offline_rebuild_time");
        grid.Number("Constant power draw", def.ConstantPowerDraw, iniKey: "constant_power_draw");
        grid.Number("Rebuild power draw", def.RebuildPowerDraw, iniKey: "rebuild_power_draw");
        grid.Text("Shield type", def.ShieldType, "shield_type");
    }

    private void Engine(EngineEquipment engine)
    {
        var def = engine.Def;
        grid.Number("Max force", def.MaxForce, iniKey: "max_force");
        grid.Number("Linear drag", def.LinearDrag, iniKey: "linear_drag");
        grid.Number("Power usage", def.PowerUsage, iniKey: "power_usage");
        grid.Number("Reverse fraction", def.ReverseFraction, iniKey: "reverse_fraction");
        grid.Number("Cruise charge time", def.CruiseChargeTime, iniKey: "cruise_charge_time");
        grid.Number("Cruise power usage", def.CruisePowerUsage, iniKey: "cruise_power_usage");
        grid.Number("Cruise speed", engine.CruiseSpeed, iniKey: "cruise_speed");
    }

    private void Light(LightEquipment light)
    {
        grid.Color("Color", light.Color.ToColor4(), "color");
        grid.Color("Min color", light.MinColor.ToColor4(), "min_color");
        grid.Color("Glow color", light.GlowColor.ToColor4(), "glow_color");
        grid.Number("Bulb size", light.BulbSize, iniKey: "bulb_size");
        grid.Number("Glow size", light.GlowSize, iniKey: "glow_size");
        grid.Number("Emit range", light.EmitRange, iniKey: "emit_range");
        grid.Bool("Always on", light.AlwaysOn, "always_on");
        grid.Bool("Docking light", light.DockingLight, "docking_light");
        grid.SetNextNote("A light that has avg_delay and blink_duration is animated.");
        grid.Bool("Animated", light.Animated);
        grid.Number("Average delay", light.AvgDelay, iniKey: "avg_delay");
        grid.Number("Blink duration", light.BlinkDuration, iniKey: "blink_duration");
    }

    /// <summary>A link to the munition, missile or mine that a launcher fires.</summary>
    private void Projectile(Equipment? projectile)
    {
        grid.Reference("Projectile", projectile == null ? ObjectKinds.AnyEquipment : ObjectKinds.Of(projectile),
            projectile?.Nickname, projectile == null ? null : Name(projectile.IdsName), projectile,
            "projectile_archetype");
    }

    /// <summary>The INI definition of the item, for the types that keep it.</summary>
    private static object? Definition(Equipment equipment) => equipment switch
    {
        GunEquipment gun => gun.Def,
        MissileLauncherEquipment launcher => launcher.Def,
        MunitionEquip munition => munition.Def,
        MissileEquip missile => missile.Def,
        ShieldEquipment shield => shield.Def,
        EngineEquipment engine => engine.Def,
        PowerEquipment power => power.Def,
        CountermeasureEquipment countermeasure => countermeasure.Def,
        MineDropperEquipment dropper => dropper.Def,
        ScannerEquipment scanner => scanner.Def,
        TractorEquipment tractor => tractor.Def,
        RepairKitEquipment kit => kit.Def,
        ShieldBatteryEquipment battery => battery.Def,
        _ => null,
    };

    private string? Name(int ids) => window.Session?.Name(ids);
}

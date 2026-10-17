using System;
using System.Collections.Generic;
using System.Globalization;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a zone: identity and the items that use the zone, transform, shape and size, flags and
/// environment, and the encounters of a population or patrol zone. With the section of the zone in W, the rows edit its
/// keys, and the page ends with the keys that no row shows. The panel adds the raw INI block (see
/// <see cref="SectionExtras"/>).
/// </summary>
/// <remarks>In a bound scope, a row of a key that not every zone has shows when the section has a line of the key (see
/// <see cref="PropertyGrid.Shows(bool, string[])"/>). The encounter lines are read-only, because their lines belong
/// together.</remarks>
internal sealed class ZonePage(EditorWindow window, PropertyGrid grid)
{
    private const string BoxNote = "The size of a box is its full extent on each axis, from one face to the other.";
    private const string EllipsoidNote = "The size of an ellipsoid is its radius on each axis, from the center.";
    private const string CylinderNote = "The size of a cylinder is its radius and its full length along its axis.";
    private const string RingNote = "The INI size order is outer radius, inner radius, height.";

    private const string SharedBitNote = "Spacedust and Music have the same bit, 0x200000. So a chip cannot show " +
                                         "which of the two names the INI file uses.";

    private static readonly string[] LaneKeys = ["lane_id", "tradelane_attack", "tradelane_down"];

    private static readonly string[] PopulationKeys =
    [
        "pop_type", "path_label", "usage", "mission_type", "attack_ids", "vignette_type", "toughness", "density",
        "repop_time", "max_battle_size", "relief_time", "mission_eligible", "population_additive", "encounter",
        "faction", "faction_weight", "density_restriction",
    ];

    private readonly SectionExtras extras = new(window, grid);

    /// <param name="section">The section of the zone in W, or null when the edit view has none.</param>
    public void Draw(Zone zone, SectionRef? section)
    {
        using (grid.Bind(section))
        {
            Identity(zone);
            Transform(zone);
            ShapeAndSize(zone);
            FlagsAndEnvironment(zone);
            if (grid.Shows(zone.IsPopulationZone || zone.IsPatrolPathZone, PopulationKeys))
            {
                Encounters(zone);
            }
        }

        if (section != null)
        {
            extras.Draw([section], "Zone", moreKeys: true);
        }
    }

    private void Identity(Zone zone)
    {
        if (!grid.BeginSection("Zone.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        grid.Text("Nickname", zone.Nickname, "nickname");
        grid.Ids("Name", zone.IdsName, "ids_name");
        grid.Info("Info", zone.IdsInfo, "ids_info");
        grid.SetNextNote("The editor finds the role from the asteroid fields, the nebulae and the encounters of " +
                         "the system.");
        grid.Text("Role", window.Scene.KindOf(zone)?.Label);
        if (grid.Shows(!string.IsNullOrEmpty(zone.Comment), "comment"))
        {
            grid.Text("Comment", zone.Comment, "comment");
        }

        var uses = window.Scene.Index?.UsesOf(zone) ?? [];
        if (uses.Count > 0)
        {
            grid.SubHeading("Used by");
            // The keys of these rows belong to the sections that use the zone.
            using (grid.Bind(null))
            {
                foreach (var use in uses)
                {
                    UsedBy(use);
                }
            }
        }

        grid.EndSection();
    }

    private void UsedBy(ZoneUse use)
    {
        switch (use)
        {
            case { Kind: ZoneUseKind.AsteroidField, Item: AsteroidField field }:
                grid.Text("Asteroid field", field.SourceFile, "file");
                break;
            case { Kind: ZoneUseKind.Nebula, Item: Nebula nebula }:
                grid.Text("Nebula", nebula.SourceFile, "file");
                break;
            case { Kind: ZoneUseKind.Exclusion, Item: Zone parent }:
                grid.Reference("Exclusion zone of", window.Scene.KindOf(parent) ?? ObjectKinds.Of(ZoneRole.Other),
                    parent.Nickname, Name(parent), parent, "exclusion");
                break;
            case { Kind: ZoneUseKind.Ring, Item: SystemObject obj }:
                grid.Reference("Ring of", ObjectKinds.Of(obj), obj.Nickname, Name(obj), obj, "ring");
                break;
        }
    }

    private void Transform(Zone zone)
    {
        if (!grid.BeginSection("Zone.Transform", EditorIcons.MapMarkerAlt, "Transform"))
        {
            return;
        }

        grid.Vector("Position", zone.Position, "pos");
        // The game data keeps the INI angles in radians.
        grid.SetNextNote("The rotation in degrees, as in the INI file.");
        grid.Vector("Rotation", zone.RotationAngles * (180 / MathF.PI), "rotate");
        grid.Number("Sort", zone.Sort, iniKey: "sort");
        grid.EndSection();
    }

    /// <summary>
    /// The shape and the size. The size labels depend on the shape, because each shape uses the size in its own way.
    /// </summary>
    private void ShapeAndSize(Zone zone)
    {
        if (!grid.BeginSection("Zone.Shape", EditorIcons.VectorSquare, "Shape and size"))
        {
            return;
        }

        grid.Enum("Shape", zone.Shape, "shape");
        if (zone.Shape switch
            {
                ShapeKind.Ellipsoid => EllipsoidNote,
                ShapeKind.Box => BoxNote,
                ShapeKind.Cylinder => CylinderNote,
                ShapeKind.Ring => RingNote,
                _ => null,
            } is { } note)
        {
            grid.SetNextNote(note);
        }

        grid.Size("Size", zone.Shape, zone.Size, "size");
        grid.Number("Edge fraction", zone.EdgeFraction, iniKey: "edge_fraction");
        grid.EndSection();
    }

    private void FlagsAndEnvironment(Zone zone)
    {
        if (!grid.BeginSection("Zone.Flags", EditorIcons.Cloud, "Flags and environment"))
        {
            return;
        }

        grid.Flags("Properties", zone.PropertyFlags, "property_flags");
        var flags = (int)zone.PropertyFlags;
        grid.SetNextNote(SharedBitNote);
        grid.Text("Properties value", string.Create(CultureInfo.InvariantCulture, $"{flags} (0x{flags:X})"),
            "property_flags");
        if (grid.Shows(zone.PropertyFogColor != null, "property_fog_color"))
        {
            grid.Color("Fog color", zone.PropertyFogColor, "property_fog_color");
        }

        grid.Flags("Visit", zone.VisitFlags, "visit");
        grid.Number("Visit value", (int)zone.VisitFlags, iniKey: "visit");
        grid.Text("Spacedust", zone.Spacedust, "spacedust");
        grid.Number("Spacedust particles", zone.SpacedustMaxParticles, iniKey: "spacedust_maxparticles");
        grid.Text("Music", zone.Music, "music");
        grid.Number("Interference", zone.Interference, iniKey: "interference");
        grid.Number("Power modifier", zone.PowerModifier, iniKey: "power_modifier");
        grid.Number("Drag modifier", zone.DragModifier, iniKey: "drag_modifier");
        grid.Number("Damage", zone.Damage, iniKey: "damage");
        if (grid.Shows(zone.LaneId != 0, LaneKeys))
        {
            grid.SubHeading("Trade lane");
            grid.Number("Lane id", zone.LaneId, iniKey: "lane_id");
            grid.Number("Attack chance", zone.TradelaneAttack, iniKey: "tradelane_attack");
            grid.Number("Down chance", zone.TradelaneDown, iniKey: "tradelane_down");
        }

        grid.EndSection();
    }

    /// <summary>The population settings and the encounter list of a population or patrol zone.</summary>
    private void Encounters(Zone zone)
    {
        if (!grid.BeginSection("Zone.Encounters", EditorIcons.Crosshairs, "Encounters"))
        {
            return;
        }

        grid.Text("Population type", Join(zone.PopType), "pop_type");
        grid.Text("Path label", Join(zone.PathLabel), "path_label");
        grid.Text("Usage", Join(zone.Usage), "usage");
        grid.Text("Mission type", Join(zone.MissionType), "mission_type");
        grid.Text("Attack ids", Join(zone.AttackIds), "attack_ids");
        grid.Text("Vignette type", zone.VignetteType, "vignette_type");
        grid.Number("Toughness", zone.Toughness, iniKey: "toughness");
        grid.Number("Density", zone.Density, iniKey: "density");
        grid.Number("Repop time", zone.RepopTime, iniKey: "repop_time");
        grid.Number("Max battle size", zone.MaxBattleSize, iniKey: "max_battle_size");
        grid.Number("Relief time", zone.ReliefTime, iniKey: "relief_time");
        grid.Bool("Mission eligible", zone.MissionEligible, "mission_eligible");
        grid.Text("Population additive", zone.PopulationAdditive?.ToString(), "population_additive");

        grid.SubHeading("Encounters");
        // The encounters as the engine reads them. In a bound scope, the lines follow as read-only rows.
        using (grid.Bind(null))
        {
            foreach (var encounter in zone.Encounters ?? [])
            {
                grid.Text(encounter.Archetype, string.Create(CultureInfo.InvariantCulture,
                    $"Difficulty {encounter.Difficulty}, chance {encounter.Chance:0.###}"), "encounter");
                foreach (var spawn in encounter.FactionSpawns)
                {
                    grid.Text("Faction", string.Create(CultureInfo.InvariantCulture,
                        $"{spawn.Faction}, chance {spawn.Chance:0.###}"), "faction");
                }
            }
        }

        if (grid.IsBound)
        {
            grid.Text("Encounter lines", null, "encounter");
            grid.Text("Faction lines", null, "faction");
            grid.Text("Faction weights", null, "faction_weight");
            grid.Text("Density restrictions", null, "density_restriction");
        }
        else if (zone.DensityRestrictions is { Length: > 0 } restrictions)
        {
            grid.SubHeading("Density restrictions");
            foreach (var restriction in restrictions)
            {
                grid.Number(restriction.Type, restriction.Count, iniKey: "density_restriction");
            }
        }

        grid.EndSection();
    }

    /// <summary>The name of an item of the open system, from W (see <see cref="EditorSession.ItemName"/>).</summary>
    private string? Name(object item) => window.Session?.ItemName(item);

    private static string? Join(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join(", ", values) : null;
}

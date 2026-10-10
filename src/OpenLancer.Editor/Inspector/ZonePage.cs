using System;
using System.Collections.Generic;
using System.Globalization;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a zone: identity and the items that use the zone, transform, shape and size, flags and
/// environment, and the encounters of a population or patrol zone.
/// </summary>
internal sealed class ZonePage(EditorWindow window, PropertyGrid grid)
{
    private const string BoxNote = "The size of a box is its full extent on each axis, from one face to the other.";
    private const string EllipsoidNote = "The size of an ellipsoid is its radius on each axis, from the center.";
    private const string CylinderNote = "The height is the full length of the cylinder along its axis.";
    private const string RingNote = "The INI size order is outer radius, inner radius, height.";

    private const string SharedBitNote = "Spacedust and Music have the same bit, 0x200000. So a chip cannot show " +
                                         "which of the two names the INI file uses.";

    public void Draw(Zone zone)
    {
        Identity(zone);
        Transform(zone);
        ShapeAndSize(zone);
        FlagsAndEnvironment(zone);
        if (zone.IsPopulationZone || zone.IsPatrolPathZone)
        {
            Encounters(zone);
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
        if (!string.IsNullOrEmpty(zone.Comment))
        {
            grid.Text("Comment", zone.Comment, "comment");
        }

        var uses = window.Scene.Index?.UsesOf(zone) ?? [];
        if (uses.Count > 0)
        {
            grid.SubHeading("Used by");
            foreach (var use in uses)
            {
                UsedBy(use);
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
                    parent.Nickname, Name(parent.IdsName), parent, "exclusion");
                break;
            case { Kind: ZoneUseKind.Ring, Item: SystemObject obj }:
                grid.Reference("Ring of", ObjectKinds.Of(obj), obj.Nickname, Name(obj.IdsName), obj, "ring");
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
        var size = zone.Size;
        switch (zone.Shape)
        {
            case ShapeKind.Sphere:
                grid.Number("Radius", size.X, iniKey: "size");
                break;
            case ShapeKind.Ellipsoid:
                grid.SetNextNote(EllipsoidNote);
                grid.Number("Radius X", size.X, iniKey: "size");
                grid.Number("Radius Y", size.Y, iniKey: "size");
                grid.Number("Radius Z", size.Z, iniKey: "size");
                break;
            case ShapeKind.Box:
                grid.SetNextNote(BoxNote);
                grid.Number("Width (X)", size.X, iniKey: "size");
                grid.Number("Height (Y)", size.Y, iniKey: "size");
                grid.Number("Depth (Z)", size.Z, iniKey: "size");
                break;
            case ShapeKind.Cylinder:
                grid.Number("Radius", size.X, iniKey: "size");
                grid.SetNextNote(CylinderNote);
                grid.Number("Height", size.Y, iniKey: "size");
                break;
            case ShapeKind.Ring:
                grid.SetNextNote(RingNote);
                grid.Number("Outer radius", size.X, iniKey: "size");
                grid.Number("Height", size.Y, iniKey: "size");
                grid.Number("Inner radius", size.Z, iniKey: "size");
                break;
        }

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
        if (zone.PropertyFogColor is { } fog)
        {
            grid.Color("Fog color", fog, "property_fog_color");
        }

        grid.Flags("Visit", zone.VisitFlags, "visit");
        grid.Text("Spacedust", zone.Spacedust, "spacedust");
        grid.Number("Spacedust particles", zone.SpacedustMaxParticles, iniKey: "spacedust_maxparticles");
        grid.Text("Music", zone.Music, "music");
        grid.Number("Interference", zone.Interference, iniKey: "interference");
        grid.Number("Power modifier", zone.PowerModifier, iniKey: "power_modifier");
        grid.Number("Drag modifier", zone.DragModifier, iniKey: "drag_modifier");
        grid.Number("Damage", zone.Damage, iniKey: "damage");
        if (zone.LaneId != 0)
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

        if (zone.DensityRestrictions is { Length: > 0 } restrictions)
        {
            grid.SubHeading("Density restrictions");
            foreach (var restriction in restrictions)
            {
                grid.Number(restriction.Type, restriction.Count, iniKey: "density_restriction");
            }
        }

        grid.EndSection();
    }

    private string? Name(int ids) => window.Session?.Name(ids);

    private static string? Join(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join(", ", values) : null;
}

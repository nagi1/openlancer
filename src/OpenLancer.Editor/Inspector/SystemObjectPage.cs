using System.Collections.Generic;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a system object: identity, transform, archetype and loadout, docking, faction, visit flags
/// and appearance.
/// </summary>
internal sealed class SystemObjectPage(EditorWindow window, PropertyGrid grid)
{
    public void Draw(SystemObject obj)
    {
        Identity(obj);
        Transform(obj);
        ArchetypeAndLoadout(obj);
        Docking(obj);
        FactionAndReputation(obj);
        Visit(obj);
        Appearance(obj);
    }

    private void Identity(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        grid.Text("Nickname", obj.Nickname, "nickname");
        grid.Ids("Name", obj.IdsName, "ids_name");
        grid.Info("Info", obj.IdsInfo, "ids_info");
        grid.Text("Base", Named(obj.Base), "base");
        var parent = Object(obj.Parent);
        grid.Reference("Parent", parent == null ? ObjectKinds.OtherObject : ObjectKinds.Of(parent), obj.Parent,
            Name(parent?.IdsName), parent, "parent");
        if (!string.IsNullOrEmpty(obj.Comment))
        {
            grid.Text("Comment", obj.Comment);
        }

        grid.EndSection();
    }

    private void Transform(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Transform", EditorIcons.MapMarkerAlt, "Transform"))
        {
            return;
        }

        grid.Vector("Position", obj.Position, "pos");
        grid.Euler("Rotation", obj.Rotation, "rotate");
        grid.Vector("Spin", obj.Spin, "spin");
        grid.SetNextNote("The cell of the navmap grid. The game calculates it from the position and the navmap " +
                         "scale of the system.");
        grid.Text("Sector", window.Scene.Index?.System.WaypointSector(obj.Position));
        grid.EndSection();
    }

    private void ArchetypeAndLoadout(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Archetype", EditorIcons.Boxes, "Archetype and loadout"))
        {
            return;
        }

        var archetype = obj.Archetype;
        grid.Text("Archetype", archetype?.Nickname, "archetype");
        if (archetype != null)
        {
            grid.Enum("Type", archetype.Type, "type");
            grid.Text("Model", archetype.ModelFile?.ModelFile, "DA_archetype");
            grid.Number("Solar radius", archetype.SolarRadius, iniKey: "solar_radius");
            grid.Text("Navmap icon", archetype.NavmapIcon, "shape_name");
        }

        if (obj.Star is { } star)
        {
            grid.SubHeading("Star");
            grid.Text("Star", star.Nickname, "star");
            grid.Number("Radius", star.Radius, iniKey: "radius");
        }

        grid.SubHeading("Loadout");
        grid.Text("Loadout", obj.Loadout?.Nickname, "loadout");
        foreach (var item in obj.Loadout?.Items ?? [])
        {
            var equipment = item.Equipment;
            grid.Reference(item.Hardpoint ?? "Internal", ObjectKinds.Of(equipment), equipment.Nickname,
                Name(equipment.IdsName), equipment);
        }

        grid.EndSection();
    }

    private void Docking(SystemObject obj)
    {
        if (obj.Dock is not { } dock || !grid.BeginSection("SystemObject.Docking", EditorIcons.LifeRing, "Docking"))
        {
            return;
        }

        grid.Enum("Kind", dock.Kind);
        var items = window.Session?.GameData?.Items;
        switch (dock.Kind)
        {
            case DockKinds.Base:
                grid.Text("Docks with", Named(items?.Bases.Get(dock.Target)) ?? dock.Target, "dock_with");
                break;
            case DockKinds.Jump:
                var system = items?.Systems.Get(dock.Target);
                grid.Reference("Destination system", ObjectKinds.StarSystem, dock.Target, Name(system?.IdsName),
                    system, "goto");
                grid.Text("Exit object", dock.Exit, "goto");
                grid.Text("Tunnel effect", dock.Tunnel, "goto");
                break;
            case DockKinds.Tradelane:
                Ring("Next ring", dock.Target, "next_ring");
                Ring("Previous ring", dock.TargetLeft, "prev_ring");
                grid.Ids("Lane name", obj.TradelaneSpaceName, "tradelane_space_name");
                grid.Ids("Left name", obj.IdsLeft, "ids_left");
                grid.Ids("Right name", obj.IdsRight, "ids_right");
                break;
        }

        grid.EndSection();
    }

    private void FactionAndReputation(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Faction", EditorIcons.Users, "Faction and reputation"))
        {
            return;
        }

        grid.Text("Reputation", Named(obj.Reputation), "reputation");
        grid.Text("Pilot", obj.Pilot?.Nickname, "pilot");
        grid.Number("Difficulty level", obj.DifficultyLevel, iniKey: "difficulty_level");
        grid.Text("Voice", obj.Voice, "voice");
        grid.Text("Behavior", obj.Behavior, "behavior");
        grid.SetNextNote("The engine does not use this field. The editor keeps it so that the INI text stays the " +
                         "same.");
        grid.Text("Faction", Named(obj.Faction), "faction");
        grid.EndSection();
    }

    private void Visit(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Visit", EditorIcons.CheckCircle, "Visit flags"))
        {
            return;
        }

        grid.FlagChecks(obj.Visit, "visit");
        grid.EndSection();
    }

    /// <summary>
    /// The atmosphere, colors and ring of a planet or a sun. The section shows only when one of them is set.
    /// </summary>
    private void Appearance(SystemObject obj)
    {
        var hasAppearance = obj.AtmosphereRange != 0 || obj.BurnColor != null || obj.AmbientColor != null ||
                            obj.RingZone != null || obj.RingFile != null;
        if (!hasAppearance || !grid.BeginSection("SystemObject.Appearance", EditorIcons.Star, "Appearance"))
        {
            return;
        }

        if (obj.AtmosphereRange != 0)
        {
            grid.Number("Atmosphere range", obj.AtmosphereRange, iniKey: "atmosphere_range");
        }

        if (obj.BurnColor is { } burn)
        {
            grid.Color("Burn color", burn, "burn_color");
        }

        if (obj.AmbientColor is { } ambient)
        {
            grid.Color("Ambient color", ambient, "ambient_color");
        }

        if (obj.RingZone != null)
        {
            var ring = window.Scene.Index?.System.ZoneDict.GetValueOrDefault(obj.RingZone);
            grid.Reference("Ring zone", window.Scene.KindOf(ring) ?? ObjectKinds.Of(ZoneRole.Other), obj.RingZone,
                Name(ring?.IdsName), ring, "ring");
        }

        if (obj.RingFile != null)
        {
            grid.Text("Ring file", obj.RingFile, "ring");
        }

        grid.EndSection();
    }

    /// <summary>A link to a trade lane ring of the open system.</summary>
    private void Ring(string label, string? nickname, string iniKey)
    {
        var ring = Object(nickname);
        grid.Reference(label, ObjectKinds.TradeLaneRing, nickname, Name(ring?.IdsName), ring, iniKey);
    }

    /// <summary>An object of the open system by nickname, or null.</summary>
    private SystemObject? Object(string? nickname) =>
        nickname == null ? null : window.Scene.Index?.ObjectsByNickname.GetValueOrDefault(nickname);

    private string? Name(int? ids) => ids is { } id ? window.Session?.Name(id) : null;

    /// <summary>"nickname (Name)", the nickname when the item has no name, or null for no item.</summary>
    private string? Named(NamedItem? item)
    {
        if (item == null)
        {
            return null;
        }

        var name = Name(item.IdsName);
        return string.IsNullOrEmpty(name) ? item.Nickname : $"{item.Nickname} ({name})";
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a system object: the components strip, identity, transform, archetype and loadout, docking,
/// faction, behaviors, visit flags and appearance, and the lines that name the object. With the section of the object in
/// W, the rows edit its keys, and the page ends with the keys that no row shows, and the panel adds the raw INI block
/// (see <see cref="SectionExtras"/>).
/// </summary>
/// <remarks>In a bound scope, a row of a key that not every object has shows when the section has a line of the key
/// (see <see cref="PropertyGrid.Shows(bool, string[])"/>). Rows of the values of other files, for example the model
/// of the archetype, do not bind.</remarks>
internal sealed class SystemObjectPage(EditorWindow window, PropertyGrid grid)
{
    private static readonly string[] DockingKeys = ["dock_with", "goto", "next_ring", "prev_ring", "tradelane_space_name"];
    private static readonly string[] LaneKeys = ["next_ring", "prev_ring", "tradelane_space_name"];
    private static readonly string[] AppearanceKeys = ["atmosphere_range", "burn_color", "ambient_color", "ambient", "ring"];

    private readonly SectionExtras extras = new(window, grid);
    private readonly ComponentStrip strip = new(window, grid);
    private readonly UsedBySection usedBy = new(window, grid);

    // The story mentions of the last object, with the edit view that they came from (decision D261 of the M3 design).
    private (string Nickname, object View, IReadOnlyList<Data.Editing.Scene.StoryMention> Mentions)? mentions;

    /// <param name="section">The section of the object in W, or null when the edit view has none.</param>
    public void Draw(SystemObject obj, SectionRef? section)
    {
        if (section != null)
        {
            strip.Draw(section);
        }

        using (grid.Bind(section))
        {
            Identity(obj);
            Transform(obj);
            ArchetypeAndLoadout(obj);
            Docking(obj);
            FactionAndReputation(obj);
            Behaviors(obj, section);
            Visit(obj);
            Appearance(obj);
        }

        if (section != null)
        {
            usedBy.Draw(section, "SystemObject");
            extras.Draw([section], "SystemObject", moreKeys: true);
        }
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
            Name(parent), parent, "parent");
        if (grid.Shows(!string.IsNullOrEmpty(obj.Comment), "comment"))
        {
            grid.Text("Comment", obj.Comment, "comment");
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
            // The values of the archetype in the solar archetype file.
            using (grid.Bind(null))
            {
                grid.Enum("Type", archetype.Type, "type");
                grid.Text("Model", archetype.ModelFile?.ModelFile, "DA_archetype");
                grid.Number("Solar radius", archetype.SolarRadius, iniKey: "solar_radius");
                grid.Text("Navmap icon", archetype.NavmapIcon, "shape_name");
            }
        }

        if (grid.Shows(obj.Star != null, "star"))
        {
            grid.SubHeading("Star");
            grid.Text("Star", obj.Star?.Nickname, "star");
            if (obj.Star is { } star)
            {
                using (grid.Bind(null))
                {
                    grid.Number("Radius", star.Radius, iniKey: "radius");
                }
            }
        }

        grid.SubHeading("Loadout");
        grid.Text("Loadout", obj.Loadout?.Nickname, "loadout");
        foreach (var item in obj.Loadout?.Items ?? [])
        {
            var equipment = item.Equipment;
            grid.Reference(item.Hardpoint ?? "Internal", ObjectKinds.Of(equipment), equipment.Nickname,
                Name(equipment), equipment);
        }

        grid.EndSection();
    }

    /// <summary>
    /// The dock of the object: the base of a station, the destination of a jump gate or a jump hole, or the lane of a
    /// trade lane ring.
    /// </summary>
    private void Docking(SystemObject obj)
    {
        var dock = obj.Dock;
        var (showBase, showJump, showLane) = DockingRows(grid.Layout, dock);
        if (!(showBase || showJump || showLane) ||
            !grid.BeginSection("SystemObject.Docking", EditorIcons.LifeRing, "Docking"))
        {
            return;
        }

        if (dock != null)
        {
            grid.Enum("Kind", dock.Kind);
        }

        var items = window.Session?.GameData?.Items;
        if (showBase)
        {
            grid.Text("Docks with", dock is { Kind: DockKinds.Base, Target: { } target }
                ? Named(items?.Bases.Get(target)) ?? target
                : null, "dock_with");
        }

        if (showJump)
        {
            var target = dock?.Kind == DockKinds.Jump ? dock.Target : null;
            var system = target == null ? null : items?.Systems.Get(target);
            if (grid.IsBound)
            {
                // One line has the three values.
                grid.SetNextNote("The destination system, the object where the ship comes out, and the tunnel effect.");
                grid.Reference("Destination", ObjectKinds.StarSystem, target, Name(system), system, "goto");
            }
            else
            {
                grid.Reference("Destination system", ObjectKinds.StarSystem, target, Name(system), system, "goto");
                grid.Text("Exit object", dock?.Exit, "goto");
                grid.Text("Tunnel effect", dock?.Tunnel, "goto");
            }
        }

        if (showLane)
        {
            var lane = dock?.Kind == DockKinds.Tradelane ? dock : null;
            Ring("Next ring", lane?.Target, "next_ring");
            Ring("Previous ring", lane?.TargetLeft, "prev_ring");
            grid.Ids("Lane name", obj.TradelaneSpaceName, "tradelane_space_name");
            if (lane != null)
            {
                // The engine gives these names in a walk over the lanes of the system.
                using (grid.Bind(null))
                {
                    grid.Ids("Left name", obj.IdsLeft, "ids_left");
                    grid.Ids("Right name", obj.IdsRight, "ids_right");
                }
            }
        }

        grid.EndSection();
    }

    /// <summary>
    /// The rows of the docking section: the base, the destination and the lane. Without a bound section, the dock of the
    /// game data decides. In a bound scope, only the lines of the keys decide (see
    /// <see cref="PropertyGrid.Shows(bool, string[])"/>), because an edit of <c>dock_with</c> or <c>goto</c> changes
    /// the dock of the scene item at once. A row would then come while the field of its key is open in "More keys",
    /// and that field would end.
    /// </summary>
    /// <param name="layout">The section that a bound page lays out its rows from, or null without a bound
    /// section.</param>
    internal static (bool Base, bool Jump, bool Lane) DockingRows(SectionView? layout, DockAction? dock) =>
    (
        PropertyGrid.Shows(layout, dock?.Kind == DockKinds.Base, "dock_with"),
        PropertyGrid.Shows(layout, dock?.Kind == DockKinds.Jump, "goto"),
        PropertyGrid.Shows(layout, dock?.Kind == DockKinds.Tradelane, LaneKeys)
    );

    private void FactionAndReputation(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Faction", EditorIcons.Users, "Faction and reputation"))
        {
            return;
        }

        grid.Text("Reputation", Named(obj.Reputation), "reputation");
        grid.Text("Voice", obj.Voice, "voice");
        grid.SetNextNote("The engine does not use this field. The editor keeps it so that the INI text stays the " +
                         "same.");
        grid.Text("Faction", Named(obj.Faction), "faction");
        grid.EndSection();
    }

    /// <summary>
    /// The behaviors of the object (decision D247 of the M3 design): its pilot, behavior and difficulty, its reputation,
    /// the encounters of the zones that hold its position, and the files that name it as text. The rows of keys stay
    /// bound, so a raw edit shows at once. The other rows are read-only, and each one selects its item. Milestone M29 adds
    /// the cutscene scripts and the mission triggers.
    /// </summary>
    private void Behaviors(SystemObject obj, SectionRef? section)
    {
        if (!grid.BeginSection("SystemObject.Behaviors", EditorIcons.Bolt, "Behaviors"))
        {
            return;
        }

        grid.Text("Pilot", obj.Pilot?.Nickname, "pilot");
        grid.Text("Behavior", obj.Behavior, "behavior");
        grid.Number("Difficulty level", obj.DifficultyLevel, iniKey: "difficulty_level");
        using (grid.Bind(null))
        {
            grid.Reference("Reputation", ObjectKinds.OfList("faction"), obj.Reputation?.Nickname, Name(obj.Reputation), null);
            foreach (var zone in window.Scene.System?.Zones.Where(z => z.Encounters is { Length: > 0 } && z.ContainsPoint(obj.Position)) ?? [])
            {
                foreach (var encounter in zone.Encounters!)
                {
                    var factions = encounter.FactionSpawns is { Count: > 0 } spawns
                        ? string.Join(", ", spawns.Select(f => f.Faction))
                        : "";
                    grid.Reference("Encounter", window.Scene.KindOf(zone) ?? ObjectKinds.Of(ZoneRole.Population),
                        $"{encounter.Archetype} in {zone.Nickname}", factions, zone);
                }
            }

            foreach (var mention in Mentions(section))
            {
                grid.Text("Named in", $"{mention.Path} ({mention.Count} {(mention.Count == 1 ? "time" : "times")})");
            }
        }

        grid.EndSection();
    }

    /// <summary>The story mentions of the object, when the text scan is ready. The page asks in each frame, so it keeps
    /// the result until the object or the edit view changes.</summary>
    private IReadOnlyList<Data.Editing.Scene.StoryMention> Mentions(SectionRef? section)
    {
        if (section?.Id.Key is not { } nickname || window.Session is not { } session || !session.Edits.StoryMentionsReady)
        {
            return [];
        }

        var view = session.Edits.EditView;
        if (mentions is not { } known || known.Nickname != nickname || !ReferenceEquals(known.View, view))
        {
            mentions = (nickname, view, session.Edits.StoryMentions(nickname));
        }

        return mentions.Value.Mentions;
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
    /// The atmosphere, colors and ring of a planet or a sun. The section shows only when one of them is set. In a
    /// bound scope, it then shows each of them, so that the other values can be added.
    /// </summary>
    private void Appearance(SystemObject obj)
    {
        var hasAppearance = obj.AtmosphereRange != 0 || obj.BurnColor != null || obj.AmbientColor != null ||
                            obj.RingZone != null || obj.RingFile != null;
        if (!grid.Shows(hasAppearance, AppearanceKeys) ||
            !grid.BeginSection("SystemObject.Appearance", EditorIcons.Star, "Appearance"))
        {
            return;
        }

        var all = grid.IsBound;
        if (all || obj.AtmosphereRange != 0)
        {
            grid.Number("Atmosphere range", obj.AtmosphereRange, iniKey: "atmosphere_range");
        }

        if (all || obj.BurnColor != null)
        {
            grid.Color("Burn color", obj.BurnColor, "burn_color");
        }

        if (all || obj.AmbientColor != null)
        {
            // The row edits the alias that the section uses (Schema/Universe/SystemObject.cs:18-19).
            grid.Color("Ambient color", obj.AmbientColor, grid.KeyOrAlias("ambient_color", "ambient"));
        }

        if (all || obj.RingZone != null)
        {
            var ring = obj.RingZone == null ? null : window.Scene.Index?.System.ZoneDict.GetValueOrDefault(obj.RingZone);
            if (all)
            {
                grid.SetNextNote("The zone of the ring, and the file of the ring model.");
            }

            grid.Reference(all ? "Ring" : "Ring zone", window.Scene.KindOf(ring) ?? ObjectKinds.Of(ZoneRole.Other),
                obj.RingZone, Name(ring), ring, "ring");
        }

        if (!all && obj.RingFile != null)
        {
            grid.Text("Ring file", obj.RingFile, "ring");
        }

        grid.EndSection();
    }

    /// <summary>A link to a trade lane ring of the open system.</summary>
    private void Ring(string label, string? nickname, string iniKey)
    {
        var ring = Object(nickname);
        grid.Reference(label, ObjectKinds.TradeLaneRing, nickname, Name(ring), ring, iniKey);
    }

    /// <summary>An object of the open system by nickname, or null.</summary>
    private SystemObject? Object(string? nickname) =>
        nickname == null ? null : window.Scene.Index?.ObjectsByNickname.GetValueOrDefault(nickname);

    /// <summary>The name of an item: from W for an item of the open system or a system (see
    /// <see cref="EditorSession.ItemName"/>), or null for no item.</summary>
    private string? Name(object? item) => item == null ? null : window.Session?.ItemName(item);

    /// <summary>"nickname (Name)", the nickname when the item has no name, or null for no item.</summary>
    private string? Named(NamedItem? item)
    {
        if (item == null)
        {
            return null;
        }

        var name = Name(item);
        return string.IsNullOrEmpty(name) ? item.Nickname : $"{item.Nickname} ({name})";
    }
}

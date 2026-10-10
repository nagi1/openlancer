using System;
using System.Collections.Generic;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>One trade lane: its rings in the order of the lane, from the first ring to the last ring.</summary>
internal sealed record TradeLane(IReadOnlyList<SystemObject> Rings);

/// <summary>How an item of the system uses a zone.</summary>
internal enum ZoneUseKind
{
    /// <summary>The zone is the volume of an asteroid field. The item is the <see cref="AsteroidField"/>.</summary>
    AsteroidField,

    /// <summary>The zone is the volume of a nebula. The item is the <see cref="Nebula"/>.</summary>
    Nebula,

    /// <summary>
    /// The zone is an exclusion zone. The item is the zone of the field or the nebula that the zone excludes from.
    /// </summary>
    Exclusion,

    /// <summary>The zone is the ring of a planet. The item is the <see cref="SystemObject"/>.</summary>
    Ring,
}

/// <summary>One item that uses a zone.</summary>
internal sealed record ZoneUse(ZoneUseKind Kind, object Item);

/// <summary>
/// Data about the open system that the Hierarchy, the Scene and the Inspector share. The Scene builds it once when it
/// opens a system.
/// </summary>
internal sealed class SystemIndex
{
    private readonly Dictionary<Zone, ZoneRole> zoneRoles;
    private readonly Dictionary<Zone, List<ZoneUse>> zoneUses;

    /// <param name="system">The copy of the system that the Scene shows.</param>
    /// <param name="source">The loaded system that <paramref name="system"/> is a copy of.</param>
    public SystemIndex(StarSystem system, StarSystem source)
    {
        System = system;
        Source = source;
        var objects = new Dictionary<string, SystemObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in system.Objects)
        {
            objects.TryAdd(obj.Nickname, obj);
        }

        ObjectsByNickname = objects;
        Lanes = BuildLanes(system.Objects);
        zoneRoles = ZoneRoles(system);
        zoneUses = ZoneUses(system);

        var volumes = new List<Zone>();
        var paths = new List<Zone>();
        var markers = new List<Zone>();
        foreach (var zone in system.Zones)
        {
            switch (ZoneRole(zone))
            {
                case Ui.ZoneRole.AsteroidField or Ui.ZoneRole.Nebula:
                    volumes.Add(zone);
                    break;
                // Only a cylinder has an axis for the path line. The game map has the same rule.
                case Ui.ZoneRole.PatrolPath when zone.Shape == ShapeKind.Cylinder:
                    paths.Add(zone);
                    break;
                default:
                    markers.Add(zone);
                    break;
            }
        }

        VolumeZones = volumes;
        PatrolPaths = paths;
        MarkerZones = markers;
    }

    /// <summary>The copy of the system that the Scene shows. The user selects its objects.</summary>
    public StarSystem System { get; }

    /// <summary>
    /// The loaded system from the game data. The Hierarchy root selects it, so the root and the Database row of the
    /// system select the same object.
    /// </summary>
    public StarSystem Source { get; }

    /// <summary>The objects of <see cref="System"/> by nickname. Case does not matter.</summary>
    public IReadOnlyDictionary<string, SystemObject> ObjectsByNickname { get; }

    /// <summary>The trade lanes of the system.</summary>
    public IReadOnlyList<TradeLane> Lanes { get; }

    /// <summary>The zones that the Scene shows as volumes: the zones of the asteroid fields and the nebulae.</summary>
    public IReadOnlyList<Zone> VolumeZones { get; }

    /// <summary>
    /// The patrol path zones with a cylinder shape. The Scene shows each one as a line along its axis.
    /// </summary>
    public IReadOnlyList<Zone> PatrolPaths { get; }

    /// <summary>All other zones. The Scene shows each one as a diamond marker.</summary>
    public IReadOnlyList<Zone> MarkerZones { get; }

    /// <summary>The role of a zone of <see cref="System"/>. A zone of another system gets the Other role.</summary>
    public ZoneRole ZoneRole(Zone zone) => zoneRoles.GetValueOrDefault(zone, Ui.ZoneRole.Other);

    /// <summary>The kind of an item. A zone of this system gets the kind of its role.</summary>
    public ObjectKind? KindOf(object? item) =>
        item is Zone zone && zoneRoles.TryGetValue(zone, out var role) ? ObjectKinds.Of(role) : ObjectKinds.Of(item);

    /// <summary>The items of <see cref="System"/> that use a zone, in the order of <see cref="ZoneUseKind"/>.</summary>
    public IReadOnlyList<ZoneUse> UsesOf(Zone zone) => zoneUses.TryGetValue(zone, out var uses) ? uses : [];

    /// <summary>
    /// Finds the trade lanes in a list of objects. A ring is an object with a trade lane dock. A lane starts at a ring
    /// that has no previous ring in the list and follows the next ring until the end. The rings that no start ring
    /// reaches, for example a closed loop, form lanes that start at their first ring in the list. Each ring is in one
    /// lane only, so a loop or a damaged chain cannot make the search run forever.
    /// </summary>
    internal static IReadOnlyList<TradeLane> BuildLanes(IEnumerable<SystemObject> objects)
    {
        var rings = new List<SystemObject>();
        var byNickname = new Dictionary<string, SystemObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in objects)
        {
            if (obj.Dock is { Kind: DockKinds.Tradelane } && byNickname.TryAdd(obj.Nickname, obj))
            {
                rings.Add(obj);
            }
        }

        var visited = new HashSet<SystemObject>(ReferenceEqualityComparer.Instance);
        var lanes = new List<TradeLane>();

        // Follows the next ring until the end of the lane, a missing ring or a ring that is already in a lane.
        void AddLane(SystemObject start)
        {
            var lane = new List<SystemObject>();
            for (var ring = start; ring != null && visited.Add(ring); ring = Find(byNickname, ring.Dock!.Target))
            {
                lane.Add(ring);
            }

            if (lane.Count > 0)
            {
                lanes.Add(new TradeLane(lane));
            }
        }

        foreach (var ring in rings)
        {
            if (Find(byNickname, ring.Dock!.TargetLeft) == null)
            {
                AddLane(ring);
            }
        }

        // The rings that no start ring reaches, for example the rings of a closed loop.
        foreach (var ring in rings)
        {
            AddLane(ring);
        }

        return lanes;
    }

    /// <summary>
    /// The role of each zone, in the order of <see cref="Ui.ZoneRole"/>: the zones of the asteroid fields and the
    /// nebulae, their exclusion zones, then population, patrol path and trade lane zones. Zones that are not in the
    /// result have the Other role.
    /// </summary>
    internal static Dictionary<Zone, ZoneRole> ZoneRoles(StarSystem system)
    {
        var roles = new Dictionary<Zone, ZoneRole>(ReferenceEqualityComparer.Instance);

        // A zone keeps the first role that it gets, because the roles are in the order of the list.
        void Add(Zone? zone, ZoneRole role)
        {
            if (zone != null)
            {
                roles.TryAdd(zone, role);
            }
        }

        foreach (var field in system.AsteroidFields)
        {
            Add(field.Zone, Ui.ZoneRole.AsteroidField);
        }

        foreach (var nebula in system.Nebulae)
        {
            Add(nebula.Zone, Ui.ZoneRole.Nebula);
        }

        foreach (var field in system.AsteroidFields)
        {
            foreach (var exclusion in field.ExclusionZones)
            {
                Add(exclusion.Zone, Ui.ZoneRole.Exclusion);
            }
        }

        foreach (var nebula in system.Nebulae)
        {
            foreach (var exclusion in nebula.ExclusionZones ?? [])
            {
                Add(exclusion.Zone, Ui.ZoneRole.Exclusion);
            }
        }

        foreach (var zone in system.Zones)
        {
            if (zone.IsPopulationZone)
            {
                Add(zone, Ui.ZoneRole.Population);
            }
            else if (zone.IsPatrolPathZone)
            {
                Add(zone, Ui.ZoneRole.PatrolPath);
            }
            else if (zone.LaneId != 0)
            {
                Add(zone, Ui.ZoneRole.TradeLane);
            }
        }

        return roles;
    }

    /// <summary>
    /// The items that use each zone: the asteroid fields and the nebulae, the fields and nebulae that a zone is an
    /// exclusion zone of, and the planets that a zone is the ring of.
    /// </summary>
    internal static Dictionary<Zone, List<ZoneUse>> ZoneUses(StarSystem system)
    {
        var uses = new Dictionary<Zone, List<ZoneUse>>(ReferenceEqualityComparer.Instance);

        void Add(Zone? zone, ZoneUseKind kind, object? item)
        {
            if (zone == null || item == null)
            {
                return;
            }

            if (!uses.TryGetValue(zone, out var list))
            {
                uses[zone] = list = [];
            }

            list.Add(new ZoneUse(kind, item));
        }

        foreach (var field in system.AsteroidFields)
        {
            Add(field.Zone, ZoneUseKind.AsteroidField, field);
        }

        foreach (var nebula in system.Nebulae)
        {
            Add(nebula.Zone, ZoneUseKind.Nebula, nebula);
        }

        foreach (var field in system.AsteroidFields)
        {
            foreach (var exclusion in field.ExclusionZones)
            {
                Add(exclusion.Zone, ZoneUseKind.Exclusion, field.Zone);
            }
        }

        foreach (var nebula in system.Nebulae)
        {
            foreach (var exclusion in nebula.ExclusionZones ?? [])
            {
                Add(exclusion.Zone, ZoneUseKind.Exclusion, nebula.Zone);
            }
        }

        var zones = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in system.Zones)
        {
            zones.TryAdd(zone.Nickname, zone);
        }

        foreach (var obj in system.Objects)
        {
            if (obj.RingZone != null && zones.TryGetValue(obj.RingZone, out var ring))
            {
                Add(ring, ZoneUseKind.Ring, obj);
            }
        }

        return uses;
    }

    private static SystemObject? Find(Dictionary<string, SystemObject> byNickname, string? nickname) =>
        nickname != null && byNickname.TryGetValue(nickname, out var obj) ? obj : null;
}

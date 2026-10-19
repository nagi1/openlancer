using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
/// A nebula or an asteroid field of the open system as a scene item: its <c>[Nebula]</c> or <c>[Asteroids]</c> section
/// (decision D151 of the M22 design). As in a Unity GameObject, its zone is its Transform.
/// </summary>
/// <param name="Item">The <see cref="Nebula"/> or the <see cref="AsteroidField"/>.</param>
/// <param name="Zone">The zone of the item.</param>
/// <param name="Kind"><see cref="ObjectKinds.Nebula"/> or <see cref="ObjectKinds.AsteroidField"/>.</param>
internal sealed record VolumeItem(object Item, Zone Zone, ObjectKind Kind);

/// <summary>
/// Data about the open system that the Hierarchy, the Scene and the Inspector share. The Scene builds it once when it
/// opens a system.
/// </summary>
internal sealed class SystemIndex
{
    private readonly Dictionary<Zone, ZoneRole> zoneRoles;
    private readonly Dictionary<Zone, List<ZoneUse>> zoneUses;
    private readonly Dictionary<object, VolumeItem> volumeOf;

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

        // The fields first, then the nebulae, each in file order. An item whose zone the system does not have is no
        // scene item: the engine drops it (GameItemDb.cs:1714-1728, :1773-1777).
        var items = new List<VolumeItem>();
        items.AddRange(system.AsteroidFields.Where(f => f.Zone != null)
            .Select(f => new VolumeItem(f, f.Zone!, ObjectKinds.AsteroidField)));
        items.AddRange(system.Nebulae.Where(n => n.Zone != null)
            .Select(n => new VolumeItem(n, n.Zone!, ObjectKinds.Nebula)));
        VolumeItems = items;
        volumeOf = items.ToDictionary(v => v.Item, ReferenceEqualityComparer.Instance);
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
    /// The patrol path zones with a cylinder shape. The Scene shows each one as its <see cref="PathLine"/>.
    /// </summary>
    public IReadOnlyList<Zone> PatrolPaths { get; }

    /// <summary>The line of a patrol path: the axis of its cylinder, through its center.</summary>
    public static (Vector3 Start, Vector3 End) PathLine(Zone zone)
    {
        var half = Vector3.TransformNormal(new Vector3(0, zone.Size.Y / 2, 0), zone.RotationMatrix);
        return (zone.Position - half, zone.Position + half);
    }

    /// <summary>All other zones. The Scene shows each one as a diamond marker.</summary>
    public IReadOnlyList<Zone> MarkerZones { get; }

    /// <summary>The asteroid fields and then the nebulae of <see cref="System"/> that have a zone, in file order. A field
    /// and a nebula that share a zone give two items.</summary>
    public IReadOnlyList<VolumeItem> VolumeItems { get; }

    /// <summary>The volume item of a nebula or a field of <see cref="System"/>, or null for another item.</summary>
    public VolumeItem? VolumeOf(object? item) => item != null && volumeOf.TryGetValue(item, out var volume) ? volume : null;

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
            else if (IsTradeLaneZone(zone))
            {
                Add(zone, Ui.ZoneRole.TradeLane);
            }
        }

        return roles;
    }

    /// <summary>
    /// True when the game uses a zone as the zone of a trade lane: its nickname has the word "Tradelane" with this letter
    /// case, and it is a box that is more than 10 times longer on its z axis than on its x axis and on its y axis. The
    /// game does not read <c>lane_id</c> (decision D513 of the console filter design), so the role does not use it. Most
    /// trade lane zones have a text such as <c>br01_2</c> in that key, and the engine reads such a text in a text file as
    /// 0.
    /// </summary>
    internal static bool IsTradeLaneZone(Zone zone) =>
        zone.Nickname.Contains("Tradelane", StringComparison.Ordinal) && zone.Shape == ShapeKind.Box &&
        zone.Size.X * 10 < zone.Size.Z && zone.Size.Y * 10 < zone.Size.Z;

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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Data.Missions;

/// <summary>A kind of a placeable item of a mission (section 7.2 of the M6a design).</summary>
public enum PlacementKind
{
    /// <summary>The start position of a <c>[MsnShip]</c>.</summary>
    ShipStart,

    /// <summary>The start position of a <c>[MsnFormation]</c>.</summary>
    FormationStart,

    /// <summary>A member of a formation at a slot of its shape.</summary>
    FormationMember,

    /// <summary>The start position of a <c>[MsnSolar]</c>.</summary>
    SolarStart,

    /// <summary>The start position of a <c>[MsnLoot]</c>, also near its anchor object.</summary>
    LootStart,

    /// <summary>A ship or formation placed near an anchor (<c>rel_pos</c>): a ring around the anchor at the range, and a
    /// dot at the heading.</summary>
    NearAnchor,

    /// <summary>The object that a ship arrives through (<c>arrival_obj</c>): a line from the ship start to it.</summary>
    Arrival,

    /// <summary>A spawn line with a position.</summary>
    Spawn,

    /// <summary>A move line of a ship, a formation or the player.</summary>
    Move,

    /// <summary>The object that a route line names.</summary>
    Route,

    /// <summary>A sphere of a distance condition: inside or outside.</summary>
    DistanceVolume,

    /// <summary>The ring of a ring pass condition.</summary>
    RingPass,

    /// <summary>The plane of a plane condition.</summary>
    Plane,

    /// <summary>The sphere of an ambient traffic area.</summary>
    TrafficArea,

    /// <summary>The marker of a <c>navmarker</c> or <c>rep_inst</c> objective.</summary>
    ObjectiveMarker,

    /// <summary>A point, a spline or a range sphere of an order list.</summary>
    OrderPath,

    /// <summary>The position of a fixed camera.</summary>
    FixedCamera,

    /// <summary>The start position of the anchor of a space cutscene.</summary>
    CutsceneAnchor,
}

/// <summary>The shape that the Scene draws for a placeable item.</summary>
public enum PlacementShape
{
    /// <summary>A glyph at the position.</summary>
    Point,

    /// <summary>A sphere at the position with the radius: solid for inside, dashed for outside.</summary>
    Sphere,

    /// <summary>A ring around the position with the radius, and a dot at the end point.</summary>
    Ring,

    /// <summary>A disc at the position with an arrow for the direction of the pass.</summary>
    Disc,

    /// <summary>A plane quad through the position with the normal of <see cref="MissionPlacement.To"/>.</summary>
    Plane,

    /// <summary>A line from the position to <see cref="MissionPlacement.To"/>.</summary>
    Line,

    /// <summary>A line through the points.</summary>
    Path,
}

/// <summary>
/// One placeable item of a mission in a system (section 7.2 of the M6a design). It has its kind, its shape, its system,
/// its position and the line or section that gives it.
/// </summary>
/// <param name="Section">The section that gives the item: the item section, the trigger, or the order list.</param>
/// <param name="Entry">The index of the line that gives the item, or -1 for the whole section.</param>
/// <param name="Label">The text of the label in the Scene, for example the nickname of the ship.</param>
public sealed record MissionPlacement(PlacementKind Kind, PlacementShape Shape, string System, Vector3 Position,
    MissionSection Section, int Entry, string Label)
{
    /// <summary>The radius of a sphere, a ring or a disc.</summary>
    public float Radius { get; init; }

    /// <summary>True for a sphere of an outside condition. The Scene draws it dashed.</summary>
    public bool Outside { get; init; }

    /// <summary>The end of a line, the dot of a ring, or the normal of a plane.</summary>
    public Vector3? To { get; init; }

    /// <summary>The points of a path, or the members of a formation.</summary>
    public IReadOnlyList<Vector3> Points { get; init; } = [];

    /// <summary>The trigger of a line of a trigger, or null.</summary>
    public MissionTrigger? Trigger { get; init; }
}

/// <summary>
/// The placeable items of a mission (section 7.2 of the M6a design). They are the start positions of the cast, the
/// anchors, and the spawn and move lines with positions. They are also the volumes of the distance conditions, the
/// objective markers, the order paths, the cameras and the cutscene anchors. The class makes no ImGui call, so the
/// Scene, the tests and later an agent server (roadmap row MCP1) read the same items.
/// </summary>
/// <remarks>
/// <para>The system of an item comes from its <c>system</c> line, else from the trigger that spawns or moves it. Else
/// it comes from its conversation, as the index finds it (<see cref="MissionIndex.SystemOf"/>). A line of a trigger has
/// the system line of its trigger. An order list has the system of the first trigger or ship that gives it.</para>
/// <para>An item placed near the player has no position (decision D532), so it is not a placement. The Hierarchy lists
/// it in the group "Placed near the player".</para>
/// </remarks>
public static class MissionPlacements
{
    /// <summary>The keys of the trigger lines that can have their own place in the Scene.</summary>
    private static readonly HashSet<string> PlacedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Act_SpawnShip", "Act_SpawnFormation", "Act_SpawnSolar", "Act_SpawnLoot", "Act_RelocateShip", "Act_RelocateForm",
        "Act_MovePlayer", "Act_SetInitialPlayerPos", "Act_NNPath", "Cnd_DistVec", "Cnd_DistShip", "Cnd_DistCircle",
        "Cnd_CmpToPlane", "Act_RandomPopSphere", "Act_StaticCam", "Act_CallThorn",
    };

    /// <summary>True when a line of a trigger can have its own place in the Scene. Examples are a distance volume and a
    /// spawn at a position.</summary>
    public static bool HasPlace(MissionLine line) => line.Trigger != null && PlacedKeys.Contains(line.Key.Trim());

    /// <summary>The placeable items of a mission in one system.</summary>
    /// <param name="objectPosition">Gives the position of an object of the system by its nickname, or null when the
    /// system has no such object.</param>
    /// <param name="formationSlots">Gives the slot offsets of a formation shape by its nickname, or null.</param>
    /// <param name="systemOverride">Gives the system that the user picked for an item, or null for the system that the
    /// index finds (the Hierarchy menu "Show in system...").</param>
    public static IReadOnlyList<MissionPlacement> In(MissionIndex index, string system,
        Func<string, Vector3?> objectPosition, Func<string, IReadOnlyList<Vector3>?>? formationSlots = null,
        Func<MissionSection, string?>? systemOverride = null)
    {
        var context = new Context(index, system, objectPosition, formationSlots, systemOverride);
        context.Cast();
        context.Triggers();
        context.Objectives();
        context.OrderLists();
        return context.Result;
    }

    /// <summary>The sections of the cast that a mission places near the player (<c>rel_pos</c> or <c>rel_pos_obj</c>
    /// with the anchor Player), in file order.</summary>
    public static IReadOnlyList<MissionSection> NearPlayer(MissionIndex index) =>
        index.Document.Sections.Where(s => s.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation or
                                               MissionSectionKind.Loot && Anchor(s) is { } anchor &&
                                           MissionIndex.Is(anchor, "Player")).ToList();

    /// <summary>The items of the cast whose system the index cannot find, in file order. Such an item is a ship, a
    /// formation, a solar or a loot without a system line. It also has no spawn trigger with a system and no
    /// conversation. An item placed near the player is not in the list.</summary>
    public static IReadOnlyList<MissionSection> SystemUnknown(MissionIndex index)
    {
        var nearPlayer = NearPlayer(index).ToHashSet();
        return index.Document.Sections.Where(s => s.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation or
                                                      MissionSectionKind.Solar or MissionSectionKind.Loot &&
                                                  index.SystemOf(s) == null && !nearPlayer.Contains(s)).ToList();
    }

    /// <summary>The system of a line of a trigger: the system line of its trigger. Else it is the system of the first
    /// item of the script that the line names. Null when the index finds none.</summary>
    /// <param name="systemOverride">Gives the system that the user picked for an item, or null.</param>
    public static string? SystemOfLine(MissionIndex index, MissionLine line,
        Func<MissionSection, string?>? systemOverride = null)
    {
        if (line.Trigger?.SystemHint is { } hint && !MissionIndex.Is(hint, "ANY"))
            return hint;
        return line.References.FirstOrDefault(r => r.Section != null) is { Section: { } item }
            ? systemOverride?.Invoke(item) ?? index.SystemOf(item)
            : null;
    }

    /// <summary>The anchor of a <c>rel_pos</c> or <c>rel_pos_obj</c> line of a section, or null.</summary>
    private static string? Anchor(MissionSection section)
    {
        foreach (var entry in section.Section)
        {
            if (MissionIndex.Is(entry.Name, "rel_pos") && entry.Count > 1)
                return MissionIndex.Text(entry[1]);
            if (MissionIndex.Is(entry.Name, "rel_pos_obj") && entry.Count > 0)
                return MissionIndex.Text(entry[0]);
        }
        return null;
    }

    private sealed class Context(MissionIndex index, string system, Func<string, Vector3?> objectPosition,
        Func<string, IReadOnlyList<Vector3>?>? formationSlots, Func<MissionSection, string?>? systemOverride)
    {
        /// <summary>The system of an item: the system that the user picked, or the system that the index finds.</summary>
        private string? SystemOf(MissionSection section) => systemOverride?.Invoke(section) ?? index.SystemOf(section);

        // The items whose start this context finds now. An anchor that leads back to such an item has no place.
        private readonly HashSet<MissionSection> resolving = [];

        public List<MissionPlacement> Result { get; } = [];

        private bool InSystem(string? itemSystem) => itemSystem != null && MissionIndex.Is(itemSystem, system);

        /// <summary>The position of a name in the system. It is the start of an item of the script in the system, or of
        /// the first member of a label with a start. Else it is an object of the system.</summary>
        public Vector3? PositionOf(string name)
        {
            if (MissionIndex.Is(name, "Player"))
                return null;
            foreach (var kind in new[] { MissionSectionKind.Ship, MissionSectionKind.Formation, MissionSectionKind.Solar,
                         MissionSectionKind.Loot })
            {
                if (index.Item(kind, name) is { } section)
                    return InSystem(SystemOf(section)) ? Start(section) : null;
            }
            if (index.Labels.TryGetValue(name.Trim(), out var members))
            {
                return members.Where(m => InSystem(SystemOf(m))).Select(Start).FirstOrDefault(p => p != null);
            }
            return objectPosition(name.Trim());
        }

        /// <summary>The start position of an item: its position line, or the place near its anchor.</summary>
        private Vector3? Start(MissionSection section)
        {
            if (Line(section, "position") is { } position && Vector(position, 0) is { } start)
                return start;
            if (!resolving.Add(section))
                return null;
            try
            {
                return NearAnchor(section);
            }
            finally
            {
                resolving.Remove(section);
            }
        }

        /// <summary>The place of an item near its anchor: at the heading and range of <c>rel_pos</c>, or at the offset of
        /// <c>rel_pos_obj</c>.</summary>
        private Vector3? NearAnchor(MissionSection section)
        {
            if (Line(section, "rel_pos") is { Count: > 2 } rel && MissionIndex.Text(rel[1]) is var anchor &&
                PositionOf(anchor) is { } center && Number(rel, 0) is { } heading && Number(rel, 2) is { } range)
                return center + Heading(heading) * range;
            if (Line(section, "rel_pos_obj") is { Count: > 0 } obj && PositionOf(MissionIndex.Text(obj[0])) is { } at)
                return at + (Line(section, "rel_pos_offset") is { } offset ? Vector(offset, 0) ?? Vector3.Zero : Vector3.Zero);
            return null;
        }

        /// <summary>The start positions of the cast in the system, the anchors, and the arrival lines.</summary>
        public void Cast()
        {
            foreach (var section in index.Document.Sections)
            {
                var kind = section.Kind switch
                {
                    MissionSectionKind.Ship => PlacementKind.ShipStart,
                    MissionSectionKind.Formation => PlacementKind.FormationStart,
                    MissionSectionKind.Solar => PlacementKind.SolarStart,
                    MissionSectionKind.Loot => PlacementKind.LootStart,
                    _ => (PlacementKind?)null,
                };
                if (kind == null || !InSystem(SystemOf(section)))
                    continue;
                var label = section.Nickname ?? section.Ref.Id.ToString();
                if (Line(section, "rel_pos") is { Count: > 2 } rel)
                {
                    var anchor = MissionIndex.Text(rel[1]);
                    if (PositionOf(anchor) is { } center && Number(rel, 2) is { } range)
                    {
                        var dot = center + Heading(Number(rel, 0) ?? 0) * range;
                        Result.Add(new MissionPlacement(PlacementKind.NearAnchor, PlacementShape.Ring, system, center,
                            section, EntryOf(section, "rel_pos"), label)
                        {
                            Radius = range, To = dot,
                        });
                    }
                    continue;
                }
                if (Start(section) is not { } start)
                    continue;
                Result.Add(new MissionPlacement(kind.Value, PlacementShape.Point, system, start, section, -1, label));
                if (kind == PlacementKind.FormationStart && Line(section, "formation") is { Count: > 0 } shape &&
                    formationSlots?.Invoke(MissionIndex.Text(shape[0])) is { } slots)
                {
                    var members = section.Section.Count(e => MissionIndex.Is(e.Name, "ship"));
                    var points = slots.Take(members).Select(s => start + s).ToList();
                    if (points.Count > 0)
                        Result.Add(new MissionPlacement(PlacementKind.FormationMember, PlacementShape.Point, system, start,
                            section, EntryOf(section, "formation"), label) { Points = points });
                }
                if (Line(section, "arrival_obj") is { Count: > 0 } arrival && MissionIndex.Text(arrival[0]) is var gate &&
                    objectPosition(gate) is { } end)
                {
                    Result.Add(new MissionPlacement(PlacementKind.Arrival, PlacementShape.Line, system, start, section,
                        EntryOf(section, "arrival_obj"), label) { To = end });
                }
            }
        }

        /// <summary>The lines of the triggers with a place: spawns, moves, routes, volumes, rings, planes, traffic
        /// areas, cameras and cutscene anchors. A line has the system of its trigger, or of the item that it
        /// names.</summary>
        public void Triggers()
        {
            foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate))
            {
                foreach (var line in trigger.Rows)
                {
                    if (!InSystem(SystemOfLine(index, line, systemOverride)) || Placement(trigger, line) is not { } placement)
                        continue;
                    Result.Add(placement);
                }
            }
        }

        private MissionPlacement? Placement(MissionTrigger trigger, MissionLine line)
        {
            MissionPlacement Make(PlacementKind kind, PlacementShape shape, Vector3 position, string label) =>
                new(kind, shape, system, position, trigger.Section, line.Entry, label) { Trigger = trigger };

            var key = line.Key.ToLowerInvariant();
            var name = line.Name?.Plain ?? line.Key;
            switch (key)
            {
                case "act_spawnship" or "act_spawnformation" or "act_spawnsolar" or "act_spawnloot":
                {
                    var spec = line.Name?.Value("position");
                    if (spec == null || Vector(line.Line, spec.Index) is not { } position)
                        return null;
                    return Make(PlacementKind.Spawn, PlacementShape.Point, position, $"{name}: {line.Value(0)}");
                }
                case "act_relocateship" or "act_relocateform":
                    return Vector(line.Line, 1) is { } moved
                        ? Make(PlacementKind.Move, PlacementShape.Point, moved, $"{name}: {line.Value(0)}")
                        : null;
                case "act_moveplayer" or "act_setinitialplayerpos":
                    return Vector(line.Line, 0) is { } player
                        ? Make(PlacementKind.Move, PlacementShape.Point, player, name)
                        : null;
                case "act_nnpath":
                    // The line names only the end of the route. The script gives no start point, so the layer shows
                    // the end as a point, not as a path line.
                    return line.Value(2) is { } where && objectPosition(where) is { } target
                        ? Make(PlacementKind.Route, PlacementShape.Point, target, $"{name}: {where}")
                        : null;
                case "cnd_distvec":
                    return Vector(line.Line, 2) is { } point && Number(line.Line, 5) is { } radius
                        ? Make(PlacementKind.DistanceVolume, PlacementShape.Sphere, point, name) with
                        {
                            Radius = radius, Outside = MissionIndex.Is(line.Value(0), "outside"),
                        }
                        : null;
                case "cnd_distship":
                    return line.Value(2) is { } other && PositionOf(other) is { } center && Number(line.Line, 3) is { } range
                        ? Make(PlacementKind.DistanceVolume, PlacementShape.Sphere, center, $"{name}: {other}") with
                        {
                            Radius = range, Outside = MissionIndex.Is(line.Value(0), "outside"),
                        }
                        : null;
                case "cnd_distcircle":
                    return line.Value(1) is { } ring && objectPosition(ring) is { } disc
                        ? Make(PlacementKind.RingPass, PlacementShape.Disc, disc, $"{name}: {ring}") with
                        {
                            Radius = 300,
                        }
                        : null;
                case "cnd_cmptoplane":
                    if (Number(line.Line, 2) is not { } a || Number(line.Line, 3) is not { } b ||
                        Number(line.Line, 4) is not { } c || Number(line.Line, 5) is not { } d)
                        return null;
                    var normal = new Vector3(a, b, c);
                    if (normal.LengthSquared() < 1e-6f)
                        return null;
                    var unit = Vector3.Normalize(normal);
                    return Make(PlacementKind.Plane, PlacementShape.Plane, -unit * (d / normal.Length()), name) with
                    {
                        To = unit, Radius = 5000,
                    };
                case "act_randompopsphere":
                    return Vector(line.Line, 0) is { } traffic && Number(line.Line, 3) is { } area
                        ? Make(PlacementKind.TrafficArea, PlacementShape.Sphere, traffic, name) with { Radius = area }
                        : null;
                case "act_staticcam":
                    return Vector(line.Line, 0) is { } camera
                        ? Make(PlacementKind.FixedCamera, PlacementShape.Point, camera, name)
                        : null;
                case "act_callthorn":
                    return line.Value(1) is { } anchor && !MissionIndex.Is(anchor, "Player") && PositionOf(anchor) is { } at
                        ? Make(PlacementKind.CutsceneAnchor, PlacementShape.Point, at, $"{name}: {line.Value(0)}")
                        : null;
                default:
                    return null;
            }
        }

        /// <summary>The markers of the <c>navmarker</c> and <c>rep_inst</c> objectives in the system. The type line has
        /// the system in value 1 and the position in values 4 to 6. A <c>rep_inst</c> objective names its object in
        /// value 7.</summary>
        public void Objectives()
        {
            foreach (var section in index.Document.OfKind(MissionSectionKind.Objective))
            {
                if (Line(section, "type") is not { Count: > 6 } type)
                    continue;
                var kind = MissionIndex.Text(type[0]);
                if (!MissionIndex.Is(kind, "navmarker") && !MissionIndex.Is(kind, "rep_inst") ||
                    !InSystem(MissionIndex.Text(type[1])) || Vector(type, 4) is not { } position)
                    continue;
                var target = MissionIndex.Is(kind, "rep_inst") && type.Count > 7 ? MissionIndex.Text(type[7]) : null;
                Result.Add(new MissionPlacement(PlacementKind.ObjectiveMarker, target != null && objectPosition(target) is { }
                        ? PlacementShape.Line : PlacementShape.Point, system, position, section, EntryOf(section, "type"),
                    section.Nickname ?? section.Ref.Id.ToString())
                {
                    To = target != null ? objectPosition(target) : null,
                });
            }
        }

        /// <summary>The paths of the order lists in the system. They are the points of GotoVec, the splines of
        /// GotoSpline, and the range spheres of StayInRange with a point.</summary>
        public void OrderLists()
        {
            foreach (var section in index.Document.OfKind(MissionSectionKind.OrderList))
            {
                if (!InSystem(SystemOfOrders(section)))
                    continue;
                var label = section.Nickname ?? section.Ref.Id.ToString();
                var points = new List<Vector3>();
                for (var i = 0; i < section.Section.Count; i++)
                {
                    var entry = section.Section[i];
                    if (MissionIndex.Is(entry.Name, "GotoVec") && Vector(entry, 1) is { } point)
                    {
                        points.Add(point);
                        Result.Add(new MissionPlacement(PlacementKind.OrderPath, PlacementShape.Point, system, point,
                            section, i, $"{label} {points.Count.ToString(CultureInfo.InvariantCulture)}"));
                    }
                    else if (MissionIndex.Is(entry.Name, "GotoSpline"))
                    {
                        var spline = Enumerable.Range(0, 4).Select(k => Vector(entry, 1 + 3 * k)).OfType<Vector3>().ToList();
                        if (spline.Count < 2)
                            continue;
                        points.AddRange(spline);
                        Result.Add(new MissionPlacement(PlacementKind.OrderPath, PlacementShape.Path, system, spline[0],
                            section, i, label) { Points = spline });
                    }
                    else if (MissionIndex.Is(entry.Name, "StayInRange") && Vector(entry, 0) is { } center &&
                             Number(entry, 3) is { } range)
                    {
                        Result.Add(new MissionPlacement(PlacementKind.OrderPath, PlacementShape.Sphere, system, center,
                            section, i, label) { Radius = range });
                    }
                }
                if (points.Count > 1)
                    Result.Add(new MissionPlacement(PlacementKind.OrderPath, PlacementShape.Path, system, points[0],
                        section, -1, label) { Points = points });
            }
        }

        /// <summary>The system of an order list: its own system, or the system of the first trigger line that gives it.
        /// Else it is the system of the first ship that has it as its first orders.</summary>
        private string? SystemOfOrders(MissionSection section)
        {
            if (SystemOf(section) is { } own)
                return own;
            foreach (var reference in index.ReferencesTo(MissionValueKind.ObjList, section.Nickname ?? ""))
            {
                if (reference.Line.Trigger?.SystemHint is { } hint && !MissionIndex.Is(hint, "ANY"))
                    return hint;
                if (reference.Line.Section.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation &&
                    index.SystemOf(reference.Line.Section) is { } ship)
                    return ship;
            }
            return null;
        }
    }

    /// <summary>The first line of a key in a section, or null.</summary>
    private static LibreLancer.Data.Ini.Entry? Line(MissionSection section, string key) =>
        section.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, key));

    /// <summary>The index of the first line of a key in a section, or -1.</summary>
    private static int EntryOf(MissionSection section, string key)
    {
        for (var i = 0; i < section.Section.Count; i++)
        {
            if (MissionIndex.Is(section.Section[i].Name, key))
                return i;
        }
        return -1;
    }

    /// <summary>The number of a value of a line, or null when the value is missing or not a number.</summary>
    private static float? Number(LibreLancer.Data.Ini.Entry entry, int index) =>
        index < entry.Count && float.TryParse(MissionIndex.Text(entry[index]), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value
            : null;

    /// <summary>The position of three values of a line from an index, or null.</summary>
    private static Vector3? Vector(LibreLancer.Data.Ini.Entry entry, int index) =>
        Number(entry, index) is { } x && Number(entry, index + 1) is { } y && Number(entry, index + 2) is { } z
            ? new Vector3(x, y, z)
            : null;

    /// <summary>The direction of a heading in degrees in the plane of the system. 0 is the -Z axis, and the angle turns
    /// to the +X axis. The editor shows the dot at this place. No reader checked the convention of the game.</summary>
    private static Vector3 Heading(float degrees)
    {
        var radians = degrees * MathF.PI / 180;
        return new Vector3(MathF.Sin(radians), 0, -MathF.Cos(radians));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Render;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The mission layer of the Scene (section 7.2 of the M6a design): the items of the open mission in the open system.
/// The ship, formation, solar and loot starts, the spawns, moves, routes, objective markers, cameras and cutscene anchors
/// are markers, which the picker and the pick cycle find. The volumes, rings, planes, paths and lines are helper lines of
/// the 3D scene. The layer shows while the toggle "Mission items" is on and a mission is open. An item of the highlight
/// set of the cross-links shows also when the toggle is off. The markers are read-only, so the gizmo does not take them.
/// </summary>
/// <remarks>The layer reads the placements of <see cref="MissionPlacements"/> once for each mission and system, so it does
/// no work for a frame without a change.</remarks>
internal sealed class MissionLayer(EditorWindow window)
{
    // A sphere is three great circles of this many segments. An outside sphere draws every other segment.
    private const int Segments = 48;

    private static readonly Color4 LineColor = new(EditorTheme.MissionItem with { W = 0.75f });
    private static readonly Color4 HighlightColor = new(EditorTheme.Accent);

    private static readonly Dictionary<PlacementKind, ObjectKind> Kinds = new()
    {
        [PlacementKind.ShipStart] = Kind("Mission ship start", EditorIcons.SpaceShuttle),
        [PlacementKind.FormationStart] = Kind("Mission formation start", EditorIcons.Users),
        [PlacementKind.FormationMember] = Kind("Formation member", EditorIcons.SpaceShuttle, 9),
        [PlacementKind.SolarStart] = Kind("Mission solar start", EditorIcons.Satellite),
        [PlacementKind.LootStart] = Kind("Mission loot start", EditorIcons.Box),
        [PlacementKind.NearAnchor] = Kind("Placed near an anchor", EditorIcons.DotCircle),
        [PlacementKind.Arrival] = Kind("Arrival object", EditorIcons.DoorOpen),
        [PlacementKind.Spawn] = Kind("Spawn", EditorIcons.Asterisk),
        [PlacementKind.Move] = Kind("Move", EditorIcons.ArrowsAlt),
        [PlacementKind.Route] = Kind("Route", EditorIcons.Road),
        [PlacementKind.DistanceVolume] = Kind("Distance volume", EditorIcons.Bullseye),
        [PlacementKind.RingPass] = Kind("Ring pass", EditorIcons.LifeRing),
        [PlacementKind.Plane] = Kind("Plane", EditorIcons.VectorSquare),
        [PlacementKind.TrafficArea] = Kind("Traffic area", EditorIcons.Stream),
        [PlacementKind.ObjectiveMarker] = Kind("Objective marker", EditorIcons.MapMarkerAlt),
        [PlacementKind.OrderPath] = Kind("Order path", EditorIcons.Route),
        [PlacementKind.FixedCamera] = Kind("Fixed camera", EditorIcons.Video),
        [PlacementKind.CutsceneAnchor] = Kind("Cutscene anchor", EditorIcons.Film),
    };

    private (MissionAnalysis? Analysis, StarSystemKey System, int Names, int Overrides) built;
    private IReadOnlyList<(MissionRef Ref, MissionPlacement Placement)> items = [];
    private Dictionary<string, IReadOnlyList<Vector3>>? slots;
    private object? slotsView;

    /// <summary>The key of the open system: its nickname and the index of the Scene.</summary>
    private readonly record struct StarSystemKey(string? Nickname, SystemIndex? Index);

    /// <summary>The kind of a placement, for the marker and the label.</summary>
    public static ObjectKind KindOf(PlacementKind kind) => Kinds[kind];

    /// <summary>The items of the cross-links that the layer highlights, also when the toggle is off (section 7.3 of the
    /// M6a design). The Mission tab sets them in each frame.</summary>
    public IReadOnlySet<MissionRef> Highlight { get; set; } = new HashSet<MissionRef>();

    /// <summary>The placements of the open mission in the open system, with their selections.</summary>
    public IReadOnlyList<(MissionRef Ref, MissionPlacement Placement)> Items => items;

    /// <summary>True while a mission is open, so the toggle "Mission items" has an effect and shows (UI rule 4).</summary>
    public bool Active => window.Missions.Open != null;

    private static ObjectKind Kind(string label, char icon, float size = 12) =>
        new(label, icon, EditorTheme.MissionItem, size);

    /// <summary>Reads the placements again when the mission, the system or the names of the system changed.</summary>
    public void Update(SystemIndex index, int namesVersion)
    {
        var analysis = window.Missions.Open;
        var key = (analysis, new StarSystemKey(index.Source.Nickname, index), namesVersion, window.Missions.OverridesVersion);
        if (key == built)
        {
            return;
        }

        built = key;
        if (analysis == null)
        {
            items = [];
            return;
        }

        var placements = MissionPlacements.In(analysis.Index, index.Source.Nickname,
            name => index.ObjectsByNickname.GetValueOrDefault(name)?.Position, FormationSlots,
            section => window.Missions.SystemOverride(section.Ref));
        items = placements.Select(p => (RefOf(analysis.Script.Path, p), p)).ToList();
    }

    /// <summary>The selection of a placement: the line of a trigger, or the section of an item.</summary>
    public static MissionRef RefOf(string script, MissionPlacement placement) => placement.Trigger != null
        ? new MissionRef(script, MissionItemKind.Row, placement.Section.Ref, Row: placement.Entry)
        : MissionWorkspace.RefOf(placement.Section.Ref, -1);

    /// <summary>The markers of the layer: the glyph of each item, and the ghost glyphs of the formation members. With
    /// the toggle off, only the highlighted items show.</summary>
    public IEnumerable<(object Item, ObjectKind Kind, uint Color, Vector3 Position)> Markers(bool toggle)
    {
        foreach (var (item, placement) in items)
        {
            var highlighted = Highlight.Contains(item);
            // A plane is a quad only. The whole route of an order list has the markers of its points.
            if (!toggle && !highlighted || placement.Shape == PlacementShape.Plane ||
                placement.Shape == PlacementShape.Path && placement.Entry < 0)
            {
                continue;
            }

            var kind = Kinds[placement.Kind];
            var color = EditorTheme.ToAbgr(highlighted ? EditorTheme.Accent : EditorTheme.MissionItem);
            if (placement.Kind == PlacementKind.FormationMember)
            {
                foreach (var point in placement.Points)
                {
                    yield return (item, kind, color, point);
                }

                continue;
            }

            var position = placement.Shape == PlacementShape.Ring && placement.To is { } dot ? dot : placement.Position;
            yield return (item, kind, color, position);
        }
    }

    /// <summary>The label of a marker item, or null for an item that is not of the layer.</summary>
    public string? Label(object item) =>
        item is MissionRef mission && items.FirstOrDefault(i => i.Ref.Equals(mission)) is { Placement: { } placement }
            ? placement.Label
            : null;

    /// <summary>The position of an item of the layer, for the focus of the camera, or null.</summary>
    public (Vector3 Position, float Radius)? PlaceOf(MissionRef item) =>
        items.FirstOrDefault(i => i.Ref.Equals(item)) is { Placement: { } placement }
            ? (placement.Position, Math.Max(500, placement.Radius * 1.5f))
            : null;

    /// <summary>Draws the helper lines of the layer: spheres, rings, discs, planes, paths and lines. Each line goes into
    /// the line list of the renderer, so the renderer calls this in its helper hook.</summary>
    public void DrawLines(LineRenderer lines, bool toggle)
    {
        foreach (var (item, placement) in items)
        {
            var highlighted = Highlight.Contains(item);
            if (!toggle && !highlighted)
            {
                continue;
            }

            var color = highlighted ? HighlightColor : LineColor;
            switch (placement.Shape)
            {
                case PlacementShape.Sphere:
                    Sphere(lines, placement.Position, placement.Radius, color, placement.Outside);
                    break;
                case PlacementShape.Ring:
                    Circle(lines, placement.Position, Vector3.UnitX * placement.Radius, Vector3.UnitZ * placement.Radius,
                        color, false);
                    if (placement.To is { } dot)
                    {
                        lines.DrawLine(placement.Position, dot, color);
                    }

                    break;
                case PlacementShape.Disc:
                    Circle(lines, placement.Position, Vector3.UnitX * placement.Radius, Vector3.UnitY * placement.Radius,
                        color, false);
                    // The arrow of the pass direction, along the axis of the ring.
                    var tip = placement.Position + Vector3.UnitZ * placement.Radius;
                    lines.DrawLine(placement.Position - Vector3.UnitZ * placement.Radius, tip, color);
                    lines.DrawLine(tip, tip - (Vector3.UnitZ + Vector3.UnitX) * placement.Radius * 0.3f, color);
                    lines.DrawLine(tip, tip - (Vector3.UnitZ - Vector3.UnitX) * placement.Radius * 0.3f, color);
                    break;
                case PlacementShape.Plane when placement.To is { } normal:
                    Plane(lines, placement.Position, normal, placement.Radius, color);
                    break;
                case PlacementShape.Line when placement.To is { } end:
                    lines.DrawLine(placement.Position, end, color);
                    break;
                case PlacementShape.Path:
                    for (var i = 1; i < placement.Points.Count; i++)
                    {
                        lines.DrawLine(placement.Points[i - 1], placement.Points[i], color);
                    }

                    break;
            }
        }
    }

    /// <summary>The slot offsets of the formation shapes of <c>formations.ini</c> in the edit view, by nickname. The
    /// layer reads them once for each edit view.</summary>
    private IReadOnlyList<Vector3>? FormationSlots(string shape)
    {
        if (window.Session?.Edits is not { } edits)
        {
            return null;
        }

        if (slots == null || !ReferenceEquals(slotsView, edits.EditView))
        {
            slotsView = edits.EditView;
            slots = new Dictionary<string, IReadOnlyList<Vector3>>(StringComparer.OrdinalIgnoreCase);
            if (edits.DataPath(@"MISSIONS\formations.ini") is { } path)
            {
                foreach (var section in edits.Sections(path))
                {
                    if (edits.Get(section) is not { } view ||
                        view.Entries.FirstOrDefault(e => e.Name.Equals("nickname", StringComparison.OrdinalIgnoreCase)) is not
                            { Values.Count: > 0 } nickname)
                    {
                        continue;
                    }

                    slots.TryAdd(MissionIndex.Text(nickname.Values[0]), view.Entries
                        .Where(e => e.Name.Equals("pos", StringComparison.OrdinalIgnoreCase) && e.Values.Count >= 3)
                        .Select(e => new Vector3(Number(e.Values[0]), Number(e.Values[1]), Number(e.Values[2]))).ToList());
                }
            }
        }

        return slots.GetValueOrDefault(shape);
    }

    private static float Number(LibreLancer.Data.Ini.ValueBase value) =>
        float.TryParse(MissionIndex.Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    /// <summary>A sphere as three great circles. An outside sphere is dashed.</summary>
    private static void Sphere(LineRenderer lines, Vector3 center, float radius, Color4 color, bool dashed)
    {
        Circle(lines, center, Vector3.UnitX * radius, Vector3.UnitY * radius, color, dashed);
        Circle(lines, center, Vector3.UnitX * radius, Vector3.UnitZ * radius, color, dashed);
        Circle(lines, center, Vector3.UnitY * radius, Vector3.UnitZ * radius, color, dashed);
    }

    /// <summary>A circle through center + u and center + v, as line segments, dashed or solid.</summary>
    private static void Circle(LineRenderer lines, Vector3 center, Vector3 u, Vector3 v, Color4 color, bool dashed)
    {
        var previous = center + u;
        for (var i = 1; i <= Segments; i++)
        {
            var angle = 2 * MathF.PI * i / Segments;
            var point = center + MathF.Cos(angle) * u + MathF.Sin(angle) * v;
            if (!dashed || i % 2 == 0)
            {
                lines.DrawLine(previous, point, color);
            }

            previous = point;
        }
    }

    /// <summary>A square of a plane around a point, with a line along its normal.</summary>
    private static void Plane(LineRenderer lines, Vector3 center, Vector3 normal, float size, Color4 color)
    {
        var helper = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(normal, helper)) * size;
        var v = Vector3.Normalize(Vector3.Cross(normal, u)) * size;
        var corners = new[] { center + u + v, center - u + v, center - u - v, center + u - v };
        for (var i = 0; i < 4; i++)
        {
            lines.DrawLine(corners[i], corners[(i + 1) % 4], color);
        }

        lines.DrawLine(center, center + normal * size * 0.3f, color);
    }

    /// <summary>Forgets the placements, for example when the system closes.</summary>
    public void Reset()
    {
        built = default;
        items = [];
        slots = null;
        slotsView = null;
    }
}

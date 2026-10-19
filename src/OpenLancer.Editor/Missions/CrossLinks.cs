using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>An item of the world that a line of the selection names (section 7.3 of the M6a design).</summary>
/// <param name="Line">The line that names the item.</param>
/// <param name="Item">The selection of an item of the mission layer, or null for an object of a system.</param>
/// <param name="Object">The nickname of an object of a system, or null.</param>
/// <param name="Name">The name of the item for its label.</param>
/// <param name="System">The system of an item of the mission, or null when the index does not know it.</param>
internal sealed record CrossLinkTarget(MissionLine Line, MissionRef? Item, string? Object, string Name, string? System);

/// <summary>Where a connector line starts in the graph: a row at the lines level, the chip of the group of the line at
/// the summary level, or the node of a collapsed frame.</summary>
/// <param name="Node">The key of the node that shows the line.</param>
/// <param name="Entry">The index of the line of a row, or null.</param>
/// <param name="Group">The action group of a chip, or null.</param>
internal sealed record CrossLinkSource(string Node, int? Entry, string? Group);

/// <summary>A connector line on the screen between the graph and the Scene part.</summary>
/// <param name="FromArrow">True when the source is outside the graph view, so the line starts at its edge with an
/// arrow.</param>
/// <param name="ToArrow">True when the item is outside the Scene view, so the line ends at its edge with an
/// arrow.</param>
internal readonly record struct ConnectorLine(Vector2 From, Vector2 To, bool FromArrow, bool ToArrow);

/// <summary>
/// The cross-highlighting of the Mission tab (section 7.3 of the M6a design, UI rule 6). From a node to the world: the
/// items that the lines of a trigger, a row or a prefab instance name, the highlight sets of the Scene, and where each
/// connector line starts. From the world to the graph: the triggers whose lines name a world item. The class makes no
/// ImGui call, so the tests check it. The Mission tab draws the lines.
/// </summary>
internal static class CrossLinks
{
    /// <summary>At most this many connector lines draw. More items give the chip "and {n} more".</summary>
    public const int MaxLines = 24;

    /// <summary>The lines of a selection: one row, the lines of a trigger, or the lines of the triggers of a prefab
    /// instance or an act. Empty for another selection.</summary>
    public static IReadOnlyList<MissionLine> SourceLines(MissionAnalysis analysis, MissionRef selection)
    {
        var index = analysis.Index;
        switch (selection.Kind)
        {
            case MissionItemKind.Row when selection.Section is { } row && TriggerOf(analysis, row) is { } trigger:
                return trigger.Rows.Where(l => l.Entry == selection.Row).ToList();
            case MissionItemKind.Trigger when selection.Section is { } section && TriggerOf(analysis, section) is { } trigger:
                return trigger.Rows;
            case MissionItemKind.PrefabInstance when selection.Key != null &&
                                                     analysis.Prefabs?.Instances.FirstOrDefault(i => i.Key == selection.Key) is
                                                         { } instance:
                return MissionUses.LinesOf(instance.Triggers);
            case MissionItemKind.Act when selection.Key != null:
                var acts = analysis.Acts ?? MissionActs.Propose(index);
                var number = selection.Key == "a:0" ? 0 : int.TryParse(selection.Key.AsSpan(2), out var n) ? n : -1;
                var triggers = number == 0
                    ? acts.NotStarted
                    : acts.Acts.FirstOrDefault(a => a.Number == number)?.Triggers ?? [];
                return MissionUses.LinesOf(triggers);
            default:
                return [];
        }
    }

    /// <summary>The trigger of a section of the script, or null.</summary>
    private static MissionTrigger? TriggerOf(MissionAnalysis analysis, Data.Editing.SectionRef section) =>
        analysis.Document.Find(section) is { } found ? analysis.Index.Triggers.FirstOrDefault(t => t.Section == found) : null;

    /// <summary>
    /// The items of the world that lines name (node to world): the items of the cast, the members of the labels, the
    /// order lists and the objectives, the objects of a system, and each line that has its own place, for example a
    /// distance volume or a spawn at a position.
    /// </summary>
    public static IReadOnlyList<CrossLinkTarget> Targets(MissionAnalysis analysis, IReadOnlyList<MissionLine> lines)
    {
        var index = analysis.Index;
        var script = analysis.Script.Path;
        var result = new List<CrossLinkTarget>();
        foreach (var use in MissionUses.Of(index, lines))
        {
            if (use.Section is not { } section)
            {
                result.Add(new CrossLinkTarget(use.Line, null, use.Name, use.Name, null));
                continue;
            }

            result.Add(new CrossLinkTarget(use.Line, MissionWorkspace.RefOf(section.Ref, -1), null,
                section.Nickname ?? use.Name, SystemOf(index, section)));
            // The object that a ship arrives through is a world item of the line too.
            if (section.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation &&
                section.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "arrival_obj") && e.Count > 0) is { } arrival)
            {
                var gate = MissionIndex.Text(arrival[0]);
                result.Add(new CrossLinkTarget(use.Line, null, gate, gate, null));
            }
        }

        foreach (var line in lines.Where(l => l.Trigger != null && MissionPlacements.HasPlace(l)))
        {
            result.Add(new CrossLinkTarget(line, new MissionRef(script, MissionItemKind.Row, line.Section.Ref, Row: line.Entry),
                null, line.Name?.Plain ?? line.Key, line.Trigger!.SystemHint is { } hint && !MissionIndex.Is(hint, "ANY")
                    ? hint
                    : null));
        }

        return result;
    }

    /// <summary>The system of an item of the script: its own, or the system of the trigger that gives an order list.</summary>
    private static string? SystemOf(MissionIndex index, MissionSection section) =>
        index.SystemOf(section) ?? MissionUses.Naming(index, section).Select(l => l.Trigger?.SystemHint)
            .FirstOrDefault(h => h != null && !MissionIndex.Is(h, "ANY"));

    /// <summary>The highlight sets of the Scene: the items of the mission layer, and the nicknames of the objects of the
    /// system.</summary>
    public static (IReadOnlySet<MissionRef> Items, IReadOnlySet<string> Objects) Highlight(IEnumerable<CrossLinkTarget> targets)
    {
        var items = new HashSet<MissionRef>();
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (target.Item != null)
            {
                items.Add(target.Item);
            }
            else if (target.Object != null)
            {
                objects.Add(target.Object);
            }
        }

        return (items, objects);
    }

    /// <summary>Where the connector line of a line starts: its row at the lines level, the chip of its action group at
    /// the summary level, or the node of a collapsed frame. Null when the graph does not show the trigger of the
    /// line.</summary>
    public static CrossLinkSource? SourceOf(MissionGraph graph, MissionLine line)
    {
        if (line.Trigger is not { } trigger || graph.NodeOf(trigger) is not { } node)
        {
            return null;
        }

        if (node.Kind != GraphNodeKind.Trigger)
        {
            return new CrossLinkSource(node.Key, null, null);
        }

        if (node.Lines)
        {
            return node.Rows.Any(r => r.Line == line)
                ? new CrossLinkSource(node.Key, line.Entry, null)
                : new CrossLinkSource(node.Key, null, null);
        }

        return line.Name is { Kind: MissionNameKind.Action, Group: var group } && node.Chips.Any(c => c.Group == group)
            ? new CrossLinkSource(node.Key, null, group)
            : new CrossLinkSource(node.Key, null, null);
    }

    /// <summary>
    /// The lines that name a world item (world to graph): a mission ship, formation, solar, loot, order list or objective
    /// names its section, a line of the mission layer is its own line, and an object of a system names its nickname.
    /// </summary>
    public static IReadOnlyList<MissionLine> LinesUsing(MissionAnalysis analysis, object item)
    {
        var index = analysis.Index;
        switch (item)
        {
            case MissionRef { Kind: MissionItemKind.Row, Section: { } row } mission when analysis.Document.Find(row) is { } found:
                return index.LinesOf(found).Where(l => l.Entry == mission.Row).ToList();
            case MissionRef { Section: { } section } when analysis.Document.Find(section) is { } found &&
                                                          found.Kind != MissionSectionKind.Trigger:
                return MissionUses.Naming(index, found);
            case MissionRef { Kind: MissionItemKind.Label, Key: { } label }:
                return index.ReferencesTo(MissionValueKind.Object, label).Where(r => r.Section == null)
                    .Select(r => r.Line).Distinct().ToList();
            case string nickname:
                return MissionUses.NamingObject(index, nickname);
            default:
                return [];
        }
    }

    /// <summary>
    /// The connector line from a source rectangle in the graph to a point in the Scene (section 7.3 of the M6a design).
    /// The line starts at the right edge of the source. A source outside the graph view starts at the edge of the graph
    /// with an arrow. A point outside the Scene view ends at the edge of the Scene with an arrow.
    /// </summary>
    public static ConnectorLine Connect((Vector2 Min, Vector2 Max) source, (Vector2 Min, Vector2 Max) graph, Vector2 target,
        (Vector2 Min, Vector2 Max) scene)
    {
        var from = new Vector2(source.Max.X, (source.Min.Y + source.Max.Y) / 2);
        var fromArrow = !Inside(from, graph);
        if (fromArrow)
        {
            from = Vector2.Clamp(from, graph.Min, graph.Max);
        }

        var toArrow = !Inside(target, scene);
        var to = toArrow ? EdgeToward(from, target, scene) : target;
        return new ConnectorLine(from, to, fromArrow, toArrow);
    }

    private static bool Inside(Vector2 point, (Vector2 Min, Vector2 Max) rect) =>
        point.X >= rect.Min.X && point.X <= rect.Max.X && point.Y >= rect.Min.Y && point.Y <= rect.Max.Y;

    /// <summary>The point where the line from a point to a target leaves a rectangle that the target is outside of. The
    /// start is the center of the rectangle when the point is outside it too.</summary>
    internal static Vector2 EdgeToward(Vector2 from, Vector2 target, (Vector2 Min, Vector2 Max) rect)
    {
        var start = Inside(from, rect) ? from : (rect.Min + rect.Max) / 2;
        var direction = target - start;
        var t = 1f;
        if (direction.X > 0)
        {
            t = Math.Min(t, (rect.Max.X - start.X) / direction.X);
        }
        else if (direction.X < 0)
        {
            t = Math.Min(t, (rect.Min.X - start.X) / direction.X);
        }

        if (direction.Y > 0)
        {
            t = Math.Min(t, (rect.Max.Y - start.Y) / direction.Y);
        }
        else if (direction.Y < 0)
        {
            t = Math.Min(t, (rect.Min.Y - start.Y) / direction.Y);
        }

        return start + direction * Math.Max(0, t);
    }

    /// <summary>The distance from a point to a line segment, for a click on a connector line.</summary>
    public static float Distance(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var length = ab.LengthSquared();
        var t = length <= 0 ? 0 : Math.Clamp(Vector2.Dot(point - a, ab) / length, 0, 1);
        return Vector2.Distance(point, a + ab * t);
    }
}

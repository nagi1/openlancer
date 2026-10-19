using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Data.Missions;

/// <summary>A path of Start and Load links from a start trigger to a trigger.</summary>
/// <param name="Triggers">The triggers of the path, from the start trigger to the target.</param>
/// <param name="Links">The links of the path, in path order.</param>
public sealed record GraphPath(IReadOnlyList<MissionTrigger> Triggers, IReadOnlyList<MissionLink> Links);

/// <summary>The sources of the input pins of a trigger (the node menu "List what starts or stops this trigger").</summary>
/// <param name="Start">The lines that start the trigger.</param>
/// <param name="Stop">The lines that stop it.</param>
/// <param name="Load">The checkpoint lines that continue at it after a load.</param>
/// <param name="Watches">The condition of the trigger that watches another trigger, or empty.</param>
public sealed record GraphPinSources(IReadOnlyList<MissionLink> Start, IReadOnlyList<MissionLink> Stop,
    IReadOnlyList<MissionLink> Load, IReadOnlyList<MissionLink> Watches)
{
    /// <summary>All sources, in pin order.</summary>
    public IEnumerable<MissionLink> All => Start.Concat(Stop).Concat(Load).Concat(Watches);
}

/// <summary>
/// The map of the minimap (section 6.6 of the M6a design): it fits the bounds of the graph into a screen rectangle and
/// keeps the ratio of the sides.
/// </summary>
/// <param name="Canvas">The bounds of the graph in canvas points.</param>
/// <param name="Screen">The rectangle of the minimap on the screen.</param>
public readonly record struct GraphMinimap((Vector2 Min, Vector2 Max) Canvas, (Vector2 Min, Vector2 Max) Screen)
{
    /// <summary>The screen pixels of one canvas point.</summary>
    public float Scale
    {
        get
        {
            var canvas = Vector2.Max(Canvas.Max - Canvas.Min, Vector2.One);
            var screen = Screen.Max - Screen.Min;
            return MathF.Min(screen.X / canvas.X, screen.Y / canvas.Y);
        }
    }

    /// <summary>The offset that centers the map in the screen rectangle.</summary>
    private Vector2 Offset => Screen.Min + ((Screen.Max - Screen.Min) - (Canvas.Max - Canvas.Min) * Scale) / 2;

    /// <summary>The screen point of a canvas point.</summary>
    public Vector2 ToScreen(Vector2 canvas) => Offset + (canvas - Canvas.Min) * Scale;

    /// <summary>The canvas point of a screen point.</summary>
    public Vector2 ToCanvas(Vector2 screen) => Canvas.Min + (screen - Offset) / Scale;
}

/// <summary>
/// The navigation of the mission graph (section 6.6 of the M6a design): the search, the path from a start trigger, the
/// sources of the pins, the neighbors of a node, the system filter and the minimap. The class makes no ImGui call, so
/// the tests and later an agent server (roadmap row MCP1) call it.
/// </summary>
public static class GraphNavigation
{
    /// <summary>The nodes whose triggers match a search: the nickname or a value of a line holds the text. Each node
    /// shows once, in file order of its first matching trigger. None for an empty search.</summary>
    public static IReadOnlyList<GraphNode> Search(MissionGraph graph, string text)
    {
        var result = new List<GraphNode>();
        foreach (var trigger in MissionSummary.MatchingTriggers(graph.Analysis, text.Trim()))
        {
            if (graph.NodeOf(trigger) is { } node && !result.Contains(node))
                result.Add(node);
        }
        return result;
    }

    /// <summary>
    /// The shortest path of Start and Load links from a start trigger to a trigger, or null when no start trigger reaches
    /// it. A walk in breadth-first order from all start triggers in file order finds it. So when two paths have the same
    /// length, the path of the earlier start trigger and of the earlier lines wins.
    /// </summary>
    public static GraphPath? PathFromStart(MissionIndex index, MissionTrigger target)
    {
        var from = new Dictionary<MissionTrigger, MissionLink?>();
        var queue = new Queue<MissionTrigger>();
        foreach (var start in index.StartTriggers)
        {
            if (from.TryAdd(start, null))
                queue.Enqueue(start);
        }
        var outgoing = index.Links.Where(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Load && l.Target != null)
            .GroupBy(l => l.Source).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Line.Entry).ToList());
        while (queue.Count > 0 && !from.ContainsKey(target))
        {
            var trigger = queue.Dequeue();
            foreach (var link in outgoing.GetValueOrDefault(trigger) ?? [])
            {
                if (from.TryAdd(link.Target!, link))
                    queue.Enqueue(link.Target!);
            }
        }
        if (!from.ContainsKey(target))
            return null;
        var triggers = new List<MissionTrigger>();
        var links = new List<MissionLink>();
        for (MissionTrigger? at = target; at != null;)
        {
            triggers.Add(at);
            var link = from[at];
            if (link != null)
                links.Add(link);
            at = link?.Source;
        }
        triggers.Reverse();
        links.Reverse();
        return new GraphPath(triggers, links);
    }

    /// <summary>The sources of the input pins of a trigger: Start, Stop, Load, and the trigger that its condition
    /// watches.</summary>
    public static GraphPinSources Sources(MissionIndex index, MissionTrigger trigger)
    {
        var incoming = index.LinksTo(trigger);
        return new GraphPinSources(
            incoming.Where(l => l.Kind == MissionLinkKind.Start).ToList(),
            incoming.Where(l => l.Kind == MissionLinkKind.Stop).ToList(),
            incoming.Where(l => l.Kind == MissionLinkKind.Load).ToList(),
            index.Links.Where(l => l.Kind == MissionLinkKind.Watch && l.Source == trigger).ToList());
    }

    /// <summary>The nodes one link away from a node, and the node itself (the node menu "Show only this trigger and its
    /// neighbors").</summary>
    public static IReadOnlySet<GraphNode> Neighbors(MissionGraph graph, GraphNode node)
    {
        var result = new HashSet<GraphNode> { node };
        foreach (var link in graph.Links)
        {
            if (link.From == node)
                result.Add(link.To);
            else if (link.To == node)
                result.Add(link.From);
        }
        return result;
    }

    /// <summary>True when the system filter fades a node: each trigger of the node has a system, and none is the system
    /// of the filter. A node without a system stays, because it is not of another system.</summary>
    public static bool OutsideSystem(GraphNode node, string system)
    {
        var systems = node.Triggers.Select(t => t.SystemHint).Where(h => h != null && !MissionIndex.Is(h, "ANY")).ToList();
        return systems.Count > 0 && systems.Count == node.Triggers.Count && !systems.Any(s => MissionIndex.Is(s, system));
    }

    /// <summary>The node whose rectangle is nearest to a canvas point (a click on the minimap), or null for an empty
    /// graph.</summary>
    public static GraphNode? Nearest(MissionGraph graph, GraphLayout layout, Vector2 point)
    {
        GraphNode? best = null;
        var bestDistance = float.MaxValue;
        foreach (var node in graph.Nodes)
        {
            var (min, max) = layout.RectOf(node);
            var distance = Vector2.Distance(point, Vector2.Clamp(point, min, max));
            if (distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }
        return best;
    }
}

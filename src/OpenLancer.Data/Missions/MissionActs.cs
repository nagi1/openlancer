using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenLancer.Data.Missions;

/// <summary>One act of a mission: a part of the main path from one checkpoint or system to the next (section 6.4 of the
/// M6a design).</summary>
/// <param name="Number">The number of the act, from 1.</param>
/// <param name="System">The system of the act, from the system hints of its main path triggers, or null.</param>
/// <param name="Triggers">The triggers of the act, in file order.</param>
public sealed record MissionAct(int Number, string? System, IReadOnlyList<MissionTrigger> Triggers)
{
    /// <summary>The number of triggers of the act with a checkpoint line.</summary>
    public int Checkpoints => Triggers.Count(t => t.ActionsOf("Act_Save").Any());
}

/// <summary>
/// The acts that the editor proposes for a mission, and the triggers that no start trigger reaches (decisions D542 and
/// D555, section 6.4 of the M6a design):
/// <list type="number">
/// <item>The roots are the start triggers, in file order.</item>
/// <item>The main path is the longest path of Start and Load links from a root to a trigger that ends the mission with
/// success. Back links do not count. When no root reaches a success, the main path is the longest path from the first
/// root.</item>
/// <item>A new act starts at each main path trigger with a checkpoint line, and at each change of the system along the
/// main path. An empty system and <c>ANY</c> do not count as a change.</item>
/// <item>Each other trigger that a root reaches takes the act of the nearest main path trigger above it. A tie goes to
/// the earlier act.</item>
/// </list>
/// The acts are read-only. M6b makes them editable.
/// </summary>
public sealed class MissionActs
{
    private readonly Dictionary<MissionTrigger, MissionAct> actOf;

    private MissionActs(IReadOnlyList<MissionAct> acts, IReadOnlyList<MissionTrigger> mainPath,
        IReadOnlyList<MissionTrigger> notStarted)
    {
        Acts = acts;
        MainPath = mainPath;
        NotStarted = notStarted;
        actOf = acts.SelectMany(a => a.Triggers.Select(t => (t, a))).ToDictionary(p => p.t, p => p.a);
    }

    public IReadOnlyList<MissionAct> Acts { get; }

    /// <summary>The triggers of the main path, from the root.</summary>
    public IReadOnlyList<MissionTrigger> MainPath { get; }

    /// <summary>The triggers of the frame "Not started by the mission", in file order. No start trigger reaches them
    /// over Start and Load links. The frame also holds each duplicate trigger, because the game never runs it
    /// (TR4).</summary>
    public IReadOnlyList<MissionTrigger> NotStarted { get; }

    /// <summary>The key of the frame "Not started by the mission" in the graph and in a selection.</summary>
    public const string NotStartedKey = "a:0";

    /// <summary>The act of a trigger, or null for a trigger of the frame "Not started by the mission".</summary>
    public MissionAct? ActOf(MissionTrigger trigger) => actOf.GetValueOrDefault(trigger);

    /// <summary>The key of an act in the graph and in a selection, for example <c>a:2</c>.</summary>
    public static string KeyOf(MissionAct act) => "a:" + act.Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The act of a key, or null for the frame "Not started by the mission" and for a key that is not
    /// known.</summary>
    public MissionAct? Act(string key) => Acts.FirstOrDefault(a => KeyOf(a) == key);

    /// <summary>The triggers of an act key, or of the frame "Not started by the mission". None for a key that is not
    /// known.</summary>
    public IReadOnlyList<MissionTrigger> TriggersOf(string key) =>
        key == NotStartedKey ? NotStarted : Act(key)?.Triggers ?? [];

    /// <summary>Proposes the acts of a mission.</summary>
    public static MissionActs Propose(MissionIndex index)
    {
        var triggers = index.Triggers.Where(t => !t.IsDuplicate).ToList();
        var notStarted = index.Triggers.Where(t => t.IsDuplicate || !index.Reached.Contains(t)).ToList();
        var roots = index.StartTriggers;
        if (roots.Count == 0)
            return new MissionActs([], [], notStarted);

        // The edges of Start and Load links in file order, without the back links of a depth-first walk from the roots.
        var edges = triggers.ToDictionary(t => t, _ => new List<MissionTrigger>());
        foreach (var link in index.Links.Where(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Load &&
                                                    l.Target is { IsDuplicate: false } && edges.ContainsKey(l.Source)))
        {
            if (!edges[link.Source].Contains(link.Target!))
                edges[link.Source].Add(link.Target!);
        }
        var forward = WithoutBackLinks(roots, edges);

        // The longest path to a success from each trigger, in triggers, or -1 when it reaches no success. And the
        // longest path from each trigger.
        var toSuccess = new Dictionary<MissionTrigger, (int Length, MissionTrigger? Next)>();
        var longest = new Dictionary<MissionTrigger, (int Length, MissionTrigger? Next)>();
        foreach (var trigger in TopologicalOrder(roots, forward).AsEnumerable().Reverse())
        {
            var success = trigger.ActionsOf("Act_ChangeState").Any(a => MissionIndex.Is(a.Value(0), "SUCCEED"));
            (int, MissionTrigger?) best = success ? (1, null) : (-1, null);
            (int, MissionTrigger?) far = (1, null);
            foreach (var next in forward[trigger])
            {
                var (length, _) = toSuccess[next];
                if (length > 0 && length + 1 > best.Item1)
                    best = (length + 1, next);
                if (longest[next].Length + 1 > far.Item1)
                    far = (longest[next].Length + 1, next);
            }
            toSuccess[trigger] = best;
            longest[trigger] = far;
        }
        var root = roots.Where(r => toSuccess[r].Length > 0).OrderByDescending(r => toSuccess[r].Length).FirstOrDefault();
        var path = new List<MissionTrigger>();
        for (var at = root ?? roots[0]; at != null; at = root != null ? toSuccess[at].Next : longest[at].Next)
            path.Add(at);

        // The acts along the main path.
        var mainActs = new List<(string? System, List<MissionTrigger> Path)>();
        string? system = null;
        foreach (var trigger in path)
        {
            var hint = trigger.SystemHint is { } h && !MissionIndex.Is(h, "ANY") ? h : null;
            var newSystem = hint != null && system != null && !hint.Equals(system, StringComparison.OrdinalIgnoreCase);
            var checkpoint = trigger.ActionsOf("Act_Save").Any();
            if (mainActs.Count == 0 || newSystem || checkpoint && mainActs[^1].Path.Count > 0)
                mainActs.Add((hint ?? system, []));
            if (hint != null)
                system = hint;
            mainActs[^1] = (mainActs[^1].System ?? hint, mainActs[^1].Path);
            mainActs[^1].Path.Add(trigger);
        }

        // Each other reached trigger takes the act of the nearest main path trigger above it, over the links back.
        var actIndex = new Dictionary<MissionTrigger, int>();
        for (var a = 0; a < mainActs.Count; a++)
        {
            foreach (var trigger in mainActs[a].Path)
                actIndex[trigger] = a;
        }
        var parents = triggers.ToDictionary(t => t, _ => new List<MissionTrigger>());
        foreach (var (source, targets) in edges)
        {
            foreach (var target in targets)
                parents[target].Add(source);
        }
        var mainIndex = new Dictionary<MissionTrigger, int>(actIndex);
        foreach (var trigger in triggers.Where(t => index.Reached.Contains(t) && !mainIndex.ContainsKey(t)))
            actIndex[trigger] = NearestAct(trigger, parents, mainIndex);

        var acts = mainActs.Select((a, i) => new MissionAct(i + 1, a.System,
            triggers.Where(t => actIndex.TryGetValue(t, out var n) && n == i).ToList())).ToList();
        return new MissionActs(acts, path, notStarted);
    }

    /// <summary>The act of the nearest main path trigger above a trigger, by a breadth-first walk back. A tie goes to the
    /// earlier act. A trigger without a main path trigger above it takes the first act.</summary>
    private static int NearestAct(MissionTrigger trigger, Dictionary<MissionTrigger, List<MissionTrigger>> parents,
        Dictionary<MissionTrigger, int> mainActs)
    {
        var seen = new HashSet<MissionTrigger> { trigger };
        var level = new List<MissionTrigger> { trigger };
        while (level.Count > 0)
        {
            var next = new List<MissionTrigger>();
            var found = new List<int>();
            foreach (var parent in level.SelectMany(t => parents[t]).Where(seen.Add))
            {
                if (mainActs.TryGetValue(parent, out var act))
                    found.Add(act);
                else
                    next.Add(parent);
            }
            if (found.Count > 0)
                return found.Min();
            level = next;
        }
        return 0;
    }

    /// <summary>The edges without the back links that a depth-first walk from the roots in file order finds.</summary>
    private static Dictionary<MissionTrigger, List<MissionTrigger>> WithoutBackLinks(IReadOnlyList<MissionTrigger> roots,
        Dictionary<MissionTrigger, List<MissionTrigger>> edges)
    {
        var result = edges.ToDictionary(p => p.Key, _ => new List<MissionTrigger>());
        var state = new Dictionary<MissionTrigger, int>();
        foreach (var root in roots)
        {
            if (state.ContainsKey(root))
                continue;
            // An explicit stack, because a chain of triggers can be long.
            var stack = new Stack<(MissionTrigger Node, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next >= edges[node].Count)
                {
                    state[node] = 2;
                    continue;
                }
                stack.Push((node, next + 1));
                var target = edges[node][next];
                if (state.GetValueOrDefault(target) == 1)
                    continue;
                result[node].Add(target);
                if (!state.ContainsKey(target))
                {
                    state[target] = 1;
                    stack.Push((target, 0));
                }
            }
        }
        return result;
    }

    /// <summary>The triggers that the roots reach, in an order where each trigger comes before the triggers that it
    /// links to.</summary>
    private static List<MissionTrigger> TopologicalOrder(IReadOnlyList<MissionTrigger> roots,
        Dictionary<MissionTrigger, List<MissionTrigger>> forward)
    {
        var order = new List<MissionTrigger>();
        var done = new HashSet<MissionTrigger>();
        foreach (var root in roots)
        {
            if (done.Contains(root))
                continue;
            var stack = new Stack<(MissionTrigger Node, int Next)>();
            stack.Push((root, 0));
            done.Add(root);
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next >= forward[node].Count)
                {
                    order.Add(node);
                    continue;
                }
                stack.Push((node, next + 1));
                var target = forward[node][next];
                if (done.Add(target))
                    stack.Push((target, 0));
            }
        }
        order.Reverse();
        return order;
    }
}

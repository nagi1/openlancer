using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>A run state of a node of the graph (section 4.5 of the M6c design).</summary>
internal enum RunChipKind
{
    Waits,
    Ran,
    HasRun,
    Stopped,
    StartsNext,

    /// <summary>The trigger waits for 10 minutes or more in the live view.</summary>
    Stall,

    /// <summary>The count of the waiting triggers of a collapsed frame.</summary>
    FrameWaits,
}

/// <summary>One chip of a run state: a glyph and a word, so no state depends on color alone (decision D761).</summary>
internal sealed record RunChip(RunChipKind Kind, char Glyph, string Text, string Tooltip)
{
    /// <summary>The text of the chip with its glyph.</summary>
    public string Label => $"{Glyph} {Text}";
}

/// <summary>The run state of one node of the graph: its chips, the outline of a trigger that waits, and the flash of a
/// trigger that ran.</summary>
/// <param name="Waits">True when the node waits: the outline in the run color.</param>
/// <param name="Ran">True when the node ran in the last step or second: a short flash of the outline.</param>
internal sealed record RunNodeState(IReadOnlyList<RunChip> Chips, bool Waits, bool Ran);

/// <summary>
/// The run overlay of the mission graph (section 4.5 of the M6c design): the run states of the nodes, the waiting
/// counts of the collapsed frames, the start links that flash, and the trigger that "Follow" frames. It is a model
/// without ImGui, made from a graph and a trace. The dry run and the live view of M6c-2 use the same overlay.
/// </summary>
internal sealed class RunOverlay
{
    /// <summary>The time of the flash of a run, in seconds.</summary>
    public const double FlashSeconds = 1;

    private RunOverlay(IReadOnlyDictionary<string, RunNodeState> nodes, IReadOnlySet<int> links, string? follow)
    {
        Nodes = nodes;
        Links = links;
        Follow = follow;
    }

    /// <summary>The run state of each node with a state, by node key.</summary>
    public IReadOnlyDictionary<string, RunNodeState> Nodes { get; }

    /// <summary>The ids of the start links that flash: the links of the triggers that ran.</summary>
    public IReadOnlySet<int> Links { get; }

    /// <summary>The key of the node of the trigger of the last change, or null. The toggle "Follow" frames it.</summary>
    public string? Follow { get; }

    /// <summary>The state of a node, or null.</summary>
    public RunNodeState? Of(GraphNode node) => Nodes.GetValueOrDefault(node.Key);

    /// <summary>The overlay of a trace on a graph.</summary>
    /// <param name="now">The time now in the live view, in seconds since the start of the play. The dry run does not
    /// use it.</param>
    public static RunOverlay Of(MissionGraph graph, MissionTrace trace, double now = 0)
    {
        var nodes = new Dictionary<string, RunNodeState>(StringComparer.Ordinal);
        var ran = new HashSet<MissionTrigger>();
        foreach (var node in graph.Nodes)
        {
            if (node.Frame != null)
            {
                // A collapsed frame counts its waiting members and flashes when a member runs.
                var states = node.Triggers.Select(t => trace.StateOf(t.Nickname)).OfType<TriggerTrace>().ToList();
                var waiting = states.Count(s => s.Waits);
                var frameRan = states.Any(s => trace.RanNow(s, now) > 0);
                foreach (var member in node.Triggers.Where(t => trace.StateOf(t.Nickname) is { } s && trace.RanNow(s, now) > 0))
                    ran.Add(member);
                if (waiting > 0 || frameRan)
                    nodes[node.Key] = new RunNodeState(waiting > 0 ? [FrameChip(waiting)] : [], waiting > 0, frameRan);
                continue;
            }
            if (node.Trigger is not { } trigger || trace.StateOf(trigger.Nickname) is not { } state)
                continue;
            var chips = ChipsOf(state, trace, now);
            if (chips.Count == 0)
                continue;
            var nodeRan = trace.RanNow(state, now) > 0;
            if (nodeRan)
                ran.Add(trigger);
            nodes[node.Key] = new RunNodeState(chips, state.Waits, nodeRan);
        }
        var links = graph.Links.Where(l => l.Kind == MissionLinkKind.Start && l.Sources.Any(s => ran.Contains(s.Source)))
            .Select(l => l.Id).ToHashSet();
        string? follow = null;
        if (trace.LastTrigger is { } last && graph.Index.Trigger(last) is { } lastTrigger &&
            graph.NodeOf(lastTrigger) is { } lastNode)
            follow = lastNode.Key;
        return new RunOverlay(nodes, links, follow);
    }

    /// <summary>The chips of the run state of a trigger (section 8.1 of the M6c design), in a fixed order.</summary>
    public static IReadOnlyList<RunChip> ChipsOf(TriggerTrace state, MissionTrace trace, double now = 0)
    {
        var chips = new List<RunChip>();
        if (state.StartsNext > 0)
            chips.Add(new RunChip(RunChipKind.StartsNext, EditorIcons.StepForward,
                $"Starts next ({Number(state.StartsNext)})", StartsNextTip));
        if (trace.StallMinutes(state, now) is { } minutes)
            chips.Add(new RunChip(RunChipKind.Stall, EditorIcons.History, $"Waits {Number(minutes)} min", StallTip(minutes)));
        else if (state.Waits)
            chips.Add(new RunChip(RunChipKind.Waits, EditorIcons.DotCircle, "Waits", WaitsTip));
        var ran = trace.RanNow(state, now);
        if (ran > 0)
            chips.Add(new RunChip(RunChipKind.Ran, EditorIcons.Bolt, ran == 1 ? "Ran" : $"Ran {Number(ran)} times", RanTip));
        else if (state.HasRun && state.StartsNext == 0)
            chips.Add(new RunChip(RunChipKind.HasRun, EditorIcons.CheckCircle, "Has run", HasRunTip));
        if (state.Stopped && !state.Waits && state.StartsNext == 0)
            chips.Add(new RunChip(RunChipKind.Stopped, EditorIcons.StopCircle, "Stopped", StoppedTip));
        return chips;
    }

    /// <summary>The chip of a collapsed frame: "{n} wait".</summary>
    public static RunChip FrameChip(int waiting) =>
        new(RunChipKind.FrameWaits, EditorIcons.DotCircle, $"{Number(waiting)} wait",
            "The triggers of this frame that wait for their condition.");

    // The tooltips of section 8.1 of the M6c design.
    public const string WaitsTip = "Waits: This trigger is active. It waits for its condition.";

    public const string RanTip = "Ran: This trigger ran in the last step of the dry run, or in the last second of the game.";

    public const string HasRunTip = "Has run: This trigger ran before. It is not active now.";

    public const string StoppedTip = "Stopped: Another trigger stopped this trigger.";

    public const string StartsNextTip =
        "Starts next: The game starts this trigger at the next event or heartbeat. The number gives the order.";

    public static string StallTip(int minutes) =>
        $"Waits {Number(minutes)} min: This trigger is active for {Number(minutes)} minutes.";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

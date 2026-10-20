using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using static OpenLancer.Editor.Missions.MissionCommandRefs;

namespace OpenLancer.Editor.Missions;

/// <summary>One end of a link drag: a node and its pin. A null pin is the "+" pin of the node header.</summary>
internal sealed record GraphEnd(GraphNode Node, GraphPin? Pin)
{
    /// <summary>True for the "+" pin (section 8.2 of the M6b design).</summary>
    public bool IsAdd => Pin == null;

    /// <summary>True for an input pin on the left of the node.</summary>
    public bool IsInput => Pin?.IsInput == true;
}

/// <summary>What a link drag does: a batch, or the reason why the target is not valid. A drag to a Watches pin asks
/// for the state first.</summary>
internal sealed record LinkGesture(MissionBatch? Batch, string? Reason)
{
    /// <summary>True when the view asks for the watched state, then calls <see cref="GraphEditModel.Watch"/>.</summary>
    public bool AsksState { get; init; }

    public bool Valid => Reason == null;

    public static LinkGesture Refuse(string reason) => new(null, reason);
}

/// <summary>The kind of the new trigger of the link menu "Insert trigger" (section 8.11 of the M6b design).</summary>
internal enum InsertEvent
{
    Delay,
    AtOnce,
    CommLine,
}

/// <summary>What quick add can add at its place (section 8.3 of the M6b design).</summary>
internal enum QuickAddFilter
{
    /// <summary>All items: Space in the graph and the toolbar button "Add...".</summary>
    All,

    /// <summary>Only items that make a trigger: a drag from an output pin to an empty place.</summary>
    Triggers,

    /// <summary>Only actions: "Add action..." of a node and of the Trigger page.</summary>
    Actions,

    /// <summary>Only conditions and events: "Set condition..." of a node.</summary>
    Conditions,
}

/// <summary>The place of quick add: the pin of a drag, the selected trigger and the filter.</summary>
/// <param name="From">The output pin of a drag to an empty place, or null.</param>
/// <param name="Trigger">The trigger that takes an action or a condition, or null.</param>
/// <param name="Group">The action group of the "+" of a group of the Trigger page, or null for all groups.</param>
internal sealed record QuickAddContext(GraphEnd? From, MissionTrigger? Trigger, QuickAddFilter Filter, string? Group = null)
{
    /// <summary>The place of a new node in the canvas, or null for the automatic layout.</summary>
    public GraphPoint? Place { get; init; }

    /// <summary>The act of a new trigger, or null.</summary>
    public ActRef? Act { get; init; }
}

/// <summary>
/// The edit gestures of the mission graph as mission commands (section 8.2 of the M6b design). Each gesture gives one
/// batch, so one history entry. The view draws the pins, the menus and the popups, and calls this class when a gesture
/// ends. The class checks the targets of the link drags and gives the reason for each target that is not valid. It makes
/// no ImGui call, so the tests check each gesture. All edits go through the mission layer (decision D660).
/// </summary>
internal sealed class GraphEditModel
{
    // The texts of the rejected links (section 8.11 of the M6b design).
    public const string OutputToInput = "A link goes from an output pin to an input pin.";
    public const string CheckpointOnly = "Only a checkpoint line can load a trigger.";
    public const string StateOnly = "This pin takes only the state of a trigger.";

    // Texts that the design does not give. They follow the same form.
    public const string StateToWatches = "The state of a trigger links only to the Watches pin of a trigger.";
    public const string OpenFrame = "Open the frame to link one of its triggers.";
    public const string ShowLines = "Show the lines of this trigger to change one of its links.";
    public const string StartToStart = "A start line links to a Start pin.";
    public const string StopToStop = "A stop line links to a Stop pin.";
    public const string CheckpointToStart = "A checkpoint line links to a Start pin or a Load pin.";
    public const string WatchLink = "This link is the condition of the watcher. Change the condition to remove the link.";
    public const string StartLinkOnly = "Only a Start link can take a new trigger between its two triggers.";
    public const string SeveralLinks = "This link stands for the links of several triggers to several triggers. Open the " +
                                       "frame, then insert the trigger on one of the links.";
    public const string NoTriggers = "The clipboard holds no [Trigger] section.";
    public const string MissingNode = "This trigger is missing. Drop the item on a trigger of the script.";

    /// <summary>The words of the watch menu and their texts (section 8.11 of the M6b design). The items write the words
    /// of retail.</summary>
    public static readonly IReadOnlyList<(string Word, string Label, string Tip)> WatchStates =
    [
        ("ON", "Is active", "The watched trigger is active now."),
        ("OFF", "Is not active", "The watched trigger is not active now. It did not start, it stopped, or it ran."),
        ("INACTIVE", "Is inactive", "The watched trigger did not start, or it stopped. A trigger that ran does not count."),
        ("COMPLETE", "Has run", "The watched trigger ran."),
    ];

    private readonly Dictionary<int, GraphEnd> ends = [];
    private readonly GraphIds ids;

    public GraphEditModel(MissionGraph graph)
    {
        Graph = graph;
        ids = graph.Options.Ids ?? new GraphIds();
        foreach (var node in graph.Nodes)
        {
            foreach (var pin in node.Pins)
                ends[pin.Id] = new GraphEnd(node, pin);
            if (node.Kind == GraphNodeKind.Trigger)
                ends[AddPinId(node)] = new GraphEnd(node, null);
        }
    }

    public MissionGraph Graph { get; }

    /// <summary>The install path of the script of the graph.</summary>
    public string Script => Graph.Analysis.Script.Path;

    private MissionIndex Index => Graph.Index;

    /// <summary>The node editor id of the "+" pin of a trigger node.</summary>
    public int AddPinId(GraphNode node) => ids.Of(node.Key + "|out:add");

    /// <summary>The node and the pin of a node editor pin id, or null.</summary>
    public GraphEnd? End(int pinId) => ends.GetValueOrDefault(pinId);

    // ---- References ----

    private static TriggerRef RefOf(MissionTrigger trigger) => TriggerOf(trigger);

    private static MissionBatch Batch(string label, params MissionCommand[] commands) => new(label, commands);

    /// <summary>The trigger names of the script, without letter case.</summary>
    private HashSet<string> TakenNames() => TakenNames(Index);

    /// <summary>The trigger names of a script, without letter case.</summary>
    private static HashSet<string> TakenNames(MissionIndex index) =>
        index.Triggers.Select(t => t.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A made name for a new trigger of the script.</summary>
    public string NewTriggerName() => MissionNaming.Free(Script, "Trigger", TakenNames());

    /// <summary>The name of the trigger of a target node: its trigger, or the name of a missing node.</summary>
    private static string? TargetName(GraphNode node) => node.Kind switch
    {
        GraphNodeKind.Trigger => node.Trigger!.Nickname,
        GraphNodeKind.Missing => node.MissingName,
        _ => null,
    };

    // ---- Link drags ----

    /// <summary>
    /// The gesture of a link drag between two pins (section 8.2 of the M6b design). The two ends can come in any order.
    /// A drag from "+" or from the Starts chip to a Start pin starts the target, and to a Stop pin stops it. A drag from a
    /// row of a start, stop or checkpoint line to a node changes the trigger of the line. A drag from the State pin to a
    /// Watches pin asks for the state. A link from a node to itself is valid.
    /// </summary>
    public LinkGesture Link(GraphEnd a, GraphEnd b)
    {
        if (a.IsInput == b.IsInput)
            return LinkGesture.Refuse(OutputToInput);
        var (output, input) = a.IsInput ? (b, a) : (a, b);
        if (output.Node.Kind != GraphNodeKind.Trigger || input.Node.Kind is GraphNodeKind.Prefab or GraphNodeKind.Act)
            return LinkGesture.Refuse(OpenFrame);
        var source = output.Node.Trigger!;
        var target = TargetName(input.Node)!;
        var into = input.Pin!.Kind;
        if (into == GraphPinKind.Watches)
            return output.Pin?.Kind == GraphPinKind.State ? new LinkGesture(null, null) { AsksState = true }
                : LinkGesture.Refuse(StateOnly);
        if (output.Pin?.Kind == GraphPinKind.State)
            return LinkGesture.Refuse(StateToWatches);
        if (output.IsAdd || output.Pin!.Kind is GraphPinKind.Starts or GraphPinKind.Stops)
        {
            if (into == GraphPinKind.Load)
                return LinkGesture.Refuse(CheckpointOnly);
            var kind = into == GraphPinKind.Stop ? LinkKind.Stop : LinkKind.Start;
            if (output.Pin?.Kind == GraphPinKind.Starts && kind != LinkKind.Start)
                return LinkGesture.Refuse(StartToStart);
            if (output.Pin?.Kind == GraphPinKind.Stops && kind != LinkKind.Stop)
                return LinkGesture.Refuse(StopToStop);
            var command = new LinkTriggers(RefOf(source), target, kind);
            return new LinkGesture(Batch(kind == LinkKind.Stop ? $"Stop {target} from {source.Nickname}"
                : $"Start {target} from {source.Nickname}", command), null);
        }
        if (output.Pin.Kind != GraphPinKind.Row || output.Pin.Line is not { } line)
            return LinkGesture.Refuse(ShowLines);
        var fits = line.Key.ToLowerInvariant() switch
        {
            "act_acttrig" => into == GraphPinKind.Start ? null : into == GraphPinKind.Load ? CheckpointOnly : StartToStart,
            "act_deacttrig" => into == GraphPinKind.Stop ? null : into == GraphPinKind.Load ? CheckpointOnly : StopToStop,
            _ => into is GraphPinKind.Start or GraphPinKind.Load ? null : CheckpointToStart,
        };
        return fits != null ? LinkGesture.Refuse(fits)
            : new LinkGesture(Batch($"Link to {target}", new RetargetLink(RowOf(line), target)), null);
    }

    /// <summary>The gesture of a drag from the State pin to a Watches pin after the pick of the state: the condition of
    /// the watcher watches the trigger of the State pin.</summary>
    /// <param name="state">A word of <see cref="WatchStates"/>.</param>
    public MissionBatch? Watch(GraphEnd a, GraphEnd b, string state)
    {
        var (output, input) = a.IsInput ? (b, a) : (a, b);
        if (output.Node.Trigger is not { } watched || input.Node.Trigger is not { } watcher)
            return null;
        return Batch($"Watch {watched.Nickname}", new SetCondition(RefOf(watcher),
            LineSpec.Of("Cnd_WatchTrigger", ("trigger", watched.Nickname), ("state", state))));
    }

    /// <summary>The item from which quick add makes a trigger after a drag from an output pin to an empty place. Null
    /// for a pin that cannot start a new trigger: a state, a chip of several lines, or a frame.</summary>
    public string? NewNodeRefusal(GraphEnd from)
    {
        if (from.Node.Kind != GraphNodeKind.Trigger)
            return OpenFrame;
        if (from.IsInput)
            return OutputToInput;
        if (from.Pin?.Kind == GraphPinKind.State)
            return StateToWatches;
        if (from.Pin?.Kind is GraphPinKind.Saves or GraphPinKind.More)
            return ShowLines;
        return null;
    }

    // ---- Links ----

    /// <summary>Why a link cannot go, or null.</summary>
    public static string? UnlinkRefusal(GraphLink link) =>
        link.Sources.Any(s => s.Kind == MissionLinkKind.Watch) ? WatchLink : null;

    /// <summary>
    /// The gesture of Delete on a selected link, or of the link menu "Remove link" (section 8.2 of the M6b design):
    /// <see cref="UnlinkTriggers"/> for each pair of triggers of the link. A restart removes both lines. A load link
    /// removes its checkpoint lines. Null for a watch link (see <see cref="UnlinkRefusal"/>).
    /// </summary>
    public MissionBatch? Unlink(GraphLink link)
    {
        if (UnlinkRefusal(link) != null)
            return null;
        var commands = new List<MissionCommand>();
        foreach (var group in link.Sources.GroupBy(s => (s.Source, Name: s.TargetName.ToLowerInvariant(), s.Kind)))
        {
            var first = group.First();
            switch (first.Kind)
            {
                case MissionLinkKind.Start or MissionLinkKind.Stop:
                    var restart = Index.Restarts.Any(r => r.Source == first.Source &&
                                                          r.TargetName.Equals(first.TargetName, StringComparison.OrdinalIgnoreCase));
                    var kind = restart ? LinkKind.Restart : first.Kind == MissionLinkKind.Stop ? LinkKind.Stop : LinkKind.Start;
                    if (!commands.OfType<UnlinkTriggers>().Any(u => u.Source.Equals(RefOf(first.Source)) &&
                                                                    u.Target.Equals(first.TargetName, StringComparison.OrdinalIgnoreCase) &&
                                                                    u.LinkKind == LinkKind.Restart))
                        commands.Add(new UnlinkTriggers(RefOf(first.Source), first.TargetName, kind));
                    break;
                case MissionLinkKind.Load:
                    // From the last row to the first, so each row index stays valid.
                    commands.AddRange(group.OrderByDescending(l => l.Line.Entry).Select(l => new RemoveAction(RowOf(l.Line))));
                    break;
            }
        }
        return commands.Count == 0 ? null : new MissionBatch("Remove link", commands);
    }

    /// <summary>The condition of a new trigger of the link menu "Insert trigger".</summary>
    /// <param name="seconds">The delay of <see cref="InsertEvent.Delay"/>.</param>
    /// <param name="commLine">The comm line of <see cref="InsertEvent.CommLine"/> that the user picked.</param>
    public static LineSpec InsertCondition(InsertEvent kind, double seconds = MissionLibrary.DefaultDelay,
        string? commLine = null) => kind switch
    {
        InsertEvent.Delay => LineSpec.Of("Cnd_Timer", ("seconds", seconds)),
        InsertEvent.AtOnce => LineSpec.Raw("Cnd_True", "no_params"),
        _ => LineSpec.Of("Cnd_CommComplete", ("line", commLine ?? MissionLibrary.NoName)),
    };

    /// <summary>
    /// Why a link cannot take a new trigger, or null. Only a Start link can. A link into or out of a collapsed frame can
    /// stand for several lines. One new trigger keeps each start of such a link only when one trigger starts all the
    /// targets, or when all the lines start one target. Else the user opens the frame first.
    /// </summary>
    public static string? InsertRefusal(GraphLink link)
    {
        if (link.Sources.Count == 0 ||
            !link.Sources.All(s => s.Kind == MissionLinkKind.Start && MissionIndex.Is(s.Line.Key, "Act_ActTrig")))
            return StartLinkOnly;
        var sources = link.Sources.Select(s => s.Source).Distinct().Count();
        return sources > 1 && Targets(link).Count > 1 ? SeveralLinks : null;
    }

    /// <summary>The names of the targets of the lines of a link, each one once, without letter case.</summary>
    private static List<string> Targets(GraphLink link) =>
        link.Sources.Select(s => s.TargetName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The gesture of the link menu "Insert trigger": <see cref="InsertTriggerOnLink"/> on the first line of the link,
    /// and <see cref="RetargetLink"/> of the first line of each other source to the new trigger. So a link of a summary
    /// chip with several lines gets one new trigger. Each source starts the new trigger once: its other lines of the link
    /// go. The new trigger starts each target of the lines, so no target loses its start (review finding 1 of M6b-2).
    /// </summary>
    /// <param name="place">The place of the node of the new trigger, or null for the automatic layout.</param>
    public MissionBatch? InsertOnLink(GraphLink link, LineSpec condition, GraphPoint? place = null)
    {
        if (InsertRefusal(link) != null)
            return null;
        var name = NewTriggerName();
        var sources = link.Sources.Select(s => s.Line).GroupBy(l => l.Section).OrderBy(g => g.Key.Index)
            .Select(g => g.OrderBy(l => l.Entry).ToList()).ToList();
        var first = sources[0][0];
        var commands = new List<MissionCommand>
        {
            new InsertTriggerOnLink(RowOf(first), condition) { Nickname = name, Place = place },
        };
        commands.AddRange(sources.Skip(1).Select(lines => new RetargetLink(RowOf(lines[0]), name)));
        // The last lines of a section go first, so the rows of the other lines stay.
        commands.AddRange(sources.SelectMany(lines => lines.Skip(1)).OrderByDescending(l => l.Entry)
            .Select(l => new RemoveAction(RowOf(l))));
        var target = first.Value(0)?.Trim() ?? "";
        commands.AddRange(Targets(link).Where(t => !t.Equals(target, StringComparison.OrdinalIgnoreCase))
            .Select(t => new LinkTriggers(new TriggerRef(Script, name), t, LinkKind.Start)));
        return new MissionBatch("Insert trigger", commands);
    }

    // ---- Nodes ----

    /// <summary>
    /// The gesture of Delete with nodes selected (section 8.2 of the M6b design): one batch with
    /// <see cref="RemoveAct"/> for each act frame, then <see cref="RemoveTrigger"/> for each trigger node. The triggers of
    /// an act stay. Each line that names a removed trigger stays and shows "Missing" (decision D81). A duplicate trigger,
    /// which the game never runs (TR4), goes by its section. Prefab frames and missing nodes give nothing. Null when the
    /// selection has no trigger and no act.
    /// </summary>
    /// <remarks>The acts go first. The first act edit writes the proposed acts of W, so the act numbers of the
    /// selection are the numbers of these acts before the triggers go (review finding 5 of M6b-2).</remarks>
    public MissionBatch? Delete(IEnumerable<GraphBlock> selection)
    {
        var blocks = selection.ToList();
        var acts = blocks.Select(b => b as GraphFrame ?? (b as GraphNode)?.Frame).OfType<GraphFrame>()
            .Where(f => f is { Kind: GraphFrameKind.Act, Act: not null }).Distinct().ToList();
        var commands = acts.Select(f => (MissionCommand)new RemoveAct(ActRefOf(f.Act!))).ToList();
        foreach (var trigger in blocks.OfType<GraphNode>().Select(n => n.Trigger).OfType<MissionTrigger>().Distinct())
            commands.Add(trigger.IsDuplicate ? new RemoveSection(ItemOf(trigger.Section)) : new RemoveTrigger(RefOf(trigger)));
        return commands.Count switch
        {
            0 => null,
            1 => new MissionBatch(commands[0].HistoryLabel, commands),
            _ => new MissionBatch(acts.Count == 0
                ? $"Delete {commands.Count.ToString(CultureInfo.InvariantCulture)} triggers"
                : $"Delete {commands.Count.ToString(CultureInfo.InvariantCulture)} items", commands),
        };
    }

    /// <summary>The reference of an act of the graph: its nickname in the graph file, or the nickname that the first
    /// act edit gives a proposed act (decision D679).</summary>
    public ActRef ActRefOf(MissionAct act) => new(Script, MissionActs.NicknameOf(act));

    /// <summary>The graph menu "New act from selection" (section 8.11 of the M6b design): a new act with the selected
    /// triggers. Null when the selection has no trigger.</summary>
    public MissionBatch? NewAct(IEnumerable<GraphBlock> selection)
    {
        var triggers = selection.OfType<GraphNode>().SelectMany(n => n.Triggers).Distinct().OrderBy(t => t.Section.Index)
            .Select(t => t.Nickname).ToList();
        return triggers.Count == 0 ? null : Batch("New act from selection", new AddAct(new ScriptRef(Script)) { Members = triggers });
    }

    /// <summary>
    /// The gesture of a drag of nodes (section 8.2 of the M6b design): one batch with <see cref="MoveNodes"/> for the new
    /// places of the trigger nodes, and <see cref="SetActMembers"/> for each node that the drag puts into another act
    /// frame. So each drag is one undo entry. Null when no trigger node moved.
    /// </summary>
    /// <param name="moved">Each moved node, its new top-left corner, and the act frame under its center, or null.</param>
    public MissionBatch? Moved(IReadOnlyList<(GraphNode Node, System.Numerics.Vector2 Position, GraphFrame? Act)> moved)
    {
        var places = moved.Where(m => m.Node.Trigger != null)
            .Select(m => new NodePlace(m.Node.Trigger!.Nickname, Math.Round(m.Position.X), Math.Round(m.Position.Y))).ToList();
        if (places.Count == 0)
            return null;
        var commands = new List<MissionCommand> { new MoveNodes(new ScriptRef(Script), places) };
        var acts = Graph.ActsOf();
        foreach (var group in moved.Where(m => m is { Node.Trigger: not null, Act.Act: not null } &&
                                               acts.ActOf(m.Node.Trigger!) != m.Act.Act)
                     .GroupBy(m => m.Act!))
        {
            var act = group.Key.Act!;
            var members = act.Triggers.Select(t => t.Nickname).Concat(group.Select(m => m.Node.Trigger!.Nickname)).ToList();
            commands.Add(new SetActMembers(ActRefOf(act), members));
        }
        return new MissionBatch(places.Count == 1 ? $"Move {places[0].Trigger}" : "Move nodes", commands);
    }

    /// <summary>The gesture of Ctrl+D: <see cref="DuplicateTrigger"/> for each selected trigger, with a free name after
    /// the name of the trigger, in one batch. Unity duplicates each selected object in the same way.</summary>
    /// <param name="triggers">Each trigger, and the place of the node of its copy beside the original or null for the
    /// automatic layout.</param>
    public MissionBatch? Duplicate(IEnumerable<(MissionTrigger Trigger, GraphPoint? Place)> triggers) =>
        Duplicate(Index, triggers);

    /// <summary>The batch of a duplicate of triggers of a script. The graph and the Hierarchy use it. Null for no
    /// trigger.</summary>
    internal static MissionBatch? Duplicate(MissionIndex index,
        IEnumerable<(MissionTrigger Trigger, GraphPoint? Place)> triggers)
    {
        var taken = TakenNames(index);
        var commands = new List<MissionCommand>();
        foreach (var (trigger, place) in triggers.DistinctBy(t => t.Trigger))
        {
            var name = MissionNaming.Free(trigger.Section.Ref.Path, "Trigger", taken, trigger.Nickname);
            taken.Add(name);
            commands.Add(new DuplicateTrigger(RefOf(trigger)) { NewName = name, Place = place });
        }

        return commands.Count switch
        {
            0 => null,
            1 => new MissionBatch($"Duplicate trigger {((DuplicateTrigger)commands[0]).Trigger.Name}", commands),
            _ => new MissionBatch($"Duplicate {commands.Count.ToString(CultureInfo.InvariantCulture)} triggers", commands),
        };
    }

    /// <summary>The gesture of F2: <see cref="RenameTrigger"/>. The lines that name the trigger change too. Null when the
    /// name is the same. The graph and the Hierarchy use it.</summary>
    public static MissionBatch? Rename(MissionTrigger trigger, string name) =>
        string.Equals(trigger.Nickname, name.Trim(), StringComparison.Ordinal) || name.Trim().Length == 0
            ? null
            : Batch($"Rename {trigger.Nickname} to {name.Trim()}", new RenameTrigger(RefOf(trigger), name.Trim()));

    /// <summary>The node menu "Starts with the mission".</summary>
    public MissionBatch SetStart(MissionTrigger trigger, bool starts) =>
        Batch(starts ? $"Start {trigger.Nickname} with the mission" : $"Do not start {trigger.Nickname} with the mission",
            new SetTriggerStart(RefOf(trigger), starts));

    /// <summary>The node menu "Repeats".</summary>
    public MissionBatch SetRepeats(MissionTrigger trigger, bool repeats) =>
        Batch(repeats ? $"Repeat {trigger.Nickname}" : $"Do not repeat {trigger.Nickname}",
            new SetTriggerRepeats(RefOf(trigger), repeats));

    // ---- Copy and paste (decision D689) ----

    /// <summary>The INI text of the sections of triggers, in file order, for Ctrl+C.</summary>
    public string Copy(IEnumerable<MissionTrigger> triggers)
    {
        var sections = triggers.Distinct().OrderBy(t => t.Section.Index).Select(t => t.Section.Section).ToList();
        return sections.Count == 0 ? "" : RawText.Canonical(Script, sections).Text + "\n";
    }

    /// <summary>
    /// The gesture of Ctrl+V: each <c>[Trigger]</c> section of an INI text becomes a new trigger of the script, with its
    /// lines in their order. A name that the script has, or a name that is not valid, gets a free name. Each start, stop,
    /// checkpoint and watch value inside the pasted set follows the new names. A name that two pasted triggers have gives
    /// each of them a free name, and a value inside the set names the first of them, as the game finds the first trigger
    /// of a repeated name (TR4). Null with a problem when the text holds no trigger.
    /// </summary>
    public MissionBatch? Paste(string text, out string? problem)
    {
        problem = null;
        if (RawMissionEdit.TextProblems(Script, text) is [var first, ..])
        {
            problem = $"The editor cannot read the text of the clipboard. {first}";
            return null;
        }

        var sections = RawMissionEdit.Sections(Script, text)
            .Where(s => MissionIndex.Is(s.Name, "Trigger") && TriggerRules.Nickname(s) != null).ToList();
        if (sections.Count == 0)
        {
            problem = NoTriggers;
            return null;
        }

        var taken = TakenNames();
        // The name of each pasted trigger, and the first new name of each old name for the values inside the set.
        var own = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            var old = TriggerRules.Nickname(section)!.Trim();
            var name = MissionNaming.Free(Script, "Trigger", taken, old);
            taken.Add(name);
            own.Add(name);
            names.TryAdd(old, name);
        }

        var schema = MissionSchema.Retail;
        var commands = new List<MissionCommand>();
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            var nicknameLine = TriggerRules.NicknameLine(section);
            var lines = new List<LineSpec>();
            for (var r = 0; r < section.Count; r++)
            {
                var entry = section[r];
                var texts = entry.Select(MissionIndex.Text).ToList();
                if (r == nicknameLine)
                    texts[0] = own[s];
                else if (schema.Name(entry.Name) is { } name)
                {
                    foreach (var spec in name.Values.Where(v => v.Kind == MissionValueKind.Trigger))
                    {
                        if (spec.Index < texts.Count && names.TryGetValue(texts[spec.Index].Trim(), out var renamed))
                            texts[spec.Index] = renamed;
                    }
                }
                lines.Add(LineSpec.Raw(entry.Name, [.. texts]));
            }
            commands.Add(new AddSection(new ScriptRef(Script), "Trigger", lines));
        }
        return new MissionBatch(commands.Count == 1 ? "Paste trigger"
            : $"Paste {commands.Count.ToString(CultureInfo.InvariantCulture)} triggers", commands);
    }

    // ---- Drops and rows ----

    /// <summary>
    /// The gesture of a drop of a library item on a trigger node (section 8.2 of the M6b design): an action goes to the
    /// end of the trigger, or before a row at the lines level. A condition or an event becomes the condition of the
    /// trigger. Null for a node that is not a trigger.
    /// </summary>
    /// <param name="before">The row that a dropped action goes before, or null for the end.</param>
    public MissionBatch? DropOnNode(LibraryItem item, GraphNode node, int? before = null)
    {
        if (node.Trigger is not { } trigger)
            return null;
        var line = MissionLibrary.Line(item);
        return item.Kind == LibraryKind.Action
            ? Batch($"Add {item.Name} to {trigger.Nickname}", new AddAction(RefOf(trigger), line) { Before = before })
            : Batch($"Set the condition of {trigger.Nickname}", new SetCondition(RefOf(trigger), line));
    }

    /// <summary>Why an item cannot drop on an empty place, or null. Only an event or a condition makes a new
    /// trigger.</summary>
    public static string? EmptyDropRefusal(LibraryItem item) =>
        item.MakesTrigger ? null : "Drop an action on a trigger.";

    /// <summary>The gesture of a drop of an event or a condition on an empty place or into an act frame: a new trigger
    /// with the condition, its node at the place and in the act. Null for an action.</summary>
    public MissionBatch? DropOnEmpty(LibraryItem item, GraphPoint? place = null, ActRef? act = null) =>
        EmptyDropRefusal(item) != null
            ? null
            : Batch($"Add trigger: {item.Name}", new AddTrigger(new ScriptRef(Script))
            {
                Nickname = NewTriggerName(), Condition = MissionLibrary.Line(item), Place = place, Act = act,
            });

    /// <summary>
    /// The gesture of a drag of an action row at the lines level (section 8.2 of the M6b design). In its trigger, the row
    /// moves (<see cref="ReorderAction"/>). To another trigger, the row moves there in one batch
    /// (<see cref="RemoveAction"/> and <see cref="AddAction"/>), or it gets a copy there with Ctrl. Null when the row is
    /// not an action, or when the move changes nothing.
    /// </summary>
    /// <param name="before">The row of the target that the line goes before, or null for the end.</param>
    public MissionBatch? MoveRow(MissionLine row, MissionTrigger target, int? before, bool copy)
    {
        if (row.Trigger is not { } source || !row.Trigger.Actions.Contains(row))
            return null;
        var line = LineSpec.Raw(row.Key, [.. row.Line.Select(MissionIndex.Text)]);
        if (copy)
            return Batch($"Copy {row.Name?.Plain ?? row.Key} to {target.Nickname}",
                new AddAction(RefOf(target), line) { Before = before });
        if (source == target)
            return before == row.Entry || before == row.Entry + 1 || before == null && row.Entry == source.Section.Section.Count - 1
                ? null
                : Batch("Move action", new ReorderAction(RowOf(row)) { Before = before });
        return Batch($"Move {row.Name?.Plain ?? row.Key} to {target.Nickname}",
            new RemoveAction(RowOf(row)), new AddAction(RefOf(target), line) { Before = before });
    }

    // ---- Quick add ----

    /// <summary>True when an item fits the place of quick add (section 8.3 of the M6b design).</summary>
    public static bool Fits(QuickAddContext context, LibraryItem item) => context.Filter switch
    {
        QuickAddFilter.Triggers => item.MakesTrigger,
        QuickAddFilter.Actions => item.Kind == LibraryKind.Action &&
                                  (context.Group == null || MissionSchema.Retail.Name(item.Key)?.Group == context.Group),
        QuickAddFilter.Conditions => item.MakesTrigger,
        _ => item.Kind != LibraryKind.Action || context.Trigger != null,
    };

    /// <summary>
    /// The gesture of a pick in quick add (section 8.3 of the M6b design). From a pin, the new trigger gets the condition
    /// of the item, and the pin links to it in the same batch. With a selected trigger, an action goes to its end, and a
    /// condition becomes its condition. Else an event or a condition makes a new trigger.
    /// </summary>
    public MissionBatch? QuickAdd(QuickAddContext context, LibraryItem item)
    {
        if (!Fits(context, item))
            return null;
        if (context.From is { } from)
        {
            if (NewNodeRefusal(from) != null || !item.MakesTrigger)
                return null;
            var name = NewTriggerName();
            var add = new AddTrigger(new ScriptRef(Script))
            {
                Nickname = name, Condition = MissionLibrary.Line(item), Place = context.Place, Act = context.Act,
            };
            MissionCommand link = from.Pin is { Kind: GraphPinKind.Row, Line: { } line }
                ? new RetargetLink(RowOf(line), name)
                : new LinkTriggers(RefOf(from.Node.Trigger!), name,
                    from.Pin?.Kind == GraphPinKind.Stops ? LinkKind.Stop : LinkKind.Start);
            return Batch($"Add trigger: {item.Name}", add, link);
        }
        if (context.Trigger is { } trigger && (item.Kind == LibraryKind.Action || context.Filter == QuickAddFilter.Conditions))
            return AddTo(trigger, item);
        return DropOnEmpty(item, context.Place, context.Act);
    }

    /// <summary>The batch that adds an item to a trigger: an action goes to its end, and a condition or an event becomes
    /// its condition. The quick add of the Trigger page uses it too.</summary>
    public static MissionBatch AddTo(MissionTrigger trigger, LibraryItem item)
    {
        var line = MissionLibrary.Line(item);
        var reference = RefOf(trigger);
        return item.Kind == LibraryKind.Action
            ? Batch($"Add {item.Name} to {trigger.Nickname}", new AddAction(reference, line))
            : Batch($"Set the condition of {trigger.Nickname}", new SetCondition(reference, line));
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The state of the mission graph of the session (sections 6.4 to 6.7 of the M6a design). It keeps the frames that the
/// user collapsed or opened, and the nodes that show their lines or all their rows. It also keeps the drag offsets and
/// the stable node ids of each mission. A change of the lens forgets the collapse and open of the user, so each lens
/// starts with its own frames. The editor writes no file with this state (decision D530). The class makes no ImGui
/// call.
/// </summary>
internal sealed class GraphViewState
{
    private readonly Dictionary<string, MissionState> missions = new(VfsPath.Comparer);
    private GraphLens lens;

    /// <param name="lens">The lens of the settings.</param>
    public GraphViewState(GraphLens lens = GraphLens.Beats) => this.lens = lens;

    /// <summary>The state of one mission.</summary>
    private sealed class MissionState
    {
        public Dictionary<string, bool> Collapse { get; } = new(StringComparer.Ordinal);

        public HashSet<string> LinesOf { get; } = new(StringComparer.Ordinal);

        public HashSet<string> AllRowsOf { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Vector2> Offsets { get; } = new(StringComparer.Ordinal);

        public GraphIds Ids { get; } = new();
    }

    /// <summary>A number that changes with each change of the state, so the view knows when to build the graph
    /// again.</summary>
    public int Version { get; private set; }

    public GraphLens Lens
    {
        get => lens;
        set
        {
            if (lens == value)
            {
                return;
            }

            lens = value;
            foreach (var state in missions.Values)
            {
                state.Collapse.Clear();
            }

            Version++;
        }
    }

    /// <summary>The options of the graph of a mission.</summary>
    /// <param name="systemName">Gives the name of a system for the act titles, or null.</param>
    public GraphOptions Options(string path, bool showLines, bool showBaseGame, bool hidesOpenLancer,
        Func<string, string?>? systemName)
    {
        var state = Of(path);
        return new GraphOptions(lens, showLines, state.Collapse, state.LinesOf, state.AllRowsOf, showBaseGame,
            hidesOpenLancer, systemName, state.Ids);
    }

    /// <summary>The drag offsets of the nodes and frames of a mission.</summary>
    public IReadOnlyDictionary<string, Vector2> Offsets(string path) => Of(path).Offsets;

    /// <summary>Collapses or opens a frame.</summary>
    public void SetCollapsed(string path, string frame, bool collapsed)
    {
        Of(path).Collapse[frame] = collapsed;
        Version++;
    }

    /// <summary>Shows the lines of one trigger node also at the summary level, or shows its summary again.</summary>
    public void SetLines(string path, string node, bool lines)
    {
        var set = Of(path).LinesOf;
        if (lines ? set.Add(node) : set.Remove(node))
        {
            Version++;
        }
    }

    /// <summary>True when a trigger node shows its lines at the summary level.</summary>
    public bool ShowsLines(string path, string node) => Of(path).LinesOf.Contains(node);

    /// <summary>Shows all rows of a node, without the limit of 20 rows.</summary>
    public void ShowAllRows(string path, string node)
    {
        if (Of(path).AllRowsOf.Add(node))
        {
            Version++;
        }
    }

    /// <summary>Adds the move of a drag to the offset of a node or a frame. The view keeps the node where the drag left
    /// it, so the layout does not run again for a drag.</summary>
    public void Dragged(string path, string key, Vector2 delta)
    {
        var offsets = Of(path).Offsets;
        offsets[key] = offsets.GetValueOrDefault(key) + delta;
    }

    private MissionState Of(string path)
    {
        if (!missions.TryGetValue(path, out var state))
        {
            missions[path] = state = new MissionState();
        }

        return state;
    }

    /// <summary>Forgets the state of all missions, for example when the profile closes.</summary>
    public void Reset()
    {
        missions.Clear();
        Version++;
    }

    /// <summary>The selection of a node: its trigger, its prefab instance or its act. Null for a missing
    /// node.</summary>
    public static MissionRef? RefOf(string script, GraphNode node) => node.Kind switch
    {
        GraphNodeKind.Trigger => new MissionRef(script, MissionItemKind.Trigger, node.Trigger!.Section.Ref),
        GraphNodeKind.Prefab or GraphNodeKind.Act when node.Frame is { } frame => RefOf(script, frame),
        _ => null,
    };

    /// <summary>The selection of a frame: its prefab instance or its act.</summary>
    public static MissionRef RefOf(string script, GraphFrame frame) => frame.Instance is { } instance
        ? new MissionRef(script, MissionItemKind.PrefabInstance, Key: instance.Key)
        : new MissionRef(script, MissionItemKind.Act, Key: frame.Key);

    /// <summary>
    /// The node or the frame of the graph that shows a selection, or null. A trigger or a row shows in the node of its
    /// trigger, or in the collapsed frame that holds it. A prefab instance or an act shows in its frame or its collapsed
    /// node.
    /// </summary>
    public static GraphBlock? BlockOf(MissionGraph graph, MissionRef item)
    {
        switch (item.Kind)
        {
            case MissionItemKind.Trigger or MissionItemKind.Row when item.Section is { } section &&
                                                                     graph.Index.Document.Find(section) is { } found:
                foreach (var trigger in graph.Index.Triggers)
                {
                    if (trigger.Section == found)
                    {
                        return graph.NodeOf(trigger);
                    }
                }

                return null;
            case MissionItemKind.PrefabInstance when item.Key != null:
                return Shown(graph.Block("p:" + item.Key));
            case MissionItemKind.Act when item.Key != null:
                return Shown(graph.Block(item.Key));
            default:
                return null;
        }
    }

    /// <summary>The collapsed node of a frame that shows as one node, or the frame.</summary>
    private static GraphBlock? Shown(GraphBlock? block)
    {
        if (block is not GraphFrame frame)
        {
            return block;
        }

        GraphBlock shown = frame.Collapsed ? frame.Node : frame;
        for (var parent = frame.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.Collapsed)
            {
                shown = parent.Node;
            }
        }

        return shown;
    }
}

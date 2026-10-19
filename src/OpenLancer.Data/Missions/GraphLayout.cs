using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The automatic layout of the mission graph (section 6.7 of the M6a design, decision D530). It is a compound layout:
/// <list type="number">
/// <item>Each frame lays out its blocks: its nodes and its open frames, each one with its current size. A collapse or an
/// open changes the size of one block, and "Show lines" changes the heights. The order of the blocks does not
/// change.</item>
/// <item>The layer of a block is the longest path to it over Start and Load links in its frame. Back links, which a
/// depth-first walk in file order finds, do not count.</item>
/// <item>In a layer, two sweeps order the blocks: by the mean position of their parents, then of their children. Ties keep
/// file order, so the same graph gives the same layout.</item>
/// <item>The acts sit side by side from left to right, in act order. The frame "Not started by the mission" is the last
/// one.</item>
/// <item>A drag of a node moves it for the session only. The layout adds the offset of each dragged node to its place, so
/// a later layout keeps the drag.</item>
/// </list>
/// The class makes no ImGui call. Positions and sizes are in canvas points.
/// </summary>
public sealed class GraphLayout
{
    /// <summary>The space between two layers.</summary>
    public const float LayerGap = 80;

    /// <summary>The space between two blocks of one layer.</summary>
    public const float BlockGap = 24;

    /// <summary>The space between the edge of a frame and its blocks.</summary>
    public const float FramePadding = 20;

    /// <summary>The height of the title of a frame: the title line and the detail line.</summary>
    public const float FrameTitle = 44;

    /// <summary>The space between two acts.</summary>
    public const float ActGap = 160;

    /// <summary>A layer with more blocks than this starts a new column, so that one layer does not make a very tall
    /// column. Section 6.7 of the M6a design has one column for each layer. This wrap is an addition, because the widest
    /// retail layer holds 90 triggers.</summary>
    public const int MaxColumn = 30;

    private readonly Dictionary<string, Vector2> positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2> sizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2> relative = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> layers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> orders = new(StringComparer.Ordinal);
    private readonly MissionGraph graph;

    private GraphLayout(MissionGraph graph) => this.graph = graph;

    /// <summary>The top-left corner of each node that shows and of each open frame, by key.</summary>
    public IReadOnlyDictionary<string, Vector2> Positions => positions;

    /// <summary>The smallest rectangle that holds the whole graph.</summary>
    public (Vector2 Min, Vector2 Max) Bounds { get; private set; }

    /// <summary>Lays out a graph.</summary>
    /// <param name="offsets">The drag offsets of the nodes and frames of the session, by key, or null.</param>
    public static GraphLayout Of(MissionGraph graph, IReadOnlyDictionary<string, Vector2>? offsets = null)
    {
        var layout = new GraphLayout(graph);
        layout.Place(offsets ?? new Dictionary<string, Vector2>());
        return layout;
    }

    /// <summary>The top-left corner of a block: of the node of a collapsed frame, else of the node or the frame.</summary>
    public Vector2 PositionOf(GraphBlock block) =>
        positions.GetValueOrDefault(block is GraphFrame { Collapsed: true } frame ? frame.Node.Key : block.Key);

    /// <summary>The size of a block: its node size, or the size of an open frame.</summary>
    public Vector2 SizeOf(GraphBlock block) => block switch
    {
        GraphFrame { Collapsed: true } frame => frame.Node.Size,
        GraphFrame frame => sizes.GetValueOrDefault(frame.Key),
        GraphNode node => node.Size,
        _ => Vector2.Zero,
    };

    /// <summary>The rectangle of a block.</summary>
    public (Vector2 Min, Vector2 Max) RectOf(GraphBlock block)
    {
        var min = PositionOf(block);
        return (min, min + SizeOf(block));
    }

    /// <summary>The layer of a block in its frame, from 0.</summary>
    public int LayerOf(GraphBlock block) => layers.GetValueOrDefault(block.Key);

    /// <summary>The keys of the blocks of a frame, by layer and then by their place in the layer.</summary>
    public IReadOnlyList<string> OrderIn(GraphFrame frame) => orders.GetValueOrDefault(frame.Key) ?? [];

    private void Place(IReadOnlyDictionary<string, Vector2> offsets)
    {
        var x = 0f;
        var tops = new List<(GraphBlock Block, Vector2 At)>();
        foreach (var act in graph.Acts)
        {
            var size = act.Collapsed ? act.Node.Size : Measure(act);
            tops.Add((act, new Vector2(x, 0)));
            x += size.X + ActGap;
        }
        foreach (var missing in graph.Nodes.Where(n => n.Kind == GraphNodeKind.Missing))
        {
            // The missing nodes stand at the right of the acts, one under the other.
            var y = tops.Where(t => t.Block is GraphNode).Select(t => t.At.Y + ((GraphNode)t.Block).Size.Y + BlockGap)
                .DefaultIfEmpty(0).Max();
            tops.Add((missing, new Vector2(x, y)));
        }

        foreach (var (block, at) in tops)
            Absolute(block, at, offsets);
        if (positions.Count == 0)
            return;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var (key, position) in positions)
        {
            var size = sizes.GetValueOrDefault(key);
            min = Vector2.Min(min, position);
            max = Vector2.Max(max, position + size);
        }
        Bounds = (min, max);
    }

    /// <summary>Writes the absolute place of a block and of its blocks.</summary>
    /// <param name="at">The place of the block without its own offset.</param>
    private void Absolute(GraphBlock block, Vector2 at, IReadOnlyDictionary<string, Vector2> offsets)
    {
        if (block is GraphFrame { Collapsed: true } collapsed)
        {
            positions[collapsed.Node.Key] = at + offsets.GetValueOrDefault(collapsed.Node.Key);
            sizes[collapsed.Node.Key] = collapsed.Node.Size;
            return;
        }
        positions[block.Key] = at + offsets.GetValueOrDefault(block.Key);
        if (block is GraphNode node)
        {
            sizes[block.Key] = node.Size;
            return;
        }
        var frame = (GraphFrame)block;
        foreach (var child in frame.Blocks)
            Absolute(child, at + relative[child.Key], offsets);
    }

    /// <summary>Lays out the blocks of an open frame, relative to its top-left corner, and returns its size.</summary>
    private Vector2 Measure(GraphFrame frame)
    {
        var blocks = frame.Blocks;
        var count = blocks.Count;
        var blockSizes = blocks.Select(b => b switch
        {
            GraphFrame { Collapsed: false } inner => Measure(inner),
            _ => SizeOf(b),
        }).ToArray();
        var index = new Dictionary<GraphBlock, int>();
        for (var i = 0; i < count; i++)
            index[blocks[i]] = i;

        // The Start and Load links between the blocks of the frame, in file order, each pair once.
        var edges = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach (var link in graph.Links.Where(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Load))
        {
            if (BlockIn(frame, link.From) is not { } from || BlockIn(frame, link.To) is not { } to || from == to)
                continue;
            var (a, b) = (index[from], index[to]);
            if (!edges[a].Contains(b))
                edges[a].Add(b);
        }
        foreach (var list in edges)
            list.Sort();
        var forward = WithoutBackLinks(edges);

        // The longest path layers, from the sources.
        var layer = new int[count];
        foreach (var at in TopologicalOrder(forward))
        {
            foreach (var next in forward[at])
                layer[next] = Math.Max(layer[next], layer[at] + 1);
        }
        var layerCount = count == 0 ? 0 : layer.Max() + 1;
        var columns = Enumerable.Range(0, layerCount).Select(l => Enumerable.Range(0, count).Where(i => layer[i] == l).ToList())
            .ToList();
        var parents = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < count; i++)
        {
            foreach (var next in forward[i])
                parents[next].Add(i);
        }

        // Two sweeps: the mean place of the parents, then of the children. Ties keep the order before the sweep.
        var place = new double[count];
        void Number()
        {
            foreach (var column in columns)
            {
                for (var p = 0; p < column.Count; p++)
                    place[column[p]] = p;
            }
        }
        Number();
        for (var l = 1; l < layerCount; l++)
        {
            columns[l] = columns[l].OrderBy(i => parents[i].Count > 0 ? parents[i].Average(p => place[p]) : place[i]).ToList();
            Number();
        }
        for (var l = layerCount - 2; l >= 0; l--)
        {
            columns[l] = columns[l].OrderBy(i => forward[i].Count > 0 ? forward[i].Average(c => place[c]) : place[i]).ToList();
            Number();
        }

        // The columns from left to right. A long layer wraps into more columns.
        var x = FramePadding;
        var height = 0f;
        var order = new List<string>();
        for (var l = 0; l < layerCount; l++)
        {
            for (var start = 0; start < columns[l].Count; start += MaxColumn)
            {
                var column = columns[l].Skip(start).Take(MaxColumn).ToList();
                var width = column.Max(i => blockSizes[i].X);
                var y = FrameTitle + FramePadding;
                foreach (var i in column)
                {
                    relative[blocks[i].Key] = new Vector2(x, y);
                    layers[blocks[i].Key] = l;
                    order.Add(blocks[i].Key);
                    y += blockSizes[i].Y + BlockGap;
                }
                height = Math.Max(height, y - BlockGap);
                x += width + LayerGap;
            }
        }
        orders[frame.Key] = order;
        var size = new Vector2(Math.Max(MissionGraph.SummaryWidth, x - LayerGap + FramePadding),
            Math.Max(FrameTitle + 2 * FramePadding, height + FramePadding));
        sizes[frame.Key] = size;
        return size;
    }

    /// <summary>The block of a frame that holds a node that shows, or null when the node is outside the frame.</summary>
    private static GraphBlock? BlockIn(GraphFrame frame, GraphNode node)
    {
        GraphBlock block = node.Frame ?? (GraphBlock)node;
        while (block.Parent != null && block.Parent != frame)
            block = block.Parent;
        return block.Parent == frame ? block : null;
    }

    /// <summary>The edges without the back links that a depth-first walk in file order finds.</summary>
    private static List<int>[] WithoutBackLinks(List<int>[] edges)
    {
        var result = edges.Select(_ => new List<int>()).ToArray();
        var state = new int[edges.Length];
        for (var root = 0; root < edges.Length; root++)
        {
            if (state[root] != 0)
                continue;
            // An explicit stack, because a chain of blocks can be long.
            var stack = new Stack<(int Node, int Next)>();
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
                if (state[target] == 1)
                    continue;
                result[node].Add(target);
                if (state[target] == 0)
                {
                    state[target] = 1;
                    stack.Push((target, 0));
                }
            }
        }
        return result;
    }

    /// <summary>The blocks in an order where each block comes before the blocks that it links to.</summary>
    private static List<int> TopologicalOrder(List<int>[] forward)
    {
        var order = new List<int>();
        var done = new bool[forward.Length];
        for (var root = 0; root < forward.Length; root++)
        {
            if (done[root])
                continue;
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((root, 0));
            done[root] = true;
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
                if (!done[target])
                {
                    done[target] = true;
                    stack.Push((target, 0));
                }
            }
        }
        order.Reverse();
        return order;
    }
}

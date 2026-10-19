using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.ImUI;
using LibreLancer.ImUI.NodeEditor;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The mission graph of the Mission tab (section 6 of the M6a design): the node editor with one node for each trigger,
/// the act and prefab frames, the pins, the links, the chips and the marks. It draws the model of
/// <see cref="MissionGraph"/> at the places of <see cref="GraphLayout"/>. A node outside the view is a stub, and a node
/// below 60 percent zoom shows its header only (decision D554). The node editor shortcuts that change data are off, and
/// the editor selection is the only selection (decision D558, review finding RV37). The view writes no file: the node
/// editor has no settings file, and a drag lasts for the session (decision D530).
/// </summary>
/// <remarks>
/// <para>The node editor draws its canvas at the scale 1 and restores the LibreLancer style when its canvas starts and
/// ends. So the view writes the style of the editor again after each of these calls.</para>
/// <para>The canvas is in unscaled points, so the node sizes of the model are canvas sizes. The view records the screen
/// rectangles of the rows and chips, so the cross-links of the Mission tab start at them (section 7.3).</para>
/// </remarks>
internal sealed class GraphView : IDisposable
{
    // Sizes in canvas points.
    private const float PinSize = 10;
    private const float PinInset = 2;
    private const float GlyphSize = 13;
    private const float TextSize = 14;
    private const float SmallSize = 12;
    private const float SidePadding = 16;
    private const float ChipHeight = 16;
    private const float ChipPadding = 5;
    private const float ChipGap = 4;
    private const float Rounding = 4;

    // The minimum zoom of a full node (decision D554).
    private const float FullDetailScale = 0.6f;

    private static readonly Vector4 NodeFill = EditorTheme.Rgb(0x2E2E2E);
    private static readonly Vector4 NodeHeader = EditorTheme.Rgb(0x3A3A3A);
    private static readonly Vector4 NodeBorder = EditorTheme.Rgb(0x555555);
    private static readonly Vector4 FrameFill = EditorTheme.Rgb(0xFFFFFF, 10 / 255f);
    private static readonly Vector4 FrameBorder = EditorTheme.Rgb(0xFFFFFF, 50 / 255f);
    private static readonly Vector4 PrefabFill = EditorTheme.Rgb(0xFFFFFF, 18 / 255f);
    private static readonly Vector4 ChipFill = EditorTheme.Rgb(0x4D4D4D);

    // The link greys of decision D553. No link uses a state color.
    private static readonly Vector4 LightGrey = EditorTheme.Rgb(0xC8C8C8);
    private static readonly Vector4 MidGrey = EditorTheme.Rgb(0x8C8C8C);

    private readonly EditorWindow window;
    private readonly MissionWorkspace missions;
    private readonly GraphViewState state;
    private readonly NodeEditorConfig config;
    private readonly NodeEditorContext context;

    // The node and frame keys of the node editor ids of the graph.
    private readonly Dictionary<int, string> keys = [];
    private readonly Dictionary<int, GraphBlock> blocks = [];
    private readonly List<(string Key, Vector2 Delta)> drags = [];

    // The screen rectangles of the rows and chips of this frame, for the cross-links.
    private readonly Dictionary<(string Node, int Entry), (Vector2 Min, Vector2 Max)> rowRects = [];
    private readonly Dictionary<(string Node, string Group), (Vector2 Min, Vector2 Max)> chipRects = [];

    private MissionAnalysis? builtAnalysis;
    private (int Version, bool ShowLines, bool ShowBaseGame, bool HidesOpenLancer) built;
    private bool placeNodes;
    private object? syncedSelection;

    // True in a frame where the view selected a node for a change of the editor selection. The node editor then reports
    // a change of its selection, which the view does not take back.
    private bool selectedByView;

    // The block that shows the selection in this frame, or null.
    private GraphBlock? selectedBlock;

    // The navigation of section 6.6: the focus of the path view or the neighbor view, the search and the system filter.
    private GraphPath? pathFocus;
    private MissionTrigger? neighborFocus;
    private IReadOnlySet<GraphNode>? focusNodes;
    private HashSet<GraphNode> pathNodes = [];
    private HashSet<MissionLink> pathLinks = [];
    private string searchText = "";
    private List<GraphNode> results = [];
    private int resultIndex = -1;
    private (MissionGraph? Graph, string Text) searched;
    private MissionTrigger? sourcesOf;
    private string? pendingTooltip;
    private HelpText? pendingInfo;
    private HelpText? openInfo;
    private (GraphNode? Node, GraphFrame? Frame)? menuTarget;
    private Navigation pending;
    private int pendingCenter;

    /// <summary>A view move that waits for the next frame of the node editor.</summary>
    private enum Navigation
    {
        None,
        Content,
        Center,
        Selection,

        /// <summary>Zoom in on a node, for the frame bench.</summary>
        ZoomIn,
    }

    public GraphView(EditorWindow window, MissionWorkspace missions, GraphViewState state)
    {
        this.window = window;
        this.missions = missions;
        this.state = state;
        config = new NodeEditorConfig { SettingsFile = null };
        config.SetNodeDraggedHook(Dragged);
        context = new NodeEditorContext(config);
    }

    /// <summary>The graph of the open mission, or null before the first draw.</summary>
    public MissionGraph? Graph { get; private set; }

    /// <summary>The layout of the graph, or null before the first draw.</summary>
    public GraphLayout? Layout { get; private set; }

    /// <summary>The screen rectangle of the graph part in the last frame.</summary>
    public (Vector2 Min, Vector2 Max) Screen { get; private set; }

    /// <summary>The visible canvas rectangle and the zoom of the last frame (1 at 100 percent).</summary>
    public (Vector2 Min, Vector2 Max, float Scale) View { get; private set; }

    /// <summary>The screen rectangles of the rows of the lines level in the last frame, by node key and line
    /// index.</summary>
    public IReadOnlyDictionary<(string Node, int Entry), (Vector2 Min, Vector2 Max)> RowRects => rowRects;

    /// <summary>The screen rectangles of the chips of the summary level in the last frame, by node key and action
    /// group.</summary>
    public IReadOnlyDictionary<(string Node, string Group), (Vector2 Min, Vector2 Max)> ChipRects => chipRects;

    /// <summary>The nodes that the view outlines, for example the nodes that use a selected world item (section
    /// 7.3). The Mission tab sets them in each frame.</summary>
    public IReadOnlySet<string> Outlined { get; set; } = new HashSet<string>();

    /// <summary>The system of the system filter, or null for all systems. The nodes of other systems fade.</summary>
    public string? SystemFilter { get; set; }

    /// <summary>True while the path view or the neighbor view shows. Esc ends it.</summary>
    public bool HasFocus => pathFocus != null || neighborFocus != null;

    /// <summary>Ends the path view and the neighbor view.</summary>
    public void EndFocus()
    {
        pathFocus = null;
        neighborFocus = null;
    }

    /// <summary>The text of the search of the graph (section 6.6 of the M6a design).</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (searchText != value)
            {
                searchText = value;
                resultIndex = -1;
            }
        }
    }

    /// <summary>The nodes that match the search, in file order.</summary>
    public IReadOnlyList<GraphNode> Results
    {
        get
        {
            if (Graph == null)
            {
                return [];
            }

            if (!ReferenceEquals(searched.Graph, Graph) || searched.Text != searchText)
            {
                results = GraphNavigation.Search(Graph, searchText).ToList();
                searched = (Graph, searchText);
                resultIndex = Math.Min(resultIndex, results.Count - 1);
            }

            return results;
        }
    }

    /// <summary>Frames the next result of the search (Enter), or the result before it (Shift+Enter).</summary>
    public void NextResult(int step)
    {
        var list = Results;
        if (list.Count == 0)
        {
            return;
        }

        resultIndex = ((resultIndex < 0 && step < 0 ? 0 : resultIndex) + step + list.Count) % list.Count;
        FrameBlock(list[resultIndex]);
    }

    /// <summary>Moves the view so that the whole graph shows, at the next frame.</summary>
    public void FrameAll() => pending = Navigation.Content;

    /// <summary>Moves the view to a node or a frame of the graph at the next frame.</summary>
    public void FrameBlock(GraphBlock block)
    {
        pending = Navigation.Center;
        pendingCenter = block.Id;
    }

    /// <summary>Moves the view to the selection at the next frame (the key F).</summary>
    public void FrameSelection() => pending = Navigation.Selection;

    /// <summary>
    /// A move of the view for the frame bench (<see cref="MissionBench"/>): a zoom to the whole graph each 100 frames, a
    /// zoom in on a node in the middle of each 100 frames, and a pan from node to node in the other frames.
    /// </summary>
    public void BenchStep(int frame)
    {
        if (Graph is not { Nodes.Count: > 0 } graph)
        {
            return;
        }

        var phase = frame % 100;
        pendingCenter = graph.Nodes[frame * 7 % graph.Nodes.Count].Id;
        pending = phase == 0 ? Navigation.Content : phase == 50 ? Navigation.ZoomIn : Navigation.Center;
    }

    /// <summary>Forgets the graph, for example when the profile closes.</summary>
    public void Reset()
    {
        Graph = null;
        Layout = null;
        builtAnalysis = null;
        built = default;
        keys.Clear();
        blocks.Clear();
        syncedSelection = null;
    }

    /// <summary>Builds the graph and its layout again when the analysis, the state or a toggle changed.</summary>
    private void Build(MissionAnalysis analysis)
    {
        var settings = window.Settings;
        var hides = window.Session?.Edits.HidesOpenLancer == true;
        var key = (state.Version, settings.MissionShowLines, missions.ShowBaseGame, hides);
        if (Graph != null && ReferenceEquals(builtAnalysis, analysis) && built == key)
        {
            return;
        }

        var options = state.Options(analysis.Script.Path, settings.MissionShowLines, missions.ShowBaseGame, hides,
            SystemName);
        Graph = MissionGraph.Build(analysis, options);
        Layout = GraphLayout.Of(Graph, state.Offsets(analysis.Script.Path));
        // Another mission shows the whole graph first.
        if (!string.Equals(builtAnalysis?.Script.Path, analysis.Script.Path, StringComparison.OrdinalIgnoreCase))
        {
            pending = Navigation.Content;
        }

        builtAnalysis = analysis;
        built = key;
        keys.Clear();
        blocks.Clear();
        foreach (var frame in Graph.Frames)
        {
            Register(frame);
            Register(frame.Node);
        }

        foreach (var node in Graph.Nodes)
        {
            Register(node);
        }

        placeNodes = true;
        syncedSelection = null;
    }

    private void Register(GraphBlock block)
    {
        keys[block.Id] = block.Key;
        blocks[block.Id] = block;
    }

    /// <summary>The name of a system of the game data, or null.</summary>
    private string? SystemName(string nickname) =>
        window.Session is { GameData: { } data } session && data.Items.Systems.Get(nickname) is { } system
            ? session.ItemName(system)
            : null;

    /// <summary>The node editor reports the moves of a drag. The view keeps them as session offsets.</summary>
    private void Dragged(NodeId id, float oldX, float oldY, float newX, float newY, IntPtr user)
    {
        if (keys.TryGetValue((int)(IntPtr)id, out var key))
        {
            drags.Add((key, new Vector2(newX - oldX, newY - oldY)));
        }
    }

    /// <summary>Draws the graph of an analysis into a region of the current window.</summary>
    public void Draw(MissionAnalysis analysis, Vector2 size)
    {
        Build(analysis);
        var graph = Graph!;
        var layout = Layout!;
        var origin = ImGui.GetCursorScreenPos();
        Screen = (origin, origin + size);
        rowRects.Clear();
        chipRects.Clear();
        pendingTooltip = null;
        pendingInfo = null;

        NodeEditor.SetCurrentEditor(context);
        NodeEditor.Begin("##mission-graph", size);
        EditorTheme.Reapply(1);
        NodeEditor.EnableShortcuts(false);
        if (placeNodes)
        {
            Place(graph, layout);
            placeNodes = false;
        }

        var viewMin = NodeEditor.ScreenToCanvas(origin);
        var viewMax = NodeEditor.ScreenToCanvas(origin + size);
        var zoom = NodeEditor.GetCurrentZoom();
        var scale = zoom > 0 ? 1 / zoom : 1;
        View = (viewMin, viewMax, scale);
        Focus(graph);
        selectedBlock = window.Selection is MissionRef selection &&
                        string.Equals(selection.Script, analysis.Script.Path, StringComparison.OrdinalIgnoreCase)
            ? GraphViewState.BlockOf(graph, selection)
            : null;

        foreach (var frame in OpenFrames(graph))
        {
            DrawFrame(analysis.Script.Path, frame, layout);
        }

        foreach (var node in graph.Nodes)
        {
            var (min, max) = layout.RectOf(node);
            var detail = MissionGraph.DetailOf(min, max, viewMin, viewMax, scale);
            DrawNode(analysis.Script.Path, node, detail);
        }

        foreach (var link in graph.Links)
        {
            DrawLink(link);
        }

        DrawStateChips(graph, layout, scale);
        FollowEditorSelection(analysis.Script.Path, graph);
        Navigate(graph);

        // The popups and the tooltips draw in screen space, outside the canvas.
        NodeEditor.Suspend();
        EditorTheme.Reapply(ImGuiHelper.Scale);
        Menus(analysis.Script.Path, graph);
        if (pendingTooltip != null)
        {
            ImGui.SetTooltip(pendingTooltip);
        }

        Info();
        Sources(analysis.Script.Path, graph);
        SearchResults(analysis.Script.Path);
        Minimap(graph, layout);
        NodeEditor.Resume();
        EditorTheme.Reapply(1);
        NodeEditor.End();
        EditorTheme.Reapply(ImGuiHelper.Scale);
        // The node editor takes the clicks of the user in its End, so the view reads them after it.
        TakeNodeEditorSelection(analysis.Script.Path);
        DoubleClicks(analysis.Script.Path);
        // The engine method takes null to clear the current editor.
        NodeEditor.SetCurrentEditor(null!);
        foreach (var (key, delta) in drags)
        {
            state.Dragged(analysis.Script.Path, key, delta);
        }

        drags.Clear();
    }

    /// <summary>Puts each node and each open frame that shows at its place of the layout, and gives each open frame its
    /// size.</summary>
    private static void Place(MissionGraph graph, GraphLayout layout)
    {
        foreach (var frame in OpenFrames(graph))
        {
            NodeEditor.SetNodePosition(frame.Id, layout.PositionOf(frame));
            var size = layout.SizeOf(frame);
            NodeEditor.SetGroupSize(frame.Id, new Vector2(size.X, Math.Max(1, size.Y - GraphLayout.FrameTitle)));
        }

        foreach (var node in graph.Nodes)
        {
            NodeEditor.SetNodePosition(node.Id, layout.PositionOf(node));
        }
    }

    /// <summary>The open frames that show, outer frames first, so the inner frames draw over them.</summary>
    private static IEnumerable<GraphFrame> OpenFrames(MissionGraph graph)
    {
        var stack = new Stack<GraphFrame>(graph.Acts.Where(a => !a.Collapsed).Reverse());
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            yield return frame;
            foreach (var inner in frame.Blocks.OfType<GraphFrame>().Where(f => !f.Collapsed).Reverse())
            {
                stack.Push(inner);
            }
        }
    }

    /// <summary>An open frame: a group node with its title line, its info icon and its collapse button (section 6.4 of
    /// the M6a design).</summary>
    private void DrawFrame(string script, GraphFrame frame, GraphLayout layout)
    {
        var size = layout.SizeOf(frame);
        NodeEditor.PushStyleVar(StyleVar.NodePadding, Vector4.Zero);
        NodeEditor.PushStyleColor(StyleColor.NodeBg, new Color4(frame.Kind == GraphFrameKind.Prefab ? PrefabFill : FrameFill));
        NodeEditor.PushStyleColor(StyleColor.NodeBorder, new Color4(FrameBorder));
        NodeEditor.BeginNode(frame.Id);
        ImGui.PushID(frame.Id);
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        var x = origin.X + GraphLayout.FramePadding;
        Text(drawList, font, GlyphSize, new Vector2(x, origin.Y + 6), EditorTheme.TextDim,
            MissionGlyphs.OfFrame(frame).ToString());
        var right = origin.X + size.X - GraphLayout.FramePadding;
        var buttons = right - 2 * ChipHeight - ChipGap;
        TextFit.AddText(drawList, font, TextSize, new Vector2(x + GlyphSize + 6, origin.Y + 5),
            EditorTheme.ToAbgr(EditorTheme.TextBright), frame.Title, buttons - x - GlyphSize - 6);
        TextFit.AddText(drawList, font, SmallSize, new Vector2(x + GlyphSize + 6, origin.Y + 24),
            EditorTheme.ToAbgr(EditorTheme.TextDim), frame.Detail, buttons - x - GlyphSize - 6);

        // The collapse button and the info icon at the right end of the title line.
        ImGui.SetCursorScreenPos(new Vector2(buttons, origin.Y + 5));
        if (ImGui.InvisibleButton("##collapse", new Vector2(ChipHeight)))
        {
            state.SetCollapsed(script, frame.Key, true);
        }

        HoverTip("Collapse this frame into one node.");
        Text(drawList, font, GlyphSize, ImGui.GetItemRectMin() + new Vector2(2, 0), ImGui.IsItemHovered()
            ? EditorTheme.AccentText : EditorTheme.TextDim, EditorIcons.CaretDown.ToString());
        InfoGlyph(new Vector2(right - ChipHeight, origin.Y + 5), FrameHelp(frame));

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(size.X, GraphLayout.FrameTitle));
        NodeEditor.Group(new Vector2(size.X, Math.Max(1, size.Y - GraphLayout.FrameTitle)));
        ImGui.PopID();
        NodeEditor.EndNode();
        NodeEditor.PopStyleColor(2);
        NodeEditor.PopStyleVar();
    }

    /// <summary>The help text of a frame: the prefab text, the act text, or the text of the frame "Not started by the
    /// mission".</summary>
    private static HelpText FrameHelp(GraphFrame frame) => frame.Kind switch
    {
        GraphFrameKind.Prefab => frame.Instance!.Definition.Help,
        GraphFrameKind.NotStarted => MissionSchema.Retail.Help["notStarted"],
        _ => MissionSchema.Retail.Help["act"],
    };

    /// <summary>One node at its detail level.</summary>
    private void DrawNode(string script, GraphNode node, GraphDetail detail)
    {
        var alpha = Alpha(node);
        var selected = ReferenceEquals(node, selectedBlock);
        var outlined = Outlined.Contains(node.Key) || pathNodes.Contains(node) ||
                       searchText.Length > 0 && results.Contains(node);
        var border = selected ? EditorTheme.Accent
            : outlined ? EditorTheme.AccentText
            : node.Kind == GraphNodeKind.Missing ? EditorTheme.Error with { W = 0.6f }
            : NodeBorder;
        NodeEditor.PushStyleVar(StyleVar.NodePadding, Vector4.Zero);
        NodeEditor.PushStyleVar(StyleVar.NodeBorderWidth, outlined ? 2.5f : 1f);
        NodeEditor.PushStyleColor(StyleColor.NodeBg, new Color4(NodeFill with { W = alpha }));
        NodeEditor.PushStyleColor(StyleColor.NodeBorder, new Color4(border with { W = border.W * alpha }));
        NodeEditor.BeginNode(node.Id);
        ImGui.PushID(node.Id);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
        var origin = ImGui.GetCursorScreenPos();
        Pins(node, origin, detail != GraphDetail.Stub);
        if (detail != GraphDetail.Stub)
        {
            Header(node, origin, detail == GraphDetail.Full);
        }

        if (detail == GraphDetail.Full)
        {
            switch (node.Kind)
            {
                case GraphNodeKind.Trigger when node.Lines:
                    Rows(script, node, origin);
                    break;
                case GraphNodeKind.Trigger:
                    Summary(script, node, origin);
                    break;
                default:
                    FrameBody(node, origin);
                    break;
            }
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(node.Size);
        ImGui.PopStyleVar();
        ImGui.PopID();
        NodeEditor.EndNode();
        NodeEditor.PopStyleColor(2);
        NodeEditor.PopStyleVar(2);
    }

    /// <summary>The pins: the inputs at the left edge and the outputs at the right edge, each at the line of its
    /// slot.</summary>
    private void Pins(GraphNode node, Vector2 origin, bool glyphs)
    {
        var drawList = ImGui.GetWindowDrawList();
        foreach (var pin in node.Pins)
        {
            var y = LineCenter(origin, pin.Slot) - PinSize / 2;
            var x = pin.IsInput ? origin.X + PinInset : origin.X + node.Size.X - PinSize - PinInset;
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            NodeEditor.BeginPin(pin.Id, pin.IsInput ? PinKind.Input : PinKind.Output);
            NodeEditor.PinPivotAlignment(new Vector2(pin.IsInput ? 0 : 1, 0.5f));
            ImGui.Dummy(new Vector2(PinSize));
            NodeEditor.EndPin();
            if (!glyphs)
            {
                continue;
            }

            Text(drawList, ImGuiHelper.Roboto, 9, new Vector2(x + 1, y), EditorTheme.TextDim,
                MissionGlyphs.OfPin(pin.Kind).ToString());
            if (ImGui.IsItemHovered())
            {
                pendingTooltip = PinTip(pin);
            }
        }
    }

    /// <summary>The tooltip of a pin (section 13.1 of the M6a design).</summary>
    private static string PinTip(GraphPin pin) => pin.Kind switch
    {
        GraphPinKind.Start => "Start: triggers that start this trigger.",
        GraphPinKind.Stop => "Stop: triggers that stop it.",
        GraphPinKind.Load => "Load: checkpoints that continue at it after a load.",
        GraphPinKind.Watches => "Watches: the trigger whose state the condition of this trigger waits for.",
        GraphPinKind.State => "State: conditions that watch the state of this trigger.",
        GraphPinKind.Starts => "The triggers that this trigger starts.",
        GraphPinKind.Stops => "The triggers that this trigger stops.",
        GraphPinKind.Saves => "The trigger where the mission continues after a load.",
        GraphPinKind.More => "The lines that the row \"Show more lines\" hides.",
        _ => "The trigger that this line names.",
    };

    /// <summary>The vertical center of a line of a node: 0 is the header.</summary>
    private static float LineCenter(Vector2 origin, int slot) => slot == 0
        ? origin.Y + MissionGraph.HeaderHeight / 2
        : origin.Y + MissionGraph.HeaderHeight + (slot - 1) * MissionGraph.LineHeight + MissionGraph.LineHeight / 2;

    /// <summary>The header: the glyph, the title, the chips, the marks and the info icon (section 6.2 of the M6a
    /// design).</summary>
    private void Header(GraphNode node, Vector2 origin, bool full)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        var width = node.Size.X;
        drawList.AddRectFilled(origin, origin + new Vector2(width, MissionGraph.HeaderHeight),
            EditorTheme.ToAbgr(NodeHeader), Rounding, ImDrawFlags.RoundCornersTop);
        var glyph = node.Kind switch
        {
            GraphNodeKind.Trigger => MissionGlyphs.OfEvent(node.EventGroup),
            GraphNodeKind.Missing => EditorIcons.ExclamationTriangle,
            _ => MissionGlyphs.OfFrame(node.Frame!),
        };
        var x = origin.X + SidePadding;
        var y = origin.Y + (MissionGraph.HeaderHeight - TextSize) / 2;
        Text(drawList, font, GlyphSize, new Vector2(x, y + 1), node.Kind == GraphNodeKind.Missing
            ? EditorTheme.Error : EditorTheme.Label, glyph.ToString());
        x += GlyphSize + 5;

        // The marks and the info icon take the right end. The title and the chips take the rest.
        var right = origin.X + width - SidePadding;
        if (full)
        {
            right -= ChipHeight;
            InfoGlyph(new Vector2(right, y - 1), NodeHelp(node));
            right -= ChipGap;
        }

        right = Marks(node, drawList, right, y);
        var titleWidth = TextFit.AddText(drawList, font, TextSize, new Vector2(x, y), EditorTheme.ToAbgr(EditorTheme.TextBright),
            node.Title, right - x);
        x += titleWidth + 6;
        if (!full)
        {
            return;
        }

        foreach (var chip in node.HeaderChips)
        {
            var chipWidth = font.CalcTextSizeA(SmallSize, float.MaxValue, 0, chip).X + 2 * ChipPadding;
            if (x + chipWidth > right)
            {
                break;
            }

            var min = new Vector2(x, origin.Y + (MissionGraph.HeaderHeight - ChipHeight) / 2);
            drawList.AddRectFilled(min, min + new Vector2(chipWidth, ChipHeight), EditorTheme.ToAbgr(ChipFill), 3);
            drawList.AddText(font, SmallSize, min + new Vector2(ChipPadding, 1), EditorTheme.ToAbgr(EditorTheme.Text), chip);
            if (ImGui.IsMouseHoveringRect(min, min + new Vector2(chipWidth, ChipHeight)))
            {
                pendingTooltip = HeaderChipTip(chip);
            }

            x += chipWidth + ChipGap;
        }
    }

    /// <summary>The tooltip of a chip of the header (section 13.1 of the M6a design).</summary>
    private static string HeaderChipTip(string chip) => chip switch
    {
        "Start" => "This trigger starts with the mission (InitState = ACTIVE).",
        "Repeats" => "This trigger becomes active again each time it runs (repeatable = true).",
        _ => "The system line of this trigger. The game does not use it. The editor uses it to place the trigger.",
    };

    /// <summary>The help text of the info icon of a node: the event of a trigger, or the text of its frame.</summary>
    private static HelpText NodeHelp(GraphNode node)
    {
        var schema = MissionSchema.Retail;
        if (node.Frame is { } frame)
        {
            return FrameHelp(frame);
        }

        if (node.Trigger?.Condition is { Name: { } condition })
        {
            var help = schema.Help["event"];
            return help with { Title = $"{help.Title}: {condition.Plain}" };
        }

        return schema.Help["trigger"];
    }

    /// <summary>The marks at the right end of the header: the problems, the lock glyph of the muted base-game problems,
    /// "Never starts" and "Watched state". Returns the left end of the marks.</summary>
    private float Marks(GraphNode node, ImDrawListPtr drawList, float right, float y)
    {
        var marks = new List<(char Glyph, Vector4 Color, string Tip)>();
        if (node.Errors > 0)
        {
            marks.Add((EditorIcons.TimesCircle, EditorTheme.Error, ProblemTip(node, DiagnosticSeverity.Error)));
        }

        if (node.Warnings > 0)
        {
            marks.Add((EditorIcons.ExclamationTriangle, EditorTheme.Warning, ProblemTip(node, DiagnosticSeverity.Warning)));
        }

        if (node.Muted > 0)
        {
            marks.Add((EditorIcons.Lock, EditorTheme.TextDim, "Base-game messages:\n" + string.Join("\n",
                node.Problems.Where(p => p.Origin?.IsBaseGame == true).Select(p => p.Message))));
        }

        if (node.NeverStarts)
        {
            marks.Add((EditorIcons.Ban, EditorTheme.TextDim, "Never starts. No trigger starts this trigger, and it is " +
                                                             "not a start trigger or a checkpoint target. So the game " +
                                                             "never runs it."));
        }

        if (node.WatchedState)
        {
            marks.Add((EditorIcons.Eye, EditorTheme.TextDim, "Watched state. This trigger has a condition and no " +
                                                             "actions. Other triggers watch its state."));
        }

        foreach (var (glyph, color, tip) in marks)
        {
            right -= GlyphSize + 3;
            var min = new Vector2(right, y);
            Text(drawList, ImGuiHelper.Roboto, GlyphSize, min, color, glyph.ToString());
            if (ImGui.IsMouseHoveringRect(min, min + new Vector2(GlyphSize)))
            {
                pendingTooltip = tip;
            }
        }

        return right - 4;
    }

    private static string ProblemTip(GraphNode node, DiagnosticSeverity severity) =>
        string.Join("\n", node.Problems.Where(p => p.Severity == severity && p.Origin?.IsBaseGame != true)
            .Select(p => p.Message));

    /// <summary>The summary level: the event sentence, the chips, and the OpenLancer line.</summary>
    private void Summary(string script, GraphNode node, Vector2 origin)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        var x = origin.X + SidePadding;
        var width = node.Size.X - 2 * SidePadding;
        var line1 = new Vector2(x, LineCenter(origin, 1) - TextSize / 2);
        TextFit.AddText(drawList, font, TextSize, line1, EditorTheme.ToAbgr(EditorTheme.Text), node.Sentence, width);
        if (ImGui.IsMouseHoveringRect(line1, line1 + new Vector2(width, TextSize)))
        {
            pendingTooltip = node.Sentence;
        }

        Chips(script, node, new Vector2(x, LineCenter(origin, 2) - ChipHeight / 2), width);
        if (node.TargetLine is { } target)
        {
            var line3 = new Vector2(x, LineCenter(origin, 3) - SmallSize / 2);
            Text(drawList, font, SmallSize, line3, EditorTheme.TextDim, EditorIcons.Wrench.ToString());
            TextFit.AddText(drawList, font, SmallSize, line3 + new Vector2(SmallSize + 4, 0),
                EditorTheme.ToAbgr(EditorTheme.TextDim), target, width - SmallSize - 4);
        }
    }

    /// <summary>The chips of one line: the action groups, then the flow chips. The chips that do not fit go into a
    /// last chip "+{n}".</summary>
    private void Chips(string script, GraphNode node, Vector2 start, float width)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        var x = start.X;
        var right = start.X + width;
        var more = font.CalcTextSizeA(SmallSize, float.MaxValue, 0, "+99").X + 2 * ChipPadding;
        for (var i = 0; i < node.Chips.Count; i++)
        {
            var chip = node.Chips[i];
            var glyph = chip.Group != null ? MissionGlyphs.OfGroup(chip.Group) : EditorIcons.Sync;
            var label = $"{glyph} {chip.Text}";
            var chipWidth = font.CalcTextSizeA(SmallSize, float.MaxValue, 0, label).X + 2 * ChipPadding;
            var last = i == node.Chips.Count - 1;
            if (x + chipWidth > right - (last ? 0 : more + ChipGap))
            {
                var rest = node.Chips.Count - i;
                var restText = $"+{rest.ToString(CultureInfo.InvariantCulture)}";
                var restMin = new Vector2(x, start.Y);
                var restMax = restMin + new Vector2(font.CalcTextSizeA(SmallSize, float.MaxValue, 0, restText).X +
                                                    2 * ChipPadding, ChipHeight);
                drawList.AddRectFilled(restMin, restMax, EditorTheme.ToAbgr(ChipFill), 3);
                drawList.AddText(font, SmallSize, restMin + new Vector2(ChipPadding, 1), EditorTheme.ToAbgr(EditorTheme.Text),
                    restText);
                if (ImGui.IsMouseHoveringRect(restMin, restMax))
                {
                    pendingTooltip = string.Join("\n\n", node.Chips.Skip(i).Select(c => $"{c.Text}\n{c.Tooltip}"));
                }

                for (var j = i; j < node.Chips.Count; j++)
                {
                    RecordChip(node, node.Chips[j], restMin, restMax);
                }

                return;
            }

            var min = new Vector2(x, start.Y);
            var max = min + new Vector2(chipWidth, ChipHeight);
            var color = chip.Warning ? EditorTheme.Warning : EditorTheme.Text;
            drawList.AddRectFilled(min, max, EditorTheme.ToAbgr(ChipFill), 3);
            drawList.AddText(font, SmallSize, min + new Vector2(ChipPadding, 1), EditorTheme.ToAbgr(color), label);
            if (ImGui.IsMouseHoveringRect(min, max))
            {
                pendingTooltip = chip.Tooltip;
                // A double-click on a chip frames the world items of the trigger in the Scene part.
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    missions.FrameInScene(new MissionRef(script, MissionItemKind.Trigger, node.Trigger!.Section.Ref));
                }
            }

            RecordChip(node, chip, min, max);
            x += chipWidth + ChipGap;
        }
    }

    private void RecordChip(GraphNode node, GraphChip chip, Vector2 min, Vector2 max)
    {
        if (chip.Group != null)
        {
            chipRects[(node.Key, chip.Group)] = (NodeEditor.CanvasToScreen(min), NodeEditor.CanvasToScreen(max));
        }
    }

    /// <summary>The lines level: one row for each line, with its glyph, text, link chip and OpenLancer mark. A click on
    /// a row selects the line. The last row opens the rows that the limit hides.</summary>
    private void Rows(string script, GraphNode node, Vector2 origin)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        var x = origin.X + SidePadding;
        var width = node.Size.X - 2 * SidePadding;
        var selectedRow = window.Selection is MissionRef { Kind: MissionItemKind.Row } row &&
                          node.Trigger != null && Equals(row.Section, node.Trigger.Section.Ref)
            ? row.Row
            : -1;
        for (var i = 0; i < node.Rows.Count; i++)
        {
            var graphRow = node.Rows[i];
            var min = new Vector2(origin.X + PinSize, origin.Y + MissionGraph.HeaderHeight + i * MissionGraph.LineHeight);
            var max = new Vector2(origin.X + node.Size.X - PinSize, min.Y + MissionGraph.LineHeight);
            ImGui.SetCursorScreenPos(min);
            var rowRef = new MissionRef(script, MissionItemKind.Row, node.Trigger!.Section.Ref, Row: graphRow.Line.Entry);
            if (ImGui.InvisibleButton($"##row{i}", max - min))
            {
                window.Selection = rowRef;
            }

            var hovered = ImGui.IsItemHovered();
            // A double-click on a row frames its world items in the Scene part (section 7.3 of the M6a design).
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                missions.FrameInScene(rowRef);
            }
            if (graphRow.Line.Entry == selectedRow || hovered)
            {
                drawList.AddRectFilled(min, max, EditorTheme.ToAbgr(graphRow.Line.Entry == selectedRow
                    ? EditorTheme.AccentStrong : EditorTheme.ToolbarHover));
            }

            var textY = min.Y + (MissionGraph.LineHeight - SmallSize) / 2;
            var color = graphRow.Kind is GraphRowKind.BeforeNickname or GraphRowKind.IgnoredCondition
                ? EditorTheme.TextDim
                : EditorTheme.Text;
            var glyph = graphRow.Kind == GraphRowKind.Action
                ? MissionGlyphs.OfGroup(graphRow.Line.Name?.Group)
                : MissionGlyphs.OfEvent(graphRow.Line.Name?.Group);
            Text(drawList, font, SmallSize, new Vector2(x, textY), ProblemColor(graphRow) ?? EditorTheme.TextDim,
                glyph.ToString());
            var textX = x + SmallSize + 4;
            var right = x + width;
            if (graphRow.Mark is { } mark)
            {
                var markWidth = font.CalcTextSizeA(SmallSize, float.MaxValue, 0, mark).X;
                right -= markWidth + 6;
                drawList.AddText(font, SmallSize, new Vector2(right + 6, textY), EditorTheme.ToAbgr(EditorTheme.TextDim), mark);
            }

            if (graphRow.Links.Count > 0)
            {
                var link = graphRow.Links[0];
                var linkWidth = Math.Min(font.CalcTextSizeA(SmallSize, float.MaxValue, 0, link.Text).X + 2 * ChipPadding, width / 2);
                right -= linkWidth + 4;
                var linkMin = new Vector2(right + 4, min.Y + (MissionGraph.LineHeight - ChipHeight) / 2);
                var linkMax = linkMin + new Vector2(linkWidth, ChipHeight);
                ImGui.SetCursorScreenPos(linkMin);
                if (ImGui.InvisibleButton($"##link{i}", linkMax - linkMin))
                {
                    window.Selection = LinkRef(script, node, graphRow, link);
                }

                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    missions.FrameInScene(LinkRef(script, node, graphRow, link));
                }

                drawList.AddRectFilled(linkMin, linkMax, EditorTheme.ToAbgr(ChipFill), 3);
                TextFit.AddText(drawList, font, SmallSize, linkMin + new Vector2(ChipPadding, 1),
                    EditorTheme.ToAbgr(ImGui.IsItemHovered() ? EditorTheme.AccentText : EditorTheme.Text), link.Text,
                    linkWidth - 2 * ChipPadding);
                if (ImGui.IsItemHovered())
                {
                    pendingTooltip = $"{link.Text}. Click to select it.";
                }
            }

            var textWidth = TextFit.AddText(drawList, font, SmallSize, new Vector2(textX, textY), EditorTheme.ToAbgr(color),
                graphRow.Text, right - textX);
            if (graphRow.Kind == GraphRowKind.IgnoredCondition)
            {
                // The game ignores a condition before the last one (TR1), so the row is struck through.
                var strikeY = textY + SmallSize / 2 + 1;
                drawList.AddLine(new Vector2(textX, strikeY), new Vector2(textX + textWidth, strikeY),
                    EditorTheme.ToAbgr(EditorTheme.TextDim));
            }

            if (hovered && pendingTooltip == null)
            {
                pendingTooltip = RowTip(node, graphRow);
            }

            rowRects[(node.Key, graphRow.Line.Entry)] = (NodeEditor.CanvasToScreen(min), NodeEditor.CanvasToScreen(max));
        }

        if (node.HiddenRows > 0)
        {
            var min = new Vector2(origin.X + PinSize, origin.Y + MissionGraph.HeaderHeight +
                                                      node.Rows.Count * MissionGraph.LineHeight);
            var max = new Vector2(origin.X + node.Size.X - PinSize, min.Y + MissionGraph.LineHeight);
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton("##more", max - min))
            {
                state.ShowAllRows(script, node.Key);
            }

            var text = $"Show {node.HiddenRows.ToString(CultureInfo.InvariantCulture)} more lines";
            drawList.AddText(font, SmallSize, new Vector2(x, min.Y + (MissionGraph.LineHeight - SmallSize) / 2),
                EditorTheme.ToAbgr(ImGui.IsItemHovered() ? EditorTheme.AccentText : EditorTheme.TextDim), text);
        }
    }

    /// <summary>The color of the glyph of a row with a problem that shows, or null.</summary>
    private static Vector4? ProblemColor(GraphRow row)
    {
        if (row.Problems.Any(p => p.Severity == DiagnosticSeverity.Error && p.Origin?.IsBaseGame != true))
        {
            return EditorTheme.Error;
        }

        if (row.Problems.Any(p => p.Origin?.IsBaseGame != true))
        {
            return EditorTheme.Warning;
        }

        return null;
    }

    /// <summary>The tooltip of a row: its text, its problems and its OpenLancer mark.</summary>
    private static string RowTip(GraphNode node, GraphRow row)
    {
        var lines = new List<string> { row.Text };
        if (row.Kind == GraphRowKind.BeforeNickname)
        {
            lines.Add("The game ignores this line, because it comes before the nickname.");
        }
        else if (row.Kind == GraphRowKind.IgnoredCondition)
        {
            lines.Add("The game uses only the last condition of a trigger.");
        }

        lines.AddRange(row.Problems.Select(p => (p.Origin?.IsBaseGame == true ? "Base game: " : "") + p.Message));
        if (row.Mark is { } mark)
        {
            lines.Add(mark.EndsWith("cannot load", StringComparison.Ordinal)
                ? "OpenLancer stops when it loads this line. The retail game runs it."
                : "OpenLancer reads this line but does nothing with it. The retail game runs it.");
        }

        lines.Add($"Line {(row.Line.Entry + 1).ToString(CultureInfo.InvariantCulture)} of trigger {node.Title}.");
        return string.Join("\n", lines);
    }

    /// <summary>The selection of a link chip of a row: the item of a section, a label, or the row of a file
    /// link.</summary>
    private static MissionRef LinkRef(string script, GraphNode node, GraphRow row, GraphRowLink link)
    {
        if (link.Section is { } section)
        {
            return MissionWorkspace.RefOf(section.Ref, -1);
        }

        return link.Kind == "Label"
            ? new MissionRef(script, MissionItemKind.Label, Key: link.Name)
            : new MissionRef(script, MissionItemKind.Row, node.Trigger!.Section.Ref, Row: row.Line.Entry);
    }

    /// <summary>The body of a collapsed frame: the walkthrough line of an act, or the fields of a prefab in short form
    /// and its completeness.</summary>
    private void FrameBody(GraphNode node, Vector2 origin)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        var x = origin.X + SidePadding;
        var width = node.Size.X - 2 * SidePadding;
        var lines = new List<(string Text, Vector4 Color)>();
        if (node.Kind is GraphNodeKind.Act or GraphNodeKind.Missing)
        {
            lines.Add((node.Sentence, EditorTheme.TextDim));
        }

        lines.AddRange(node.Fields.Select(f => (f, EditorTheme.Text)));
        if (node.Completeness is { } completeness)
        {
            lines.Add((completeness, EditorTheme.Warning));
        }

        for (var i = 0; i < lines.Count && i < node.BodyLines; i++)
        {
            var position = new Vector2(x, LineCenter(origin, i + 1) - SmallSize / 2);
            TextFit.AddText(drawList, font, SmallSize, position, EditorTheme.ToAbgr(lines[i].Color), lines[i].Text, width);
            if (ImGui.IsMouseHoveringRect(position, position + new Vector2(width, SmallSize)))
            {
                pendingTooltip = lines[i].Text;
            }
        }
    }

    /// <summary>One link: a solid curve in a neutral grey. The target pin tells its kind (decision D553).</summary>
    private void DrawLink(GraphLink link)
    {
        var (color, thickness) = link.Kind switch
        {
            MissionLinkKind.Start => (LightGrey, 1.5f),
            MissionLinkKind.Load => (LightGrey, 2f),
            _ => (MidGrey, 1f),
        };
        var faded = Alpha(link.From) < 1 || Alpha(link.To) < 1;
        // The links of the path view have a bold line.
        if (link.Sources.Any(pathLinks.Contains))
        {
            thickness *= 2.5f;
            faded = false;
        }

        NodeEditor.Link(link.Id, link.FromPin.Id, link.ToPin.Id,
            (VertexDiffuse)new Color4(color with { W = faded ? MissionGraph.FadeAlpha : 1 }), thickness);
    }

    /// <summary>The state chips of the watch links: "Is active", "Is not active", "Is inactive" or "Has run", at the
    /// target pin, left of the node of the watcher.</summary>
    private static void DrawStateChips(MissionGraph graph, GraphLayout layout, float scale)
    {
        if (scale < FullDetailScale)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        foreach (var link in graph.Links.Where(l => l.State != null))
        {
            var origin = layout.PositionOf(link.To);
            var y = LineCenter(origin, link.ToPin.Slot) - ChipHeight / 2;
            var width = font.CalcTextSizeA(SmallSize, float.MaxValue, 0, link.State!).X + 2 * ChipPadding;
            var min = new Vector2(origin.X - width - 6, y);
            drawList.AddRectFilled(min, min + new Vector2(width, ChipHeight), EditorTheme.ToAbgr(ChipFill), 3);
            drawList.AddText(font, SmallSize, min + new Vector2(ChipPadding, 1), EditorTheme.ToAbgr(EditorTheme.Text),
                link.State!);
        }
    }

    /// <summary>The selection of the editor shows in the node editor: one ClearSelection and SelectNode for each change
    /// of the editor selection (section 6.6 of the M6a design).</summary>
    private void FollowEditorSelection(string script, MissionGraph graph)
    {
        var selection = window.Selection;
        if (Equals(selection, syncedSelection))
        {
            return;
        }

        syncedSelection = selection;
        selectedByView = true;
        NodeEditor.ClearSelection();
        if (selection is MissionRef item && string.Equals(item.Script, script, StringComparison.OrdinalIgnoreCase) &&
            GraphViewState.BlockOf(graph, item) is { } block)
        {
            NodeEditor.SelectNode(block.Id);
        }
    }

    /// <summary>A click on a node selects its item. A click on the background selects the mission.</summary>
    private void TakeNodeEditorSelection(string script)
    {
        // The change that the view made for the editor selection is not a click of the user.
        var byView = selectedByView;
        selectedByView = false;
        if (NodeEditor.IsBackgroundClicked())
        {
            window.Selection = MissionRef.Of(script);
            syncedSelection = window.Selection;
            return;
        }

        if (!NodeEditor.HasSelectionChanged() || byView)
        {
            return;
        }

        Span<NodeId> selected = stackalloc NodeId[2];
        var count = NodeEditor.GetSelectedNodes(selected);
        if (count != 1 || !blocks.TryGetValue((int)(IntPtr)selected[0], out var block))
        {
            return;
        }

        var item = block switch
        {
            GraphNode node => GraphViewState.RefOf(script, node),
            GraphFrame frame => GraphViewState.RefOf(script, frame),
            _ => null,
        };
        // A click on a row of the node already selected its line, so the node does not take it back.
        if (item != null && !(window.Selection is MissionRef { Kind: MissionItemKind.Row } row && Equals(row.Section, item.Section)))
        {
            window.Selection = item;
        }

        syncedSelection = window.Selection;
    }

    /// <summary>A double-click on a collapsed frame opens it. A double-click on a trigger frames its world items in the
    /// Scene part (section 6.6 of the M6a design).</summary>
    private void DoubleClicks(string script)
    {
        var id = (int)(IntPtr)NodeEditor.GetDoubleClickedNode();
        if (id == 0 || !blocks.TryGetValue(id, out var block))
        {
            return;
        }

        switch (block)
        {
            case GraphNode { Frame: { } frame }:
                state.SetCollapsed(script, frame.Key, false);
                window.Selection = GraphViewState.RefOf(script, frame);
                break;
            case GraphNode { Trigger: { } trigger }:
                missions.FrameInScene(new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref));
                break;
        }
    }

    /// <summary>Runs a view move that waits for this frame.</summary>
    private void Navigate(MissionGraph graph)
    {
        switch (pending)
        {
            case Navigation.Content:
                NodeEditor.NavigateToContent(0);
                break;
            case Navigation.Center when blocks.ContainsKey(pendingCenter):
                NodeEditor.CenterNodeOnScreen(pendingCenter);
                break;
            case Navigation.Selection:
                if (window.Selection is MissionRef item && GraphViewState.BlockOf(graph, item) is { } block)
                {
                    NodeEditor.CenterNodeOnScreen(block.Id);
                }

                break;
            case Navigation.ZoomIn when blocks.ContainsKey(pendingCenter):
                NodeEditor.SelectNode(pendingCenter);
                NodeEditor.NavigateToSelection(true, 0);
                break;
        }

        pending = Navigation.None;
    }

    /// <summary>The node menu and the frame menu (section 13.1 of the M6a design).</summary>
    private void Menus(string script, MissionGraph graph)
    {
        if (NodeEditor.ShowNodeContextMenu(out var nodeId) && blocks.TryGetValue((int)(IntPtr)nodeId, out var block))
        {
            menuTarget = block switch
            {
                GraphNode { Frame: { } frame } node => (node, frame),
                GraphNode node => (node, null),
                GraphFrame frame => (null, frame),
                _ => null,
            };
            ImGui.OpenPopup("##graph-menu");
        }

        if (!ImGui.BeginPopup("##graph-menu"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        if (menuTarget is { } target)
        {
            var (node, frame) = target;
            if (frame != null)
            {
                FrameMenu(script, frame);
            }
            else if (node?.Trigger is { } trigger)
            {
                NodeMenu(script, node, trigger);
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    private void FrameMenu(string script, GraphFrame frame)
    {
        if (ImGui.MenuItem(frame.Collapsed ? "Open" : "Collapse"))
        {
            state.SetCollapsed(script, frame.Key, !frame.Collapsed);
        }

        if (frame.Instance != null && ImGui.MenuItem("Select the prefab"))
        {
            window.Selection = GraphViewState.RefOf(script, frame);
        }
    }

    private void NodeMenu(string script, GraphNode node, MissionTrigger trigger)
    {
        var lines = state.ShowsLines(script, node.Key);
        if (ImGui.MenuItem("Show the lines of this trigger", "", lines))
        {
            state.SetLines(script, node.Key, !lines);
        }

        if (ImGui.MenuItem("Show the path from a start trigger"))
        {
            neighborFocus = null;
            pathFocus = GraphNavigation.PathFromStart(Graph!.Index, trigger) ?? new GraphPath([trigger], []);
        }

        if (ImGui.MenuItem("List what starts or stops this trigger"))
        {
            sourcesOf = trigger;
        }

        if (ImGui.MenuItem("Show only this trigger and its neighbors"))
        {
            pathFocus = null;
            neighborFocus = trigger;
        }

        if (ImGui.MenuItem("Frame in the Scene"))
        {
            missions.FrameInScene(new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref));
        }

        if (ImGui.MenuItem("Show in the raw INI"))
        {
            missions.ShowRaw = true;
            missions.PendingLine = (trigger.Section.Ref, -1);
        }

        if (ImGui.MenuItem("Copy nickname"))
        {
            ImGui.SetClipboardText(trigger.Nickname);
        }
    }

    /// <summary>The focus sets of this frame: the nodes and links of the path view, or the nodes of the neighbor
    /// view.</summary>
    private void Focus(MissionGraph graph)
    {
        pathNodes = pathFocus?.Triggers.Select(graph.NodeOf).OfType<GraphNode>().ToHashSet() ?? [];
        pathLinks = pathFocus?.Links.ToHashSet() ?? [];
        focusNodes = pathFocus != null ? pathNodes
            : neighborFocus != null && graph.NodeOf(neighborFocus) is { } node ? GraphNavigation.Neighbors(graph, node)
            : null;
        _ = Results;
    }

    /// <summary>The opacity of a node: the fade of the lens, of the system filter and of the path or neighbor
    /// view.</summary>
    private float Alpha(GraphNode node)
    {
        if (focusNodes != null)
        {
            return focusNodes.Contains(node) ? 1 : MissionGraph.FadeAlpha;
        }

        if (node.Faded || SystemFilter != null && GraphNavigation.OutsideSystem(node, SystemFilter))
        {
            return MissionGraph.FadeAlpha;
        }

        return 1;
    }

    /// <summary>The list of the node menu "List what starts or stops this trigger": the sources of the pins Start,
    /// Stop, Load and Watches. A click frames the source.</summary>
    private void Sources(string script, MissionGraph graph)
    {
        if (sourcesOf != null)
        {
            ImGui.OpenPopup("##graph-sources");
        }

        var trigger = sourcesOf;
        sourcesOf = null;
        if (trigger != null)
        {
            openSources = trigger;
        }

        if (openSources == null || !ImGui.BeginPopup("##graph-sources"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        var sources = GraphNavigation.Sources(graph.Index, openSources);
        ImGui.TextDisabled($"What starts or stops {openSources.Nickname}");
        ImGui.Separator();
        foreach (var (title, links) in new[]
                 {
                     ("Start", sources.Start), ("Stop", sources.Stop), ("Load", sources.Load), ("Watches", sources.Watches),
                 })
        {
            foreach (var link in links)
            {
                var other = title == "Watches" ? link.Target : link.Source;
                var name = other?.Nickname ?? $"Missing: {link.TargetName}";
                var line = (link.Line.Entry + 1).ToString(CultureInfo.InvariantCulture);
                if (ImGui.Selectable($"{title}: {name} (line {line} of {link.Source.Nickname})##{title}{link.Line.Entry}{link.Source.Section.Index}") &&
                    other != null && graph.NodeOf(other) is { } node)
                {
                    FrameBlock(node);
                    window.Selection = new MissionRef(script, MissionItemKind.Trigger, other.Section.Ref);
                }
            }
        }

        if (!sources.All.Any())
        {
            ImGui.TextDisabled("No trigger starts, stops, loads or watches this trigger.");
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    private MissionTrigger? openSources;

    /// <summary>The results of the search under the search field, at the top-left of the graph: the count and the first
    /// results. A click frames a result.</summary>
    private void SearchResults(string script)
    {
        if (searchText.Trim().Length == 0)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var list = Results;
        var drawList = ImGui.GetWindowDrawList();
        var min = Screen.Min + new Vector2(8, 8) * scale;
        var shown = Math.Min(list.Count, 10);
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var size = new Vector2(280 * scale, (shown + 1) * lineHeight + 8 * scale);
        drawList.AddRectFilled(min, min + size, EditorTheme.ToAbgr(EditorTheme.Panel with { W = 0.95f }), 4 * scale);
        ImGui.SetCursorScreenPos(min + new Vector2(6, 4) * scale);
        ImGui.TextDisabled(list.Count == 0 ? "No trigger matches the search."
            : list.Count == 1 ? "1 result" : $"{list.Count.ToString(CultureInfo.InvariantCulture)} results");
        for (var i = 0; i < shown; i++)
        {
            ImGui.SetCursorScreenPos(min + new Vector2(6 * scale, 4 * scale + (i + 1) * lineHeight));
            if (ImGui.Selectable($"{list[i].Title}##result{i}", i == resultIndex, ImGuiSelectableFlags.None,
                    new Vector2(size.X - 12 * scale, 0)))
            {
                resultIndex = i;
                FrameBlock(list[i]);
                if (GraphViewState.RefOf(script, list[i]) is { } item)
                {
                    window.Selection = item;
                }
            }
        }
    }

    /// <summary>The minimap at the bottom right of the graph: the node rectangles and the view rectangle. A click centers
    /// the nearest node (section 6.6 of the M6a design).</summary>
    private void Minimap(MissionGraph graph, GraphLayout layout)
    {
        if (graph.Nodes.Count == 0)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var size = new Vector2(200, 130) * scale;
        var max = Screen.Max - new Vector2(8, 8) * scale;
        var min = max - size;
        if (min.X < Screen.Min.X || min.Y < Screen.Min.Y)
        {
            return;
        }

        var map = new GraphMinimap(layout.Bounds, (min + new Vector2(4) * scale, max - new Vector2(4) * scale));
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, EditorTheme.ToAbgr(EditorTheme.Backdrop with { W = 0.85f }), 4 * scale);
        drawList.AddRect(min, max, EditorTheme.ToAbgr(EditorTheme.Border), 4 * scale);
        foreach (var node in graph.Nodes)
        {
            var (a, b) = layout.RectOf(node);
            var color = ReferenceEquals(node, selectedBlock) ? EditorTheme.Accent
                : Alpha(node) < 1 ? EditorTheme.TextDim with { W = 0.3f }
                : EditorTheme.TextDim;
            drawList.AddRectFilled(map.ToScreen(a), Vector2.Max(map.ToScreen(b), map.ToScreen(a) + Vector2.One),
                EditorTheme.ToAbgr(color));
        }

        drawList.PushClipRect(min, max, true);
        drawList.AddRect(map.ToScreen(View.Min), map.ToScreen(View.Max), EditorTheme.ToAbgr(EditorTheme.AccentText));
        drawList.PopClipRect();
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton("##minimap", size) &&
            GraphNavigation.Nearest(graph, layout, map.ToCanvas(ImGui.GetMousePos())) is { } nearest)
        {
            FrameBlock(nearest);
        }

        ImGui.SetItemTooltip("Click to center the nearest trigger.");
    }

    /// <summary>A small info glyph in the canvas. A hover shows the first sentence, and a click opens the popup of the
    /// help text outside the canvas.</summary>
    private void InfoGlyph(Vector2 position, HelpText help)
    {
        // Each node and frame has its own id scope, so one id is enough.
        ImGui.SetCursorScreenPos(position);
        if (ImGui.InvisibleButton("##info", new Vector2(ChipHeight)))
        {
            pendingInfo = help;
        }

        var hovered = ImGui.IsItemHovered();
        Text(ImGui.GetWindowDrawList(), ImGuiHelper.Roboto, GlyphSize, position + new Vector2(2, 1),
            hovered ? EditorTheme.AccentText : EditorTheme.TextDim, EditorIcons.InfoCircle.ToString());
        if (hovered)
        {
            pendingTooltip = help.FirstSentence;
        }
    }

    /// <summary>The popup of the info glyph that the user clicked, in screen space.</summary>
    private void Info()
    {
        ImGui.PushID("graph-info");
        if (pendingInfo != null)
        {
            openInfo = pendingInfo;
            ImGui.OpenPopup(InfoIcon.PopupId);
        }

        if (openInfo != null)
        {
            InfoIcon.Popup(openInfo);
        }

        ImGui.PopID();
    }

    /// <summary>A tooltip of a hovered item in the canvas. The view shows it outside the canvas.</summary>
    private void HoverTip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            pendingTooltip = text;
        }
    }

    private static void Text(ImDrawListPtr drawList, ImFontPtr font, float size, Vector2 position, Vector4 color, string text) =>
        drawList.AddText(font, size, position, EditorTheme.ToAbgr(color), text);

    public void Dispose()
    {
        context.Dispose();
        config.Dispose();
    }
}

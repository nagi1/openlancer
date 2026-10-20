using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The cross-highlighting of the Mission tab (section 7.3 of the M6a design, UI rule 6). From a node to the world, the
/// Scene highlights the items that the lines of the selected trigger, row or prefab name. Connector lines go from the
/// rows or chips to them. From the world to the graph, the graph outlines the triggers that use the selected world
/// item. The item is an item of the mission, an object, the base of a docking object, or a zone. A bar counts them with
/// "Previous" and "Next". The class also has the banner of an item in another system (section 7.4). It also frames the
/// world items of a node in the Scene part.
/// </summary>
/// <remarks><see cref="CrossLinks"/> gives the sets and the line ends. This class keeps them for the selection and
/// draws them. It makes the sets and the connector lines again only when the selection, the analysis, the graph or the
/// open system changes. So a frame without a change only projects the ends of the lines.</remarks>
internal sealed class MissionLinks(EditorWindow window, MissionWorkspace missions, GraphView graph)
{
    // Sizes in unscaled points.
    private const float ArrowSize = 7;
    private const float ChipPadding = 5;
    private const float ClickDistance = 4;

    private static readonly Vector4 LineColor = EditorTheme.MissionItem with { W = 0.8f };
    private static readonly IReadOnlySet<MissionRef> NoItems = new HashSet<MissionRef>();
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>();

    private (object? Selection, MissionAnalysis? Analysis) cached;
    private IReadOnlyList<CrossLinkTarget> targets = [];
    private Uses? uses;
    private int usesIndex = -1;
    private IReadOnlyList<(ConnectorLine Line, CrossLinkTarget Target)> lines = [];

    // The connector lines of the targets in the shown graph.
    private (MissionGraph? Graph, IReadOnlyList<CrossLinkTarget>? Targets, IReadOnlyList<CrossLinkLine> Lines) connectors =
        (null, null, []);

    // The sets that the views got, and what they came from.
    private (IReadOnlyList<CrossLinkTarget>? Targets, SystemIndex? Index, MissionGraph? Graph, Uses? Uses) applied;
    private IReadOnlySet<MissionRef> highlightItems = NoItems;
    private IReadOnlyCollection<object> highlightObjects = [];
    private IReadOnlySet<string> outlined = NoKeys;

    // The request of the Scene frame for which the Scene opened another system, or null.
    private MissionRef? openedFor;

    /// <summary>The world item of the uses bar: an item of the mission, or the nicknames of a world item. It has the
    /// lines and the triggers that use it.</summary>
    /// <param name="Item">The item of the mission, or null for a world item of a system.</param>
    /// <param name="Nicknames">The nicknames of a world item of a system, or none for an item of the mission.</param>
    private sealed record Uses(MissionRef? Item, IReadOnlyList<string> Nicknames, string Name,
        IReadOnlyList<MissionLine> Lines, IReadOnlyList<MissionTrigger> Triggers);

    /// <summary>Takes the selection of this frame: the targets of a node, or the users of a world item. The sets change
    /// only when the selection or the analysis changes.</summary>
    public void Update(MissionAnalysis analysis)
    {
        var selection = window.Selection;
        if (!Equals(cached.Selection, selection) || !ReferenceEquals(cached.Analysis, analysis))
        {
            // Previous and Next select a line of a trigger that uses the item, and the bar stays for that line. A new
            // analysis has new triggers, so the bar does not stay.
            var keepBar = uses != null && ReferenceEquals(cached.Analysis, analysis) &&
                          selection is MissionRef { Kind: MissionItemKind.Trigger or MissionItemKind.Row } chosen &&
                          uses.Triggers.Any(t => Equals(t.Section.Ref, chosen.Section));
            cached = (selection, analysis);
            targets = [];
            if (!keepBar)
            {
                uses = null;
                usesIndex = -1;
            }

            if (selection is MissionRef item && string.Equals(item.Script, analysis.Script.Path, StringComparison.OrdinalIgnoreCase))
            {
                targets = CrossLinks.Targets(analysis, CrossLinks.SourceLines(analysis, item, graph.Graph?.ActsOf()), ObjectSystem);
                // A trigger, a line, a prefab, an act and the mission are not world items, so they get no bar.
                if (!keepBar && item.Kind is not (MissionItemKind.Trigger or MissionItemKind.Row or
                        MissionItemKind.PrefabInstance or MissionItemKind.Act or MissionItemKind.Mission))
                {
                    var lineList = CrossLinks.LinesUsing(analysis, item);
                    uses = new Uses(item, [], window.Scene.MissionItems.Label(item) ?? item.Key ?? SectionName(analysis, item),
                        lineList, MissionUses.TriggersOf(lineList));
                }
            }
            else if (!keepBar && WorldNames(selection) is var (nicknames, name))
            {
                var lineList = CrossLinks.LinesNaming(analysis, nicknames);
                uses = new Uses(null, nicknames, name, lineList, MissionUses.TriggersOf(lineList));
            }
        }

        Apply();
    }

    /// <summary>The nicknames and the name of a world item of the Scene: an object and the base of its dock, or a zone.
    /// Null for another selection.</summary>
    private (IReadOnlyList<string> Nicknames, string Name)? WorldNames(object? selection) => selection switch
    {
        SystemObject obj => (obj.Base?.Nickname is { } dock ? [obj.Nickname, dock] : [obj.Nickname], NameOf(obj, obj.Nickname)),
        Zone zone => ([zone.Nickname], NameOf(zone, zone.Nickname)),
        _ => null,
    };

    private string NameOf(object item, string nickname) =>
        window.Session?.ItemName(item) is { Length: > 0 } name ? name : nickname;

    /// <summary>The system of an object of a system file, from the nicknames of the edit view, or null.</summary>
    private string? ObjectSystem(string nickname) => window.Session?.Edits.SystemOfObject(nickname);

    private static string SectionName(MissionAnalysis analysis, MissionRef item) =>
        item.Section is { } section && analysis.Document.Find(section) is { } found
            ? found.Nickname ?? section.Id.ToString()
            : "the item";

    /// <summary>Gives the sets to the views: the outlined nodes of the graph, and the highlighted items of the Scene.
    /// The sets change only when the targets, the open system, the graph or the uses change. A highlighted item of the
    /// mission gets brackets too, as a highlighted object does.</summary>
    private void Apply()
    {
        var index = window.Scene.Index;
        var shown = graph.Graph;
        if (!ReferenceEquals(applied.Targets, targets) || !ReferenceEquals(applied.Index, index) ||
            !ReferenceEquals(applied.Graph, shown) || !ReferenceEquals(applied.Uses, uses))
        {
            applied = (targets, index, shown, uses);
            var (items, objects) = CrossLinks.Highlight(targets);
            highlightItems = items;
            // A set, because the overlay asks for each marker of each frame if the set holds it.
            highlightObjects = index == null
                ? []
                : objects.Select(o => index.ObjectsByNickname.GetValueOrDefault(o)).OfType<object>().Concat(items).ToHashSet();
            outlined = shown != null && uses != null ? CrossLinks.OutlineOf(shown, uses.Triggers) : NoKeys;
        }

        window.Scene.MissionItems.Highlight = highlightItems;
        window.Scene.Overlay.Highlighted = highlightObjects;
        graph.Outlined = outlined;
    }

    /// <summary>Forgets the sets, for example when the mission closes.</summary>
    public void Reset()
    {
        cached = default;
        targets = [];
        uses = null;
        usesIndex = -1;
        lines = [];
        connectors = (null, null, []);
        applied = default;
        highlightItems = NoItems;
        highlightObjects = [];
        outlined = NoKeys;
        openedFor = null;
        window.Scene.MissionItems.Highlight = NoItems;
        window.Scene.Overlay.Highlighted = [];
        graph.Outlined = NoKeys;
    }

    /// <summary>The bar at the top of the graph: "{n} triggers use {item}", with "Previous" and "Next" (F3 and
    /// Shift+F3).</summary>
    public void UsesBar()
    {
        if (uses is not { Triggers.Count: > 0 } shown)
        {
            return;
        }

        EditorLayout.Strip("##uses-bar", () =>
        {
            var count = shown.Triggers.Count == 1
                ? "1 trigger uses"
                : $"{shown.Triggers.Count.ToString(CultureInfo.InvariantCulture)} triggers use";
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{count} {shown.Name}");
            ImGui.SameLine();
            if (ToolbarItems.Button("uses-previous", EditorIcons.ArrowLeft, "Previous. Shortcut: Shift+F3."))
            {
                Step(-1);
            }

            ImGui.SameLine();
            if (ToolbarItems.Button("uses-next", EditorIcons.ArrowRight, "Next. Shortcut: F3."))
            {
                Step(1);
            }
        }, new Vector2(ImGui.GetContentRegionAvail().X, EditorLayout.StripHeight));
    }

    /// <summary>Frames the next trigger that uses the world item, or the one before it, and selects its line. Each step
    /// selects the line that names the item, so the selection is a row at each step.</summary>
    public void Step(int step)
    {
        if (uses is not { Triggers.Count: > 0 } shown || graph.Graph is not { } shownGraph || missions.Open is not { } analysis)
        {
            return;
        }

        usesIndex = ((usesIndex < 0 && step < 0 ? 0 : usesIndex) + step + shown.Triggers.Count) % shown.Triggers.Count;
        var trigger = shown.Triggers[usesIndex];
        if (shownGraph.NodeOf(trigger) is { } node)
        {
            graph.FrameBlock(node);
        }

        var line = shown.Lines.FirstOrDefault(l => l.Trigger == trigger);
        window.Selection = line != null
            ? new MissionRef(analysis.Script.Path, MissionItemKind.Row, trigger.Section.Ref, Row: line.Entry)
            : new MissionRef(analysis.Script.Path, MissionItemKind.Trigger, trigger.Section.Ref);
    }

    /// <summary>
    /// The banner of an item in another system (section 7.4 of the M6a design): "{item} is in {system name}
    /// ({nickname}). The Scene shows {open system name}." with the button "Open {nickname}". A single selection never
    /// opens a system, because a system load takes time (decision D531).
    /// </summary>
    public void Banner(MissionAnalysis analysis)
    {
        if (window.Selection is not MissionRef item || window.Scene.System is not { } open ||
            SystemOfItem(analysis, item) is not { } system || system.Equals(open.Nickname, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = window.Scene.MissionItems.Label(item) ?? item.Key ?? SectionName(analysis, item);
        var target = window.Session?.GameData?.Items.Systems.Get(system);
        var systemName = window.Session?.SystemLabel(system) ?? system;
        var openName = window.Session?.ItemName(open) is { Length: > 0 } openText ? openText : open.Nickname;
        EditorLayout.Strip("##other-system", () =>
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(EditorTheme.Warning, EditorIcons.InfoCircle.ToString());
            ImGui.SameLine();
            ImGui.Text($"{name} is in {systemName} ({system}). The Scene shows {openName}.");
            ImGui.SameLine();
            if (target != null && ImGui.SmallButton($"Open {system}"))
            {
                window.OpenSystem(target);
                window.Selection = item;
            }
        }, new Vector2(ImGui.GetContentRegionAvail().X, EditorLayout.StripHeight));
    }

    /// <summary>The system of a mission item: the system of its section. A line of a trigger has the system of its
    /// trigger, or of the item that it names.</summary>
    private static string? SystemOfItem(MissionAnalysis analysis, MissionRef item)
    {
        if (item.Section is not { } section || analysis.Document.Find(section) is not { } found)
        {
            return null;
        }

        if (found.Kind == MissionSectionKind.Trigger)
        {
            return item.Kind == MissionItemKind.Row &&
                   analysis.Index.LinesOf(found).FirstOrDefault(l => l.Entry == item.Row) is { } line
                ? MissionPlacements.SystemOfLine(analysis.Index, line)
                : null;
        }

        return analysis.Index.SystemOf(found);
    }

    /// <summary>
    /// Draws the connector lines from the rows or chips of the graph to the items in the Scene part, on the current
    /// window. The Mission tab calls it in a child window after both parts drew. At most 24 lines draw. An item in
    /// another system ends at a chip "In {system name}" at the top of the Scene part. An item outside the Scene view,
    /// also behind the camera, ends at the edge of the Scene part with an arrow. A double-click on a line frames its
    /// item in the Scene part.
    /// </summary>
    public void DrawLines((Vector2 Min, Vector2 Max) tab)
    {
        lines = [];
        if (targets.Count == 0 || graph.Graph is not { } shown || window.Scene.LastRect is not { } scene ||
            window.Scene.LastFrame != ImGui.GetFrameCount() || window.Scene.Index is not { } index)
        {
            return;
        }

        if (!ReferenceEquals(connectors.Graph, shown) || !ReferenceEquals(connectors.Targets, targets))
        {
            connectors = (shown, targets, CrossLinks.Lines(shown, targets));
        }

        var scale = ImGuiHelper.Scale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(tab.Min, tab.Max, true);
        var result = new List<(ConnectorLine, CrossLinkTarget)>();
        var chips = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
        var chipX = scene.Min.X + 6 * scale;
        var skipped = 0;
        var layer = window.Scene.MissionItems;
        foreach (var (source, target) in connectors.Lines)
        {
            // The end of the line: the item in the open system, an object of the system, or another system.
            Vector3? world = target.Item != null && layer.PlaceOf(target.Item) is var (position, _) ? position
                : target.Object != null && index.ObjectsByNickname.GetValueOrDefault(target.Object) is { } obj ? obj.Position
                : null;
            var otherSystem = world == null && target.System is { } system &&
                              !system.Equals(index.Source.Nickname, StringComparison.OrdinalIgnoreCase)
                ? system
                : null;
            if (world == null && otherSystem == null)
            {
                continue;
            }

            if (result.Count >= CrossLinks.MaxLines)
            {
                skipped++;
                continue;
            }

            if (SourceRect(shown, source) is not { } rect)
            {
                continue;
            }

            Vector2 end;
            if (world is { } point)
            {
                if (window.Scene.Toward(point) is not { } screen)
                {
                    continue;
                }

                end = screen;
            }
            else
            {
                if (!chips.TryGetValue(otherSystem!, out end))
                {
                    end = SystemChip(drawList, otherSystem!, new Vector2(chipX, scene.Min.Y + 6 * scale), scale, out var width);
                    chips[otherSystem!] = end;
                    chipX += width + 6 * scale;
                }
            }

            result.Add((CrossLinks.Connect(rect, graph.Screen, end, scene), target));
        }

        foreach (var (line, _) in result)
        {
            DrawLine(drawList, line, scale);
        }

        if (skipped > 0 && result.Count > 0)
        {
            var label = $"and {skipped.ToString(CultureInfo.InvariantCulture)} more";
            var at = result[^1].Item1.To + new Vector2(6, -8) * scale;
            drawList.AddRectFilled(at, at + ImGui.CalcTextSize(label) + new Vector2(8, 4) * scale,
                EditorTheme.ToAbgr(EditorTheme.Panel), 3 * scale);
            drawList.AddText(at + new Vector2(4, 2) * scale, EditorTheme.ToAbgr(EditorTheme.Text), label);
        }

        drawList.PopClipRect();
        lines = result;
        DoubleClickLine();
    }

    /// <summary>The chip "In {system name}" of another system at the top of the Scene part. A double-click opens the
    /// system. Returns the point where the lines end.</summary>
    private Vector2 SystemChip(ImDrawListPtr drawList, string system, Vector2 min, float scale, out float width)
    {
        var label = $"In {window.Session?.SystemLabel(system) ?? system}";
        var size = ImGui.CalcTextSize(label) + new Vector2(2 * ChipPadding * scale, 4 * scale);
        drawList.AddRectFilled(min, min + size, EditorTheme.ToAbgr(EditorTheme.Panel), 3 * scale);
        drawList.AddRect(min, min + size, EditorTheme.ToAbgr(LineColor), 3 * scale);
        drawList.AddText(min + new Vector2(ChipPadding * scale, 2 * scale), EditorTheme.ToAbgr(EditorTheme.Text), label);
        if (ImGui.IsMouseHoveringRect(min, min + size))
        {
            ImGui.SetTooltip("Double-click to open this system.");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) &&
                window.Session?.GameData?.Items.Systems.Get(system) is { } other)
            {
                window.OpenSystem(other);
            }
        }

        width = size.X;
        return new Vector2(min.X + size.X / 2, min.Y + size.Y);
    }

    /// <summary>The screen rectangle of a source in the graph: a row, a chip or a node.</summary>
    private (Vector2 Min, Vector2 Max)? SourceRect(MissionGraph shown, CrossLinkSource source)
    {
        if (source.Entry is { } entry && graph.RowRects.TryGetValue((source.Node, entry), out var row))
        {
            return row;
        }

        if (source.Group is { } group && graph.ChipRects.TryGetValue((source.Node, group), out var chip))
        {
            return chip;
        }

        // A node without a row or a chip on the screen: the right edge of its header.
        if (shown.Block(source.Node) is GraphNode node && graph.Layout is { } layout)
        {
            var view = graph.View;
            var (min, max) = layout.RectOf(node);
            var size = graph.Screen.Max - graph.Screen.Min;
            var canvas = view.Max - view.Min;
            if (canvas.X <= 0 || canvas.Y <= 0)
            {
                return null;
            }

            Vector2 ToScreen(Vector2 point) => graph.Screen.Min + (point - view.Min) / canvas * size;
            var top = ToScreen(new Vector2(max.X, min.Y + MissionGraph.HeaderHeight / 2));
            return (ToScreen(min), new Vector2(top.X, top.Y));
        }

        return null;
    }

    /// <summary>A line with an arrow at an end that is at the edge of a part.</summary>
    private static void DrawLine(ImDrawListPtr drawList, ConnectorLine line, float scale)
    {
        var color = EditorTheme.ToAbgr(LineColor);
        drawList.AddLine(line.From, line.To, color, 1.5f * scale);
        drawList.AddCircleFilled(line.From, 2.5f * scale, color);
        if (line.ToArrow)
        {
            Arrow(drawList, line.From, line.To, color, scale);
        }
        else
        {
            drawList.AddCircle(line.To, 5 * scale, color, 0, 1.5f * scale);
        }

        if (line.FromArrow)
        {
            Arrow(drawList, line.To, line.From, color, scale);
        }
    }

    private static void Arrow(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float scale)
    {
        var direction = to - from;
        if (direction.LengthSquared() < 1)
        {
            return;
        }

        direction = Vector2.Normalize(direction) * ArrowSize * scale;
        var side = new Vector2(-direction.Y, direction.X) * 0.6f;
        drawList.AddTriangleFilled(to, to - direction + side, to - direction - side, color);
    }

    /// <summary>A double-click on a connector line frames its item in the Scene part.</summary>
    private void DoubleClickLine()
    {
        if (!ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            return;
        }

        var mouse = ImGui.GetMousePos();
        foreach (var (line, target) in lines)
        {
            if (CrossLinks.Distance(mouse, line.From, line.To) <= ClickDistance * ImGuiHelper.Scale)
            {
                FrameTarget(target);
                return;
            }
        }
    }

    /// <summary>Frames the item of a target in the Scene part. An item in another system opens that system first.</summary>
    private void FrameTarget(CrossLinkTarget target)
    {
        if (target.Item != null)
        {
            missions.FrameInScene(target.Item);
        }
        else if (target.Object != null && window.Scene.Index?.ObjectsByNickname.GetValueOrDefault(target.Object) is { } obj)
        {
            window.Scene.Focus(obj);
        }
    }

    /// <summary>
    /// Frames the world items of a pending request in the Scene part (section 7.3 of the M6a design). It frames the
    /// item itself, or the first item of the open system that the request names. When the item is in another system,
    /// the Scene opens that system, and the next frame frames the item. The request ends in all cases, so a later frame
    /// never moves the camera without a reason that the user sees.
    /// </summary>
    public void FramePending(MissionAnalysis analysis)
    {
        if (missions.PendingSceneFrame is not { } request)
        {
            openedFor = null;
            return;
        }

        var found = CrossLinks.Targets(analysis, CrossLinks.SourceLines(analysis, request, graph.Graph?.ActsOf()), ObjectSystem);
        if (window.Scene.Index is { } index && Frame(index, request, found))
        {
            Done();
            return;
        }

        // The item is in another system: open that system once, and frame the item when the Scene shows it.
        var system = SystemOfItem(analysis, request) ?? found.Select(t => t.System).FirstOrDefault(s => s != null);
        if (!Equals(openedFor, request) && system != null &&
            !system.Equals(window.Scene.Index?.Source.Nickname, StringComparison.OrdinalIgnoreCase) &&
            window.Session?.GameData?.Items.Systems.Get(system) is { } target)
        {
            openedFor = request;
            var selection = window.Selection;
            window.OpenSystem(target);
            window.Selection = selection;
            return;
        }

        Done();
    }

    /// <summary>Frames the item of a request or the first of its targets that the open system has. False when the open
    /// system has none of them.</summary>
    private bool Frame(SystemIndex index, MissionRef request, IReadOnlyList<CrossLinkTarget> found)
    {
        var layer = window.Scene.MissionItems;
        foreach (var item in found.Select(t => t.Item).OfType<MissionRef>().Prepend(request))
        {
            if (layer.PlaceOf(item) != null)
            {
                window.Scene.Focus(item);
                return true;
            }
        }

        foreach (var nickname in found.Select(t => t.Object).OfType<string>())
        {
            if (index.ObjectsByNickname.GetValueOrDefault(nickname) is { } obj)
            {
                window.Scene.Focus(obj);
                return true;
            }
        }

        return false;
    }

    /// <summary>Ends the request of a Scene frame.</summary>
    private void Done()
    {
        missions.PendingSceneFrame = null;
        openedFor = null;
    }

    /// <summary>A double-click on an item of the Scene part frames the first trigger that uses it (section 7.3 of the
    /// M6a design). The item is a mission marker, an object or a zone.</summary>
    public void SceneDoubleClicked(MissionAnalysis analysis, object item)
    {
        var lineList = item is MissionRef mission ? CrossLinks.LinesUsing(analysis, mission)
            : WorldNames(item) is var (nicknames, _) ? CrossLinks.LinesNaming(analysis, nicknames)
            : [];
        if (MissionUses.TriggersOf(lineList).FirstOrDefault() is { } trigger && graph.Graph?.NodeOf(trigger) is { } node)
        {
            graph.FrameBlock(node);
        }
    }
}

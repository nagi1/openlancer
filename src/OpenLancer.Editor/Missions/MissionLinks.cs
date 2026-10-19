using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The cross-highlighting of the Mission tab (section 7.3 of the M6a design, UI rule 6). From a node to the world: the
/// Scene highlights the items that the lines of the selected trigger, row or prefab name, and connector lines go from the
/// rows or chips to them. From the world to the graph: the graph outlines the triggers that use the selected world
/// item, and a bar counts them with "Previous" and "Next". It also has the banner of an item in another system (section
/// 7.4) and the frame of the world items of a node in the Scene part.
/// </summary>
/// <remarks><see cref="CrossLinks"/> gives the sets and the line ends. This class keeps them for the selection and
/// draws them.</remarks>
internal sealed class MissionLinks(EditorWindow window, MissionWorkspace missions, GraphView graph)
{
    // Sizes in unscaled points.
    private const float ArrowSize = 7;
    private const float ChipPadding = 5;
    private const float ClickDistance = 4;

    private static readonly Vector4 LineColor = EditorTheme.MissionItem with { W = 0.8f };

    private (object? Selection, MissionAnalysis? Analysis) cached;
    private IReadOnlyList<CrossLinkTarget> targets = [];
    private IReadOnlyList<MissionTrigger> users = [];
    private string usesName = "";
    private int usesIndex = -1;
    private IReadOnlyList<(ConnectorLine Line, CrossLinkTarget Target)> lines = [];
    private int framePending;

    /// <summary>The triggers that use the selected world item, for the bar of the graph.</summary>
    public IReadOnlyList<MissionTrigger> Users => users;

    /// <summary>Takes the selection of this frame: the targets of a node, or the users of a world item. The sets change
    /// only when the selection or the analysis changes.</summary>
    public void Update(MissionAnalysis analysis)
    {
        var selection = window.Selection;
        if (Equals(cached.Selection, selection) && ReferenceEquals(cached.Analysis, analysis))
        {
            Apply(analysis);
            return;
        }

        cached = (selection, analysis);
        targets = [];
        var keepBar = selection is MissionRef { Kind: MissionItemKind.Trigger or MissionItemKind.Row } chosen &&
                      users.Any(t => Equals(t.Section.Ref, chosen.Section));
        if (!keepBar)
        {
            users = [];
            usesIndex = -1;
        }

        if (selection is MissionRef item && string.Equals(item.Script, analysis.Script.Path, StringComparison.OrdinalIgnoreCase))
        {
            targets = CrossLinks.Targets(analysis, CrossLinks.SourceLines(analysis, item));
            // A trigger, a line, a prefab, an act and the mission are not world items, so they get no bar.
            if (!keepBar && item.Kind is not (MissionItemKind.Trigger or MissionItemKind.Row or MissionItemKind.PrefabInstance or
                    MissionItemKind.Act or MissionItemKind.Mission))
            {
                Uses(analysis, item, window.Scene.MissionItems.Label(item) ?? item.Key ?? SectionName(analysis, item));
            }
        }
        else if (!keepBar && selection is SystemObject obj)
        {
            Uses(analysis, obj.Nickname, window.Session?.ItemName(obj) is { Length: > 0 } name ? name : obj.Nickname);
        }

        Apply(analysis);
    }

    /// <summary>The triggers that use a world item, in file order.</summary>
    private void Uses(MissionAnalysis analysis, object item, string name)
    {
        users = MissionUses.TriggersOf(CrossLinks.LinesUsing(analysis, item));
        usesName = name;
        usesIndex = -1;
    }

    private static string SectionName(MissionAnalysis analysis, MissionRef item) =>
        item.Section is { } section && analysis.Document.Find(section) is { } found
            ? found.Nickname ?? section.Id.ToString()
            : "the item";

    /// <summary>Gives the sets of this frame to the views: the outlined nodes of the graph, and the highlighted items
    /// of the Scene.</summary>
    private void Apply(MissionAnalysis analysis)
    {
        var (items, objects) = CrossLinks.Highlight(targets);
        window.Scene.MissionItems.Highlight = items;
        window.Scene.Overlay.Highlighted = window.Scene.Index is { } index
            ? objects.Select(o => index.ObjectsByNickname.GetValueOrDefault(o)).OfType<object>().ToList()
            : [];
        graph.Outlined = graph.Graph is { } shown ? CrossLinks.OutlineOf(shown, users) : new HashSet<string>();
    }

    /// <summary>Forgets the sets, for example when the mission closes.</summary>
    public void Reset()
    {
        cached = default;
        targets = [];
        users = [];
        lines = [];
        window.Scene.MissionItems.Highlight = new HashSet<MissionRef>();
        window.Scene.Overlay.Highlighted = [];
        graph.Outlined = new HashSet<string>();
    }

    /// <summary>The bar at the top of the graph: "{n} triggers use {item}", with "Previous" and "Next" (F3 and
    /// Shift+F3).</summary>
    public void UsesBar()
    {
        if (users.Count == 0)
        {
            return;
        }

        EditorLayout.Strip("##uses-bar", () =>
        {
            var count = users.Count == 1 ? "1 trigger uses" : $"{users.Count.ToString(CultureInfo.InvariantCulture)} triggers use";
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{count} {usesName}");
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

    /// <summary>Frames the next trigger that uses the world item, or the one before it, and selects its line.</summary>
    public void Step(int step)
    {
        if (users.Count == 0 || graph.Graph is not { } shown || missions.Open is not { } analysis)
        {
            return;
        }

        usesIndex = ((usesIndex < 0 && step < 0 ? 0 : usesIndex) + step + users.Count) % users.Count;
        var trigger = users[usesIndex];
        if (shown.NodeOf(trigger) is { } node)
        {
            graph.FrameBlock(node);
        }

        var line = CrossLinks.LinesUsing(analysis, cached.Selection is SystemObject obj ? obj.Nickname : cached.Selection!)
            .FirstOrDefault(l => l.Trigger == trigger);
        var row = line != null
            ? new MissionRef(analysis.Script.Path, MissionItemKind.Row, trigger.Section.Ref, Row: line.Entry)
            : new MissionRef(analysis.Script.Path, MissionItemKind.Trigger, trigger.Section.Ref);
        window.Selection = row;
        cached = (row, analysis);
        targets = CrossLinks.Targets(analysis, CrossLinks.SourceLines(analysis, row));
        Apply(analysis);
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
        var systemName = target != null && window.Session!.ItemName(target) is { Length: > 0 } text ? text : system;
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

    /// <summary>The system of a mission item: the system of its section, or the system line of the trigger of a
    /// line.</summary>
    private static string? SystemOfItem(MissionAnalysis analysis, MissionRef item)
    {
        if (item.Section is not { } section || analysis.Document.Find(section) is not { } found)
        {
            return null;
        }

        if (found.Kind == MissionSectionKind.Trigger)
        {
            return item.Kind == MissionItemKind.Row &&
                   analysis.Index.Triggers.FirstOrDefault(t => t.Section == found)?.SystemHint is { } hint &&
                   !MissionIndex.Is(hint, "ANY")
                ? hint
                : null;
        }

        return analysis.Index.SystemOf(found);
    }

    /// <summary>
    /// Draws the connector lines from the rows or chips of the graph to the items in the Scene part, on the current
    /// window. The Mission tab calls it in a child window after both parts drew. At most 24 lines draw. An item in another system ends at a chip "In
    /// {system name}" at the top of the Scene part. A double-click on a line frames its item in the Scene part.
    /// </summary>
    public void DrawLines(MissionAnalysis analysis, (Vector2 Min, Vector2 Max) tab)
    {
        lines = [];
        if (targets.Count == 0 || graph.Graph is not { } shown || window.Scene.LastRect is not { } scene ||
            window.Scene.LastFrame != ImGui.GetFrameCount() || window.Scene.Index is not { } index)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(tab.Min, tab.Max, true);
        var result = new List<(ConnectorLine, CrossLinkTarget)>();
        var chips = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
        var chipX = scene.Min.X + 6 * scale;
        var skipped = 0;
        foreach (var target in targets)
        {
            if (CrossLinks.SourceOf(shown, target.Line) is not { } source || SourceRect(shown, source) is not { } rect)
            {
                continue;
            }

            Vector2? point = null;
            if (target.Item != null && window.Scene.MissionItems.PlaceOf(target.Item) is var (position, _) &&
                window.Scene.TryProject(position, out var screen))
            {
                point = screen;
            }
            else if (target.Object != null && index.ObjectsByNickname.GetValueOrDefault(target.Object) is { } obj &&
                     window.Scene.TryProject(obj.Position, out var objScreen))
            {
                point = objScreen;
            }
            else if (target.System is { } system && !system.Equals(index.Source.Nickname, StringComparison.OrdinalIgnoreCase))
            {
                if (!chips.TryGetValue(system, out var chip))
                {
                    var label = $"In {SystemName(system)}";
                    var size = ImGui.CalcTextSize(label) + new Vector2(2 * ChipPadding * scale, 4 * scale);
                    var min = new Vector2(chipX, scene.Min.Y + 6 * scale);
                    drawList.AddRectFilled(min, min + size, EditorTheme.ToAbgr(EditorTheme.Panel), 3 * scale);
                    drawList.AddRect(min, min + size, EditorTheme.ToAbgr(LineColor), 3 * scale);
                    drawList.AddText(min + new Vector2(ChipPadding * scale, 2 * scale), EditorTheme.ToAbgr(EditorTheme.Text),
                        label);
                    if (ImGui.IsMouseHoveringRect(min, min + size))
                    {
                        ImGui.SetTooltip("Double-click to open this system.");
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) &&
                            window.Session?.GameData?.Items.Systems.Get(system) is { } other)
                        {
                            window.OpenSystem(other);
                        }
                    }

                    chip = new Vector2(min.X + size.X / 2, min.Y + size.Y);
                    chips[system] = chip;
                    chipX += size.X + 6 * scale;
                }

                point = chip;
            }

            if (point is not { } end)
            {
                continue;
            }

            if (result.Count >= CrossLinks.MaxLines)
            {
                skipped++;
                continue;
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

    /// <summary>The name of a system of the game data, or its nickname.</summary>
    private string SystemName(string nickname) =>
        window.Session is { GameData: { } data } session && data.Items.Systems.Get(nickname) is { } system &&
        session.ItemName(system) is { Length: > 0 } name
            ? name
            : nickname;

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
    /// Frames the world items of a pending request in the Scene part (section 7.3 of the M6a design): the first item of
    /// the open system that the request names, or the item itself. When the item is in another system, the Scene opens
    /// that system and frames the item at the next frame.
    /// </summary>
    public void FramePending(MissionAnalysis analysis)
    {
        if (missions.PendingSceneFrame is not { } request || window.Scene.Index is not { } index)
        {
            return;
        }

        var layer = window.Scene.MissionItems;
        var found = CrossLinks.Targets(analysis, CrossLinks.SourceLines(analysis, request));
        var candidates = new List<object> { request };
        candidates.AddRange(found.Select(t => (object?)t.Item ?? t.Object).OfType<object>());
        foreach (var candidate in candidates)
        {
            if (candidate is MissionRef item && layer.PlaceOf(item) != null)
            {
                window.Scene.Focus(item);
                missions.PendingSceneFrame = null;
                return;
            }

            if (candidate is string nickname && index.ObjectsByNickname.GetValueOrDefault(nickname) is { } obj)
            {
                window.Scene.Focus(obj);
                missions.PendingSceneFrame = null;
                return;
            }
        }

        // The item is in another system: open that system, and frame the item when the Scene shows it.
        var system = SystemOfItem(analysis, request) ?? found.Select(t => t.System).FirstOrDefault(s => s != null);
        if (framePending == 0 && system != null && !system.Equals(index.Source.Nickname, StringComparison.OrdinalIgnoreCase) &&
            window.Session?.GameData?.Items.Systems.Get(system) is { } target)
        {
            framePending = 1;
            var selection = window.Selection;
            window.OpenSystem(target);
            window.Selection = selection;
            return;
        }

        framePending = 0;
        missions.PendingSceneFrame = null;
    }

    /// <summary>A double-click on a mission marker or an object of the Scene part frames the first trigger that uses
    /// it (section 7.3 of the M6a design).</summary>
    public void SceneDoubleClicked(MissionAnalysis analysis, object item)
    {
        var lines = CrossLinks.LinesUsing(analysis, item is SystemObject obj ? obj.Nickname : item);
        if (MissionUses.TriggersOf(lines).FirstOrDefault() is { } trigger && graph.Graph?.NodeOf(trigger) is { } node)
        {
            graph.FrameBlock(node);
        }
    }
}

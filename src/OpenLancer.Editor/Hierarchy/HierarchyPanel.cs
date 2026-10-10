using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Hierarchy;

/// <summary>
/// The tree of the open system: the system row, then the objects, zones and lights in groups by kind. A click selects
/// an item, and a double-click frames it in the Scene. The arrow keys move the selection along the visible rows.
/// </summary>
internal sealed class HierarchyPanel(EditorWindow window)
{
    private const string SearchTip =
        "Search by name or nickname. A t: word shows one type, for example t:station.\n" +
        "Types: sun, planet, station, ring, gate, hole, lane, satellite, platform, zone, field, nebula, light.";

    private HierarchyModel? model;
    private string search = "";

    // The selection at the last frame. A different selection that another panel made opens the tree to its row.
    private object? lastSeenSelection;

    // The row to scroll to in the next frame, and true to put it in the middle of the panel.
    private (object Item, bool Center)? scrollRequest;

    /// <summary>Draws the items of the toolbar strip: the search field with a clear button in it.</summary>
    public void Toolbar() => ToolbarItems.SearchField("search", "Search (t:station)", ref search, -1, SearchTip);

    /// <summary>Sets the text of the search field, for example "t:zone" to show only the zones.</summary>
    public void SetSearch(string text) => search = text;

    /// <summary>Forgets the tree of the last system. Call it when the profile closes.</summary>
    public void Reset()
    {
        model = null;
        lastSeenSelection = null;
        scrollRequest = null;
    }

    public void Draw()
    {
        var session = window.Session;
        if (session?.GameData == null)
        {
            PanelStates.NoGameData(session);
            return;
        }

        if (window.Scene.Index is not { } index)
        {
            PanelStates.Message("No system is open.", "Select a system in the Scene toolbar.");
            return;
        }

        if (model?.Index != index)
        {
            model = new HierarchyModel(index, session.Name);
            lastSeenSelection = null;
            scrollRequest = null;
        }

        model.SetSearch(search);
        var selection = window.Selection;
        if (!ReferenceEquals(selection, lastSeenSelection))
        {
            lastSeenSelection = selection;
            if (model.Reveal(selection))
            {
                scrollRequest = (selection!, true);
            }
        }

        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        Rows(model, focused);
        if (model.IsSearching && model.Root.Matches == 0)
        {
            PanelStates.Message("No object matches the search.", "Esc in the search field clears it.");
        }

        if (focused && !ImGui.GetIO().WantTextInput)
        {
            Keys(model);
        }
    }

    /// <summary>
    /// Draws the visible rows. The clipper skips the rows outside the view. A row that must scroll into view is drawn
    /// also when it is outside the view.
    /// </summary>
    private void Rows(HierarchyModel tree, bool focused)
    {
        var rows = tree.Rows;
        var scrollIndex = scrollRequest is { } request ? tree.IndexOf(request.Item) : -1;
        var center = scrollRequest is { Center: true };
        scrollRequest = null;

        ListRow.PushStyle(focused);
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        if (scrollIndex >= 0)
        {
            clipper.IncludeItemByIndex(scrollIndex);
        }

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                Row(tree, rows[i], right);
                if (i == scrollIndex)
                {
                    ScrollToRow(center);
                }
            }
        }

        clipper.End();
        ListRow.PopStyle();
    }

    private void Row(HierarchyModel tree, HierarchyRow row, float right)
    {
        var node = row.Node;
        var style = ImGui.GetStyle();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + row.Depth * style.IndentSpacing);
        var start = ImGui.GetCursorScreenPos();

        // A group opens and closes with a click on its row. The system row selects the system with a click, so it
        // opens and closes only with its arrow.
        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding |
                    ImGuiTreeNodeFlags.NoTreePushOnOpen;
        var isGroup = node.Children.Count > 0;
        if (isGroup)
        {
            ImGui.SetNextItemOpen(tree.IsOpen(node), ImGuiCond.Always);
        }
        else
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }

        if (node == tree.Root)
        {
            flags |= ImGuiTreeNodeFlags.OpenOnArrow;
        }

        if (node.Item != null && ReferenceEquals(node.Item, window.Selection))
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        var open = ImGui.TreeNodeEx(node.Label, flags);
        if (isGroup && open != tree.IsOpen(node))
        {
            tree.SetOpen(node, open);
        }

        if (node.Item is { } item && !ImGui.IsItemToggledOpen())
        {
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) || ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                Select(item);
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                Frame(item);
            }
        }

        if (node.Tooltip != null)
        {
            ImGui.SetItemTooltip(node.Tooltip);
        }

        if (node.Item != null && ImGui.BeginPopupContextItem())
        {
            EditorTheme.PushMenuColors();
            ContextMenu(node.Item);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // The text starts where ImGui puts the label of a tree node: after the arrow.
        ListRow.Text(start, start.X + ImGui.GetFontSize() + 2 * style.FramePadding.X, right, node.Kind.Icon,
            node.Kind.Abgr, node.Name, node.Detail, node.CountText);
    }

    /// <summary>Scrolls the panel so that the row that was just drawn is in view.</summary>
    private static void ScrollToRow(bool center)
    {
        if (center)
        {
            ImGui.SetScrollHereY(0.5f);
            return;
        }

        var top = ImGui.GetWindowPos().Y;
        var bottom = top + ImGui.GetWindowHeight();
        if (ImGui.GetItemRectMin().Y < top)
        {
            ImGui.SetScrollHereY(0);
        }
        else if (ImGui.GetItemRectMax().Y > bottom)
        {
            ImGui.SetScrollHereY(1);
        }
    }

    /// <summary>
    /// Reads the keys while the panel has the focus: Up and Down move the selection, F and Enter frame it, and Ctrl+C
    /// copies its nickname.
    /// </summary>
    private void Keys(HierarchyModel tree)
    {
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
        {
            Move(tree, 1);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            Move(tree, -1);
        }

        if (window.Selection is not { } selection)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F, false) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
        {
            Frame(selection);
        }

        if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.C)) && selection is NicknameItem named)
        {
            ImGui.SetClipboardText(named.Nickname);
        }
    }

    /// <summary>Selects the next row with an item above or below the selected row.</summary>
    private void Move(HierarchyModel tree, int step)
    {
        var rows = tree.Rows;
        var current = tree.IndexOf(window.Selection);
        var start = current >= 0 ? current + step : step > 0 ? 0 : rows.Count - 1;
        for (var i = start; i >= 0 && i < rows.Count; i += step)
        {
            if (rows[i].Node.Item is { } item)
            {
                Select(item);
                scrollRequest = (item, false);
                return;
            }
        }
    }

    /// <summary>
    /// Selects an item from this panel. The panel does not scroll, because the row is where the user is.
    /// </summary>
    private void Select(object item)
    {
        window.Selection = item;
        lastSeenSelection = item;
    }

    /// <summary>Moves the Scene camera to an item, or frames the whole system for the system row.</summary>
    private void Frame(object item)
    {
        if (item is StarSystem)
        {
            window.Scene.FrameSystem();
        }
        else
        {
            window.Scene.Focus(item);
        }
    }

    private void ContextMenu(object item)
    {
        if (Theme.IconMenuItem(EditorIcons.CompressArrowsAlt, "Frame in Scene", true))
        {
            Frame(item);
        }

        ImGui.Separator();
        if (item is NicknameItem named && Theme.IconMenuItem(EditorIcons.Copy, "Copy nickname", true))
        {
            ImGui.SetClipboardText(named.Nickname);
        }

        var name = window.Session?.ItemName(item) ?? "";
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy name", name.Length > 0))
        {
            ImGui.SetClipboardText(name);
        }

        if (Position(item) is { } position &&
            Theme.IconMenuItem(EditorIcons.Copy, "Copy position as INI", true))
        {
            ImGui.SetClipboardText(string.Create(CultureInfo.InvariantCulture,
                $"pos = {position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}"));
        }

        if (item is not SystemObject { Dock: { } dock })
        {
            return;
        }

        if (dock.Kind == DockKinds.Jump)
        {
            ImGui.Separator();
            var target = dock.Target == null ? null : window.Session?.GameData?.Items.Systems.Get(dock.Target);
            if (Theme.IconMenuItem(EditorIcons.Map, "Open destination system", target != null))
            {
                window.OpenSystem(target!);
            }

            if (Theme.IconMenuItem(EditorIcons.MousePointer, "Open and select exit",
                    target != null && dock.Exit != null))
            {
                window.OpenSystem(target!);
                if (window.Scene.Index?.ObjectsByNickname.GetValueOrDefault(dock.Exit!) is { } exit)
                {
                    window.Selection = exit;
                    window.Scene.Focus(exit);
                }
            }
        }
        else if (dock.Kind == DockKinds.Tradelane && window.Scene.Index is { } index)
        {
            ImGui.Separator();
            var next = dock.Target == null ? null : index.ObjectsByNickname.GetValueOrDefault(dock.Target);
            var previous = dock.TargetLeft == null ? null : index.ObjectsByNickname.GetValueOrDefault(dock.TargetLeft);
            if (Theme.IconMenuItem(EditorIcons.ArrowRight, "Select next ring", next != null))
            {
                window.Selection = next;
            }

            if (Theme.IconMenuItem(EditorIcons.ArrowLeft, "Select previous ring", previous != null))
            {
                window.Selection = previous;
            }
        }
    }

    /// <summary>The position of an item in its system, or null for an item without a position.</summary>
    private static Vector3? Position(object item) => item switch
    {
        SystemObject obj => obj.Position,
        Zone zone => zone.Position,
        LightSource light => light.Light.Position,
        _ => null,
    };
}

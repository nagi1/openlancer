using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.IO;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The cutscene list in the Cutscene toolbar strip (section 5 of the cutscene categories design). It opens a cutscene
/// script of the loaded game data or a new unsaved cutscene of the session. The scripts are in the categories and groups
/// of <see cref="CutsceneCategories"/>, with collapsible headers and counts. The filter field searches all categories,
/// also by the words of the groups. The keyboard moves a highlight over the visible rows.
/// </summary>
internal sealed class CutscenePicker(EditorWindow window, CutsceneWorkspace workspace)
{
    // Sizes in unscaled points.
    public const float Width = 220;
    private const float PopupWidth = 380;

    /// <summary>The height of the list in rows. The height does not change, so the list does not jump while the user
    /// types or opens a group (decision D616).</summary>
    private const int VisibleRows = 20;

    /// <summary>The identity of each tree of the list. All trees show the same list, so a new tree keeps the open
    /// groups of the tree before.</summary>
    internal const string TreeIdentity = "cutscene-list";

    private const string NoMatch = "Nothing matches.";

    // The tree, and the categories that it shows.
    private HierarchyModel? tree;
    private CutsceneCategories? treeOf;
    private string filter = "";

    // The id of the node with the highlight, or null. An id survives a new build of the tree.
    private string? highlight;

    // The node to scroll to in the next frame, and true to put it in the middle of the list.
    private (string Id, bool Center)? scrollRequest;

    // True to give the keyboard to the filter field in the next frame.
    private bool focusFilter;

    // True when the filter field had the keyboard at the end of the last frame. Then the field reads Enter.
    private bool filterActive;

    /// <summary>Forgets the tree of the game data. Call it when the profile closes.</summary>
    public void Reset()
    {
        tree = null;
        treeOf = null;
        highlight = null;
        scrollRequest = null;
    }

    /// <summary>Draws the combo. It is disabled until the game data is loaded.</summary>
    /// <param name="width">The width of the combo, in pixels.</param>
    public void Draw(float width)
    {
        var data = window.Session?.GameData;
        var current = workspace.Path;
        var style = ImGui.GetStyle();
        var previewWidth = width - ImGui.GetFrameHeight() - 2 * style.FramePadding.X;
        var preview = current != null
            ? TextFit.CutRight($"{EditorIcons.Film}  {Path.GetFileNameWithoutExtension(current)}", previewWidth)
            : data != null ? "Open a cutscene"
            : "No game data";
        ImGui.SetNextItemWidth(width);
        ImGui.BeginDisabled(data == null);
        EditorTheme.PushMenuColors();
        // The popup is at least this wide, so the details fit. The combo makes it at least as wide as the combo too.
        ImGui.SetNextWindowSizeConstraints(new Vector2(MathF.Round(PopupWidth * ImGuiHelper.Scale), 0),
            new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.BeginCombo("##cutscene", preview))
        {
            // The combo asks for the categories only while it is open, so a closed combo never starts the scan of the
            // references.
            Popup(workspace.Categories(), current);
            ImGui.EndCombo();
        }
        else
        {
            ImGui.SetItemTooltip(data == null ? "The game data is not loaded."
                : current ?? "Open a cutscene script of the game data.");
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
    }

    /// <summary>
    /// The tree of the list: one node for each category, below it one node for each group, and below it the scripts. A
    /// category with only one group without a label has its scripts right below it. The root does not draw. The first
    /// tree opens Intro scenes, Main story and the group of the game opening (decision D612). A tree after it keeps the
    /// open groups and the search of the tree before.
    /// </summary>
    internal static HierarchyModel Tree(CutsceneCategories categories, HierarchyModel? previous)
    {
        var root = new HierarchyNode("root", ObjectKinds.Thn.Script, "Cutscenes");
        foreach (var category in categories.Categories)
        {
            var kind = KindOf(category.Category);
            var node = new HierarchyNode(CategoryId(category.Category), kind, CutsceneCategories.Label(category.Category),
                CutsceneCategories.Detail(category.Category), tooltip: CutsceneCategories.Tooltip(category.Category));
            var flat = category.Groups is [{ Label.Length: 0 }];
            foreach (var group in category.Groups)
            {
                var parent = node;
                if (!flat)
                {
                    parent = new HierarchyNode(GroupId(category.Category, group.Id), kind, group.Label, group.Detail,
                        tooltip: group.Tooltip.Length > 0 ? group.Tooltip : null);
                    node.Add(parent);
                }

                foreach (var row in group.Rows)
                {
                    parent.Add(new HierarchyNode(ScriptId(row.Path),
                        row.Used ? ObjectKinds.Thn.Script : ObjectKinds.Thn.UnusedScript, row.Name, row.Detail, row.Path,
                        search: row.SearchText));
                }
            }

            root.Add(node);
        }

        var model = HierarchyModel.ForList(root, TreeIdentity, previous);
        if (previous == null)
        {
            Open(model, CategoryId(CutsceneCategory.Intro));
            Open(model, CategoryId(CutsceneCategory.MainStory));
            if (categories.OpeningGroup is { } opening)
            {
                Open(model, GroupId(CutsceneCategory.MainStory, opening));
            }
        }

        return model;
    }

    /// <summary>The kind of a category, for its glyph (section 5.1). The group menu of the Cutscenes tab uses it
    /// too.</summary>
    internal static ObjectKind KindOf(CutsceneCategory category) => category switch
    {
        CutsceneCategory.Intro => ObjectKinds.Thn.IntroScenes,
        CutsceneCategory.MainStory => ObjectKinds.Thn.MainStory,
        CutsceneCategory.Bases => ObjectKinds.Thn.BasesAndRooms,
        CutsceneCategory.BarConversations => ObjectKinds.Thn.BarConversations,
        CutsceneCategory.Characters => ObjectKinds.Thn.Characters,
        CutsceneCategory.RandomMissions => ObjectKinds.Thn.RandomMissions,
        CutsceneCategory.Mods => ObjectKinds.Thn.ModCutscenes,
        _ => ObjectKinds.Thn.OtherScripts,
    };

    internal static string CategoryId(CutsceneCategory category) => "c:" + category;

    internal static string GroupId(CutsceneCategory category, string group) => $"g:{category}/{group}";

    internal static string ScriptId(string path) => "s:" + path;

    /// <summary>The id of the first script row of the visible rows, or null. When the filter changes, the highlight
    /// goes there, so Enter opens the first match.</summary>
    internal static string? FirstScript(HierarchyModel model) =>
        model.Rows.FirstOrDefault(r => r.Node.Item != null).Node?.Id;

    /// <summary>
    /// The id of the row above or below the highlight in the visible rows, headers too. The root row does not count.
    /// Without a visible highlight, Down goes to the first row and Up goes to the last row.
    /// </summary>
    internal static string? Step(IReadOnlyList<HierarchyRow> rows, string? highlight, int step)
    {
        if (rows.Count < 2)
        {
            return null;
        }

        var index = highlight == null ? -1 : IndexOf(rows, highlight);
        var next = index < 1 ? (step > 0 ? 1 : rows.Count - 1) : Math.Clamp(index + step, 1, rows.Count - 1);
        return rows[next].Node.Id;
    }

    /// <summary>The index of the row of a node id in the visible rows, or -1.</summary>
    private static int IndexOf(IReadOnlyList<HierarchyRow> rows, string id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Node.Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Open(HierarchyModel model, string id)
    {
        if (Find(model.Root, id) is { } node)
        {
            model.SetOpen(node, true);
        }
    }

    private static HierarchyNode? Find(HierarchyNode node, string id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (child.Children.Count > 0 && Find(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The body of the open popup: the filter field and the rows.</summary>
    private void Popup(CutsceneCategories categories, string? current)
    {
        if (!ReferenceEquals(treeOf, categories))
        {
            tree = Tree(categories, tree);
            treeOf = categories;
        }

        var model = tree!;
        if (ImGui.IsWindowAppearing())
        {
            // The field is empty at each open. The open cutscene shows in its open group, with the highlight
            // (section 5.5). The path of the row has the letter case of the list (review finding R7).
            filter = "";
            model.SetSearch("");
            highlight = FirstScript(model);
            scrollRequest = null;
            if (categories.RowOf(current) is { } row && model.Reveal(row.Path))
            {
                highlight = ScriptId(row.Path);
                scrollRequest = (highlight, true);
            }

            focusFilter = true;
            filterActive = false;
        }

        if (focusFilter)
        {
            ImGui.SetKeyboardFocusHere();
            focusFilter = false;
        }

        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##filter", $"{EditorIcons.MagnifyingGlass}  Filter", ref filter, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        // While the field does not have the keyboard, the list reads Enter itself.
        enter |= !filterActive && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) ||
                                   ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
        filterActive = ImGui.IsItemActive();
        if (filter != model.Search)
        {
            model.SetSearch(filter);
            highlight = FirstScript(model);
            scrollRequest = highlight == null ? null : (highlight, false);
        }

        // ImGui closes a popup on Escape only with keyboard navigation, which the editor does not use (review finding
        // R2).
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        var step = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? -1 : 0;
        if (step != 0 && Step(model.Rows, highlight, step) is { } next)
        {
            highlight = next;
            scrollRequest = (next, false);
        }

        if (enter && highlight != null)
        {
            var index = IndexOf(model.Rows, highlight);
            if (index > 0)
            {
                Choose(model, model.Rows[index].Node, current);
            }
        }

        Rows(model, current);
    }

    /// <summary>
    /// Runs the action of a row: a header opens or closes, and the filter field gets the keyboard back (review finding
    /// R3). A script opens, and the popup closes. The row of the open cutscene only closes the popup, so the preview
    /// keeps its context and its playhead (decision D617).
    /// </summary>
    private void Choose(HierarchyModel model, HierarchyNode node, string? current)
    {
        highlight = node.Id;
        if (node.Item is not string path)
        {
            model.SetOpen(node, !model.IsOpen(node));
            focusFilter = true;
            return;
        }

        if (!VfsPath.Comparer.Equals(path, current))
        {
            workspace.OpenOrWarn(path);
        }

        ImGui.CloseCurrentPopup();
    }

    /// <summary>
    /// Draws the visible rows below the root in a child of a fixed height. The clipper skips the rows outside the view.
    /// A row that must scroll into view draws also when it is outside the view.
    /// </summary>
    private void Rows(HierarchyModel model, string? current)
    {
        ListRow.PushStyle(true);
        // The menu colors give a hover the selection color. Here the highlight has that color, so a hover is grey.
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorTheme.ToolbarHover);
        ImGui.BeginChild("##rows", new Vector2(0, VisibleRows * ListRow.Height));
        var rows = model.Rows;
        if (rows.Count < 2)
        {
            ImGui.TextDisabled(NoMatch);
        }

        var scrollIndex = scrollRequest is { } request ? IndexOf(rows, request.Id) : -1;
        var center = scrollRequest is { Center: true };
        scrollRequest = null;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        HierarchyNode? chosen = null;
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count - 1);
        if (scrollIndex > 0)
        {
            clipper.IncludeItemByIndex(scrollIndex - 1);
        }

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (Row(model, rows[i + 1], right, current))
                {
                    chosen = rows[i + 1].Node;
                }

                if (i + 1 == scrollIndex)
                {
                    ListRow.ScrollToRow(center);
                }
            }
        }

        clipper.End();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ListRow.PopStyle();
        if (chosen != null)
        {
            Choose(model, chosen, current);
        }
    }

    /// <summary>Draws one row. Returns true when the user clicked it.</summary>
    private bool Row(HierarchyModel model, HierarchyRow row, float right, string? current)
    {
        var node = row.Node;
        var style = ImGui.GetStyle();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (row.Depth - 1) * style.IndentSpacing);
        var start = ImGui.GetCursorScreenPos();

        // The flags of the Hierarchy panel. A header opens and closes with a click on its row.
        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding |
                    ImGuiTreeNodeFlags.NoTreePushOnOpen;
        var isGroup = node.Children.Count > 0;
        if (isGroup)
        {
            ImGui.SetNextItemOpen(model.IsOpen(node), ImGuiCond.Always);
        }
        else
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }

        if (node.Id == highlight)
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        var open = ImGui.TreeNodeEx(node.Label, flags);
        var clicked = false;
        if (isGroup)
        {
            if (open != model.IsOpen(node))
            {
                // The tree node changed its open state itself. The filter field gets the keyboard back.
                model.SetOpen(node, open);
                highlight = node.Id;
                focusFilter = true;
            }
        }
        else
        {
            clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        }

        if (node.Item is string path)
        {
            // The row makes the text of its hover only while the mouse is on the row.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            {
                ImGui.SetTooltip(RowTooltip(path));
            }
        }
        else if (node.Tooltip != null)
        {
            ImGui.SetItemTooltip(node.Tooltip);
        }

        // The text starts where ImGui puts the label of a tree node: after the arrow. A category header has an info
        // icon at its right edge, after the count (Editor UI rule 10).
        var textStart = start.X + ImGui.GetFontSize() + 2 * style.FramePadding.X;
        var textRight = right;
        if (row.Depth == 1)
        {
            var icon = EditorIcons.InfoCircle.ToString();
            var iconSize = ImGui.CalcTextSize(icon);
            var iconX = right - style.FramePadding.X - iconSize.X;
            ImGui.GetWindowDrawList().AddText(new Vector2(iconX, start.Y + style.FramePadding.Y),
                ImGui.GetColorU32(EditorTheme.TextDim), icon);
            textRight = iconX - style.ItemSpacing.X + style.FramePadding.X;
        }

        var iconColor = node.Item is string script && VfsPath.Comparer.Equals(script, current)
            ? EditorTheme.ToAbgr(EditorTheme.AccentText)
            : node.Kind.Abgr;
        ListRow.Text(start, textStart, textRight, node.Kind.Icon, iconColor, node.Name, node.Detail, node.CountText);
        return clicked;
    }

    /// <summary>The hover of a script row: its install path and the INI lines that name it, as in the Cutscenes
    /// tab.</summary>
    private string RowTooltip(string path)
    {
        var uses = workspace.References().UsedBy(path);
        return uses.Count == 0
            ? $"{path}\n\n{CutsceneBrowser.NoReferenceText}"
            : $"{path}\n\nUsed by:\n{string.Join("\n", uses.Select(CutsceneBrowser.Describe))}";
    }
}

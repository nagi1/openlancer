using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>One panel of the workspace. The layout draws its chrome, and the panel draws only its content.</summary>
/// <param name="id">A stable id. ImGui keeps the state of the panel, for example its scroll position, under it.</param>
/// <param name="title">The text of the header tab.</param>
/// <param name="icon">The glyph of the header tab.</param>
/// <param name="draw">Draws the body.</param>
/// <param name="toolbar">
/// Draws the items of the toolbar strip below the header. When it is null, the panel has no strip.
/// </param>
/// <param name="padding">
/// When false, the body has no padding and no scroll bar. Use it for content that fills the panel, for example the
/// Scene.
/// </param>
/// <param name="badges">
/// Gives the badges after the title, for example the unseen error count. An empty list shows nothing. The tab of the
/// panel that shows has no badges, because the user sees the panel.
/// </param>
internal sealed class EditorPanel(string id, string title, char icon, Action draw, Action? toolbar = null,
    bool padding = true, Func<IReadOnlyList<PanelBadge>>? badges = null, string? tooltip = null)
{
    public string Id { get; } = id;

    /// <summary>The tooltip of the tab, or null for none.</summary>
    public string? Tooltip { get; } = tooltip;
    public Action Draw { get; } = draw;
    public Action? Toolbar { get; } = toolbar;
    public bool Padding { get; } = padding;
    public Func<IReadOnlyList<PanelBadge>>? Badges { get; } = badges;

    // The texts and ids that the layout uses each frame, made once.
    internal string TabText { get; } = $"{icon}  {title}";
    internal string TabLabel { get; } = $"{icon}  {title}###{id}";
    internal string ToolbarId { get; } = $"##toolbar-{id}";
    internal string BodyId { get; } = $"##body-{id}";
}

/// <summary>A colored glyph and a short text after the title of a panel tab, for example a count.</summary>
/// <param name="Glyph">The glyph from <see cref="EditorIcons"/>, as a string.</param>
internal readonly record struct PanelBadge(string Glyph, string Text, Vector4 Color);

/// <summary>
/// Panels that share one place in the workspace. Each panel has a tab in the header, and the selected tab shows its
/// panel.
/// </summary>
internal sealed class EditorPanelGroup(string id, IReadOnlyList<EditorPanel> tabs)
{
    public string Id { get; } = id;
    public IReadOnlyList<EditorPanel> Tabs { get; } = tabs;

    /// <summary>The ImGui id of the panel child, made once.</summary>
    internal string ChildId { get; } = $"##panel-{id}";

    /// <summary>The panel of the selected tab in the last frame.</summary>
    internal EditorPanel? Shown { get; set; }

    /// <summary>True when the group, or a window in it, had the focus in its last draw.</summary>
    internal bool Focused { get; set; }
}

/// <summary>
/// The workspace between the menu bar and the status bar, in the Unity default layout. The Inspector is on the right at
/// full height. The left area has the Hierarchy and the center tab group (the Scene and the Cutscene) on top and the
/// bottom tab group below them. The backdrop shows in the gaps between the panels, and a drag on a gap moves the panel
/// edge. The ImGui build has no docking, so the layout has a fixed shape.
/// </summary>
internal sealed class EditorLayout(EditorSettings settings, EditorPanel hierarchy, EditorPanelGroup center,
    EditorPanel inspector, EditorPanelGroup bottom)
{
    // Sizes in unscaled points.
    private const float Gap = 4;
    private const float HeaderHeight = 24;
    private const float ToolbarHeight = 26;
    private const float ToolbarPaddingX = 4;
    private const float BodyPaddingX = 8;
    private const float BodyPaddingY = 6;
    private const float HierarchyMin = 180;
    private const float InspectorMin = 280;
    private const float BottomMin = 120;
    private const float SceneMinWidth = 320;
    private const float SceneMinHeight = 200;

    private const float BadgeGap = 8;
    private const float BadgeInnerGap = 3;

    // The maximum panel sizes, as fractions of the workspace size.
    private const float HierarchyMaxFraction = 0.4f;
    private const float InspectorMaxFraction = 0.45f;
    private const float BottomMaxFraction = 0.6f;

    // ImGuiTabItemFlags_NoCloseButton from imgui_internal.h. The BeginTabItem overload that takes flags also takes an
    // open flag, and an open flag adds a close button. This flag removes the button again.
    internal const ImGuiTabItemFlags NoCloseButton = (ImGuiTabItemFlags)(1 << 20);

    private readonly EditorPanelGroup hierarchyGroup = new(hierarchy.Id, [hierarchy]);
    private readonly EditorPanelGroup inspectorGroup = new(inspector.Id, [inspector]);

    // The center group, with the Scene, has the focus when the editor starts, as in Unity.
    private bool focusCenter = true;

    // The group that gets the focus at its next draw, or null (see KeepFocus).
    private EditorPanelGroup? focusRequest;

    // The panels whose tabs must become the selected tabs of their groups. A tab clears its request when it applies it.
    private readonly HashSet<string> selectRequests = [];

    /// <summary>The panel sizes of one frame in pixels, and the splitter limits in unscaled points.</summary>
    /// <param name="LeftWidth">The width of the area left of the Inspector.</param>
    /// <param name="TopHeight">The height of the Hierarchy and the Scene.</param>
    internal readonly record struct Sizes(float Gap, float HierarchyWidth, float InspectorWidth, float BottomHeight,
        float LeftWidth, float TopHeight, float HierarchyMax, float InspectorMax, float BottomMax);

    /// <summary>Makes the tab of a panel the selected tab of its group, when its tab bar draws next. Panels of other
    /// groups can show in the same frame.</summary>
    public void Show(string panelId) => selectRequests.Add(panelId);

    /// <summary>
    /// True when the panel shows in its group and the group has the focus: the user clicked in it last. A group draws
    /// its header before the body of its panel, so the body reads the focus of the same frame.
    /// </summary>
    public bool IsFocused(string panelId) => Groups.Any(g => g.Focused && g.Shown?.Id == panelId);

    /// <summary>
    /// Gives the focus back to the group that has it now, at the next draw. Call it after a call that takes the focus
    /// from all windows, for example to end the edit of a text field.
    /// </summary>
    public void KeepFocus() => focusRequest = Groups.FirstOrDefault(g => g.Focused) ?? focusRequest;

    /// <summary>True when the panel is the selected tab of its group in the last draw.</summary>
    public bool IsShown(string panelId) => Groups.Any(g => g.Shown?.Id == panelId);

    private EditorPanelGroup[] Groups => [hierarchyGroup, center, bottom, inspectorGroup];

    /// <summary>
    /// Draws the workspace. Returns true when the user releases a splitter, so that the caller saves the settings.
    /// </summary>
    public bool Draw(Vector2 position, Vector2 size)
    {
        size = Vector2.Max(size, Vector2.One);
        var scale = ImGuiHelper.Scale;
        var s = Measure(size, scale, settings);

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // The window has no background, so the gaps show the window clear color, which is the backdrop. A table gets
        // NoSavedSettings from its root window, so this window must not set it. Otherwise the column widths, order,
        // visibility and sort of the panel tables are not kept.
        ImGui.Begin("##workspace", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                   ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoScrollWithMouse |
                                   ImGuiWindowFlags.NoBackground);
        ImGui.PopStyleVar();

        if (focusCenter)
        {
            focusRequest = center;
            focusCenter = false;
        }

        DrawGroup(hierarchyGroup, position, new Vector2(s.HierarchyWidth, s.TopHeight));
        DrawGroup(center, position + new Vector2(s.HierarchyWidth + s.Gap, 0),
            new Vector2(s.LeftWidth - s.HierarchyWidth - s.Gap, s.TopHeight));
        DrawGroup(bottom, position + new Vector2(0, s.TopHeight + s.Gap), new Vector2(s.LeftWidth, s.BottomHeight));
        DrawGroup(inspectorGroup, position + new Vector2(s.LeftWidth + s.Gap, 0),
            new Vector2(s.InspectorWidth, size.Y));
        focusRequest = null;

        // A splitter starts a drag from the size that shows. It changes the value only while the user drags it, and
        // then the new value goes into the settings.
        var released = false;
        var shown = s.HierarchyWidth / scale;
        var value = shown;
        released |= Splitter.Draw("##split-hierarchy", position + new Vector2(s.HierarchyWidth, 0),
            new Vector2(s.Gap, s.TopHeight), true, ref value, 1, HierarchyMin, s.HierarchyMax,
            EditorSettings.DefaultHierarchyWidth);
        if (value != shown)
        {
            settings.HierarchyWidth = value;
        }

        shown = value = s.InspectorWidth / scale;
        released |= Splitter.Draw("##split-inspector", position + new Vector2(s.LeftWidth, 0),
            new Vector2(s.Gap, size.Y), true, ref value, -1, InspectorMin, s.InspectorMax,
            EditorSettings.DefaultInspectorWidth);
        if (value != shown)
        {
            settings.InspectorWidth = value;
        }

        shown = value = s.BottomHeight / scale;
        released |= Splitter.Draw("##split-bottom", position + new Vector2(0, s.TopHeight),
            new Vector2(s.LeftWidth, s.Gap), false, ref value, -1, BottomMin, s.BottomMax,
            EditorSettings.DefaultBottomHeight);
        if (value != shown)
        {
            settings.BottomHeight = value;
        }

        ImGui.End();
        return released;
    }

    /// <summary>Calculates the panel sizes for a workspace of <paramref name="size"/> pixels.</summary>
    internal static Sizes Measure(Vector2 size, float scale, EditorSettings settings)
    {
        var gap = Pixels(Gap, scale);

        // The limits are unscaled, as the saved sizes are.
        var hierarchyMax = Math.Max(HierarchyMin, HierarchyMaxFraction * size.X / scale);
        var inspectorMax = Math.Max(InspectorMin, InspectorMaxFraction * size.X / scale);
        var bottomMax = Math.Max(BottomMin, BottomMaxFraction * size.Y / scale);
        var hierarchyWidth = Pixels(Math.Clamp(settings.HierarchyWidth, HierarchyMin, hierarchyMax), scale);
        var inspectorWidth = Pixels(Math.Clamp(settings.InspectorWidth, InspectorMin, inspectorMax), scale);
        var bottomHeight = Pixels(Math.Clamp(settings.BottomHeight, BottomMin, bottomMax), scale);

        // When the window is too small for the minimum Scene size, the bottom group, then the Hierarchy and then the
        // Inspector become smaller, down to their minimums. This applies to this frame only and is not saved.
        var missingHeight = Pixels(SceneMinHeight, scale) - (size.Y - bottomHeight - gap);
        Shrink(ref bottomHeight, ref missingHeight, Pixels(BottomMin, scale));
        var missingWidth = Pixels(SceneMinWidth, scale) - (size.X - hierarchyWidth - inspectorWidth - 2 * gap);
        Shrink(ref hierarchyWidth, ref missingWidth, Pixels(HierarchyMin, scale));
        Shrink(ref inspectorWidth, ref missingWidth, Pixels(InspectorMin, scale));

        // In a window that is smaller than all the minimums, the panels overlap. ImGui items cannot have a size of 0.
        var leftWidth = Math.Max(1, size.X - inspectorWidth - gap);
        var topHeight = Math.Max(1, size.Y - bottomHeight - gap);
        return new Sizes(gap, hierarchyWidth, inspectorWidth, bottomHeight, leftWidth, topHeight, hierarchyMax,
            inspectorMax, bottomMax);
    }

    /// <summary>Draws a panel group: the header strip, the toolbar strip of the selected panel and its body.</summary>
    private void DrawGroup(EditorPanelGroup group, Vector2 min, Vector2 size)
    {
        ImGui.SetCursorScreenPos(min);
        if (group == focusRequest)
        {
            ImGui.SetNextWindowFocus();
        }

        // Only the panel child has the panel color. The children in it are transparent.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Panel);
        var visible = ImGui.BeginChild(group.ChildId, Vector2.Max(size, Vector2.One), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleColor();
        group.Focused = visible && ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        if (visible)
        {
            var scale = ImGuiHelper.Scale;
            var top = ImGui.GetCursorScreenPos();
            var headerHeight = Pixels(HeaderHeight, scale);
            var panel = Header(group, size.X, headerHeight);
            group.Shown = panel;
            ImGui.SetCursorScreenPos(top + new Vector2(0, headerHeight));
            if (panel.Toolbar is { } toolbar)
            {
                var toolbarHeight = Pixels(ToolbarHeight, scale);
                Toolbar(panel, toolbar, new Vector2(size.X, toolbarHeight));
                ImGui.SetCursorScreenPos(top + new Vector2(0, headerHeight + toolbarHeight));
            }

            Body(panel);
        }

        ImGui.EndChild();
    }

    /// <summary>Draws the header strip with one tab for each panel, and returns the selected panel.</summary>
    private EditorPanel Header(EditorPanelGroup group, float width, float height)
    {
        var min = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height),
            ImGui.GetColorU32(EditorTheme.Strip));

        // Outside docking, ImGui draws the overline of the selected tab on each tab bar. So the overline shows only
        // when the focus is in this panel.
        var focused = group.Focused;
        if (!focused)
        {
            ImGui.PushStyleColor(ImGuiCol.TabSelectedOverline, EditorTheme.Transparent);
        }

        // A tab bar is the font size plus two frame paddings high. This padding makes it as high as the strip, so the
        // selected tab touches the toolbar strip below it.
        ImGui.PushStyleVarY(ImGuiStyleVar.FramePadding, (height - ImGui.GetFontSize()) / 2);
        var selected = group.Tabs[0];
        if (ImGui.BeginTabBar("##header", ImGuiTabBarFlags.DrawSelectedOverline | ImGuiTabBarFlags.NoTooltip))
        {
            foreach (var panel in group.Tabs)
            {
                // The panel draws its body after the header. So the panel that showed in the last frame gets no
                // badges, also for a frame in which new warnings arrived before the panel could mark them seen.
                if (Tab(panel, !ReferenceEquals(panel, group.Shown)))
                {
                    selected = panel;
                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
        }

        ImGui.PopStyleVar();
        if (!focused)
        {
            ImGui.PopStyleColor();
        }

        return selected;
    }

    /// <summary>
    /// Draws the tab of a panel, with its badges after the title when <paramref name="showBadges"/> is true. Returns
    /// true when the tab is selected. Then call EndTabItem.
    /// </summary>
    private bool Tab(EditorPanel panel, bool showBadges)
    {
        IReadOnlyList<PanelBadge> badges = showBadges ? panel.Badges?.Invoke() ?? [] : [];
        var scale = ImGuiHelper.Scale;
        var gap = MathF.Truncate(BadgeGap * scale);
        var innerGap = MathF.Truncate(BadgeInnerGap * scale);
        var textWidth = ImGui.CalcTextSize(panel.TabText).X;
        if (badges.Count > 0)
        {
            // The tab is as wide as ImGui makes it for the text, plus the badges.
            var badgesWidth = 0f;
            foreach (var badge in badges)
            {
                badgesWidth += gap + ImGui.CalcTextSize(badge.Glyph).X + innerGap + ImGui.CalcTextSize(badge.Text).X;
            }

            ImGui.SetNextItemWidth(MathF.Ceiling(textWidth + badgesWidth + 2 * ImGui.GetStyle().FramePadding.X + 1));
        }

        var flags = NoCloseButton;
        if (selectRequests.Remove(panel.Id))
        {
            flags |= ImGuiTabItemFlags.SetSelected;
        }

        var open = true;
        var selected = ImGui.BeginTabItem(panel.TabLabel, ref open, flags);
        if (badges.Count > 0)
        {
            // The badges are drawn after the title, in their own colors. They are cut at the tab edge when the tab
            // bar has too little space.
            var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            var drawList = ImGui.GetWindowDrawList();
            var padding = ImGui.GetStyle().FramePadding;
            var position = new Vector2(min.X + padding.X + textWidth, min.Y + padding.Y);
            drawList.PushClipRect(min, max, true);
            foreach (var badge in badges)
            {
                position.X += gap;
                drawList.AddText(position, ImGui.GetColorU32(badge.Color), badge.Glyph);
                position.X += ImGui.CalcTextSize(badge.Glyph).X + innerGap;
                drawList.AddText(position, ImGui.GetColorU32(ImGuiCol.Text), badge.Text);
                position.X += ImGui.CalcTextSize(badge.Text).X;
            }

            drawList.PopClipRect();
        }

        if (panel.Tooltip != null)
        {
            ImGui.SetItemTooltip(panel.Tooltip);
        }

        return selected;
    }

    /// <summary>
    /// Draws the toolbar strip of a panel. Its buttons are flat, and a toggle that is on keeps its pressed color.
    /// </summary>
    private static void Toolbar(EditorPanel panel, Action draw, Vector2 size) => Strip(panel.ToolbarId, draw, size);

    /// <summary>The height of a toolbar strip, in pixels.</summary>
    public static float StripHeight => Pixels(ToolbarHeight, ImGuiHelper.Scale);

    /// <summary>
    /// Draws a toolbar strip at the cursor, as the strip of a panel, for example the Scene toolbar above the Scene part
    /// of the Mission tab. Its buttons are flat, and a toggle that is on keeps its pressed color.
    /// </summary>
    public static void Strip(string id, Action draw, Vector2 size)
    {
        // The items are centered vertically in the strip.
        var padding = new Vector2(Pixels(ToolbarPaddingX, ImGuiHelper.Scale),
            MathF.Truncate((size.Y - ImGui.GetFrameHeight()) / 2));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Toolbar);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        var visible = ImGui.BeginChild(id, size, ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        if (visible)
        {
            EditorTheme.PushFlatButtonColors();
            draw();
            EditorTheme.PopFlatButtonColors();
        }

        ImGui.EndChild();
    }

    /// <summary>Draws the body of a panel in the space below its strips.</summary>
    private static void Body(EditorPanel panel)
    {
        var padding = panel.Padding
            ? new Vector2(Pixels(BodyPaddingX, ImGuiHelper.Scale), Pixels(BodyPaddingY, ImGuiHelper.Scale))
            : Vector2.Zero;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        var visible = ImGui.BeginChild(panel.BodyId, Vector2.Zero,
            panel.Padding ? ImGuiChildFlags.AlwaysUseWindowPadding : ImGuiChildFlags.None,
            panel.Padding ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (visible)
        {
            panel.Draw();
        }

        ImGui.EndChild();
    }

    /// <summary>Takes up to <paramref name="missing"/> pixels from a size, but not below its minimum.</summary>
    private static void Shrink(ref float size, ref float missing, float minimum)
    {
        var cut = Math.Clamp(missing, 0, Math.Max(0, size - minimum));
        size -= cut;
        missing -= cut;
    }

    private static float Pixels(float points, float scale) => MathF.Truncate(points * scale);
}

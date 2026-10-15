using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// Items for the toolbar strip of a panel: flat icon buttons, toggles and separators. Each item has a tooltip, because
/// an icon alone does not say what it does. The caller puts <see cref="ImGui.SameLine()"/> between the items.
/// </summary>
internal static class ToolbarItems
{
    // Sizes in unscaled points.
    private const float IconButtonWidth = 28;
    private const float SeparatorInset = 3;

    /// <summary>The width of an icon button or a toggle, in pixels.</summary>
    public static float IconWidth => MathF.Truncate(IconButtonWidth * ImGuiHelper.Scale);

    /// <summary>A flat icon button. Returns true when the user clicks it.</summary>
    public static bool Button(string id, char icon, string tooltip, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        var clicked = IconButton(id, icon);
        ImGui.EndDisabled();
        ImGui.SetItemTooltip(tooltip);
        return clicked;
    }

    /// <summary>
    /// A flat icon button that is on or off. When it is on, it has the pressed color, also under the mouse. Returns the
    /// new value.
    /// </summary>
    public static bool Toggle(string id, char icon, string tooltip, bool value, bool enabled = true)
    {
        if (value)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, EditorTheme.Pressed);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, EditorTheme.PressedHover);
        }

        ImGui.BeginDisabled(!enabled);
        var clicked = IconButton(id, icon);
        ImGui.EndDisabled();
        if (value)
        {
            ImGui.PopStyleColor(2);
        }

        ImGui.SetItemTooltip(tooltip);
        return clicked ? !value : value;
    }

    /// <summary>
    /// A flat toggle with a colored glyph and a count, for example the severity filters of the Console. When it is on,
    /// it has the pressed color, also under the mouse. Returns the new value.
    /// </summary>
    public static bool CountToggle(string id, char icon, Vector4 iconColor, string count, string tooltip, bool value)
    {
        if (value)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, EditorTheme.Pressed);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, EditorTheme.PressedHover);
        }

        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(CountToggleWidth(icon, count), ImGui.GetFrameHeight());
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        var clicked = ImGui.Button($"##{id}", size);
        ImGui.PopStyleVar();
        if (value)
        {
            ImGui.PopStyleColor(2);
        }

        ImGui.SetItemTooltip(tooltip);
        var style = ImGui.GetStyle();
        var drawList = ImGui.GetWindowDrawList();
        var position = min + style.FramePadding;
        var glyph = icon.ToString();
        drawList.AddText(position, ImGui.GetColorU32(iconColor), glyph);
        position.X += ImGui.CalcTextSize(glyph).X + style.ItemInnerSpacing.X;
        drawList.AddText(position, ImGui.GetColorU32(ImGuiCol.Text), count);
        return clicked ? !value : value;
    }

    /// <summary>The width of a <see cref="CountToggle"/>, in pixels.</summary>
    public static float CountToggleWidth(char icon, string count)
    {
        var style = ImGui.GetStyle();
        return MathF.Ceiling(ImGui.CalcTextSize(icon.ToString()).X + style.ItemInnerSpacing.X +
                             ImGui.CalcTextSize(count).X + 2 * style.FramePadding.X);
    }

    /// <summary>
    /// A search field with a clear button in it while it has text. Esc also clears the text, because the default Esc
    /// only restores the text that the field had before the user clicked it.
    /// </summary>
    /// <param name="width">The width in pixels. -1 fills the strip.</param>
    public static void SearchField(string id, string hint, ref string text, float width, string? tooltip = null)
    {
        ImGui.SetNextItemWidth(width);
        // The clear button draws over the right end of the field, so the field must let it take the mouse.
        ImGui.SetNextItemAllowOverlap();
        ImGui.InputTextWithHint($"##{id}", hint, ref text, 128, ImGuiInputTextFlags.EscapeClearsAll);
        if (tooltip != null)
        {
            ImGui.SetItemTooltip(tooltip);
        }

        if (text.Length > 0)
        {
            var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            ImGui.SetCursorScreenPos(new Vector2(max.X - IconWidth, min.Y));
            if (Button($"{id}-clear", EditorIcons.X, "Clear the search"))
            {
                text = "";
            }
        }
    }

    /// <summary>
    /// A 1 pixel vertical line between groups of items. It has the border color, because the inner line color is
    /// almost the same as the toolbar color.
    /// </summary>
    public static void Separator()
    {
        var position = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight();
        var inset = MathF.Truncate(SeparatorInset * ImGuiHelper.Scale);
        PixelLine.Vertical(ImGui.GetWindowDrawList(), position.X, position.Y + inset, position.Y + height - inset,
            ImGui.GetColorU32(EditorTheme.Border));
        ImGui.Dummy(new Vector2(1, height));
    }

    /// <summary>A button with a glyph and no border, as wide as <see cref="IconWidth"/>.</summary>
    private static bool IconButton(string id, char icon)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        var clicked = ImGui.Button($"{icon}##{id}", new Vector2(IconWidth, 0));
        ImGui.PopStyleVar();
        return clicked;
    }
}

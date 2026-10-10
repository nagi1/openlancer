using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// A splitter in the gap between two panels. A drag moves the panel edge. A double-click sets the default size.
/// </summary>
internal static class Splitter
{
    // ImGuiButtonFlags_NoNavFocus from imgui_internal.h. A click on the splitter then gives the focus back to the panel
    // that had it, as a click on an ImGui window border does. Without it, no panel shows the focus overline after a
    // drag.
    private const ImGuiButtonFlags NoNavFocus = (ImGuiButtonFlags)(1 << 18);

    // Only one splitter can be active at a time, so one start state is sufficient.
    private static float startValue;
    private static float startMouse;

    /// <summary>
    /// Draws the splitter at <paramref name="min"/> and applies a drag to <paramref name="value"/>, an unscaled size.
    /// </summary>
    /// <remarks>
    /// The drag is anchored to its start position. So when the mouse goes past a limit and comes back, the edge is
    /// under the mouse again. The value changes only while the user drags the splitter or double-clicks it.
    /// </remarks>
    /// <param name="vertical">True for a splitter between a left and a right panel.</param>
    /// <param name="sign">
    /// +1 when a drag to the right or down makes the value larger. -1 when it makes the value smaller.
    /// </param>
    /// <returns>True when the user releases the splitter, so that the caller can save the value.</returns>
    public static bool Draw(string id, Vector2 min, Vector2 size, bool vertical, ref float value, float sign,
        float minValue, float maxValue, float defaultValue)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton(id, size, NoNavFocus);
        var mouse = vertical ? ImGui.GetMousePos().X : ImGui.GetMousePos().Y;
        if (ImGui.IsItemActivated())
        {
            startValue = value;
            startMouse = mouse;
        }

        var active = ImGui.IsItemActive();
        if (active)
        {
            value = Drag(startValue, startMouse, mouse, sign, ImGuiHelper.Scale, minValue, maxValue);
        }

        var hovered = ImGui.IsItemHovered();
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            // The second click of a double-click also starts a drag. It starts from the default size.
            value = startValue = defaultValue;
            startMouse = mouse;
        }

        if (active || hovered)
        {
            ImGui.SetMouseCursor(vertical ? ImGuiMouseCursor.ResizeEW : ImGuiMouseCursor.ResizeNS);
            var color = active ? EditorTheme.Accent : EditorTheme.Accent with { W = 0.5f };
            ImGui.GetWindowDrawList().AddRectFilled(min, min + size, ImGui.GetColorU32(color));
        }

        return ImGui.IsItemDeactivated();
    }

    /// <summary>
    /// The unscaled value for a drag from <paramref name="startMouse"/> to <paramref name="mouse"/>, in pixels. The
    /// result depends only on the start and the current position, not on the path between them.
    /// </summary>
    internal static float Drag(float startValue, float startMouse, float mouse, float sign, float scale,
        float minValue, float maxValue) =>
        Math.Clamp(startValue + sign * (mouse - startMouse) / scale, minValue, maxValue);
}

using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>The tooltip of the last item, also when the item is disabled, so that a disabled item tells why the user
/// cannot use it.</summary>
internal static class ItemTooltip
{
    /// <summary>Shows the text as the tooltip of the last item while the mouse is on it, after the usual delay of a
    /// tooltip.</summary>
    public static void Show(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(text);
        }
    }
}

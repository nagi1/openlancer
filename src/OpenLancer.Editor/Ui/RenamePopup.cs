using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// A small popup with the field "New name" for F2 and the menu item "Rename". Enter gives the new name, and Esc closes
/// the popup without a change. The Inspector and the Hierarchy use it for the items of a mission.
/// </summary>
/// <param name="id">The id of the popup in its window.</param>
internal sealed class RenamePopup(string id)
{
    private string text = "";
    private bool open;

    /// <summary>Opens the popup in the next draw, with the name now in its field.</summary>
    public void Open(string name)
    {
        text = name;
        open = true;
    }

    /// <summary>Draws the popup. Call it in each frame in the ImGui id scope of its window. Returns the new name after
    /// Enter, or null.</summary>
    public string? Draw()
    {
        if (open)
        {
            ImGui.OpenPopup(id);
            open = false;
        }

        if (!ImGui.BeginPopup(id))
        {
            return null;
        }

        ImGui.Text("New name");
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        string? name = null;
        if (ImGui.InputText("##rename-text", ref text, 128, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            name = text;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
        return name;
    }
}

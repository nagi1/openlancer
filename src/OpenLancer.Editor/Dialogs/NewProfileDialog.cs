using System.Collections.Generic;
using ImGuiNET;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Games;

namespace OpenLancer.Editor.Dialogs;

/// <summary>
/// New profile, from the Game link window and the Welcome page: the profile part of the first-run screen in a dialog
/// (section 8 of the M11a design). After Create, the new profile opens (decision D216).
/// </summary>
internal sealed class NewProfileDialog(EditorWindow window) : ModalDialog
{
    private readonly NewProfileForm form = new(window);

    protected override string Title => "New profile";

    /// <param name="games">The games that the user can select. The first one is selected.</param>
    public void Show(IReadOnlyList<GameReport> games)
    {
        form.Reset(games);
        Open();
    }

    protected override void Body()
    {
        form.Draw();
        ImGui.Spacing();
        if (Button("Create", primary: true, enabled: form.CanCreate) && form.Create())
        {
            Close();
            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }
}

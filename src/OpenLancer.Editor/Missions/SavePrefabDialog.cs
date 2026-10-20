using System;
using System.Linq;
using ImGuiNET;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Dialogs;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The dialog "Save as prefab" of a matched instance (sections 5.9 and 8.11 of the M6b design): the name in the library
/// and the file name of the new prefab asset. "Save" runs <see cref="CreatePrefabAsset"/> with the instance as its
/// source, so the triggers of the instance become a linked instance of the new prefab, in one undo entry.
/// </summary>
internal sealed class SavePrefabDialog(Func<MissionBatch, bool> run) : ModalDialog
{
    /// <summary>The rule of the file name of a prefab.</summary>
    public const string FileNameRule = "Use lowercase letters, digits, '_' and '-'.";

    private InstanceRef? instance;
    private string name = "";
    private string id = "";
    private string? error;

    protected override string Title => "Save as prefab";

    /// <summary>Opens the dialog for a matched instance with a proposed name and file name.</summary>
    public void Show(InstanceRef source, string proposedName, string proposedId)
    {
        (instance, name, id, error) = (source, proposedName, proposedId, null);
        Open();
    }

    /// <summary>Why a file name cannot be the file name of a prefab, or null. A prefab file name has letters, digits,
    /// '_' and '-', with at most 47 characters.</summary>
    internal static string? FileNameProblem(string text) =>
        text.Trim().Length == 0 ? "Give a file name."
        : text.Trim().Length > 47 ? "The file name has more than 47 characters."
        : text.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? null
        : $"'{text.Trim()}' is not a valid file name. {FileNameRule}";

    /// <summary>The batch of the dialog: the new asset from the instance.</summary>
    internal static MissionBatch Batch(InstanceRef source, string name, string id) =>
        new($"Save as prefab {id.Trim()}",
            [new CreatePrefabAsset(id.Trim()) { Name = name.Trim().Length > 0 ? name.Trim() : null, Source = source }]);

    protected override void Body()
    {
        ImGui.TextWrapped("The triggers of this instance become an instance of the new prefab.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputText("Name", ref name, 64);
        ImGui.SetItemTooltip("The name in the library.");
        ImGui.InputText("File name", ref id, 47);
        ImGui.SetItemTooltip($"The name of the prefab file. {FileNameRule}");
        var problem = FileNameProblem(id);
        Problem(problem ?? error);
        ImGui.Spacing();
        if (Button("Save", primary: true, enabled: problem == null && instance != null))
        {
            if (run(Batch(instance!, name, id)))
            {
                Close();
            }
            else
            {
                error = "The editor did not make the prefab. The Console tells why.";
            }
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }
}

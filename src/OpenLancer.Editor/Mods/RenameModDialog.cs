using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// Rename... of the Mods window (decisions D562 and D588, section 12.3 of the M11c design). The dialog has one field,
/// the new mod id. It shows the plan before a file changes, as the dialog of a delete in the Scene does: the folder, the
/// profiles, the history folder, the settings of the editor, the warnings, or the reason of a refusal.
/// </summary>
/// <remarks>The run uses the request of the shown plan, so it keeps the workspace of the open profile after the session
/// closes.</remarks>
internal sealed class RenameModDialog(ModsWindow mods) : ModalDialog
{
    private ModRow? row;
    private string id = "";
    private string planned = "";
    private RenameRequest? request;
    private RenamePlan? plan;
    private IReadOnlyList<string> settingsChanges = [];

    protected override string Title => "Rename mod###renamemod";

    public void Show(ModRow mod)
    {
        row = mod;
        id = mod.Id;
        planned = id;
        (request, plan, settingsChanges) = (null, null, []);
        Open();
    }

    protected override void Body()
    {
        if (row == null)
        {
            Close();
            return;
        }

        ImGui.TextWrapped($"Give the mod {row.Name} ({row.Id}) a new id. The id is also the name of the folder of the mod.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputText("New mod id (also the folder name)", ref id, 64);
        ImGui.SetItemTooltip(ModManifest.IdRule);
        // A plan that a busy state refused runs again when the state ends, for example after a check of the mod.
        if (id != planned || request != null && request.Busy != mods.RenameBusy(row.Folder))
        {
            planned = id;
            request = id == row.Id || id.Length == 0 ? null : mods.RenameRequestOf(row, id);
            plan = request == null ? null : ModFolderRename.Plan(request);
            settingsChanges = plan is { Refusal: null } && request != null
                ? mods.RenameSettingsChanges(plan, request)
                : [];
        }

        if (plan != null)
        {
            Plan(plan, settingsChanges);
        }

        ImGui.Spacing();
        if (Button("Rename", primary: true, enabled: plan is { Refusal: null } && request != null))
        {
            Close();
            mods.RunRename(request!);
            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    /// <summary>The plan: what changes, the warnings, or the reason of a refusal.</summary>
    /// <param name="settingsChanges">The changes of the settings of the editor.</param>
    private static void Plan(RenamePlan plan, IReadOnlyList<string> settingsChanges)
    {
        ImGui.Spacing();
        if (plan.Refusal != null)
        {
            Problem(plan.Refusal);
            return;
        }

        List("These folders move:",
        [
            $"{plan.OldFolder} to {plan.NewFolder}",
            .. plan.HistoryFrom != null ? [$"{plan.HistoryFrom} to {plan.HistoryTo} (the publish history)"] : Array.Empty<string>(),
        ]);
        List("These profiles change:", plan.Profiles);
        List("These settings of the editor change:", settingsChanges);
        List("Warnings:", plan.Warnings);
    }

    /// <summary>A heading and a bullet for each line, or nothing when there are no lines.</summary>
    private static void List(string heading, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        ImGui.TextColored(EditorTheme.Label, heading);
        foreach (var line in lines)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped(line);
        }

        ImGui.Spacing();
    }
}

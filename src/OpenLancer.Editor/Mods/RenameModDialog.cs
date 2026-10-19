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
/// profiles, the history folder, the warnings, or the reason of a refusal.
/// </summary>
internal sealed class RenameModDialog(ModsWindow mods) : ModalDialog
{
    private ModRow? row;
    private string id = "";
    private string planned = "";
    private RenamePlan? plan;

    protected override string Title => "Rename mod###renamemod";

    public void Show(ModRow mod)
    {
        row = mod;
        id = mod.Id;
        planned = id;
        plan = null;
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
        if (id != planned)
        {
            planned = id;
            plan = id == row.Id || id.Length == 0 ? null : ModFolderRename.Plan(mods.RenameRequestOf(row, id));
        }

        if (plan != null)
        {
            Plan(plan);
        }

        ImGui.Spacing();
        if (Button("Rename", primary: true, enabled: plan is { Refusal: null }))
        {
            Close();
            mods.RunRename(row, id);
            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    /// <summary>The plan: what changes, the warnings, or the reason of a refusal.</summary>
    private static void Plan(RenamePlan plan)
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

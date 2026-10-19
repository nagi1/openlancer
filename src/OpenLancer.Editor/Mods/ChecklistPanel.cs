using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Export;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// The publish checklist at the top of the detail page (section 11 of the M11c design, AGENTS.md UI rule 12): "N of 6
/// steps complete", a bar, and one row for each step. A missing step tells what is missing and why, with its buttons.
/// The recommended items follow in the dim color and do not count. The panel closes to one line when all steps are
/// complete.
/// </summary>
/// <remarks>The checklist reads the applied files of the mod, the history and the linked games, as the export does.
/// An unapplied change of the draft counts after Apply.</remarks>
internal sealed class ChecklistPanel(ModsWindow mods)
{
    private const string Info =
        "The steps that a release of the mod needs. The export runs without them, but players get a better mod with " +
        "them. OpenLancer reads the applied settings, the publish history and the linked games.";

    // The mod of the panel, and true when the user opened the steps of its complete checklist.
    private string? openFolder;
    private bool showAll;

    public void Draw(ModRow row)
    {
        if (row.Manifest is not { } manifest)
        {
            return;
        }

        var checklist = PublishChecklist.Of(Input(row, manifest));
        if (openFolder != row.Folder)
        {
            openFolder = row.Folder;
            showAll = false;
        }

        ImGui.PushID("##checklist");
        ImGui.TextDisabled(EditorIcons.InfoCircle.ToString());
        ImGui.SetItemTooltip(Info);
        ImGui.SameLine();
        CardLayout.Bold($"Publish checklist: {checklist.Summary}");
        // A complete checklist closes to one line, and a link shows the steps again.
        if (checklist.AllComplete)
        {
            ImGui.SameLine();
            if (ImGui.TextLink(showAll ? "Hide the steps" : "Show the steps"))
            {
                showAll = !showAll;
            }
        }

        ImGui.ProgressBar(checklist.Steps.Count == 0 ? 0 : (float)checklist.CompleteCount / checklist.Steps.Count,
            new Vector2(-1, MathF.Truncate(4 * ImGuiHelper.Scale)), "");
        if (!checklist.AllComplete || showAll)
        {
            foreach (var step in checklist.Steps)
            {
                Step(row, step);
            }

            foreach (var item in checklist.Recommended)
            {
                Recommended(row, item);
            }
        }

        ImGui.PopID();
    }

    /// <summary>What the checklist reads: the manifest, the settings, the history and the fingerprints of the row, and
    /// the linked games.</summary>
    private ChecklistInput Input(ModRow row, Data.Mods.ModManifest manifest)
    {
        var reports = mods.LinkedReports;
        return new ChecklistInput(manifest, row.Settings, row.History, row.Content, row.Page,
            target => Export.ExportDialog.BaseOf(target, reports) is { } report ? TargetCheck.BaseKey(report) : null,
            [.. mods.PlayGames(manifest).Select(g => ExportTarget.Find(g.Game.Target ?? "")).OfType<ExportTarget>().Distinct()]);
    }

    private void Step(ModRow row, ChecklistStep step)
    {
        ImGui.PushID(step.Number);
        if (step.Complete)
        {
            ImGui.TextColored(EditorTheme.Success, EditorIcons.Check.ToString());
            ImGui.SameLine();
            ImGui.Text($"{step.Number}. {step.Name}");
            ImGui.PopID();
            return;
        }

        ImGui.TextColored(EditorTheme.Error, EditorIcons.X.ToString());
        ImGui.SameLine();
        ImGui.Text($"{step.Number}. {step.Name}");
        ImGui.Indent(ImGui.GetFrameHeight());
        foreach (var missing in step.Missing)
        {
            CardLayout.Dim(missing);
        }

        for (var i = 0; i < step.Buttons.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            Button(row, step.Buttons[i], i);
        }

        ImGui.Unindent(ImGui.GetFrameHeight());
        ImGui.PopID();
    }

    private void Recommended(ModRow row, ChecklistItem item)
    {
        ImGui.PushID(item.Text);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        ImGui.TextWrapped($"Recommended: {item.Text}");
        ImGui.PopStyleColor();
        if (item.Button is { } button)
        {
            ImGui.SameLine();
            Button(row, button, 0);
        }

        ImGui.PopID();
    }

    /// <summary>A button of a step: it goes to a field of the Settings tab or fixes the step.</summary>
    private void Button(ModRow row, ChecklistButton button, int index)
    {
        ImGui.PushID(index);
        var draft = mods.Draft;
        var block = button.Action switch
        {
            ChecklistAction.SetVersion or ChecklistAction.RemoveTarget => draft?.ManifestReadOnly,
            ChecklistAction.AddImages => draft?.SettingsReadOnly,
            ChecklistAction.Play => mods.PlayBlock(),
            ChecklistAction.CheckNow when mods.Checks.IsChecking(row.Folder) => "The check of this mod runs now.",
            _ => null,
        };
        ImGui.BeginDisabled(block != null);
        if (ImGui.SmallButton(button.Label))
        {
            Run(row, button);
        }

        ImGui.EndDisabled();
        if (block != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(block);
        }

        ImGui.PopID();
    }

    private void Run(ModRow row, ChecklistButton button)
    {
        var draft = mods.Draft;
        switch (button.Action)
        {
            case ChecklistAction.GoTo when button.Field is { } field:
                mods.GoTo(field);
                break;
            case ChecklistAction.SetVersion when button.Argument is { } version && draft != null:
                draft.SetVersion(version);
                mods.GoTo(PublishField.Version);
                break;
            case ChecklistAction.RemoveTarget when button.Argument is { } target && draft != null:
                draft.RemoveTarget(target);
                mods.GoTo(PublishField.Targets);
                break;
            case ChecklistAction.CheckNow:
                mods.CheckNow(row);
                break;
            case ChecklistAction.ShowInConsole:
                mods.ShowProblems(row);
                break;
            case ChecklistAction.GameLink:
                mods.ShowGameLink();
                break;
            case ChecklistAction.Play when button.Argument is { } target && row.Manifest != null:
                if (mods.PlayGames(row.Manifest).FirstOrDefault(g => g.Game.Target == target) is { } game)
                {
                    mods.PlayAlone(row, game);
                }

                break;
            case ChecklistAction.MarkAsTested:
                mods.MarkAsTested(row);
                break;
            case ChecklistAction.Export:
                mods.Export(row);
                break;
            case ChecklistAction.AddImages:
                mods.AddImages();
                break;
        }
    }
}

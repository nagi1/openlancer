using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Editor.Ui.Fields;

/// <summary>
/// The completeness panel at the top of an Inspector page (AGENTS.md UI rule 12, section 11.4 of the M6a design): "4 of 5
/// steps complete" with a progress bar, and a list of the missing steps with the reason of each and "Go to".
/// </summary>
/// <remarks>The bar is neutral while a step is missing and green when all steps are done. It is never red. Hidden
/// base-game steps count as done.</remarks>
internal static class CompletenessPanel
{
    /// <param name="id">The id of the panel. Two panels in one window need different ids.</param>
    /// <param name="showBaseGame">True while the base-game messages show. The hidden steps then show muted with the lock
    /// glyph.</param>
    /// <summary>The tooltip of the button "Fix" (section 8.11 of the M6b design).</summary>
    public const string FixTip = "Make the change that the step needs. Undo takes it back.";

    /// <param name="goTo">Selects the item of a step, or null when the page cannot go to items.</param>
    /// <param name="fixes">Gives the fixes of a step with a fix: the label and what a click does. Null when the page
    /// takes no edits.</param>
    public static void Draw(string id, CompletenessReport report, bool showBaseGame, Action<object>? goTo,
        Func<CompletenessStep, IReadOnlyList<(string Label, Action Run)>>? fixes = null)
    {
        if (report.Total == 0)
        {
            return;
        }

        ImGui.PushID(id);
        ImGui.Text(report.Summary);
        var fraction = report.Total == 0 ? 1 : (float)report.Done / report.Total;
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, report.Complete ? EditorTheme.Success : EditorTheme.TextDim);
        ImGui.ProgressBar(fraction, new Vector2(-1, MathF.Truncate(6 * ImGuiHelper.Scale)), "");
        ImGui.PopStyleColor();

        var missing = report.Missing.ToList();
        if (missing.Count > 0)
        {
            ImGui.SetNextItemOpen(report.HasError, ImGuiCond.Appearing);
            if (ImGui.TreeNode($"Show missing steps ({missing.Count})###missing"))
            {
                for (var i = 0; i < missing.Count; i++)
                {
                    Step(i, missing[i], muted: false, goTo, fixes);
                }

                ImGui.TreePop();
            }
        }

        if (showBaseGame)
        {
            var hidden = report.Steps.Where(s => s.State == CompletenessState.Hidden).ToList();
            for (var i = 0; i < hidden.Count; i++)
            {
                Step(100 + i, hidden[i], muted: true, goTo, null);
            }
        }
        else if (report.HiddenLine is { } line)
        {
            ImGui.TextDisabled(line);
        }

        ImGui.PopID();
    }

    private static void Step(int index, CompletenessStep step, bool muted, Action<object>? goTo,
        Func<CompletenessStep, IReadOnlyList<(string Label, Action Run)>>? fixes)
    {
        ImGui.PushID(index);
        var glyph = muted ? EditorIcons.Lock : step.IsError ? EditorIcons.TimesCircle : EditorIcons.ExclamationTriangle;
        var color = muted ? EditorTheme.TextDim : step.IsError ? EditorTheme.Error : EditorTheme.Warning;
        ImGui.TextColored(color, glyph.ToString());
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, muted ? EditorTheme.TextDim : EditorTheme.Text);
        ImGui.Text(step.Name);
        ImGui.PopStyleColor();
        if (step.GoTo is { } target && goTo != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Go to"))
            {
                goTo(target);
            }
        }

        // The Fix button of a step whose fix needs no choice. A step with two fixes has a button for each fix.
        if (step.Fix != null && fixes?.Invoke(step) is { Count: > 0 } list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(list.Count == 1 ? "Fix##fix" : $"{list[i].Label}##fix{i}"))
                {
                    list[i].Run();
                }

                ImGui.SetItemTooltip(list.Count == 1 ? $"{list[i].Label}. {FixTip}" : FixTip);
            }
        }

        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(step.Reason);
        if (step.Detail is { } detail)
        {
            ImGui.TextDisabled(detail);
        }

        ImGui.PopTextWrapPos();
        ImGui.PopID();
    }
}

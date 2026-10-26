using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Play start page of the Inspector (section 8.3 of the M6c design): the start, the story slot, the story stage, the
/// accept, the start place, the ship, the triggers that wait at the start, the warnings, and the setup triggers as
/// read-only raw INI in the closed section "Advanced". A change of the start or of the place changes the play start in
/// the editor settings. It is not data of the mod, so it has no Undo entry.
/// </summary>
internal sealed class PlayStartPage(EditorWindow window, PropertyGrid grid)
{
    private const float HeaderPaddingX = 10;

    private readonly ValuePicker basePicker = new();
    private bool pickBase;
    private (MissionAnalysis Analysis, PlayStartSetting Setting, PlayStartPageModel Model)? cache;

    public void Draw(MissionRef item)
    {
        var missions = window.Missions;
        if (missions.Analysis(item.Script) is not { } analysis)
        {
            PanelStates.Message("This mission script is not in the edit view.", "Select a mission in the Missions tab.");
            return;
        }

        var plays = missions.Plays;
        var setting = plays.PlayStartOf(item.Script);
        if (cache is not var (a, s, model) || !ReferenceEquals(a, analysis) || s != setting)
        {
            model = PlayStartPageModel.Of(analysis, setting);
            cache = (analysis, setting, model);
        }

        Header();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.BeginChild("##play-start-page", Vector2.Zero);
        ImGui.PopStyleVar();
        if (visible)
        {
            Body(analysis, model);
        }

        ImGui.EndChild();
    }

    private static void Header()
    {
        var padding = MathF.Truncate(HeaderPaddingX * ImGuiHelper.Scale);
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight() * 2;
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(EditorTheme.Section));
        ImGui.SetCursorScreenPos(min + new Vector2(padding, MathF.Truncate(ImGui.GetFrameHeight() / 2)));
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        ImGui.Text($"{EditorIcons.Play}  Play start");
        ImGui.PopStyleColor();
        ImGui.SetCursorScreenPos(new Vector2(min.X + width - InfoIcon.Width - padding,
            min.Y + MathF.Truncate((height - ImGui.GetFrameHeight()) / 2)));
        InfoIcon.Draw("play-start-help", PlayStartPageModel.Help);
        ImGui.SetCursorScreenPos(min + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 1));
    }

    private void Body(MissionAnalysis analysis, PlayStartPageModel model)
    {
        var plan = model.Plan;
        var script = model.Script;
        var plays = window.Missions.Plays;
        if (grid.BeginSection("PlayStart.Start", EditorIcons.Play, "Play start", true, PlayStartPageModel.Help))
        {
            grid.Help(PlayStartPageModel.StartTip);
            grid.Row("Start", () => StartCombo(analysis, model));
            if (plan.Kind == StartKind.Approximate)
            {
                grid.Row("Approximate", () =>
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled("The setup triggers rebuild the state of the path.");
                    ImGui.SameLine();
                    InfoIcon.Draw("approximate-help", PlayStartPageModel.ApproximateHelp);
                });
            }

            if (!plan.CanStart)
            {
                grid.FullRow(() =>
                {
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {StartPlan.RefusalPrefix}{plan.Reason}");
                    ImGui.PopTextWrapPos();
                });
            }

            grid.Help(PlayStartPageModel.SlotTip);
            grid.Text("Story slot", plan.Slot?.Slot ?? "No story slot");
            if (plan.CanStart)
            {
                grid.Help(PlayStartPageModel.StageTip);
                grid.Text("Story stage", model.StageText);
                grid.Help(PlayStartPageModel.AcceptTip);
                grid.Text("Mission accepted", plan.Accepted ? "Yes" : "No");
                grid.Help(PlayStartPageModel.PlaceTip);
                grid.Row("Start place", () => PlaceCombo(script, model));
                grid.Help(PlayStartPageModel.ShipTip);
                grid.Text("Ship", ShipText());
            }

            grid.EndSection();
        }

        foreach (var warning in plan.Warnings)
        {
            grid.FullRow(() =>
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {warning}");
                ImGui.PopTextWrapPos();
            });
        }

        if (grid.BeginSection("PlayStart.Wait", EditorIcons.DotCircle, $"Triggers that wait at the start ({plan.ActiveSet.Count})",
                true, null))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(PlayStartPageModel.WaitTip);
            ImGui.PopTextWrapPos();
            foreach (var name in plan.ActiveSet)
            {
                if (ImGui.Selectable($"{RunOverlay.TriggerName(analysis, name)}##wait-{name}") &&
                    analysis.Index.Trigger(name) is { } trigger)
                {
                    window.Selection = new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref);
                    window.Missions.PendingGraphFrame = true;
                }
            }

            grid.EndSection();
        }

        if (model.SetupIni is { } ini && grid.BeginSection("PlayStart.Advanced", EditorIcons.Terminal, "Advanced", false, null))
        {
            ImGui.TextDisabled("Setup lines");
            ImGui.SetItemTooltip(PlayStartPageModel.SetupTip);
            var text = ini;
            var size = new Vector2(-1, ImGui.GetTextLineHeight() * Math.Min(20, ini.Count(c => c == '\n') + 2));
            ImGui.InputTextMultiline("##setup-lines", ref text, (uint)(ini.Length + 1), size, ImGuiInputTextFlags.ReadOnly);
            grid.EndSection();
        }

        // Play from the start: the same request as F5 in the Mission tab.
        grid.FullRow(() =>
        {
            ImGui.BeginDisabled(!plan.CanStart);
            if (ImGui.Button($"{EditorIcons.Play}  Play from the play start"))
            {
                plays.PlayFromPlayStart(script);
            }

            ImGui.EndDisabled();
            ImGui.SetItemTooltip("Play the mission from this start in OpenLancer (F5 in the Mission tab).");
        });
    }

    private void StartCombo(MissionAnalysis analysis, PlayStartPageModel model)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##start", model.Plan.StartName))
        {
            ImGui.SetItemTooltip(PlayStartPageModel.StartTip);
            return;
        }

        EditorTheme.PushMenuColors();
        foreach (var (start, label) in model.StartChoices(analysis.Index))
        {
            if (ImGui.Selectable(label, start.Equals(model.Setting.Start)))
            {
                window.Missions.Plays.SetPlayStart(model.Script, model.Setting with { Trigger = start.Trigger ?? "" });
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndCombo();
    }

    private void PlaceCombo(string script, PlayStartPageModel model)
    {
        var plays = window.Missions.Plays;
        var (place, dock) = model.Setting.PlaceChoice;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##place", model.PlaceText))
        {
            EditorTheme.PushMenuColors();
            if (ImGui.Selectable($"Suggested: {model.SuggestedText}", place == StartPlaceChoice.Suggested))
            {
                plays.SetPlayStart(script, PlayStartSetting.Of(model.Setting.Start, StartPlaceChoice.Suggested, null));
            }

            if (ImGui.Selectable("At base...", place == StartPlaceChoice.Base))
            {
                pickBase = true;
            }

            if (ImGui.Selectable("At the Scene camera", place == StartPlaceChoice.Camera))
            {
                plays.SetPlayStart(script, PlayStartSetting.Of(model.Setting.Start, StartPlaceChoice.Camera, null));
            }

            EditorTheme.PopMenuColors();
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip(PlayStartPageModel.PlaceTip);
        var open = pickBase;
        pickBase = false;
        if (window.Session is not { } session)
        {
            return;
        }

        var picked = basePicker.Draw("##start-base", open, dock ?? "", () =>
            session.Edits.Lists.List("base")?.Entries.Select(e => (e, e.Name ?? e.Value)).ToList() ?? [],
            none: false, ObjectKinds.OfList("base"));
        if (picked is { Length: > 0 })
        {
            plays.SetPlayStart(script, PlayStartSetting.Of(model.Setting.Start, StartPlaceChoice.Base, picked));
        }
    }

    private string ShipText()
    {
        if (window.Session is not { } session)
        {
            return "";
        }

        var package = window.Settings.PlayPackageOf(session.Profile.FilePath);
        var name = PlayShips.ShipName(session, package);
        return package == null ? $"{name ?? "the ship of a new character"} (default)" : name ?? package;
    }
}

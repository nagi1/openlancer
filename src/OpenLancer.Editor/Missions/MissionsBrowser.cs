using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The Missions tab (section 5 of the M6a design): the 14 story slots in slot order with their rules, states, counts and
/// OpenLancer gaps, then the other mission scripts. A row opens into its bundle. Unity: the Project window in list mode,
/// with the story chain as the scene list of Build Settings.
/// </summary>
internal sealed class MissionsBrowser(EditorWindow window, MissionWorkspace missions)
{
    private const ImGuiTableFlags Flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
                                          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                                          ImGuiTableFlags.SizingFixedFit;

    private static readonly float[] Widths = [60, 200, 70, 80, 150, 140, 60, 50, 70, 150];

    private string search = "";
    private bool filterErrors;
    private bool filterWarnings;
    private bool filterGaps;
    private string? filterSystem;
    private string? selected;

    // The game kind of the base folder of the profile, for the base label.
    private (string? Folder, Task<GameReport>? Report) game;

    /// <summary>Forgets the state of the profile.</summary>
    public void Reset()
    {
        selected = null;
        game = default;
    }

    public void Toolbar()
    {
        var scale = ImGuiHelper.Scale;
        ToolbarItems.SearchField("search", "Search missions, triggers and values", ref search, 260 * scale);
        ImGui.SameLine();
        if (ToolbarItems.Button("filter", EditorIcons.Filter, "Show only the missions that match."))
        {
            ImGui.OpenPopup("##filter");
        }

        FilterMenu();
        ImGui.SameLine();
        BaseGameToggle(window, missions);
        ImGui.SameLine();
        ImGui.TextDisabled(MissionsBrowserModel.BaseLabel(GameReport()));
        ImGui.SetItemTooltip("The game folder of this profile.");
        ImGui.SameLine();
        InfoIcon.Draw("help", MissionSchema.Retail.Help["missionsTab"]);
    }

    /// <summary>The base-game toggle of the mission views: the Console count toggle "base-game messages", with the
    /// number of hidden mission problems (decision D527).</summary>
    internal static void BaseGameToggle(EditorWindow window, MissionWorkspace missions)
    {
        var count = missions.HiddenTotal;
        var showing = missions.ShowBaseGame;
        var tip = Logs.ConsolePanel.BaseGameToggleTip(count, showing);
        var value = ToolbarItems.CountToggle("basegame", EditorIcons.Lock, EditorTheme.TextDim,
            count.ToString("N0", CultureInfo.InvariantCulture), tip, showing);
        if (value != showing)
        {
            missions.SetShowBaseGame(value);
        }
    }

    private void FilterMenu()
    {
        if (!ImGui.BeginPopup("##filter"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        ImGui.MenuItem("Has errors", "", ref filterErrors);
        ImGui.MenuItem("Has warnings", "", ref filterWarnings);
        ImGui.MenuItem("Has OpenLancer gaps", "", ref filterGaps);
        if (ImGui.BeginMenu("Uses system..."))
        {
            if (ImGui.MenuItem("Any system", "", filterSystem == null))
            {
                filterSystem = null;
            }

            var systems = missions.Result?.Analyses.Values.SelectMany(a => MissionsBrowserModel.SystemsOf(a.Index))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            foreach (var system in systems)
            {
                if (ImGui.MenuItem(system, "", string.Equals(filterSystem, system, StringComparison.OrdinalIgnoreCase)))
                {
                    filterSystem = system;
                }
            }

            ImGui.EndMenu();
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    public void Draw()
    {
        if (window.Session?.GameData == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (missions.Result is not { } result)
        {
            PanelStates.Loading("Checking the mission scripts");
            return;
        }

        if (result.Scripts.Count == 0)
        {
            PanelStates.Message("No mission scripts found.",
                "The game data of this profile has no mission script in DATA\\MISSIONS.");
            return;
        }

        var rows = MissionsBrowserModel.Rows(result, missions.ShowBaseGame)
            .Where(r => Shows(r, result.Of(r.Script.Path))).ToList();
        if (!ImGui.BeginTable("##missions", Widths.Length, Flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        ImGui.TableSetupScrollFreeze(1, 1);
        for (var c = 0; c < Widths.Length; c++)
        {
            ImGui.TableSetupColumn(MissionsBrowserModel.Columns[c].Name, ImGuiTableColumnFlags.WidthFixed, Widths[c] * scale);
        }

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var c = 0; c < Widths.Length; c++)
        {
            // The OpenLancer column shows unless the active mod has a targets list without OpenLancer (UI rule 4).
            if (c == Widths.Length - 1 && window.Session.Edits.HidesOpenLancer)
            {
                continue;
            }

            ImGui.TableSetColumnIndex(c);
            ImGui.TableHeader(MissionsBrowserModel.Columns[c].Name);
            ImGui.SetItemTooltip(MissionsBrowserModel.Columns[c].Tooltip);
        }

        var story = rows.Where(r => r.Script.Slot != null).ToList();
        var other = rows.Where(r => r.Script.Slot == null).ToList();
        Group("story", $"Story missions ({story.Count})", "The 14 story slots of the game code, in their fixed order.",
            story, result, defaultOpen: true);
        if (other.Count > 0)
        {
            Group("other", $"Other mission scripts ({other.Count})",
                "Mission scripts that no story slot loads, for example test scripts and scripts of mods. The game can load " +
                "such a script in another way, for example from a new-game file.", other, result, defaultOpen: false);
        }

        ImGui.EndTable();
    }

    private bool Shows(MissionsBrowserRow row, MissionAnalysis? analysis) =>
        MissionsBrowserModel.Matches(row, analysis, search.Trim()) &&
        (!filterErrors || row.Errors > 0) && (!filterWarnings || row.Warnings > 0) && (!filterGaps || row.HasGaps) &&
        (filterSystem == null || row.Systems.Contains(filterSystem));

    private void Group(string id, string title, string tooltip, IReadOnlyList<MissionsBrowserRow> rows,
        MissionRunResult result, bool defaultOpen)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var open = ImGui.TreeNodeEx($"{title}###{id}",
            ImGuiTreeNodeFlags.SpanAllColumns | (defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : 0));
        ImGui.SetItemTooltip(tooltip);
        if (!open)
        {
            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            Row(rows[i], result, $"{id}{i}");
        }

        ImGui.TreePop();
    }

    private void Row(MissionsBrowserRow row, MissionRunResult result, string id)
    {
        ImGui.PushID(id);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var path = row.Script.Path;
        var analysis = result.Of(path);
        var isSelected = string.Equals(selected, path, StringComparison.OrdinalIgnoreCase);
        var flags = ImGuiTreeNodeFlags.SpanAllColumns | ImGuiTreeNodeFlags.OpenOnArrow |
                    (isSelected ? ImGuiTreeNodeFlags.Selected : 0) | (analysis == null ? ImGuiTreeNodeFlags.Leaf : 0);
        var open = ImGui.TreeNodeEx($"{row.Slot}###row", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            selected = path;
            // A click selects the mission in each center tab, so the Inspector shows the Mission page (decision D548).
            if (analysis != null)
            {
                window.Selection = MissionRef.Of(path);
            }
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && analysis != null)
        {
            missions.OpenMission(path, show: true);
        }

        ItemTooltip.Show(row.Script.ModId is { } mod ? $"{row.ScriptText}\nMod: {mod}" : row.ScriptText);
        Cell(1, row.ScriptText, null);
        Cell(2, row.StartStage, row.StartTooltip);
        Cell(3, row.AcceptStage, row.AcceptTooltip);
        Cell(4, row.NextMission, row.NextTooltip);
        Cell(5, row.State, null);
        Cell(6, row.Triggers.ToString(CultureInfo.InvariantCulture), null);
        Count(7, row.Errors, 0, EditorTheme.Error);
        Count(8, row.Warnings, row.Hidden, EditorTheme.Warning);
        if (!window.Session!.Edits.HidesOpenLancer)
        {
            Cell(9, row.OpenLancer, row.OpenLancerTooltip);
        }

        if (open)
        {
            if (analysis != null)
            {
                Triggers(analysis);
                Bundle(result, analysis);
            }

            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void Cell(int column, string text, string? tooltip)
    {
        if (!ImGui.TableSetColumnIndex(column))
        {
            return;
        }

        ImGui.Text(text);
        if (tooltip != null)
        {
            ItemTooltip.Show(tooltip);
        }
    }

    /// <summary>A count cell: the count, and a dim count of the hidden base-game messages in brackets.</summary>
    private static void Count(int column, int count, int hidden, Vector4 color)
    {
        if (!ImGui.TableSetColumnIndex(column))
        {
            return;
        }

        if (count > 0)
        {
            ImGui.TextColored(color, count.ToString("N0", CultureInfo.InvariantCulture));
        }
        else
        {
            ImGui.Text("0");
        }

        if (hidden > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"({hidden:N0})"));
            ItemTooltip.Show(hidden == 1
                ? "1 base-game message is hidden."
                : string.Create(CultureInfo.InvariantCulture, $"{hidden:N0} base-game messages are hidden."));
        }
    }

    /// <summary>The triggers that match the search, under their mission. A double-click opens the mission and selects
    /// the trigger.</summary>
    private void Triggers(MissionAnalysis analysis)
    {
        foreach (var trigger in MissionsBrowserModel.MatchingTriggers(analysis, search.Trim()))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TreeNodeEx($"{EditorIcons.Bolt}  {trigger.Nickname}###t{trigger.Section.Index}",
                ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAllColumns);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                missions.GoTo(trigger.Section.Ref, -1);
            }

            ItemTooltip.Show(trigger.Condition?.Sentence() ?? "The trigger has no condition.");
        }
    }

    private void Bundle(MissionRunResult result, MissionAnalysis analysis)
    {
        var rows = MissionsBrowserModel.Bundle(result, analysis);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TreeNodeEx($"{row.Name}###b{i}",
                ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAllColumns);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                Act(row, analysis);
            }

            if (row.Tooltip != null)
            {
                ItemTooltip.Show(row.Tooltip);
            }

            if (row.Files.Count > 1 && ImGui.BeginPopupContextItem($"##bundle{i}"))
            {
                EditorTheme.PushMenuColors();
                foreach (var file in row.Files)
                {
                    if (ImGui.MenuItem(file) && row.Action == MissionBundleAction.OpenCutscene)
                    {
                        OpenCutscene(file);
                    }
                }

                EditorTheme.PopMenuColors();
                ImGui.EndPopup();
            }

            Cell(1, row.Value, row.Tooltip);
        }
    }

    private void Act(MissionBundleRow row, MissionAnalysis analysis)
    {
        switch (row.Action)
        {
            case MissionBundleAction.OpenMission:
                missions.OpenMission(analysis.Script.Path, show: true);
                break;
            case MissionBundleAction.OpenCutscene when row.Files.Count > 0:
                OpenCutscene(row.Files[0]);
                break;
            case MissionBundleAction.SelectMission:
                window.Selection = MissionRef.Of(analysis.Script.Path);
                break;
        }
    }

    /// <summary>Opens a space cutscene of a mission line in the Cutscene tab. The line holds a path below the data
    /// folder.</summary>
    private void OpenCutscene(string value)
    {
        if (MissionFiles.RelativeOf(value) is { } relative && window.Session?.Edits is { } edits &&
            edits.DataPath(relative) is { } path)
        {
            window.Cutscenes.OpenAndShow(path);
        }
    }

    /// <summary>The report of the base folder of the profile: the report of the game link, or a detect in a worker
    /// task.</summary>
    private GameReport? GameReport()
    {
        if (window.Session?.Profile.BaseFolder is not { } folder)
        {
            return null;
        }

        if (window.Links.ReportOf(folder) is { } linked)
        {
            return linked;
        }

        if (!string.Equals(game.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            game = (folder, Task.Run(() => GameDetector.Detect(folder)));
        }

        return game.Report is { IsCompletedSuccessfully: true } done ? done.Result : null;
    }
}

using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The Mission tab (section 7.1 of the M6a design): the open mission as raw INI text beside the Scene, with a splitter
/// between them. The raw text has the colors of the INI mode and the marks of the checks. A cursor in a section selects
/// its item, and a selection of an item scrolls the text to its section. M6a-2 adds the graph in place of the text.
/// </summary>
internal sealed class MissionView(EditorWindow window, MissionWorkspace missions) : IDisposable
{
    // Sizes in unscaled points.
    private const float SplitterWidth = 6;
    private const float MinSceneTabWidth = 640;
    private const float MinPart = 160;

    private readonly RawIniEditor raw = new();
    private int lastCursor = -1;
    private object? lastSelection;
    private string? shownPath;

    /// <summary>The share of the width of the raw part, by default 55 percent (decision D522).</summary>
    public const float DefaultSplit = 0.55f;

    public void Toolbar()
    {
        var result = missions.Result;
        var open = missions.OpenPath;
        ImGui.SetNextItemWidth(220 * ImGuiHelper.Scale);
        if (ImGui.BeginCombo("##mission", open != null ? Label(result, open) : "Mission"))
        {
            EditorTheme.PushMenuColors();
            foreach (var script in result?.Scripts.Where(s => s.State != MissionScriptState.Missing) ?? [])
            {
                if (ImGui.Selectable(Label(result, script.Path), string.Equals(script.Path, open, StringComparison.OrdinalIgnoreCase)))
                {
                    missions.OpenMission(script.Path, show: true);
                }
            }

            EditorTheme.PopMenuColors();
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip("Open another mission.");
        ImGui.SameLine();
        MissionsBrowser.BaseGameToggle(window, missions);
        ImGui.SameLine();
        var settings = window.Settings;
        var scene = ToolbarItems.Toggle("scene", EditorIcons.Globe, "Show the Scene beside the mission.",
            settings.MissionShowScene);
        if (scene != settings.MissionShowScene)
        {
            settings.MissionShowScene = scene;
            settings.Save();
        }

        ImGui.SameLine();
        Chip.Flow([new ChipItem("Read-only", ChipLook.Grey, MissionPageModel.ReadOnlyNote)]);
        ImGui.SameLine();
        InfoIcon.Draw("help", MissionSchema.Retail.Help["rawIni"]);
    }

    private static string Label(MissionRunResult? result, string path) =>
        result?.Scripts.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)) is
            { Slot: { } slot }
            ? $"{slot.Slot}: {MissionFiles.Display(path)}"
            : MissionFiles.Display(path);

    public void Draw(double elapsed)
    {
        if (window.Session?.GameData == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (missions.Result == null)
        {
            PanelStates.Loading("Checking the mission scripts");
            return;
        }

        if (missions.OpenPath is not { } path || missions.RawTextOf(path) is not { } text)
        {
            PanelStates.Message("No mission is open.", "Double-click a mission in the Missions tab.");
            return;
        }

        var scale = ImGuiHelper.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var settings = window.Settings;
        // The Scene part closes in a narrow tab. The toolbar keeps its toggle.
        var withScene = settings.MissionShowScene && avail.X >= MinSceneTabWidth * scale;
        var gap = MathF.Truncate(SplitterWidth * scale);
        var rawWidth = withScene
            ? MathF.Truncate(Math.Clamp(settings.MissionSplit, 0.1f, 0.9f) * (avail.X - gap))
            : avail.X;

        Follow(path, text);
        raw.Draw("##raw", text, missions.MarksOf(path), new Vector2(rawWidth, avail.Y));
        FollowCursor(path, text);

        if (!withScene)
        {
            return;
        }

        var split = settings.MissionSplit * Math.Max(1, avail.X - gap) / scale;
        var max = Math.Max(MinPart, (avail.X - gap) / scale - MinPart);
        if (Splitter.Draw("##split", origin + new Vector2(rawWidth, 0), new Vector2(gap, avail.Y), true, ref split, 1,
                MinPart, max, DefaultSplit * (avail.X - gap) / scale))
        {
            settings.Save();
        }

        settings.MissionSplit = Math.Clamp(split * scale / Math.Max(1, avail.X - gap), 0.1f, 0.9f);
        ImGui.SetCursorScreenPos(origin + new Vector2(rawWidth + gap, 0));
        if (ImGui.BeginChild("##scene", new Vector2(Math.Max(1, avail.X - rawWidth - gap), avail.Y)))
        {
            window.Scene.Draw(elapsed);
        }

        ImGui.EndChild();
    }

    /// <summary>Scrolls the text to the line of a new selection or of a request of the Console.</summary>
    private void Follow(string path, Data.Editing.Fields.RawText text)
    {
        if (!string.Equals(shownPath, path, StringComparison.OrdinalIgnoreCase))
        {
            shownPath = path;
            lastCursor = -1;
        }

        if (missions.PendingLine is var (pendingPath, line) &&
            string.Equals(pendingPath, path, StringComparison.OrdinalIgnoreCase))
        {
            raw.GoToLine(line);
            missions.PendingLine = null;
            lastCursor = line;
            lastSelection = window.Selection;
            return;
        }

        if (!Equals(window.Selection, lastSelection))
        {
            lastSelection = window.Selection;
            if (window.Selection is MissionRef { Section: { } section } item &&
                string.Equals(item.Script, path, StringComparison.OrdinalIgnoreCase) &&
                text.LineOf(section.Id, item.Row) is { } selectedLine && selectedLine != lastCursor)
            {
                raw.GoToLine(selectedLine);
                lastCursor = selectedLine;
            }
        }
    }

    /// <summary>A cursor in a section selects its item: the trigger, or the line of a trigger.</summary>
    private void FollowCursor(string path, Data.Editing.Fields.RawText text)
    {
        var cursor = raw.CursorLine;
        if (cursor < 0 || cursor == lastCursor)
        {
            return;
        }

        lastCursor = cursor;
        var (section, entry) = text.At(cursor);
        if (section < 0 || missions.Analysis(path) is not { } analysis || section >= analysis.Document.Sections.Count)
        {
            return;
        }

        var item = missions.RefOf(analysis.Document.Sections[section].Ref, entry);
        window.Selection = item;
        lastSelection = item;
    }

    public void Dispose() => raw.Dispose();
}

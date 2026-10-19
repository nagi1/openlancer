using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The Mission tab (section 7.1 of the M6a design): the graph of the open mission, or its raw INI text, beside the Scene,
/// with a splitter between them. The graph toolbar is the strip of the tab, and the Scene toolbar is a strip above the
/// Scene part. In the raw view, the text has the colors of the INI mode and the marks of the checks, a cursor in a
/// section selects its item, and a selection of an item scrolls the text to its section.
/// </summary>
internal sealed class MissionView : IDisposable
{
    // Sizes in unscaled points.
    private const float SplitterWidth = 6;
    private const float MinSceneTabWidth = 640;
    private const float MinPart = 160;
    private const float LensWidth = 150;
    private const float SearchWidth = 200;

    /// <summary>The share of the width of the mission part, by default 55 percent (decision D522).</summary>
    public const float DefaultSplit = 0.55f;

    private readonly EditorWindow window;
    private readonly MissionWorkspace missions;
    private readonly RawIniEditor raw = new(MissionWorkspace.Vocabulary);
    private int lastCursor = -1;
    private object? lastSelection;
    private string? shownPath;

    // True when the search field of the graph takes the keyboard in the next frame: Ctrl+F, or after Enter.
    private bool focusSearch;

    public MissionView(EditorWindow window, MissionWorkspace missions)
    {
        this.window = window;
        this.missions = missions;
        State = new GraphViewState(window.Settings.MissionLens);
        Graph = new GraphView(window, missions, State);
    }

    /// <summary>The state of the graph of the session: frames, lines, drags.</summary>
    public GraphViewState State { get; }

    /// <summary>The graph part of the tab.</summary>
    public GraphView Graph { get; }

    /// <summary>The lens texts of section 13.1 of the M6a design.</summary>
    internal static string LensTip(GraphLens lens) => lens switch
    {
        GraphLens.Beats => "Shows each behavior prefab as one box. This is the default view.",
        GraphLens.Logic => "Shows all triggers and opens all frames.",
        GraphLens.Battle => "Shows spawns, orders, sides and combat conditions. Other triggers fade.",
        GraphLens.Conversation => "Shows comm lines, conversations and the triggers that wait for them. Other triggers fade.",
        GraphLens.Cutscene => "Shows space cutscenes, cameras and base scenes, in the order of the mission.",
        _ => "Shows objectives, titles, offers and log texts, in the order of the mission.",
    };

    public void Toolbar()
    {
        var result = missions.Result;
        var open = missions.OpenPath;
        var scale = ImGuiHelper.Scale;
        ImGui.SetNextItemWidth(220 * scale);
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
        ToolbarItems.Separator();
        ImGui.SameLine();
        if (ToolbarItems.Toggle("graph", EditorIcons.ProjectDiagram, "Graph: show the graph of the mission.",
                !missions.ShowRaw))
        {
            missions.ShowRaw = false;
        }

        ImGui.SameLine();
        if (ToolbarItems.Toggle("raw", EditorIcons.Terminal, "Raw INI: show the whole script as text.", missions.ShowRaw))
        {
            missions.ShowRaw = true;
        }

        if (!missions.ShowRaw)
        {
            GraphToolbar(scale);
        }

        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        var settings = window.Settings;
        var scene = ToolbarItems.Toggle("scene", EditorIcons.Globe, "Scene: show the Scene beside the graph.",
            settings.MissionShowScene);
        if (scene != settings.MissionShowScene)
        {
            settings.MissionShowScene = scene;
            settings.Save();
        }

        ImGui.SameLine();
        MissionsBrowser.BaseGameToggle(window, missions);
        ImGui.SameLine();
        Chip.Flow([new ChipItem("Read-only", ChipLook.Grey, MissionPageModel.ReadOnlyNote)]);
        ImGui.SameLine();
        InfoIcon.Draw("help", MissionSchema.Retail.Help[missions.ShowRaw ? "rawIni" : "missionGraph"]);
    }

    /// <summary>The items of the graph toolbar: the lens, "Show lines" and "Frame all".</summary>
    private void GraphToolbar(float scale)
    {
        var settings = window.Settings;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(LensWidth * scale);
        if (ImGui.BeginCombo("##lens", $"Lens: {settings.MissionLens}"))
        {
            EditorTheme.PushMenuColors();
            foreach (var lens in Enum.GetValues<GraphLens>())
            {
                if (ImGui.Selectable(lens.ToString(), lens == settings.MissionLens))
                {
                    settings.MissionLens = lens;
                    State.Lens = lens;
                    settings.Save();
                }

                ImGui.SetItemTooltip(LensTip(lens));
            }

            EditorTheme.PopMenuColors();
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip("Change what the graph shows. A lens does not change data.");
        ImGui.SameLine();
        var lines = ToolbarItems.Toggle("lines", EditorIcons.List,
            "Show lines: show the condition line and the action lines of each trigger. Off shows a summary.",
            settings.MissionShowLines);
        if (lines != settings.MissionShowLines)
        {
            settings.MissionShowLines = lines;
            settings.Save();
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("frame-all", EditorIcons.CompressArrowsAlt,
                "Frame all: show the whole graph. Shortcut: A or Home."))
        {
            Graph.FrameAll();
        }

        ImGui.SameLine();
        SearchField(scale);
        ImGui.SameLine();
        SystemsMenu();
    }

    /// <summary>The search of the graph (section 6.6 of the M6a design): the results list under the field. Enter frames
    /// the next result, and Shift+Enter the one before.</summary>
    private void SearchField(float scale)
    {
        ImGui.SetNextItemWidth(SearchWidth * scale);
        if (focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        var text = Graph.SearchText;
        var enter = ImGui.InputTextWithHint("##graph-search", "Search triggers and values", ref text, 128,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.EscapeClearsAll);
        ImGui.SetItemTooltip("Search the nicknames and the values of the triggers. Enter frames the next result, and " +
                             "Shift+Enter the one before. Shortcut: Ctrl+F.");
        Graph.SearchText = text;
        if (enter)
        {
            Graph.NextResult(ImGui.GetIO().KeyShift ? -1 : 1);
            focusSearch = true;
        }
    }

    /// <summary>The menu "Systems": the systems of the mission. A system fades the triggers of the other
    /// systems.</summary>
    private void SystemsMenu()
    {
        var on = Graph.SystemFilter != null;
        if (ToolbarItems.Toggle("systems", EditorIcons.Filter, "Systems: fade the triggers of other systems.", on) != on)
        {
            ImGui.OpenPopup("##systems");
        }

        if (!ImGui.BeginPopup("##systems"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        if (ImGui.MenuItem("All systems", "", Graph.SystemFilter == null))
        {
            Graph.SystemFilter = null;
        }

        foreach (var (system, triggers) in missions.Open is { } open ? MissionSummary.TriggerSystems(open.Index) : [])
        {
            var name = SystemName(system);
            var label = name != null ? $"{name} ({system})" : system;
            var count = triggers == 1 ? "1 trigger" : $"{triggers} triggers";
            if (ImGui.MenuItem($"{label}: {count}", "", string.Equals(Graph.SystemFilter, system,
                    StringComparison.OrdinalIgnoreCase)))
            {
                Graph.SystemFilter = system;
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The name of a system of the game data, or null when it has no name.</summary>
    private string? SystemName(string nickname) =>
        window.Session is { GameData: { } data } session && data.Items.Systems.Get(nickname) is { } system &&
        session.ItemName(system) is { Length: > 0 } name
            ? name
            : null;

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

        if (missions.OpenPath is not { } path || missions.Analysis(path) is not { } analysis)
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
        var partWidth = withScene
            ? MathF.Truncate(Math.Clamp(settings.MissionSplit, 0.1f, 0.9f) * (avail.X - gap))
            : avail.X;

        if (ImGui.BeginChild("##mission-part", new Vector2(partWidth, avail.Y), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (missions.ShowRaw)
            {
                RawPart(path);
            }
            else
            {
                GraphPart(analysis);
            }
        }

        ImGui.EndChild();
        if (!withScene)
        {
            return;
        }

        var split = settings.MissionSplit * Math.Max(1, avail.X - gap) / scale;
        var max = Math.Max(MinPart, (avail.X - gap) / scale - MinPart);
        if (Splitter.Draw("##split", origin + new Vector2(partWidth, 0), new Vector2(gap, avail.Y), true, ref split, 1,
                MinPart, max, DefaultSplit * (avail.X - gap) / scale))
        {
            settings.Save();
        }

        settings.MissionSplit = Math.Clamp(split * scale / Math.Max(1, avail.X - gap), 0.1f, 0.9f);
        ImGui.SetCursorScreenPos(origin + new Vector2(partWidth + gap, 0));
        if (ImGui.BeginChild("##scene", new Vector2(Math.Max(1, avail.X - partWidth - gap), avail.Y)))
        {
            ScenePart(elapsed);
        }

        ImGui.EndChild();
    }

    /// <summary>The graph part: the graph of the mission, and the keys of the graph while it has the focus.</summary>
    private void GraphPart(MissionAnalysis analysis)
    {
        if (missions.PendingGraphFrame)
        {
            missions.PendingGraphFrame = false;
            missions.PendingLine = null;
            Graph.FrameSelection();
        }

        Graph.Draw(analysis, ImGui.GetContentRegionAvail());
        Keys();
    }

    /// <summary>The keys of the graph (decision D558): F frames the selection, A or Home frame all, Ctrl+F goes to the
    /// search, and Esc ends the path view and the neighbor view. The node editor shortcuts that change data are off, so
    /// Delete does nothing.</summary>
    private void Keys()
    {
        var io = ImGui.GetIO();
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || io.WantTextInput)
        {
            return;
        }

        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false))
        {
            focusSearch = true;
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false) && Graph.HasFocus)
        {
            Graph.EndFocus();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F, false))
        {
            Graph.FrameSelection();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.A, false) || ImGui.IsKeyPressed(ImGuiKey.Home, false))
        {
            Graph.FrameAll();
        }
    }

    /// <summary>The raw part: the whole script as text, with the colors and the marks of the checks.</summary>
    private void RawPart(string path)
    {
        if (missions.RawTextOf(path) is not { } text)
        {
            if (missions.RawTextError(path) is { } error)
            {
                PanelStates.Message("The text of this mission script cannot show.", error);
            }
            else
            {
                PanelStates.Loading("Reading the mission script");
            }

            return;
        }

        if (text.CanonicalReason is { } reason)
        {
            // The file text was not possible, so the user learns why the comments of the file do not show.
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(CanonicalNote(reason));
            ImGui.PopTextWrapPos();
        }

        missions.PendingGraphFrame = false;
        Follow(path, text);
        raw.Draw("##raw", text, missions.MarksOf(path), ImGui.GetContentRegionAvail());
        FollowCursor(path, text);
    }

    /// <summary>The Scene part: the Scene toolbar strip and the Scene view.</summary>
    private void ScenePart(double elapsed)
    {
        var width = ImGui.GetContentRegionAvail().X;
        EditorLayout.Strip("##scene-toolbar", window.Scene.Toolbar, new Vector2(width, EditorLayout.StripHeight));
        window.Scene.Draw(elapsed);
    }

    /// <summary>The note of a raw view that shows the canonical text in place of the file text.</summary>
    /// <param name="reason">Why the file text was not possible, as a part of a sentence.</param>
    internal static string CanonicalNote(string reason) =>
        $"This view shows the text that the editor writes, without the comments of the file, because {reason}.";

    /// <summary>Scrolls the text to the line of a new selection or of a request of the Console.</summary>
    private void Follow(string path, RawText text)
    {
        if (!string.Equals(shownPath, path, StringComparison.OrdinalIgnoreCase))
        {
            shownPath = path;
            lastCursor = -1;
        }

        if (missions.PendingLine is var (target, entry) &&
            string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            missions.PendingLine = null;
            lastSelection = window.Selection;
            if (text.LineOf(target.Id, entry) is { } line)
            {
                raw.GoToLine(line);
                lastCursor = line;
            }

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
    private void FollowCursor(string path, RawText text)
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

        var item = MissionWorkspace.RefOf(analysis.Document.Sections[section].Ref, entry);
        window.Selection = item;
        lastSelection = item;
    }

    /// <summary>Forgets the graph and its state, for example when the profile closes.</summary>
    public void Reset()
    {
        Graph.Reset();
        State.Reset();
    }

    public void Dispose()
    {
        raw.Dispose();
        Graph.Dispose();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The Run panel of the Mission tab (section 4 of the M6c design): the buttons of the dry run, the facts about the
/// player, the events and facts that the active triggers wait for, and the Run log. It shows at the right of the graph
/// while the toggle "Dry run" is on. Each button adds one input to the dry run of the open mission, and "Back" removes
/// the last one.
/// </summary>
internal sealed class RunPanel(EditorWindow window, MissionWorkspace missions, GraphView graph)
{
    /// <summary>The width of the panel, in points.</summary>
    public const float Width = 340;

    /// <summary>The text of the panel before the first Start mission or Load checkpoint.</summary>
    public const string EmptyText = "Click Start mission or Load checkpoint to start a dry run.";

    /// <summary>The info icon of the dry run (section 8.5 of the M6c design).</summary>
    public static readonly HelpText Help = new("Dry run",
        "A step tool that runs the trigger rules of the retail game on this graph. It has no game world. You decide which " +
        "events happen. The graph shows which triggers wait, run and stop, in the order of the game. One event can match " +
        "two conditions in the game, for example a base and ANY. The dry run lists them as two events.",
        "Nothing.");

    // The tooltips of section 8.2 of the M6c design.
    internal const string StartTip = "Start mission: Start the start triggers of the mission.";
    internal const string LoadTip =
        "Load checkpoint: Start the restore trigger of a checkpoint, as a load of a saved game does.";
    internal const string RunTip =
        "Run trigger: Run the selected trigger now, as if its condition is met. Only a trigger that waits can run.";
    internal const string HeartbeatTip =
        "Heartbeat: Advance the time by 1/30 s. The game tests timers and some conditions at each heartbeat.";
    internal const string NextTimerTip = "Next timer: Advance the time until the next timer runs.";
    internal const string BackTip = "Back: Remove the last step.";
    internal const string ResetTip = "Reset: Remove all steps.";
    internal const string TimersTip = "The game picks a time in the range of a timer when the mission loads.";
    internal const string PlayerTip = "The facts about the player that some conditions read.";
    internal const string SystemTip = "Player system: Some conditions are true at once when the player is in their system.";
    internal const string SpaceTip = "Player is in space: Some timers count only in space.";
    internal const string WaitingTip =
        "The events that active triggers wait for. Click Happens when the event happens in your test.";
    internal const string HappensTip = "All active triggers that wait for this event react to it, in the order of the game.";
    internal const string IsTrueTip = "The game tests this condition at each heartbeat. The fact stays true until you clear it.";
    internal const string CauseTip = "The run made this event likely at that step.";
    internal const string FollowTip = "Frame the trigger of each new change.";
    internal const string LogTip = "The changes of the run, in order. Click a change to select its trigger.";

    /// <summary>The tooltip of a step button after the end of the mission or a stop of the run.</summary>
    internal const string OverTip = "The run is over. Click Start mission or Load checkpoint to start a new run.";

    /// <summary>The note of a fact that is true while no active trigger tests it.</summary>
    internal const string IdleFactNote = "No active trigger tests this fact now.";

    private readonly ValuePicker systemPicker = new();
    private bool pickSystem;
    private (int Version, DryRun Run, List<LogRow> Rows, IReadOnlyList<PanelRow> Waiting,
        IReadOnlyList<MissionTrigger> Checkpoints)? cache;
    private int scrolledSteps = -1;

    /// <summary>One row of the Run log: the title of a step, or a change under it.</summary>
    internal sealed record LogRow(string Text, bool Title, string? Trigger, bool Dim);

    /// <summary>One row of the world panel: a line that the active triggers wait for, or a fact that is true.</summary>
    /// <param name="Waits">True when an active trigger waits for the line. A fact that is true while no active trigger
    /// tests it has a row too, so the modder can clear it.</param>
    private sealed record PanelRow(MissionLine Line, string Sentence, WorldLineKind Kind, string? Note, bool Waits);

    /// <summary>Draws the panel into a region at the cursor.</summary>
    public void Draw(MissionAnalysis analysis, DryRun run, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Panel);
        var visible = ImGui.BeginChild("##run-panel", size, ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleColor();
        if (visible)
        {
            var rows = Rows(analysis, run);
            Header();
            Buttons(analysis, run, rows.Checkpoints);
            if (!run.Started)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextDisabled(EmptyText);
                ImGui.PopTextWrapPos();
            }

            Player(run);
            Waiting(rows.Waiting, run);
            Log(analysis, rows.Rows);
        }

        ImGui.EndChild();
    }

    private void Header()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{EditorIcons.Flask}  Dry run");
        ImGui.SameLine();
        InfoIcon.Draw("run-help", Help);
        ImGui.Separator();
    }

    private void Buttons(MissionAnalysis analysis, DryRun run, IReadOnlyList<MissionTrigger> checkpoints)
    {
        if (ImGui.Button("Start mission"))
        {
            missions.AddDryRunInput(DryRunInput.StartMission());
        }

        ImGui.SetItemTooltip(StartTip);
        ImGui.SameLine();
        ImGui.BeginDisabled(checkpoints.Count == 0);
        if (ImGui.Button($"Load checkpoint {EditorIcons.CaretDown}"))
        {
            ImGui.OpenPopup("##checkpoints");
        }

        ImGui.EndDisabled();
        DisabledTip(LoadTip);
        if (ImGui.BeginPopup("##checkpoints"))
        {
            EditorTheme.PushMenuColors();
            foreach (var checkpoint in checkpoints)
            {
                if (ImGui.MenuItem(RunOverlay.TriggerName(analysis, checkpoint.Nickname)))
                {
                    missions.AddDryRunInput(DryRunInput.LoadCheckpoint(checkpoint.Nickname));
                }
            }

            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // After the end of the mission or a stop, a step does nothing, so the step buttons tell why and stay off.
        var over = run.State.Machine.IsOver;
        var selected = SelectedTrigger(analysis);
        ImGui.BeginDisabled(selected == null || over);
        if (ImGui.Button("Run trigger") && selected != null)
        {
            missions.AddDryRunInput(DryRunInput.RunTrigger(selected.Nickname));
        }

        ImGui.EndDisabled();
        DisabledTip(over ? OverTip : RunTip);
        ImGui.SameLine();
        ImGui.BeginDisabled(over);
        if (ImGui.Button("Heartbeat"))
        {
            missions.AddDryRunInput(DryRunInput.Heartbeats(1));
        }

        DisabledTip(over ? OverTip : HeartbeatTip);
        ImGui.SameLine();
        if (ImGui.Button("Next timer"))
        {
            missions.AddDryRunInput(DryRunInput.NextTimer());
        }

        DisabledTip(over ? OverTip : NextTimerTip);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(run.Inputs.Count == 0);
        if (ImGui.Button($"{EditorIcons.StepBackward} Back"))
        {
            missions.DryRuns.Back(analysis);
        }

        DisabledTip(BackTip);
        ImGui.SameLine();
        if (ImGui.Button("Reset"))
        {
            missions.DryRuns.Reset(analysis);
        }

        DisabledTip(ResetTip);
        ImGui.EndDisabled();
        ImGui.SameLine();
        // The combo fits its longest text and its arrow.
        var timerTexts = new[] { "Timer ranges: Shortest", "Timer ranges: Longest" };
        ImGui.SetNextItemWidth(timerTexts.Max(t => ImGui.CalcTextSize(t).X) + ImGui.GetStyle().FramePadding.X * 2 +
                               ImGui.GetFrameHeight());
        if (ImGui.BeginCombo("##timers", timerTexts[run.Timers == TimerBound.Shortest ? 0 : 1]))
        {
            EditorTheme.PushMenuColors();
            foreach (var (bound, label) in new[] { (TimerBound.Shortest, "Shortest"), (TimerBound.Longest, "Longest") })
            {
                if (ImGui.Selectable(label, run.Timers == bound))
                {
                    missions.DryRuns.SetTimers(analysis, bound);
                }
            }

            EditorTheme.PopMenuColors();
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip(TimersTip);
        var follow = missions.DryRuns.Follow;
        if (ImGui.Checkbox("Follow", ref follow))
        {
            missions.DryRuns.Follow = follow;
        }

        ImGui.SetItemTooltip(FollowTip);
    }

    /// <summary>The tooltip of the last item, also while the item is disabled.</summary>
    private static void DisabledTip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(text);
        }
    }

    /// <summary>The group "Player": the system of the player with the system picker of M21, and "Player is in
    /// space".</summary>
    private void Player(DryRun run)
    {
        if (!ImGui.CollapsingHeader("Player", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetItemTooltip(PlayerTip);
            return;
        }

        ImGui.SetItemTooltip(PlayerTip);
        var world = run.State.World;
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Player system");
        ImGui.SetItemTooltip(SystemTip);
        ImGui.SameLine();
        var system = world.PlayerSystem;
        var shown = system == null ? "None" : window.Session?.SystemName(system) is { Length: > 0 } name ? $"{name} ({system})" : system;
        if (ImGui.Button($"{shown}##player-system"))
        {
            pickSystem = true;
        }

        ImGui.SetItemTooltip(SystemTip);
        var open = pickSystem;
        pickSystem = false;
        if (window.Session is { } session)
        {
            var picked = systemPicker.Draw("##player-system-picker", open, system ?? "", () =>
                session.Edits.Lists.List("system")?.Entries.Select(e => (e, Name(session, e))).ToList() ?? [],
                none: true, ObjectKinds.StarSystem);
            if (picked != null && !string.Equals(picked, system ?? "", StringComparison.OrdinalIgnoreCase))
            {
                missions.AddDryRunInput(DryRunInput.PlayerSystem(picked.Length == 0 ? null : picked));
            }
        }

        var inSpace = world.InSpace;
        if (ImGui.Checkbox("Player is in space", ref inSpace))
        {
            missions.AddDryRunInput(DryRunInput.PlayerInSpace(inSpace));
        }

        ImGui.SetItemTooltip(SpaceTip);
    }

    /// <summary>The display name of a list entry: the text of its name, or its nickname.</summary>
    private static string Name(EditorSession session, ListEntry entry) =>
        entry.Section != null && session.SectionName(entry.Section) is { Length: > 0 } name ? name : entry.Name ?? entry.Value;

    /// <summary>The group "Waiting events ({n})": one row for each line that the active triggers wait for. The count
    /// holds only these rows. After them, one row for each fact that is true while no active trigger tests it.</summary>
    private void Waiting(IReadOnlyList<PanelRow> rows, DryRun run)
    {
        var count = rows.Count(r => r.Waits).ToString(CultureInfo.InvariantCulture);
        var open = ImGui.CollapsingHeader($"Waiting events ({count})###waiting", ImGuiTreeNodeFlags.DefaultOpen);
        ImGui.SetItemTooltip(WaitingTip);
        if (!open)
        {
            return;
        }

        var world = run.State.World;
        var over = run.State.Machine.IsOver;
        ImGui.PushTextWrapPos(0);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ImGui.PushID(i);
            ImGui.TextWrapped(row.Sentence);
            if (row.Note != null)
            {
                ImGui.TextDisabled(row.Note);
                ImGui.SetItemTooltip(CauseTip);
            }

            if (!row.Waits)
            {
                ImGui.TextDisabled(IdleFactNote);
            }

            if (row.Kind is WorldLineKind.Event or WorldLineKind.EventAndFact)
            {
                ImGui.BeginDisabled(over);
                if (ImGui.SmallButton("Happens"))
                {
                    missions.AddDryRunInput(DryRunInput.Event(WorldEvents.PatternOf(row.Line)));
                }

                ImGui.EndDisabled();
                DisabledTip(over ? OverTip : HappensTip);
                if (row.Kind == WorldLineKind.EventAndFact)
                {
                    ImGui.SameLine();
                }
            }

            if (row.Kind is WorldLineKind.Fact or WorldLineKind.EventAndFact)
            {
                var on = world.IsFact(row.Line);
                if (ImGui.Checkbox("Is true", ref on))
                {
                    missions.AddDryRunInput(DryRunInput.Fact(WorldEvents.PatternOf(row.Line), on));
                }

                ImGui.SetItemTooltip(IsTrueTip);
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        ImGui.PopTextWrapPos();
    }

    /// <summary>The group "Run log": one row for each step and each change. A click on a change selects its trigger and
    /// frames it in the graph.</summary>
    private void Log(MissionAnalysis analysis, List<LogRow> rows)
    {
        var open = ImGui.CollapsingHeader("Run log", ImGuiTreeNodeFlags.DefaultOpen);
        ImGui.SetItemTooltip(LogTip);
        if (!open)
        {
            return;
        }

        // The log keeps a usable height when the list of waiting events is long. The panel then scrolls.
        var height = MathF.Max(ImGui.GetContentRegionAvail().Y, 150 * ImGuiHelper.Scale);
        if (ImGui.BeginChild("##run-log", new Vector2(0, height)))
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(rows.Count);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var row = rows[i];
                    ImGui.PushID(i);
                    if (row.Title)
                    {
                        ImGui.TextColored(EditorTheme.TextBright, row.Text);
                    }
                    else
                    {
                        ImGui.Indent();
                        if (row.Trigger != null)
                        {
                            if (ImGui.Selectable(row.Text))
                            {
                                Select(analysis, row.Trigger);
                            }
                        }
                        else if (row.Dim)
                        {
                            ImGui.TextDisabled(row.Text);
                        }
                        else
                        {
                            // One line for each row, so the clipper of the log can skip the rows that do not show.
                            ImGui.Text(row.Text);
                        }

                        ImGui.Unindent();
                    }

                    ImGui.PopID();
                }
            }

            clipper.End();
            // A new step scrolls the log to its end. A fold after an edit adds no step, so the log stays where it is.
            if (scrolledSteps != missions.DryRuns.Steps)
            {
                scrolledSteps = missions.DryRuns.Steps;
                ImGui.SetScrollHereY(1);
            }
        }

        ImGui.EndChild();
    }

    /// <summary>Selects a trigger of the mission and frames it in the graph.</summary>
    private void Select(MissionAnalysis analysis, string nickname)
    {
        if (analysis.Index.Trigger(nickname) is not { } trigger)
        {
            return;
        }

        window.Selection = new MissionRef(analysis.Script.Path, MissionItemKind.Trigger, trigger.Section.Ref);
        if (graph.Graph?.NodeOf(trigger) is { } node)
        {
            graph.FrameBlock(node);
        }
    }

    /// <summary>The trigger that the selection names in the mission: a trigger or a line of it, or null.</summary>
    private MissionTrigger? SelectedTrigger(MissionAnalysis analysis) =>
        window.Selection is MissionRef { Kind: MissionItemKind.Trigger or MissionItemKind.Row, Section: { } section } item &&
        string.Equals(item.Script, analysis.Script.Path, StringComparison.OrdinalIgnoreCase)
            ? analysis.Index.Triggers.FirstOrDefault(t => t.Section.Ref.Equals(section) && !t.IsDuplicate)
            : null;

    /// <summary>The rows of the log and of the world panel, and the checkpoints of the script. They stay until the dry
    /// run changes.</summary>
    private (List<LogRow> Rows, IReadOnlyList<PanelRow> Waiting, IReadOnlyList<MissionTrigger> Checkpoints) Rows(
        MissionAnalysis analysis, DryRun run)
    {
        var version = missions.DryRuns.Version;
        if (cache is var (v, r, rows, waiting, checkpoints) && v == version && ReferenceEquals(r, run))
        {
            return (rows, waiting, checkpoints);
        }

        rows = LogRows(analysis, run.State.Trace);
        waiting = PanelRows(run);
        checkpoints = DryRun.Checkpoints(run.Index);
        cache = (version, run, rows, waiting, checkpoints);
        return (rows, waiting, checkpoints);
    }

    /// <summary>The rows of the Run log (section 8.2 of the M6c design). A trigger of a prefab instance shows the name of
    /// the prefab first (UI rule 5), in the rows of the changes, the notes and the step causes.</summary>
    internal static List<LogRow> LogRows(MissionAnalysis analysis, MissionTrace trace)
    {
        string Name(string nickname) => RunOverlay.TriggerName(analysis, nickname);
        var rows = new List<LogRow>();
        foreach (var step in trace.Steps)
        {
            rows.Add(new LogRow(step.TitleWith(Name), true, null, false));
            foreach (var change in step.Changes)
            {
                if (change.TextWith(Name) is { } text)
                {
                    rows.Add(new LogRow(text, false, change.Trigger.Length > 0 ? change.Trigger : null,
                        change.Kind == TraceChangeKind.Note));
                }
            }
        }

        return rows;
    }

    /// <summary>The rows of the world panel: the waiting lines, then each fact that is true and that no active trigger
    /// waits for, so the modder can clear it.</summary>
    private static IReadOnlyList<PanelRow> PanelRows(DryRun run)
    {
        var state = run.State;
        var rows = state.Waiting.Select(w => new PanelRow(w.Line, w.Sentence, w.Kind, w.Cause?.Text, true)).ToList();
        var keys = rows.Select(r => WorldEvents.KeyOf(r.Line)).ToHashSet(StringComparer.Ordinal);
        foreach (var line in WorldEvents.FactLines(run.Index).Where(state.World.IsFact))
        {
            if (keys.Add(WorldEvents.KeyOf(line)))
            {
                rows.Add(new PanelRow(line, line.Sentence(), WorldLineKind.Fact, null, false));
            }
        }

        return rows;
    }
}

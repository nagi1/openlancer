using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Games;
using OpenLancer.Data.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>One row of the Missions tab (section 5.1 of the M6a design).</summary>
/// <param name="Slot">"M01a" to "M13", or "No slot".</param>
/// <param name="Errors">The errors that show.</param>
/// <param name="Warnings">The warnings that show.</param>
/// <param name="Hidden">The hidden base-game problems, or 0 while they show.</param>
/// <param name="Systems">The systems that the mission names: the system lines of the triggers and the items.</param>
internal sealed record MissionsBrowserRow(MissionScriptInfo Script, string Slot, string ScriptText, string StartStage,
    string StartTooltip, string AcceptStage, string AcceptTooltip, string NextMission, string NextTooltip, string State,
    string StateTooltip, int Triggers, int Errors, int Warnings, int Hidden, string OpenLancer, string OpenLancerTooltip,
    bool HasGaps, IReadOnlySet<string> Systems);

/// <summary>What a bundle row does on a double-click (section 5.2 of the M6a design).</summary>
internal enum MissionBundleAction
{
    /// <summary>Opens the mission.</summary>
    OpenMission,

    /// <summary>Opens the first file in the Cutscene tab.</summary>
    OpenCutscene,

    /// <summary>Selects the mission, so the Inspector shows the Mission page.</summary>
    SelectMission,

    /// <summary>No action in M6a. The tooltip says where the items are.</summary>
    None,
}

/// <summary>One row of the bundle of a mission (section 5.2 of the M6a design).</summary>
/// <param name="Files">The files of the row, for the row menu.</param>
internal sealed record MissionBundleRow(string Name, string Value, string? Tooltip, MissionBundleAction Action,
    IReadOnlyList<string> Files);

/// <summary>
/// The rows, columns, counts and bundles of the Missions tab (section 5 of the M6a design). The model makes no ImGui call,
/// so the tests check it (review finding RV12).
/// </summary>
internal static class MissionsBrowserModel
{
    /// <summary>The tooltips of the columns (section 13.1 of the M6a design).</summary>
    public static readonly IReadOnlyList<(string Name, string Tooltip)> Columns =
    [
        ("Slot", "The story slot. The game code has 14 slots in a fixed order."),
        ("Mission script", "The file that the game loads for this slot."),
        ("Start stage", "The story stage when the mission starts. News and rumors use the stage."),
        ("Accept stage", "The story stage after the player accepts the mission. A success adds 1 to it."),
        ("Next mission", "The time when the next mission starts after a success. The game code sets it. A mod cannot change it."),
        ("State", "Base game: no mod changes this script. Changed: a mod patch changes it. Replaced: a mod has its own file " +
                  "at this path. Missing: no file is at this path."),
        ("Triggers", "The number of triggers in the script."),
        ("Errors", "Problems that make the game stop, drop an item or ignore a line. The number in brackets counts the " +
                   "hidden base-game messages."),
        ("Warnings", "Problems that can give a result that you do not expect. The number in brackets counts the hidden " +
                     "base-game messages."),
        ("OpenLancer", "What the OpenLancer engine runs of this script. The retail game runs all lines."),
    ];

    /// <summary>The rows of the scripts: the slot scripts in slot order, then the other scripts in path order.</summary>
    /// <param name="showBaseGame">True while the base-game messages show.</param>
    public static IReadOnlyList<MissionsBrowserRow> Rows(MissionRunResult result, bool showBaseGame) =>
        result.Scripts.Select(s => Row(result, s, showBaseGame)).ToList();

    private static MissionsBrowserRow Row(MissionRunResult result, MissionScriptInfo script, bool showBaseGame)
    {
        var analysis = result.Of(script.Path);
        var slot = script.Slot;
        var problems = analysis?.Problems ?? [];
        var shown = problems.Where(p => showBaseGame || p.Origin?.IsBaseGame != true).ToList();
        var (openLancer, openLancerTip, gaps) = analysis == null ? ("", "", false) : OpenLancerOf(analysis.Index);
        var systems = analysis == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : SystemsOf(analysis.Index);
        return new MissionsBrowserRow(script, slot?.Slot ?? "No slot", MissionFiles.Display(script.Path),
            slot == null ? "" : Number(slot.StartStage), slot == null ? "" : StageTooltip(slot.StartStage),
            slot == null ? "" : Number(slot.AcceptStage), slot == null ? "" : StageTooltip(slot.AcceptStage),
            slot == null ? "" : NextText(slot.Next), slot == null ? "" : NextTooltip(slot.Next),
            StateText(script), StateTooltip(script),
            analysis?.Index.Triggers.Count ?? 0,
            shown.Count(p => p.Severity == DiagnosticSeverity.Error),
            shown.Count(p => p.Severity == DiagnosticSeverity.Warning),
            showBaseGame ? 0 : problems.Count(p => p.Origin?.IsBaseGame == true),
            openLancer, openLancerTip, gaps, systems);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string StageTooltip(int stage) =>
        $"The story stage {StoryStages.NameOf(stage)} ({stage}). The game code sets it for this slot.";

    /// <summary>The text of a next rule: "At once", "Worth grows by {credits}" or "The story ends".</summary>
    public static string NextText(NextMission next) => next.Kind switch
    {
        NextMissionKind.AtOnce => "At once",
        NextMissionKind.Worth => string.Create(CultureInfo.InvariantCulture, $"Worth grows by {next.Credits:N0}"),
        _ => "The story ends",
    };

    private static string NextTooltip(NextMission next) => next.Kind switch
    {
        NextMissionKind.AtOnce => "The next mission starts at the success.",
        NextMissionKind.Worth => "The next mission starts when the worth of the player grows by this amount after the " +
                                 "success. Worth is the money plus the value of the ship and the cargo.",
        _ => "This is the last story mission.",
    };

    /// <summary>The text of a state: "Base game", "Changed by {mod}", "Replaced by {mod}" or "Missing".</summary>
    public static string StateText(MissionScriptInfo script) => script.State switch
    {
        MissionScriptState.BaseGame => "Base game",
        MissionScriptState.Changed => $"Changed by {script.ModId}",
        MissionScriptState.Replaced => $"Replaced by {script.ModId}",
        _ => "Missing",
    };

    private static string StateTooltip(MissionScriptInfo script) => script.State switch
    {
        MissionScriptState.BaseGame => "No mod changes this script.",
        MissionScriptState.Changed => $"A patch of mod '{script.ModId}' changes this script.",
        MissionScriptState.Replaced => $"Mod '{script.ModId}' has its own file at this path.",
        _ => "No file is at this path. The game cannot run this mission.",
    };

    /// <summary>
    /// The OpenLancer column (section 5.1 of the M6a design): "Runs", "{n} lines do nothing" or "Does not load", with a
    /// tooltip that lists the names with no effect and the blockers, blockers first.
    /// </summary>
    public static (string Text, string Tooltip, bool Gaps) OpenLancerOf(MissionIndex index)
    {
        var rows = index.Triggers.Where(t => !t.IsDuplicate).SelectMany(t => t.Rows).ToList();
        var blockers = rows.Where(r => r.Mark == EngineMark.CannotLoad).GroupBy(r => r.Name?.Name ?? r.Key)
            .Select(g => (g.Key, g.Count())).ToList();
        var nothing = rows.Where(r => r.Mark == EngineMark.NoEffect).GroupBy(r => r.Name?.Name ?? r.Key)
            .Select(g => (g.Key, g.Count())).OrderByDescending(p => p.Item2).ToList();
        if (blockers.Count == 0 && nothing.Count == 0)
            return ("Runs", "OpenLancer runs all lines of this script.", false);
        var lines = blockers.Select(b => $"{b.Key}: cannot load ({Lines(b.Item2)})")
            .Concat(nothing.Select(n => $"{n.Key}: no effect ({Lines(n.Item2)})"));
        var tooltip = string.Join("\n", lines);
        if (blockers.Count > 0)
            return ("Does not load", "OpenLancer stops when it loads this script. The retail game runs it.\n" + tooltip, true);
        var count = nothing.Sum(n => n.Item2);
        return (count == 1 ? "1 line does nothing" : $"{count} lines do nothing",
            "OpenLancer reads these lines but does nothing with them. The retail game runs them.\n" + tooltip, true);
    }

    private static string Lines(int count) => count == 1 ? "1 line" : $"{count} lines";

    /// <summary>The systems that a mission names: the system lines of its triggers and of its items.</summary>
    public static HashSet<string> SystemsOf(MissionIndex index)
    {
        var systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in index.Triggers)
        {
            if (trigger.SystemHint is { } hint && !hint.Equals("ANY", StringComparison.OrdinalIgnoreCase))
                systems.Add(hint);
        }
        foreach (var section in index.Document.Sections)
        {
            if (index.SystemOf(section) is { } system)
                systems.Add(system);
        }
        return systems;
    }

    /// <summary>True when the row matches the search text: the slot, the path or the name of a trigger or a
    /// value.</summary>
    public static bool Matches(MissionsBrowserRow row, MissionAnalysis? analysis, string search) =>
        search.Length == 0 || row.Slot.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.ScriptText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        analysis != null && MatchingTriggers(analysis, search).Count > 0;

    /// <summary>The triggers whose nickname or a value of a line holds the search text, in file order.</summary>
    public static IReadOnlyList<MissionTrigger> MatchingTriggers(MissionAnalysis analysis, string search)
    {
        if (search.Length == 0)
            return [];
        return analysis.Index.Triggers.Where(t =>
            t.Nickname.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            t.Section.Section.Any(e => e.Any(v => MissionIndex.Text(v).Contains(search, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    /// <summary>The bundle of a mission (section 5.2 of the M6a design): script, mission ships, base scenes, space
    /// cutscenes, voice lines, text, and news and rumors.</summary>
    public static IReadOnlyList<MissionBundleRow> Bundle(MissionRunResult result, MissionAnalysis analysis)
    {
        var index = analysis.Index;
        var rows = new List<MissionBundleRow>
        {
            new("Script", MissionFiles.Display(analysis.Script.Path), "Double-click to open the mission.",
                MissionBundleAction.OpenMission, [analysis.Script.Path]),
            new("Mission ships", analysis.ShipBuilds == 1 ? "1 ship build" : $"{analysis.ShipBuilds} ship builds",
                analysis.ShipFile is { } shipFile ? $"The ship builds of {MissionFiles.Display(shipFile)}." : "This mission names no mission ship file.",
                MissionBundleAction.OpenMission, analysis.ShipFile is { } file ? [file] : []),
        };
        var scenes = Values(index, "Act_AddRTC");
        rows.Add(new MissionBundleRow("Base scenes", Files(scenes.Count), string.Join("\n", scenes),
            MissionBundleAction.OpenMission, scenes));
        var cutscenes = Values(index, "Act_CallThorn");
        rows.Add(new MissionBundleRow("Space cutscenes", Files(cutscenes.Count), string.Join("\n", cutscenes),
            MissionBundleAction.OpenCutscene, cutscenes));
        var comms = index.Lines.SelectMany(l => l.References).Where(r => r.Kind == MissionValueKind.CommLine)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = comms.Count(g => g.Any(r => r.State == MissionReferenceState.Missing));
        rows.Add(new MissionBundleRow("Voice lines",
            $"{(comms.Count == 1 ? "1 comm line" : $"{comms.Count} comm lines")}, {missing} missing",
            "The comm lines that the script names, and the comm lines that no voices file has.",
            MissionBundleAction.SelectMission, []));
        var texts = index.TextIds();
        rows.Add(new MissionBundleRow("Text", texts.Count == 1 ? "1 text id" : $"{texts.Count} text ids",
            "The text ids that the script names.", MissionBundleAction.SelectMission, []));
        if (analysis.Script.Slot is { } slot)
        {
            var (news, rumors) = StoryItems(result, slot);
            rows.Add(new MissionBundleRow("News and rumors",
                $"{(news == 1 ? "1 news item" : $"{news} news items")}, {(rumors == 1 ? "1 rumor" : $"{rumors} rumors")}",
                "The news items are in DATA\\MISSIONS\\news.ini. The rumors are in DATA\\MISSIONS\\mBases.ini. A later " +
                "version shows them.", MissionBundleAction.None, []));
        }
        return rows;
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>The distinct values 0 of the lines of an action, in file order.</summary>
    private static List<string> Values(MissionIndex index, string action) =>
        index.Lines.Where(l => l.Trigger != null && l.Key.Equals(action, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Value(0)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The news items and rumors whose stage range overlaps the stage range of a slot: from its start stage
    /// to the start stage of the next slot.</summary>
    public static (int News, int Rumors) StoryItems(MissionRunResult result, StorySlot slot)
    {
        var slots = MissionSchema.Retail.StorySlots;
        var at = slots.ToList().FindIndex(s => s.Slot == slot.Slot);
        var end = at >= 0 && at + 1 < slots.Count ? slots[at + 1].StartStage : StoryStages.Names.Count;
        var items = result.StoryItems.Where(i => i.From < end && i.To >= slot.StartStage).ToList();
        return (items.Count(i => !i.Rumor), items.Count(i => i.Rumor));
    }

    /// <summary>The label of the base game (section 5.1 of the M6a design): "Vanilla 1.0", "Vanilla 1.1", "HD Edition
    /// {version}" or "Unknown version".</summary>
    public static string BaseLabel(GameReport? report) => report switch
    {
        { Kind: GameKind.Vanilla, Version: { } version } => $"Base game: Vanilla {version}",
        { Kind: GameKind.HdEdition, Version: { } version } => $"Base game: HD Edition {version}",
        _ => "Base game: Unknown version",
    };
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Games;

namespace OpenLancer.Data.Missions;

/// <summary>The summary of one mission script of a run (section 5.1 of the M6a design): a row of the Missions tab.</summary>
/// <param name="Slot">"M01a" to "M13", or "No slot".</param>
/// <param name="ScriptText">The install path for display.</param>
/// <param name="Errors">The errors that show.</param>
/// <param name="Warnings">The warnings that show.</param>
/// <param name="Hidden">The hidden base-game problems, or 0 while they show.</param>
/// <param name="Systems">The systems that the mission names: the system lines of the triggers and the items.</param>
public sealed record MissionRow(MissionScriptInfo Script, string Slot, string ScriptText, string StartStage,
    string StartTooltip, string AcceptStage, string AcceptTooltip, string NextMission, string NextTooltip, string State,
    string StateTooltip, int Triggers, int Errors, int Warnings, int Hidden, OpenLancerReport OpenLancer,
    IReadOnlySet<string> Systems)
{
    /// <summary>True when OpenLancer does not run each line of the script.</summary>
    public bool HasGaps => OpenLancer.HasGaps;
}

/// <summary>What a bundle row opens (section 5.2 of the M6a design).</summary>
public enum MissionBundleAction
{
    /// <summary>Opens the mission.</summary>
    OpenMission,

    /// <summary>Opens the first file in the Cutscene tab. The row menu lists each file.</summary>
    OpenCutscene,

    /// <summary>Selects the mission and opens a section of the Mission page.</summary>
    ShowSection,

    /// <summary>No action in M6a. The tooltip says where the items are.</summary>
    None,
}

/// <summary>One row of the bundle of a mission (section 5.2 of the M6a design).</summary>
/// <param name="Tooltip">What the row holds, without what it does.</param>
/// <param name="Files">The files of the row, for the row menu of a row that opens files.</param>
/// <param name="Section">The id of the Mission page section that a <see cref="MissionBundleAction.ShowSection"/> row
/// opens, or null.</param>
public sealed record MissionBundleRow(string Name, string Value, string Tooltip, MissionBundleAction Action,
    IReadOnlyList<string> Files, string? Section = null)
{
    /// <summary>What the row does, as an infinitive without "to", for example "open the mission". Null for a row
    /// without an action. A view adds its gesture, for example "Double-click to open the mission."</summary>
    public string? ActionPhrase => Action switch
    {
        MissionBundleAction.OpenMission => "open the mission",
        MissionBundleAction.OpenCutscene when Files.Count > 1 => "open the first file in the Cutscene tab",
        MissionBundleAction.OpenCutscene when Files.Count == 1 => "open the file in the Cutscene tab",
        MissionBundleAction.ShowSection => "show the list on the Mission page",
        _ => null,
    };
}

/// <summary>A name or a group of lines that the OpenLancer engine does not run, with its line count.</summary>
/// <param name="Name">The condition or action name, or the name of the group of lines that never run.</param>
public sealed record OpenLancerGap(string Name, int Lines, EngineMark Mark)
{
    /// <summary>The state of the lines, for example "no effect (2 lines)".</summary>
    public string Text => $"{MarkText(Mark)} ({(Lines == 1 ? "1 line" : $"{Lines} lines")})";

    /// <summary>The words of a mark: "cannot load", "no effect" or "never runs".</summary>
    public static string MarkText(EngineMark mark) => mark switch
    {
        EngineMark.CannotLoad => "cannot load",
        EngineMark.NoEffect => "no effect",
        EngineMark.NeverRuns => "never runs",
        _ => "runs",
    };
}

/// <summary>What the OpenLancer engine runs of a script (sections 5.1 and 14.8 of the M6a design).</summary>
/// <param name="Text">"Runs", "{n} lines do nothing" or "Does not load".</param>
/// <param name="Tooltip">The meaning of the text, then one line for each gap.</param>
/// <param name="Gaps">The gaps, the blockers first.</param>
public sealed record OpenLancerReport(string Text, string Tooltip, IReadOnlyList<OpenLancerGap> Gaps)
{
    public bool HasGaps => Gaps.Count > 0;
}

/// <summary>A text id that a script names, with its group: "Title", "Offer", "Objective", "Fail text" or "Log
/// line".</summary>
public sealed record MissionTextId(string Group, string Id);

/// <summary>A line of a mission script that names an object of a system, a base or a zone (section 10.18 of the M6a
/// design).</summary>
public sealed record MissionObjectUse(MissionScriptInfo Script, MissionLine Line)
{
    /// <summary>The text of the row: "{slot}: {trigger nickname} ({plain name})". A script without a slot gives its
    /// path.</summary>
    public string Text =>
        $"{Script.Slot?.Slot ?? MissionFiles.Display(Script.Path)}: " +
        $"{Line.Trigger?.Nickname ?? Line.Section.Nickname ?? Line.Section.Ref.Id.ToString()} ({Line.Name?.Plain ?? Line.Key})";
}

/// <summary>
/// The queries of the mission studio over a run of the mission checks (sections 5 and 10.2 of the M6a design): the rows
/// of the Missions tab, the OpenLancer report, the systems, the search, the bundle, the news and rumors, the text ids and
/// the comm lines. The queries make no ImGui call and read no session, so the editor views, the tests and later an agent
/// server (roadmap row MCP1) call the same code.
/// </summary>
public static class MissionSummary
{
    /// <summary>The name of the group of the actions that never run (see <see cref="EngineMark.NeverRuns"/>).</summary>
    public const string NeverRunsGroup = "Actions after a condition with no effect";

    /// <summary>The rows of the scripts: the slot scripts in slot order, then the other scripts in path order.</summary>
    /// <param name="showBaseGame">True while the base-game messages show.</param>
    public static IReadOnlyList<MissionRow> Rows(MissionRunResult result, bool showBaseGame) =>
        result.Scripts.Select(s => Row(result, s, showBaseGame)).ToList();

    /// <summary>The row of one script.</summary>
    public static MissionRow Row(MissionRunResult result, MissionScriptInfo script, bool showBaseGame)
    {
        var analysis = result.Of(script.Path);
        var slot = script.Slot;
        var problems = analysis?.Problems ?? [];
        var shown = BaseGameMessages.Shown(problems, showBaseGame);
        return new MissionRow(script, slot?.Slot ?? "No slot", MissionFiles.Display(script.Path),
            slot == null ? "" : Number(slot.StartStage), slot == null ? "" : StageTooltip(slot.StartStage),
            slot == null ? "" : Number(slot.AcceptStage), slot == null ? "" : StageTooltip(slot.AcceptStage),
            slot == null ? "" : NextText(slot.Next), slot == null ? "" : NextTooltip(slot.Next),
            StateText(script), StateTooltip(script),
            analysis?.Index.Triggers.Count ?? 0,
            shown.Count(p => p.Severity == DiagnosticSeverity.Error),
            shown.Count(p => p.Severity == DiagnosticSeverity.Warning),
            BaseGameMessages.Hidden(problems, showBaseGame),
            analysis == null ? new OpenLancerReport("", "", []) : OpenLancer(analysis.Index),
            analysis == null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : Systems(analysis.Index));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The text of a story stage: "{stage name} ({number})".</summary>
    public static string Stage(int stage) => $"{StoryStages.NameOf(stage)} ({Number(stage)})";

    /// <summary>The tooltip of a stage of a slot.</summary>
    public static string StageTooltip(int stage) =>
        $"The story stage {Stage(stage)}. The game code sets it for this slot.";

    /// <summary>The text of a next rule: "At once", "Worth grows by {credits}" or "The story ends".</summary>
    public static string NextText(NextMission next) => next.Kind switch
    {
        NextMissionKind.AtOnce => "At once",
        NextMissionKind.Worth => string.Create(CultureInfo.InvariantCulture, $"Worth grows by {next.Credits:N0}"),
        _ => "The story ends",
    };

    /// <summary>The meaning of a next rule (section 13.1 of the M6a design).</summary>
    public static string NextTooltip(NextMission next) => next.Kind switch
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

    /// <summary>The meaning of a state, with the mod of a changed or replaced script.</summary>
    public static string StateTooltip(MissionScriptInfo script) => script.State switch
    {
        MissionScriptState.BaseGame => "No mod changes this script.",
        MissionScriptState.Changed => $"A patch of mod '{script.ModId}' changes this script.",
        MissionScriptState.Replaced => $"Mod '{script.ModId}' has its own file at this path.",
        _ => "No file is at this path. The game cannot run this mission.",
    };

    /// <summary>
    /// What the OpenLancer engine runs of a script (section 14.8 of the M6a design): "Runs", "{n} lines do nothing" or
    /// "Does not load". The gaps list the names that cannot load first, then the names with no effect, then the actions
    /// that never run because the condition of their trigger has no effect.
    /// </summary>
    public static OpenLancerReport OpenLancer(MissionIndex index)
    {
        var rows = index.Triggers.Where(t => !t.IsDuplicate).SelectMany(t => t.Rows).ToList();
        List<OpenLancerGap> Group(EngineMark mark) => rows.Where(r => r.Mark == mark)
            .GroupBy(r => r.Name?.Name ?? r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OpenLancerGap(g.Key, g.Count(), mark)).OrderByDescending(g => g.Lines).ToList();
        var blockers = Group(EngineMark.CannotLoad);
        var nothing = Group(EngineMark.NoEffect);
        var never = rows.Count(r => r.Mark == EngineMark.NeverRuns);
        var gaps = blockers.Concat(nothing).ToList();
        if (never > 0)
            gaps.Add(new OpenLancerGap(NeverRunsGroup, never, EngineMark.NeverRuns));
        if (gaps.Count == 0)
            return new OpenLancerReport("Runs", "OpenLancer runs all lines of this script.", gaps);
        var list = string.Join("\n", gaps.Select(g => $"{g.Name}: {g.Text}"));
        if (blockers.Count > 0)
            return new OpenLancerReport("Does not load",
                "OpenLancer stops when it loads this script. The retail game runs it.\n" + list, gaps);
        var count = gaps.Sum(g => g.Lines);
        return new OpenLancerReport(count == 1 ? "1 line does nothing" : $"{count} lines do nothing",
            "OpenLancer reads these lines but does nothing with them. The retail game runs them.\n" + list, gaps);
    }

    /// <summary>The systems that a mission names: the system lines of its triggers and of its items, without
    /// <c>ANY</c>.</summary>
    public static IReadOnlySet<string> Systems(MissionIndex index)
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

    /// <summary>
    /// The lines of all mission scripts of a run that name an object of a system, a base or a zone by its nickname, in
    /// the order of the scripts and of the lines (section 10.18 of the M6a design). The Behaviors section of a system
    /// object and the Used by sections of a base and of a zone list them.
    /// </summary>
    public static IReadOnlyList<MissionObjectUse> ObjectUses(MissionRunResult result, string nickname) =>
        result.Scripts.Select(s => result.Of(s.Path)).OfType<MissionAnalysis>()
            .SelectMany(a => MissionUses.NamingObject(a.Index, nickname).Select(l => new MissionObjectUse(a.Script, l)))
            .ToList();

    /// <summary>
    /// The systems that the triggers of a mission name, each with its trigger count, the most first (the menu "Mission
    /// systems", section 7.4 of the M6a design). Ties keep the order of the first trigger that names the system.
    /// <c>ANY</c> does not count.
    /// </summary>
    public static IReadOnlyList<(string System, int Triggers)> TriggerSystems(MissionIndex index) =>
        index.Triggers.Where(t => !t.IsDuplicate && t.SystemHint is { } hint && !MissionIndex.Is(hint, "ANY"))
            .GroupBy(t => t.SystemHint!, StringComparer.OrdinalIgnoreCase)
            .Select((g, order) => (System: g.Key, Triggers: g.Count(), Order: order))
            .OrderByDescending(s => s.Triggers).ThenBy(s => s.Order)
            .Select(s => (s.System, s.Triggers)).ToList();

    /// <summary>
    /// The system that the Scene opens when a mission opens and no system is open (section 7.1 of the M6a design): the
    /// system of the first start trigger that has one, else the system that most triggers name. Null when no trigger
    /// names a system.
    /// </summary>
    public static string? StartSystem(MissionIndex index) =>
        index.StartTriggers.Select(t => t.SystemHint).FirstOrDefault(h => h != null && !MissionIndex.Is(h, "ANY")) ??
        TriggerSystems(index).Select(s => s.System).FirstOrDefault();

    /// <summary>True when the row matches the search text: the slot, the path, or a trigger of the search.</summary>
    /// <param name="triggers">The triggers that match the search (see <see cref="MatchingTriggers"/>).</param>
    public static bool Matches(MissionRow row, IReadOnlyList<MissionTrigger> triggers, string search) =>
        search.Length == 0 || row.Slot.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.ScriptText.Contains(search, StringComparison.OrdinalIgnoreCase) || triggers.Count > 0;

    /// <summary>The triggers whose nickname or a value of a line holds the search text, in file order. None for an empty
    /// search.</summary>
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
            new("Script", MissionFiles.Display(analysis.Script.Path), "The mission script.",
                MissionBundleAction.OpenMission, [analysis.Script.Path]),
            new("Mission ships", analysis.ShipBuilds == 1 ? "1 ship build" : $"{analysis.ShipBuilds} ship builds",
                analysis.ShipFile is { } shipFile
                    ? $"The ship builds of {MissionFiles.Display(shipFile)}."
                    : "This mission names no mission ship file.",
                MissionBundleAction.OpenMission, analysis.ShipFile is { } file ? [file] : []),
        };
        var scenes = Values(index, "Act_AddRTC");
        rows.Add(new MissionBundleRow("Base scenes", Files(scenes.Count), FileList(scenes), MissionBundleAction.OpenMission,
            scenes));
        var cutscenes = Values(index, "Act_CallThorn");
        rows.Add(new MissionBundleRow("Space cutscenes", Files(cutscenes.Count), FileList(cutscenes),
            cutscenes.Count > 0 ? MissionBundleAction.OpenCutscene : MissionBundleAction.None, cutscenes));
        var comms = CommLines(index);
        var missing = comms.Count(name => index.Lines.SelectMany(l => l.References)
            .Any(r => r.Kind == MissionValueKind.CommLine && r.State == MissionReferenceState.Missing &&
                      r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        rows.Add(new MissionBundleRow("Voice lines",
            $"{(comms.Count == 1 ? "1 comm line" : $"{comms.Count} comm lines")}, {missing} missing",
            "The comm lines that the script names, and the comm lines that no voices file has.",
            MissionBundleAction.ShowSection, [], "voice"));
        var texts = index.TextIds();
        rows.Add(new MissionBundleRow("Text", texts.Count == 1 ? "1 text id" : $"{texts.Count} text ids",
            "The text ids that the script names.", MissionBundleAction.ShowSection, [], "text"));
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

    private static string FileList(IReadOnlyList<string> files) =>
        files.Count == 0 ? "The script names no such file." : string.Join("\n", files);

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

    /// <summary>The comm lines that a script names, in file order, without letter case.</summary>
    public static IReadOnlyList<string> CommLines(MissionIndex index) =>
        index.Lines.SelectMany(l => l.References).Where(r => r.Kind == MissionValueKind.CommLine)
            .Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The text ids that a script names, grouped as titles, offers, objectives, fail texts and log lines, in
    /// this order and in file order in a group.</summary>
    public static IReadOnlyList<MissionTextId> TextIds(MissionIndex index)
    {
        var result = new List<MissionTextId>();
        void Add(string group, IEnumerable<string?> ids)
        {
            foreach (var id in ids.OfType<string>().Distinct())
                result.Add(new MissionTextId(group, id));
        }
        var lines = index.Triggers.Where(t => !t.IsDuplicate).SelectMany(t => t.Actions).ToList();
        Add("Title", lines.Where(l => MissionIndex.Is(l.Key, "Act_SetTitle")).Select(l => l.Value(0)));
        Add("Offer", lines.Where(l => MissionIndex.Is(l.Key, "Act_SetOffer")).Select(l => l.Value(0)));
        Add("Objective", index.Document.OfKind(MissionSectionKind.Objective).SelectMany(o => o.Section)
            .Where(e => MissionIndex.Is(e.Name, "type")).SelectMany(MissionIndex.ObjectiveTextIds));
        Add("Fail text", lines.Where(l => MissionIndex.Is(l.Key, "Act_ChangeState") && MissionIndex.Is(l.Value(0), "FAIL"))
            .Select(l => l.Value(1)));
        Add("Log line", lines.Where(l => MissionIndex.Is(l.Key, "Act_NNIds")).Select(l => l.Value(0)));
        return result;
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

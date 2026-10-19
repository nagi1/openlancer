using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>One field of a page section: a plain name and a value, with an optional help text and an item to go
/// to.</summary>
/// <param name="Link">The item that a click on the field selects, or null.</param>
/// <param name="Muted">True for a base-game problem that shows muted.</param>
internal sealed record PageField(string Label, string Value, string? Help = null, object? Link = null, bool Muted = false,
    DiagnosticSeverity? Severity = null);

/// <summary>One component section of a mission page (section 10.1 of the M6a design).</summary>
/// <param name="Id">The id of the section, for the open state in the settings.</param>
/// <param name="Notes">Lines under the fields, for example the special rules of a slot.</param>
/// <param name="Advanced">True for the closed Advanced section.</param>
internal sealed record PageSection(string Id, string Title, HelpText? Help, IReadOnlyList<PageField> Fields,
    IReadOnlyList<string> Notes, bool Advanced = false);

/// <summary>
/// The model of a mission page of the Inspector (section 10.1 of the M6a design): the header, the read-only note, the
/// completeness report, the component sections and the INI block. The page draws from it. The model makes no ImGui
/// call, so the tests check it (review finding RV12).
/// </summary>
/// <param name="Title">The title of the header line, for example "M04: {title text}".</param>
/// <param name="Kind">The kind label of the header line, for example "Mission".</param>
/// <param name="Help">The text of the info icon of the header line.</param>
/// <param name="Completeness">The completeness report, or null for an item without parts.</param>
/// <param name="Ini">The section of the INI block, or null.</param>
internal sealed record MissionPageModel(string Title, string Kind, HelpText Help, CompletenessReport? Completeness,
    IReadOnlyList<PageSection> Sections, SectionRef? Ini)
{
    /// <summary>The read-only note of each mission page (section 13.1 of the M6a design).</summary>
    public const string ReadOnlyNote = "This editor version shows mission scripts but does not change them.";

    /// <summary>The note of the keys of <c>[Mission]</c> that the game does not use (decision D426).</summary>
    public const string UnusedMissionKey =
        "The game does not use this value. Mission titles and offers come from 'Set mission title' and 'Set mission " +
        "offer text'.";

    /// <summary>The section of an id, or null.</summary>
    public PageSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    /// <summary>The Mission page (section 10.2 of the M6a design).</summary>
    /// <param name="text">Gives the text of a text id, or an empty text.</param>
    /// <param name="commFile">Gives the voices file of a comm line, or null when no voices file has it.</param>
    /// <param name="showBaseGame">True while the base-game messages show.</param>
    /// <param name="hidesOpenLancer">True when the active mod has a targets list without OpenLancer (UI rule 4).</param>
    public static MissionPageModel Mission(MissionRunResult result, MissionAnalysis analysis, Func<int, string> text,
        Func<string, string?> commFile, bool showBaseGame, bool hidesOpenLancer)
    {
        var schema = MissionSchema.Retail;
        var index = analysis.Index;
        var slot = analysis.Script.Slot;
        var title = slot != null
            ? $"{slot.Slot}: {Text(text, slot.TitleId) ?? MissionFiles.Display(analysis.Script.Path)}"
            : MissionFiles.Display(analysis.Script.Path);
        var sections = new List<PageSection>();

        if (slot != null)
        {
            sections.Add(new PageSection("slot", "Story slot", schema.Help["storySlot"],
            [
                new("Slot", slot.Slot, "The story slot. The game code has 14 slots in a fixed order."),
                new("Script", MissionFiles.Display(analysis.Script.Path), "The file that the game loads for this slot."),
                new("Start stage", Stage(slot.StartStage), "The story stage when the mission starts. News and rumors use the stage."),
                new("Accept stage", Stage(slot.AcceptStage), "The story stage after the player accepts the mission. A success adds 1 to it."),
                new("Next mission", MissionsBrowserModel.NextText(slot.Next),
                    "The time when the next mission starts after a success. The game code sets it. A mod cannot change it."),
                new("Title text", Text(text, slot.TitleId) is { } titleText ? $"{titleText} ({slot.TitleId})" : Number(slot.TitleId),
                    "The title of the mission that the game code shows."),
            ], slot.Notes.Select(n => schema.SlotNotes.GetValueOrDefault(n) ?? n).ToList()));
        }

        sections.Add(Overview(analysis));
        sections.Add(Problems(analysis, showBaseGame));
        if (!hidesOpenLancer)
            sections.Add(OpenLancer(index));
        sections.Add(VoiceLines(index, commFile));
        sections.Add(Texts(index, text));
        sections.Add(new PageSection("files", "Files", schema.Help["missionFiles"],
            MissionsBrowserModel.Bundle(result, analysis).Select(b => new PageField(b.Name, b.Value, b.Tooltip)).ToList(), []));

        var header = analysis.Document.OfKind(MissionSectionKind.Mission).FirstOrDefault();
        var advanced = new List<PageField>();
        foreach (var key in new[] { ("mission_title", "Mission title"), ("mission_offer", "Mission offer"), ("reward", "Reward") })
        {
            if (header?.Section.FirstOrDefault(e => e.Name.Equals(key.Item1, StringComparison.OrdinalIgnoreCase)) is { } line)
                advanced.Add(new PageField(key.Item2, string.Join(", ", line.Select(MissionIndex.Text)), UnusedMissionKey));
        }
        if (advanced.Count > 0)
            sections.Add(new PageSection("advanced", "Advanced", null, advanced, [], Advanced: true));

        return new MissionPageModel(title, "Mission", schema.Help["mission"], MissionCompleteness.OfMission(analysis),
            sections, header?.Ref);
    }

    /// <summary>The page of another item of a mission in M6a: the header, the read-only note, the problems of its
    /// section and its INI block. M6a-2 gives each kind its own page.</summary>
    public static MissionPageModel Item(MissionAnalysis analysis, MissionRef item, bool showBaseGame)
    {
        var schema = MissionSchema.Retail;
        var section = item.Section is { } target ? analysis.Document.Find(target) : null;
        var (kind, help) = KindOf(item.Kind, schema);
        var title = section?.Nickname ?? section?.Ref.Id.ToString() ?? item.Key ?? MissionFiles.Display(item.Script);
        if (item.Row >= 0 && section != null && item.Row < section.Section.Count)
            title = $"{title}: {section.Section[item.Row].Name}";
        var problems = analysis.Problems.Where(p => section != null && Equals(p.Target, section.Ref) &&
                                                    (item.Row < 0 || p.EntryIndex == item.Row || p.EntryIndex < 0)).ToList();
        var sections = new List<PageSection>
        {
            new("problems", "Problems", schema.Help["problems"], ProblemFields(problems, showBaseGame),
                HiddenNote(problems, showBaseGame)),
        };
        if (section?.Kind == MissionSectionKind.Trigger && analysis.Index.Trigger(section.Nickname ?? "") is { } trigger &&
            trigger.Section == section)
        {
            sections.Insert(0, new PageSection("trigger", "Trigger", schema.Help["triggerComponent"],
            [
                new("Starts with the mission", trigger.StartsActive ? "Yes" : "No",
                    "The game starts this trigger when the mission starts (InitState = ACTIVE)."),
                new("Repeats", trigger.Repeats ? "Yes" : "No",
                    "The trigger becomes active again each time it runs (repeatable = true)."),
                new("Starts when", trigger.Condition?.Sentence() ?? "No condition",
                    "The event that the trigger waits for. The game uses only the last condition."),
            ], []));
        }
        return new MissionPageModel(title, kind, help, null, sections, section?.Ref);
    }

    private static (string Kind, HelpText Help) KindOf(MissionItemKind kind, MissionSchema schema) => kind switch
    {
        MissionItemKind.Trigger or MissionItemKind.Row => ("Trigger", schema.Help["trigger"]),
        MissionItemKind.Ship => ("Mission ship", schema.Help["missionShip"]),
        MissionItemKind.Formation => ("Formation", schema.Help["formation"]),
        MissionItemKind.Solar => ("Solar", schema.Help["solar"]),
        MissionItemKind.Loot => ("Loot", schema.Help["loot"]),
        MissionItemKind.OrderList => ("Order list", schema.Help["orderList"]),
        MissionItemKind.Objective => ("Objective", schema.Help["objective"]),
        MissionItemKind.Conversation => ("Conversation", schema.Help["conversation"]),
        MissionItemKind.Npc => ("NPC", schema.Help["npc"]),
        MissionItemKind.RandomEncounter => ("Random encounter", schema.Help["randomEncounter"]),
        _ => ("Mission section", schema.Help["mission"]),
    };

    private static PageSection Overview(MissionAnalysis analysis)
    {
        var index = analysis.Index;
        var fields = new List<PageField>
        {
            new("Triggers", Number(index.Triggers.Count)),
            new("Mission ships", Number(index.Document.OfKind(MissionSectionKind.Ship).Count())),
            new("Objectives", Number(index.Document.OfKind(MissionSectionKind.Objective).Count())),
            new("Conversations", Number(index.Document.OfKind(MissionSectionKind.Conversation).Count())),
        };
        if (analysis.Prefabs is { } prefabs)
        {
            foreach (var group in prefabs.Instances.GroupBy(i => i.Definition).OrderBy(g => g.Key.Order))
                fields.Add(new PageField(group.Key.Name, Number(group.Count()), group.Key.Help.WhatItIs));
        }
        var systems = MissionsBrowserModel.SystemsOf(index).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (systems.Count > 0)
            fields.Add(new PageField("Systems", string.Join(", ", systems), "The systems that the mission names."));
        foreach (var start in index.StartTriggers)
            fields.Add(new PageField("Start trigger", start.Nickname, start.Condition?.Sentence(),
                new MissionRef(index.Document.Path, MissionItemKind.Trigger, start.Section.Ref)));
        return new PageSection("overview", "Overview", MissionSchema.Retail.Help["overview"], fields, []);
    }

    private static PageSection Problems(MissionAnalysis analysis, bool showBaseGame) =>
        new("problems", "Problems", MissionSchema.Retail.Help["problems"], ProblemFields(analysis.Problems, showBaseGame),
            HiddenNote(analysis.Problems, showBaseGame));

    private static List<PageField> ProblemFields(IReadOnlyList<Diagnostic> problems, bool showBaseGame) =>
        problems.Where(p => showBaseGame || p.Origin?.IsBaseGame != true)
            .Select(p => new PageField(p.Rule ?? "Problem", p.Message, p.Origin?.Text, p.Target is { } t ? (t, p.EntryIndex) : null,
                p.Origin?.IsBaseGame == true, p.Severity)).ToList();

    /// <summary>The dim line of the hidden base-game problems, or none.</summary>
    private static List<string> HiddenNote(IReadOnlyList<Diagnostic> problems, bool showBaseGame)
    {
        var hidden = showBaseGame ? 0 : problems.Count(p => p.Origin?.IsBaseGame == true);
        return hidden switch
        {
            0 => [],
            1 => ["1 base-game message is hidden."],
            _ => [$"{hidden} base-game messages are hidden."],
        };
    }

    private static PageSection OpenLancer(MissionIndex index)
    {
        var (_, tooltip, gaps) = MissionsBrowserModel.OpenLancerOf(index);
        var fields = gaps
            ? tooltip.Split('\n').Skip(1).Select(line => line.Split(": ", 2)).Select(p => new PageField(p[0], p[1])).ToList()
            : [];
        return new PageSection("openlancer", "OpenLancer", MissionSchema.Retail.Help["openLancer"], fields,
            gaps ? [] : ["OpenLancer runs all lines of this mission."]);
    }

    private static PageSection VoiceLines(MissionIndex index, Func<string, string?> commFile)
    {
        var names = index.Lines.SelectMany(l => l.References).Where(r => r.Kind == MissionValueKind.CommLine)
            .Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var fields = names.Select(n => commFile(n) is { } file
            ? new PageField(n, file)
            : new PageField(n, "Missing", "No voices file has this comm line. The game plays no sound for it.",
                Severity: DiagnosticSeverity.Error)).ToList();
        return new PageSection("voice", "Voice lines", MissionSchema.Retail.Help["voiceLines"], fields,
            fields.Count == 0 ? ["The script names no comm line."] : []);
    }

    /// <summary>The text ids of a script, grouped as titles, offers, objectives, fail texts and log lines.</summary>
    private static PageSection Texts(MissionIndex index, Func<int, string> text)
    {
        var fields = new List<PageField>();
        void Add(string group, IEnumerable<string?> ids)
        {
            foreach (var id in ids.OfType<string>().Distinct())
                fields.Add(new PageField(group, int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                                                Text(text, n) is { } value ? $"{value} ({id})" : id));
        }
        var lines = index.Triggers.Where(t => !t.IsDuplicate).SelectMany(t => t.Actions).ToList();
        Add("Title", lines.Where(l => Is(l.Key, "Act_SetTitle")).Select(l => l.Value(0)));
        Add("Offer", lines.Where(l => Is(l.Key, "Act_SetOffer")).Select(l => l.Value(0)));
        Add("Objective", index.Document.OfKind(MissionSectionKind.Objective).SelectMany(o => o.Section)
            .Where(e => Is(e.Name, "type")).SelectMany(MissionIndex.ObjectiveTextIds));
        Add("Fail text", lines.Where(l => Is(l.Key, "Act_ChangeState") && Is(l.Value(0), "FAIL")).Select(l => l.Value(1)));
        Add("Log line", lines.Where(l => Is(l.Key, "Act_NNIds")).Select(l => l.Value(0)));
        return new PageSection("text", "Text", MissionSchema.Retail.Help["missionText"], fields,
            fields.Count == 0 ? ["The script names no text id."] : []);
    }

    private static string Stage(int stage) => $"{StoryStages.NameOf(stage)} ({stage})";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(Func<int, string> text, int id) => text(id) is { Length: > 0 } value ? value : null;

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

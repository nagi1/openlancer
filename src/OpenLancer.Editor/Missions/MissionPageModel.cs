using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>A chip of a page field, for example a system of a mission.</summary>
/// <param name="Link">The item that a click on the chip goes to, or null for a chip without a click.</param>
internal sealed record PageChip(string Text, string Tooltip, object? Link);

/// <summary>A link to a line of a mission script: the Mission tab opens the mission at the line.</summary>
/// <param name="Entry">The index of the line in the section, or -1 for the whole section.</param>
internal sealed record LineLink(SectionRef Target, int Entry);

/// <summary>A link to a system: the Scene opens the system.</summary>
internal sealed record SystemLink(string Nickname);

/// <summary>A link to a bundle row of a mission: a click does what the row does in the Missions tab.</summary>
internal sealed record BundleLink(string Script, MissionBundleRow Row);

/// <summary>A link to a space cutscene: a click opens it in the Cutscene tab. The value is the path of the line.</summary>
internal sealed record CutsceneLink(string Value);

/// <summary>A value of a page field that names an item of the script or of the game data (section 10.1 of the M6a
/// design). The page draws it with the reference widget of the Inspector, read-only in M6a.</summary>
/// <param name="Nickname">The name as the line gives it.</param>
/// <param name="Name">The name text of the item, or null.</param>
/// <param name="Glyph">The glyph of the kind of the item.</param>
/// <param name="Kind">The kind of the item in plain words, for example "Faction".</param>
internal sealed record PageReference(string Nickname, string? Name, char Glyph, string Kind);

/// <summary>One field of a page section: a plain name and a value, with an optional help text and an item to go
/// to.</summary>
/// <param name="Help">The help text of the field, or null.</param>
/// <param name="Link">The item that a click on the field selects, or null.</param>
/// <param name="Muted">True for a base-game problem that shows muted, with the lock glyph.</param>
/// <param name="Severity">The severity of a problem, or null for a field that is not a problem.</param>
internal sealed record PageField(string Label, string Value, string? Help = null, object? Link = null, bool Muted = false,
    DiagnosticSeverity? Severity = null)
{
    /// <summary>The chips of the field in place of its value, for example the systems of a mission.</summary>
    public IReadOnlyList<PageChip> Chips { get; init; } = [];

    /// <summary>True for a field that the schema marks as hard to understand. Its label gets the help glyph (section
    /// 11.3 of the M6a design).</summary>
    public bool Hard { get; init; }

    /// <summary>The list of a list field in place of the value, or null (section 11.1 of the M6a design). The page shows
    /// it read-only in M6a.</summary>
    public ListFieldModel? List { get; init; }

    /// <summary>True for a heading row of the lines that follow it, for example "Line 3: Spawn ship".</summary>
    public bool Heading { get; init; }

    /// <summary>True for a line that the game ignores, for example a condition before the last one. The value is struck
    /// through.</summary>
    public bool Struck { get; init; }

    /// <summary>True for a name that does not resolve. The value has the "Missing" look of M21.</summary>
    public bool Missing { get; init; }

    /// <summary>The item that the value names, or null. The page draws it with the reference widget.</summary>
    public PageReference? Reference { get; init; }

    /// <summary>The named parts of a value of several numbers, for example the X, Y and Z of a position. The page draws
    /// one field for each part. Empty for another value.</summary>
    public IReadOnlyList<(string Label, string Value)> Parts { get; init; } = [];
}

/// <summary>One component section of a mission page (section 10.1 of the M6a design).</summary>
/// <param name="Id">The id of the section, for the open state in the settings.</param>
/// <param name="Glyph">The glyph of the header.</param>
/// <param name="Notes">Lines under the fields, for example the special rules of a slot.</param>
/// <param name="Advanced">True for the closed Advanced section.</param>
internal sealed record PageSection(string Id, string Title, char Glyph, HelpText? Help, IReadOnlyList<PageField> Fields,
    IReadOnlyList<string> Notes, bool Advanced = false);

/// <summary>
/// The model of a mission page of the Inspector (section 10.1 of the M6a design): the header, the read-only note, the
/// completeness report, the component sections and the INI block. The page draws from it. The model makes no ImGui
/// call, so the tests check it (review finding RV12). The queries of <see cref="MissionSummary"/> give its data.
/// </summary>
/// <param name="Title">The title of the header line, for example "M04: {title text}".</param>
/// <param name="Kind">The kind label of the header line, for example "Mission".</param>
/// <param name="Glyph">The glyph of the kind in the header line.</param>
/// <param name="Help">The text of the info icon of the header line.</param>
/// <param name="Completeness">The completeness report, or null for an item without parts.</param>
/// <param name="Ini">The section of the INI block, or null.</param>
internal sealed partial record MissionPageModel(string Title, string Kind, char Glyph, HelpText Help,
    CompletenessReport? Completeness, IReadOnlyList<PageSection> Sections, SectionRef? Ini)
{
    /// <summary>The tooltip of a field of a mission page. The fields take edits in milestone M6b-2. Until then, the INI
    /// block of the page and the raw view take the edits (section 8.8 of the M6b design).</summary>
    public const string FieldNote = "Change this value in the INI block of the page.";

    /// <summary>The section of an id, or null.</summary>
    public PageSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    /// <summary>The lines under the header, before the completeness panel, for example "Part of".</summary>
    public IReadOnlyList<PageField> Lead { get; init; } = [];

    /// <summary>The chips of the header, for example "Start" and "Repeats" of a trigger.</summary>
    public IReadOnlyList<string> HeaderChips { get; init; } = [];

    /// <summary>The sections of the INI block when it shows more than one, for example all triggers of a prefab
    /// instance. Empty for the one section of <see cref="Ini"/>.</summary>
    public IReadOnlyList<SectionRef> IniSections { get; init; } = [];

    /// <summary>The title of the INI block: "INI", "INI (all triggers)" or "INI (all sections)".</summary>
    public string IniTitle { get; init; } = "INI";

    /// <summary>The id of a section that the page opens and scrolls to, for example the group of a selected line, or
    /// null.</summary>
    public string? FocusSection { get; init; }

    /// <summary>The Mission page (section 10.2 of the M6a design).</summary>
    /// <param name="text">Gives the text of a text id, or an empty text.</param>
    /// <param name="commFile">Gives the voices file of a comm line, or null when no voices file has it.</param>
    /// <param name="showBaseGame">True while the base-game messages show.</param>
    /// <param name="hidesOpenLancer">True when the active mod has a targets list without OpenLancer (UI rule 4).</param>
    /// <param name="systemName">Gives the name of a system of the game data, or null when the game data has no such
    /// system. Null when the page knows no system names.</param>
    public static MissionPageModel Mission(MissionRunResult result, MissionAnalysis analysis, Func<int, string> text,
        Func<string, string?> commFile, bool showBaseGame, bool hidesOpenLancer, Func<string, string?>? systemName = null)
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
            var columns = MissionsBrowserModel.Columns.ToDictionary(c => c.Name, c => c.Tooltip);
            sections.Add(new PageSection("slot", "Story slot", EditorIcons.Route, schema.Help["storySlot"],
            [
                new("Slot", slot.Slot, columns["Slot"]),
                new("Script", MissionFiles.Display(analysis.Script.Path), columns["Mission script"]),
                new("Start stage", MissionSummary.Stage(slot.StartStage), columns["Start stage"]),
                new("Accept stage", MissionSummary.Stage(slot.AcceptStage), columns["Accept stage"]),
                new("Next mission", MissionSummary.NextText(slot.Next),
                    $"{columns["Next mission"]} {MissionSummary.NextTooltip(slot.Next)}"),
                new("Title text", Text(text, slot.TitleId) is { } titleText ? $"{titleText} ({slot.TitleId})" : Number(slot.TitleId),
                    schema.Notes["titleText"]),
            ], slot.Notes.Select(n => schema.SlotNotes.GetValueOrDefault(n) ?? n).ToList()));
        }

        sections.Add(Overview(analysis, systemName));
        sections.Add(Problems(analysis, analysis.Problems, showBaseGame));
        if (!hidesOpenLancer)
            sections.Add(OpenLancer(index));
        sections.Add(VoiceLines(index, commFile));
        sections.Add(Texts(index, text));
        sections.Add(new PageSection("files", "Files", EditorIcons.Folder, schema.Help["missionFiles"],
            MissionSummary.Bundle(result, analysis).Select(b => new PageField(b.Name, b.Value, FileTooltip(b),
                b.ActionPhrase != null ? new BundleLink(analysis.Script.Path, b) : null)).ToList(), []));

        var header = analysis.Document.OfKind(MissionSectionKind.Mission).FirstOrDefault();
        var advanced = new List<PageField>();
        // The keys that the game does not use (decision D426). Their help text says where the titles come from.
        foreach (var key in schema.Section("Mission")?.Keys.Where(k => k.Unused) ?? [])
        {
            if (header?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, key.Key)) is { } line)
                advanced.Add(new PageField(key.Label, string.Join(", ", line.Select(MissionIndex.Text)), key.Help));
        }
        if (advanced.Count > 0)
            sections.Add(new PageSection("advanced", "Advanced", EditorIcons.Cog, null, advanced, [], Advanced: true));

        return new MissionPageModel(title, "Mission", EditorIcons.Route, schema.Help["mission"],
            MissionCompleteness.OfMission(analysis), sections, header?.Ref);
    }

    /// <summary>The tooltip of a row of the Files section: what it holds, and what a click does.</summary>
    private static string FileTooltip(MissionBundleRow row) =>
        row.ActionPhrase is { } action ? $"{row.Tooltip}\nClick to {action}." : row.Tooltip;

    /// <summary>The page of another section of a mission, for example <c>[MetaBehavior]</c>. It has the header, the
    /// read-only note, the problems of its section and its INI block. Each other kind has its own page (see
    /// <see cref="For"/>).</summary>
    public static MissionPageModel Item(MissionAnalysis analysis, MissionRef item, bool showBaseGame)
    {
        var schema = MissionSchema.Retail;
        var section = item.Section is { } target ? analysis.Document.Find(target) : null;
        var (kind, glyph, help) = KindOf(item.Kind, item.Key, schema);
        var title = section?.Nickname ?? section?.Ref.Id.ToString() ?? item.Key ?? MissionFiles.Display(item.Script);
        if (item.Row >= 0 && section != null && item.Row < section.Section.Count)
            title = $"{title}: {section.Section[item.Row].Name}";
        var problems = analysis.Problems.Where(p => section != null && Equals(p.Target, section.Ref) &&
                                                    (item.Row < 0 || p.EntryIndex == item.Row || p.EntryIndex < 0)).ToList();
        var sections = new List<PageSection> { Problems(analysis, problems, showBaseGame) };
        return new MissionPageModel(title, kind, glyph, help, null, sections, section?.Ref);
    }

    /// <summary>The kind label, the glyph and the help text of an item kind (section 10.1 of the M6a design).</summary>
    /// <param name="key">The name of an item without a section, for example the id of a behavior prefab.</param>
    internal static (string Kind, char Glyph, HelpText Help) KindOf(MissionItemKind kind, string? key, MissionSchema schema) =>
        kind switch
        {
            MissionItemKind.Act when key == MissionActs.NotStartedKey => ("Frame", EditorIcons.EyeSlash, schema.Help["notStarted"]),
            MissionItemKind.Mission => ("Mission", EditorIcons.Route, schema.Help["mission"]),
            MissionItemKind.Trigger or MissionItemKind.Row => ("Trigger", EditorIcons.Bolt, schema.Help["trigger"]),
            MissionItemKind.Ship => ("Mission ship", EditorIcons.SpaceShuttle, schema.Help["missionShip"]),
            MissionItemKind.Formation => ("Formation", EditorIcons.Users, schema.Help["formation"]),
            MissionItemKind.Solar => ("Solar", EditorIcons.Satellite, schema.Help["solar"]),
            MissionItemKind.Loot => ("Loot", EditorIcons.Box, schema.Help["loot"]),
            MissionItemKind.OrderList => ("Order list", EditorIcons.List, schema.Help["orderList"]),
            MissionItemKind.Objective => ("Objective", EditorIcons.Bullseye, schema.Help["objective"]),
            MissionItemKind.Conversation => ("Conversation", EditorIcons.VolumeUp, schema.Help["conversation"]),
            MissionItemKind.Npc => ("NPC", EditorIcons.User, schema.Help["npc"]),
            MissionItemKind.Label => ("Label", EditorIcons.Tag, schema.Help["label"]),
            MissionItemKind.RandomEncounter => ("Random encounter", EditorIcons.Crosshairs, schema.Help["randomEncounter"]),
            MissionItemKind.Act => ("Act", EditorIcons.Film, schema.Help["act"]),
            MissionItemKind.PrefabInstance or MissionItemKind.PrefabKind => ("Behavior prefab", EditorIcons.Cubes,
                MissionPrefabs.Retail.Definitions.FirstOrDefault(d => key != null &&
                    key.Split('|')[0].Equals(d.Id, StringComparison.OrdinalIgnoreCase))?.Help ?? schema.Help["behaviorPrefab"]),
            _ => ("Mission section", EditorIcons.Th, schema.Help["otherSection"]),
        };

    private static PageSection Overview(MissionAnalysis analysis, Func<string, string?>? systemName)
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
        var systems = MissionSummary.Systems(index).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (systems.Count > 0)
        {
            fields.Add(new PageField("Systems", string.Join(", ", systems), MissionSchema.Retail.Notes["missionSystems"])
            {
                Chips = systems.Select(nickname => SystemChip(nickname, systemName)).ToList(),
            });
        }
        foreach (var start in index.StartTriggers)
            fields.Add(new PageField("Start trigger", start.Nickname, start.Condition?.Sentence(),
                new MissionRef(index.Document.Path, MissionItemKind.Trigger, start.Section.Ref)));
        return new PageSection("overview", "Overview", EditorIcons.Th, MissionSchema.Retail.Help["overview"], fields, []);
    }

    /// <summary>The chip of a system: its name, and a link that opens it in the Scene. A system that the game data does
    /// not have shows its nickname, without a link.</summary>
    private static PageChip SystemChip(string nickname, Func<string, string?>? systemName)
    {
        if (systemName?.Invoke(nickname) is not { } name)
            return new PageChip(nickname, $"{nickname}. The game data has no system with this name.", null);
        var text = name.Length > 0 ? name : nickname;
        return new PageChip(text, $"{text} ({nickname}). Click to open the system in the Scene.", new SystemLink(nickname));
    }

    /// <summary>The Problems section (section 10.2 of the M6a design): each problem that shows, with its place, its
    /// message and "Go to". A base-game problem shows muted with the lock glyph while the base-game messages show
    /// (decision D512).</summary>
    private static PageSection Problems(MissionAnalysis analysis, IReadOnlyList<Diagnostic> problems, bool showBaseGame) =>
        new("problems", "Problems", EditorIcons.ExclamationTriangle, MissionSchema.Retail.Help["problems"],
            BaseGameMessages.Shown(problems, showBaseGame).Select(p => new PageField(PlaceOf(analysis, p), p.Message,
                p.Origin?.Text, p.Target is { } t ? new LineLink(t, p.EntryIndex) : null, p.Origin?.IsBaseGame == true,
                p.Severity)).ToList(),
            BaseGameMessages.HiddenLine(BaseGameMessages.Hidden(problems, showBaseGame)) is { } line ? [line] : []);

    /// <summary>The place of a problem in plain words, for example "Trigger t_start, line 3", or "Mission" for a
    /// problem of the whole script.</summary>
    internal static string PlaceOf(MissionAnalysis analysis, Diagnostic problem)
    {
        if (problem.Target is not { } target || analysis.Document.Find(target) is not { } section)
            return "Mission";
        var kind = KindOf(MissionRef.KindOf(section.Kind), null, MissionSchema.Retail).Kind;
        var name = section.Nickname ?? section.Ref.Id.ToString();
        return problem.EntryIndex >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{kind} {name}, line {problem.EntryIndex + 1}")
            : $"{kind} {name}";
    }

    private static PageSection OpenLancer(MissionIndex index)
    {
        var report = MissionSummary.OpenLancer(index);
        return new PageSection("openlancer", "OpenLancer", EditorIcons.Rocket, MissionSchema.Retail.Help["openLancer"],
            report.Gaps.Select(g => new PageField(g.Name, g.Text)).ToList(),
            report.HasGaps ? [] : ["OpenLancer runs all lines of this mission."]);
    }

    private static PageSection VoiceLines(MissionIndex index, Func<string, string?> commFile)
    {
        var fields = MissionSummary.CommLines(index).Select(n => commFile(n) is { } file
            ? new PageField(n, file)
            : new PageField(n, "Missing", MissionSchema.Retail.Notes["missingCommLine"],
                Severity: DiagnosticSeverity.Error)).ToList();
        return new PageSection("voice", "Voice lines", EditorIcons.VolumeUp, MissionSchema.Retail.Help["voiceLines"],
            fields, fields.Count == 0 ? ["The script names no comm line."] : []);
    }

    /// <summary>The text ids of a script, grouped as titles, offers, objectives, fail texts and log lines.</summary>
    private static PageSection Texts(MissionIndex index, Func<int, string> text)
    {
        var fields = MissionSummary.TextIds(index).Select(t => new PageField(t.Group,
            int.TryParse(t.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && Text(text, n) is { } value
                ? $"{value} ({t.Id})"
                : t.Id)).ToList();
        return new PageSection("text", "Text", EditorIcons.Pen, MissionSchema.Retail.Help["missionText"], fields,
            fields.Count == 0 ? ["The script names no text id."] : []);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(Func<int, string> text, int id) => text(id) is { Length: > 0 } value ? value : null;
}

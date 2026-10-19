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

/// <summary>
/// What a mission page needs beyond the analysis: the texts, the names of the game data and the ship builds of the
/// mission ship files. The Inspector gives them from the session on the UI thread. A test gives fixed functions.
/// </summary>
/// <param name="ShowBaseGame">True while the base-game messages show.</param>
/// <param name="HidesOpenLancer">True when the active mod has a targets list without OpenLancer (UI rule 4).</param>
internal sealed record PageContext(MissionRunResult Result, MissionAnalysis Analysis, bool ShowBaseGame,
    bool HidesOpenLancer)
{
    /// <summary>Gives the text of a text id, or an empty text.</summary>
    public Func<int, string> Text { get; init; } = _ => "";

    /// <summary>Gives the voices file of a comm line, or null when no voices file has it.</summary>
    public Func<string, string?> CommFile { get; init; } = _ => null;

    /// <summary>Gives the name of a system, or null. Null when the page knows no system names.</summary>
    public Func<string, string?>? SystemName { get; init; }

    /// <summary>Gives the name text of an item of the game data of a kind, for example a faction, or null.</summary>
    public Func<MissionValueKind, string, string?> NameOf { get; init; } = (_, _) => null;

    /// <summary>Gives the lines of a ship build of the mission ship files by nickname, as keys and values, or null when
    /// the game data has no such build.</summary>
    public Func<string, IReadOnlyList<(string Key, string Value)>?> ShipBuild { get; init; } = _ => null;
}

/// <summary>The pages of the items of a mission (sections 10.3 to 10.17 of the M6a design).</summary>
internal sealed partial record MissionPageModel
{
    /// <summary>The note of a value that the game does not use (decision D426).</summary>
    public const string UnusedSystem = "The game does not use this value. The editor uses it to find the system of the trigger.";

    /// <summary>The help text of the hull points of a ship without a hull points line (section 10.6 of the M6a
    /// design).</summary>
    public const string HullPointsHelp = "The ship uses the hull points of its archetype.";

    /// <summary>The page of a selection of the Mission tab: the Mission page, the page of a trigger or a line, of a cast
    /// item, of an order list, an objective or a conversation, of a label, an act or a behavior prefab, or the plain page
    /// of another section.</summary>
    public static MissionPageModel For(PageContext context, MissionRef item)
    {
        var analysis = context.Analysis;
        var index = analysis.Index;
        var section = item.Section is { } target ? analysis.Document.Find(target) : null;
        var trigger = section?.Kind == MissionSectionKind.Trigger
            ? index.Triggers.FirstOrDefault(t => t.Section == section)
            : null;
        switch (item.Kind)
        {
            case MissionItemKind.Mission:
                return Mission(context.Result, analysis, context.Text, context.CommFile, context.ShowBaseGame,
                    context.HidesOpenLancer, context.SystemName);
            case MissionItemKind.Trigger when trigger != null:
                return Trigger(context, trigger, -1);
            case MissionItemKind.Row when trigger != null:
                var line = index.LinesOf(section!).FirstOrDefault(l => l.Entry == item.Row);
                if (line != null && MissionIndex.Is(line.Key, "Act_CallThorn"))
                    return CutsceneLink(context, trigger, line);
                if (line != null && (MissionIndex.Is(line.Key, "Act_AddRTC") || MissionIndex.Is(line.Key, "Act_RemoveRTC")))
                    return BaseSceneLink(context, line);
                return Trigger(context, trigger, item.Row);
            case MissionItemKind.Ship when section != null:
                return Ship(context, section);
            case MissionItemKind.Formation when section != null:
                return Formation(context, section);
            case MissionItemKind.Solar when section != null:
                return Solar(context, section);
            case MissionItemKind.Loot when section != null:
                return Loot(context, section);
            case MissionItemKind.Npc when section != null:
                return Npc(context, section);
            case MissionItemKind.OrderList when section != null:
                return OrderList(context, section);
            case MissionItemKind.Objective when section != null:
                return Objective(context, section);
            case MissionItemKind.Conversation when section != null:
                return Conversation(context, section);
            case MissionItemKind.RandomEncounter when section != null:
                return RandomEncounter(context, section);
            case MissionItemKind.Label when item.Key != null:
                return Label(context, item.Key);
            case MissionItemKind.Act when item.Key != null:
                return Act(context, item.Key);
            case MissionItemKind.PrefabInstance when analysis.Prefabs?.Instances.FirstOrDefault(i => i.Key == item.Key) is
                { } instance:
                return PrefabInstance(context, instance);
            case MissionItemKind.PrefabKind when MissionPrefabs.Retail.Definition(item.Key ?? "") is { } definition:
                return PrefabKind(context, definition);
            default:
                return Item(analysis, item, context.ShowBaseGame);
        }
    }

    // ---- The trigger page (section 10.3).

    /// <summary>The Trigger page: the trigger component, "Starts when", one section for each action group, the sources,
    /// the OpenLancer marks, the problems and the Advanced section.</summary>
    /// <param name="row">The line that the selection names, or -1. The page opens its section.</param>
    private static MissionPageModel Trigger(PageContext context, MissionTrigger trigger, int row)
    {
        var analysis = context.Analysis;
        var index = analysis.Index;
        var schema = MissionSchema.Retail;
        var keys = schema.Section("Trigger");
        var script = analysis.Script.Path;
        var sections = new List<PageSection>();
        PageField Key(string key, string value) => keys?.Key(key) is { } spec
            ? new PageField(spec.Label, value, spec.Help) { Hard = spec.Hard }
            : new PageField(key, value);

        sections.Add(new PageSection("trigger", "Trigger", EditorIcons.Bolt, schema.Help["triggerComponent"],
        [
            Key("InitState", trigger.StartsActive ? "Yes" : "No"),
            Key("repeatable", trigger.Repeats ? "Yes" : "No"),
        ], []));

        // Starts when: the event sentence, then the named fields of the condition. A condition before the last one is
        // struck through (TR1).
        var startsWhen = new List<PageField>();
        if (trigger.Condition is { } condition)
        {
            startsWhen.Add(new PageField("Event", condition.Sentence(), condition.Name?.Plain));
            startsWhen.AddRange(ValueFields(context, condition));
        }
        else
        {
            startsWhen.Add(new PageField("Event", "No condition", TriggerRules.NoConditionMessage(trigger.Nickname),
                Severity: DiagnosticSeverity.Error));
        }

        foreach (var ignored in trigger.Conditions.Take(Math.Max(0, trigger.Conditions.Count - 1)))
        {
            startsWhen.Add(new PageField($"Line {LineNumber(ignored)}", LineText(ignored),
                "The game uses only the last condition of a trigger, so it ignores this line.") { Struck = true });
        }

        sections.Add(new PageSection("startsWhen", "Starts when", MissionGlyphs.OfEvent(trigger.Condition?.Name?.Group),
            schema.Help["startsWhen"], startsWhen, []));

        // One section for each action group, in group order. Each line shows its line number, so the order stays
        // visible.
        string? focus = row >= 0 && trigger.Condition?.Entry == row ? "startsWhen" : null;
        foreach (var group in trigger.Actions.GroupBy(a => a.Name?.Group ?? "").OrderBy(g => schema.Group(g.Key)?.Order ?? int.MaxValue))
        {
            var info = schema.Group(group.Key);
            var id = $"group.{(group.Key.Length > 0 ? group.Key : "other")}";
            var fields = new List<PageField>();
            foreach (var action in group)
            {
                fields.Add(new PageField($"Line {LineNumber(action)}", MissionGraph.ActionText(action), action.Name?.Plain,
                    new MissionRef(script, MissionItemKind.Row, trigger.Section.Ref, Row: action.Entry)) { Heading = true });
                fields.AddRange(ValueFields(context, action));
                if (action.Entry == row)
                    focus = id;
            }

            sections.Add(new PageSection(id, info?.Section ?? "Other lines", MissionGlyphs.OfGroup(group.Key), info?.Help, fields,
                []));
        }

        // Started by, Stopped by, Watched by and Loaded by.
        foreach (var (id, title, kind) in new[]
                 {
                     ("startedBy", "Started by", MissionLinkKind.Start), ("stoppedBy", "Stopped by", MissionLinkKind.Stop),
                     ("watchedBy", "Watched by", MissionLinkKind.Watch), ("loadedBy", "Loaded by", MissionLinkKind.Load),
                 })
        {
            var links = index.LinksTo(trigger).Where(l => l.Kind == kind && l.Source != trigger).ToList();
            if (links.Count > 0)
                sections.Add(new PageSection(id, title, EditorIcons.Link, null, links.Select(l => RowField(script, l.Line)).ToList(),
                    []));
        }

        var marked = trigger.Rows.Where(r => r.Mark != EngineMark.Runs).ToList();
        if (!context.HidesOpenLancer && marked.Count > 0)
        {
            sections.Add(new PageSection("openlancer", "OpenLancer", EditorIcons.Rocket, schema.Help["openLancer"],
                marked.Select(r => new PageField($"Line {LineNumber(r)}", $"{LineText(r)}: {OpenLancerGap.MarkText(r.Mark)}",
                    r.Mark == EngineMark.CannotLoad
                        ? "OpenLancer stops when it loads this line. The retail game runs it."
                        : "OpenLancer reads this line but does nothing with it. The retail game runs it.")).ToList(), []));
        }

        sections.Add(Problems(analysis, SectionProblems(analysis, trigger.Section), context.ShowBaseGame));

        var advanced = new List<PageField>();
        if (trigger.SystemHint is { } hint)
            advanced.Add(new PageField("System", SystemText(context, hint), UnusedSystem));
        foreach (var entry in trigger.LinesBeforeNickname)
        {
            var line = index.LinesOf(trigger.Section)[entry];
            advanced.Add(new PageField($"Line {LineNumber(line)}", LineText(line),
                "This line comes before the nickname, so the game ignores it.") { Struck = true });
        }

        advanced.Add(new PageField("Lines in file order", trigger.Rows.Count.ToString(CultureInfo.InvariantCulture)) { Heading = true });
        advanced.AddRange(trigger.Rows.Select(r => new PageField($"Line {LineNumber(r)}", LineText(r))));
        sections.Add(new PageSection("advanced", "Advanced", EditorIcons.Cog, null, advanced, [], Advanced: true));

        var chips = new List<string>();
        if (trigger.StartsActive)
            chips.Add("Start");
        if (trigger.Repeats)
            chips.Add("Repeats");
        if (trigger.SystemHint is { } system)
            chips.Add(system);
        var lead = analysis.Prefabs?.Of(trigger) is { } instance
            ? new[]
            {
                new PageField("Part of", $"{instance.Definition.Name} {instance.Name}", instance.Definition.Help.FirstSentence,
                    new MissionRef(script, MissionItemKind.PrefabInstance, Key: instance.Key)),
            }
            : [];
        return new MissionPageModel(trigger.Nickname, "Trigger", EditorIcons.Bolt, schema.Help["trigger"], null, sections,
            trigger.Section.Ref)
        {
            HeaderChips = chips,
            Lead = lead,
            FocusSection = focus,
        };
    }

    /// <summary>The line number of a line in its section, from 1.</summary>
    private static string LineNumber(MissionLine line) => (line.Entry + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The plain name and the values of a line. A key of a section has the label of the schema, for example
    /// "Repeats".</summary>
    private static string LineText(MissionLine line)
    {
        if (line.Kind() == MissionNameKind.Action)
            return MissionGraph.ActionText(line);
        if (line.Name == null && line.KeySpec is { } key)
        {
            var values = Enumerable.Range(0, line.Count).Select(line.Value).OfType<string>().ToList();
            return values.Count == 0 ? key.Label : $"{key.Label}: {string.Join(", ", values)}";
        }

        return MissionGraph.LineText(line);
    }

    /// <summary>A field of a line of another trigger, with a link that selects the line.</summary>
    private static PageField RowField(string script, MissionLine line) =>
        new(line.Trigger?.Nickname ?? line.Section.Nickname ?? line.Section.Ref.Id.ToString(),
            $"Line {LineNumber(line)}: {LineText(line)}", null,
            line.Trigger != null
                ? new MissionRef(script, MissionItemKind.Row, line.Section.Ref, Row: line.Entry)
                : MissionWorkspace.RefOf(line.Section.Ref, line.Entry));

    /// <summary>
    /// The named fields of the values of a line, from the schema. A name shows its name text and its nickname, or the
    /// "Missing" look when it does not resolve. A value list in one line shows as a list field.
    /// </summary>
    private static IEnumerable<PageField> ValueFields(PageContext context, MissionLine line)
    {
        if (line.Name is not { } name)
            yield break;
        foreach (var spec in name.Values)
        {
            if (line.ValueOf(spec) is null)
                continue;
            if (spec.List != null && MissionSchema.Retail.ValueList(name.Name, spec.Name) is { } listSpec)
            {
                var model = ListFieldModel.Read(listSpec, [ListFieldLine.Of(line.Line)]);
                yield return new PageField(listSpec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), spec.Help)
                {
                    List = model, Hard = spec.Hard,
                };
                continue;
            }

            var (text, missing, link) = ValueText(context, line, spec);
            yield return new PageField(spec.Label, text, spec.Help, link) { Hard = spec.Hard, Missing = missing };
        }
    }

    /// <summary>The text of a value of a line: the names with their name texts, the texts of text ids, or the values.
    /// Returns whether a name is missing, and the item of the first name of the script.</summary>
    private static (string Text, bool Missing, object? Link) ValueText(PageContext context, MissionLine line,
        MissionValueSpec spec)
    {
        var parts = new List<string>();
        var missing = false;
        object? link = null;
        var end = Math.Min(spec.End, line.Count);
        for (var i = spec.Index; i < end; i++)
        {
            if (line.Value(i) is not { } value)
                continue;
            var reference = line.References.FirstOrDefault(r => r.ValueIndex == i);
            switch (reference?.State)
            {
                case MissionReferenceState.Missing:
                    missing = true;
                    parts.Add($"Missing: {value}");
                    break;
                case MissionReferenceState.Found when reference.Section != null:
                    link ??= MissionWorkspace.RefOf(reference.Section.Ref, -1);
                    parts.Add(value);
                    break;
                case MissionReferenceState.Found when spec.Kind == MissionValueKind.System:
                    parts.Add(SystemText(context, value));
                    break;
                case MissionReferenceState.Found when context.NameOf(spec.Kind, value) is { Length: > 0 } named:
                    parts.Add($"{named} ({value})");
                    break;
                default:
                    parts.Add(spec.Kind == MissionValueKind.TextId && int.TryParse(value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var id) && context.Text(id) is { Length: > 0 } text
                        ? $"{text} ({value})"
                        : value);
                    break;
            }
        }

        return (string.Join(", ", parts), missing, link);
    }

    /// <summary>The name of a system and its nickname, or the nickname.</summary>
    private static string SystemText(PageContext context, string nickname) =>
        context.SystemName?.Invoke(nickname) is { Length: > 0 } name ? $"{name} ({nickname})" : nickname;

    /// <summary>The problems of the checks for a section.</summary>
    private static List<Diagnostic> SectionProblems(MissionAnalysis analysis, params MissionSection[] sections) =>
        analysis.Problems.Where(p => p.Target != null && sections.Any(s => s.Ref.Equals(p.Target))).ToList();

    // ---- The pages of the cast (sections 10.6 to 10.9 and 10.13).

    /// <summary>The fields of keys of a section, in the order of the keys, with the labels and the help texts of the
    /// schema. A key with a list field gives a list field. A key that the section does not have does not show (UI rule
    /// 4).</summary>
    private static List<PageField> KeyFields(PageContext context, MissionSection section, params string[] keys)
    {
        var spec = MissionSchema.Retail.Section(section.Type);
        var lines = context.Analysis.Index.LinesOf(section);
        var fields = new List<PageField>();
        foreach (var key in keys)
        {
            var keyLines = lines.Where(l => MissionIndex.Is(l.Key, key)).ToList();
            if (keyLines.Count == 0)
                continue;
            var keySpec = spec?.Key(key);
            if (MissionSchema.Retail.KeyList(section.Type, key) is { } listSpec)
            {
                var model = ListFieldModel.Read(listSpec, keyLines.Select(l => ListFieldLine.Of(l.Line)).ToList());
                fields.Add(new PageField(listSpec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), keySpec?.Help)
                {
                    List = model, Hard = keySpec?.Hard == true,
                });
                continue;
            }

            var line = keyLines[0];
            var specs = line.Specs.Count > 0 ? line.Specs : [new MissionValueSpec(0, key, keySpec?.Label ?? key, keySpec?.Kind ?? MissionValueKind.Text) { Rest = true }];
            var values = specs.Select(s => ValueText(context, line, s)).Where(v => v.Text.Length > 0).ToList();
            fields.Add(new PageField(keySpec?.Label ?? key, string.Join(", ", values.Select(v => v.Text)),
                keySpec?.Help, values.Select(v => v.Link).FirstOrDefault(l => l != null))
            {
                Hard = keySpec?.Hard == true, Missing = values.Any(v => v.Missing),
            });
        }

        return fields;
    }

    /// <summary>The section "Used by": the lines that name an item, each with a link.</summary>
    private static PageSection UsedBy(PageContext context, IReadOnlyList<MissionLine> lines, string title = "Used by") =>
        new("usedBy", $"{title} ({lines.Count.ToString(CultureInfo.InvariantCulture)})", EditorIcons.Link, null,
            lines.Select(l => RowField(context.Analysis.Script.Path, l)).ToList(),
            lines.Count == 0 ? ["No line of this mission names this item."] : []);

    /// <summary>The page of an item of the script with the common parts: the problems, Used by and the INI
    /// block.</summary>
    /// <param name="usedBy">The title of the section of the lines that name the item, for example "Given by".</param>
    private static MissionPageModel ItemPage(PageContext context, MissionSection section, string kind, char glyph,
        HelpText help, CompletenessReport? completeness, List<PageSection> sections, IReadOnlyList<PageField>? lead = null,
        string usedBy = "Used by")
    {
        sections.Add(UsedBy(context, MissionUses.Naming(context.Analysis.Index, section), usedBy));
        sections.Add(Problems(context.Analysis, SectionProblems(context.Analysis, section), context.ShowBaseGame));
        return new MissionPageModel(section.Nickname ?? section.Ref.Id.ToString(), kind, glyph, help, completeness,
            sections, section.Ref) { Lead = lead ?? [] };
    }

    /// <summary>The page of a mission ship (section 10.6 of the M6a design): the ship, its pilot and voice, and its ship
    /// build, with the completeness of the three parts.</summary>
    private static MissionPageModel Ship(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var index = context.Analysis.Index;
        var sections = new List<PageSection>();
        var ship = new List<PageField>();
        var lines = index.LinesOf(section);
        bool Has(string key) => lines.Any(l => MissionIndex.Is(l.Key, key));

        // The start: a position and a rotation, or a place near an anchor.
        ship.AddRange(Has("rel_pos") ? KeyFields(context, section, "rel_pos") : KeyFields(context, section, "position", "orientation"));
        ship.AddRange(KeyFields(context, section, "system", "label"));
        // The engine keeps the first of the initial orders and the initial behavior.
        var first = lines.FirstOrDefault(l => MissionIndex.Is(l.Key, "init_objectives") || MissionIndex.Is(l.Key, "init_metabehavior"));
        if (first != null)
            ship.AddRange(KeyFields(context, section, first.Key));
        ship.AddRange(KeyFields(context, section, "arrival_obj", "jumper", "random_name", "radius", "cargo"));
        ship.AddRange(Has("hit_points")
            ? KeyFields(context, section, "hit_points")
            : [new PageField(schema.Section("MsnShip")?.Key("hit_points")?.Label ?? "Hull points", "Not set", HullPointsHelp)]);
        var notes = new List<string>();
        if (section.Nickname != null && index.TakenShips.TryGetValue(section.Nickname, out var formation) && formation != section)
            notes.Add($"Formation {formation.Nickname} takes this ship. A spawn of this ship spawns nothing.");
        sections.Add(new PageSection("ship", "Ship", EditorIcons.SpaceShuttle, schema.Help["ship"], ship, notes));

        var npcName = lines.FirstOrDefault(l => MissionIndex.Is(l.Key, "npc"))?.Value(0);
        var npc = npcName != null ? index.Item(MissionSectionKind.Npc, npcName) : null;
        if (npc != null)
            sections.Add(PilotAndVoice(context, npc));
        var buildName = npc != null ? index.LinesOf(npc).FirstOrDefault(l => MissionIndex.Is(l.Key, "npc_ship_arch"))?.Value(0) : null;
        var build = buildName != null ? context.ShipBuild(buildName) : null;
        if (build != null)
            sections.Add(ShipBuild(context, build));

        var lead = npc != null && index.LinesOf(npc).FirstOrDefault(l => MissionIndex.Is(l.Key, "individual_name")) is { } nameLine
            ? new[] { new PageField("Name text", ValueText(context, nameLine, nameLine.Specs.FirstOrDefault() ??
                new MissionValueSpec(0, "name", "Name text", MissionValueKind.TextId)).Text) }
            : [];
        return ItemPage(context, section, "Mission ship", EditorIcons.SpaceShuttle, schema.Help["missionShip"],
            ShipCompleteness(context, section, npc, build), sections, lead);
    }

    /// <summary>The section "Pilot and voice" of an <c>[NPC]</c>.</summary>
    private static PageSection PilotAndVoice(PageContext context, MissionSection npc) =>
        new("pilot", "Pilot and voice", EditorIcons.User, MissionSchema.Retail.Help["pilotAndVoice"],
            KeyFields(context, npc, "affiliation", "voice", "space_costume", "individual_name", "npc_ship_arch"), []);

    /// <summary>The section "Ship build" of an <c>[NPCShipArch]</c> of the mission ship files.</summary>
    private static PageSection ShipBuild(PageContext context, IReadOnlyList<(string Key, string Value)> build)
    {
        var fields = new List<PageField>();
        foreach (var (key, label, kind) in new[]
                 {
                     ("ship_archetype", "Ship archetype", MissionValueKind.ShipArch), ("loadout", "Loadout", MissionValueKind.Loadout),
                     ("pilot", "Pilot", MissionValueKind.Pilot), ("state_graph", "State graph", MissionValueKind.Text),
                     ("level", "Level", MissionValueKind.Text),
                 })
        {
            if (build.FirstOrDefault(b => MissionIndex.Is(b.Key, key)) is { Key: not null } entry)
                fields.Add(new PageField(label, context.NameOf(kind, entry.Value) is { Length: > 0 } name
                    ? $"{name} ({entry.Value})"
                    : entry.Value));
        }

        var classes = build.Where(b => MissionIndex.Is(b.Key, "npc_class")).Select(b => b.Value).ToList();
        if (classes.Count > 0)
        {
            var spec = new ListFieldSpec("NPCShipArch.npc_class", "NPC classes", "The classes that encounters and missions use " +
                "to pick this ship build.", ListFieldCodec.RepeatedKey, ["npc_class"],
                [new ListColumn("Class", 0, ListColumnKind.Text, Rest: true)]) { Items = "classes" };
            var model = ListFieldModel.Read(spec, classes.Select(c => new ListFieldLine("npc_class",
                c.Split(',').Select(v => (LibreLancer.Data.Ini.ValueBase)new LibreLancer.Data.Ini.StringValue(v.Trim())).ToList())).ToList());
            fields.Add(new PageField(spec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), spec.Help) { List = model });
        }

        return new PageSection("build", "Ship build", EditorIcons.Wrench, MissionSchema.Retail.Help["shipBuild"], fields, []);
    }

    /// <summary>The completeness of a mission ship (section 13.5 of the M6a design): the pilot and voice, the ship build,
    /// and the archetype, the loadout and the pilot of the build. A step that the game data cannot tell stays done. In
    /// a script that no mod changes, a missing step is hidden (decision D551).</summary>
    private static CompletenessReport ShipCompleteness(PageContext context, MissionSection section, MissionSection? npc,
        IReadOnlyList<(string Key, string Value)>? build)
    {
        // The pilot and voice and the ship build come from the checks of the names (M06 and M23). The parts of the build
        // are in a mission ship file, which the checks do not read, so the page checks them with the names of the game
        // data.
        var analysis = context.Analysis;
        var world = analysis.Index.World;
        var shipProblems = SectionProblems(analysis, section).Where(p => p.Rule is "M06" or "M23" &&
                                                                          MissionIndex.Is(p.Key, "npc")).ToList();
        var npcProblems = npc != null
            ? SectionProblems(analysis, npc).Where(p => p.Rule is "M06" or "M23" && MissionIndex.Is(p.Key, "npc_ship_arch")).ToList()
            : [];
        var parts = build == null || new[]
        {
            (MissionValueKind.ShipArch, "ship_archetype"), (MissionValueKind.Loadout, "loadout"), (MissionValueKind.Pilot, "pilot"),
        }.All(p => build.FirstOrDefault(b => MissionIndex.Is(b.Key, p.Item2)).Value is not { } value || !world.Knows(p.Item1) ||
                   world.Has(p.Item1, value));
        var partsState = parts ? CompletenessState.Done
            : analysis.Script.State == MissionScriptState.BaseGame ? CompletenessState.Hidden
            : CompletenessState.Missing;
        return new CompletenessReport("Mission ship",
        [
            MissionCompleteness.RuleStep("Pilot and voice", "Without it, the game drops the ship.", shipProblems),
            MissionCompleteness.RuleStep("Ship build", "Without it, the game drops the ship.", npcProblems),
            new CompletenessStep("Archetype, loadout and pilot", partsState, "Without them, the game drops the ship.",
                parts ? null : "The ship build names an archetype, a loadout or a pilot that the game data does not have.",
                IsError: partsState == CompletenessState.Missing),
        ]) { HiddenMessages = BaseGameMessages.Count(shipProblems.Concat(npcProblems)) };
    }

    /// <summary>The page of a mission formation (section 10.7 of the M6a design).</summary>
    private static MissionPageModel Formation(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var sections = new List<PageSection>
        {
            new("formation", "Formation", EditorIcons.Users, schema.Help["formation"],
                KeyFields(context, section, "formation", "position", "orientation", "rel_pos", "system", "npc", "label",
                    "init_objectives", "arrival_obj"), []),
            new("members", "Members", EditorIcons.SpaceShuttle, null, KeyFields(context, section, "ship"), []),
        };
        return ItemPage(context, section, "Formation", EditorIcons.Users, schema.Help["formation"], null, sections);
    }

    /// <summary>The page of a mission solar (section 10.8 of the M6a design).</summary>
    private static MissionPageModel Solar(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var sections = new List<PageSection>
        {
            new("solar", "Solar", EditorIcons.Satellite, schema.Help["solar"],
                KeyFields(context, section, "archetype", "loadout", "base", "faction", "position", "orientation", "system",
                    "visit", "label", "string_id", "radius"), []),
            new("pilot", "Pilot and voice", EditorIcons.User, schema.Help["pilotAndVoice"],
                KeyFields(context, section, "pilot", "voice", "costume"), []),
        };
        return ItemPage(context, section, "Solar", EditorIcons.Satellite, schema.Help["solar"], null, sections);
    }

    /// <summary>The page of mission loot (section 10.9 of the M6a design).</summary>
    private static MissionPageModel Loot(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var sections = new List<PageSection>
        {
            new("loot", "Loot", EditorIcons.Box, schema.Help["loot"],
                KeyFields(context, section, "archetype", "position", "rel_pos_obj", "rel_pos_offset", "orientation",
                    "velocity", "angular_velocity", "equip_amount", "health", "can_jettison", "system", "label", "string_id"),
                []),
        };
        return ItemPage(context, section, "Loot", EditorIcons.Box, schema.Help["loot"], null, sections);
    }

    /// <summary>The page of an <c>[NPC]</c> (section 10.13 of the M6a design).</summary>
    private static MissionPageModel Npc(PageContext context, MissionSection section) =>
        ItemPage(context, section, "NPC", EditorIcons.User, MissionSchema.Retail.Help["npc"], null,
            [PilotAndVoice(context, section)]);

    // ---- The other pages of section 10.

    /// <summary>The page of an order list (section 10.10 of the M6a design): its order prefab, its orders as a list
    /// field, and the lines that give it.</summary>
    private static MissionPageModel OrderList(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var lead = context.Analysis.Prefabs?.Instances.Where(i => i.CoreSection == section)
            .Select(i => new PageField("Order prefab", i.Definition.Name, i.Definition.Help.FirstSentence,
                new MissionRef(context.Analysis.Script.Path, MissionItemKind.PrefabInstance, Key: i.Key))).ToList() ?? [];
        var orders = new List<PageField>();
        if (schema.OrderList(section.Type) is { } spec)
        {
            var lines = section.Section.Where(e => spec.Keys.Any(k => MissionIndex.Is(e.Name, k))).Select(ListFieldLine.Of).ToList();
            var model = ListFieldModel.Read(spec, lines);
            orders.Add(new PageField(spec.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture), spec.Help) { List = model });
        }

        var sections = new List<PageSection>
        {
            new("orders", "Orders", EditorIcons.Route, schema.Help["orderList"], orders, []),
        };
        return ItemPage(context, section, "Order list", EditorIcons.List, schema.Help["orderList"], null, sections, lead,
            "Given by");
    }

    /// <summary>The page of an objective (section 10.11 of the M6a design): the type, the texts, the state and the place,
    /// and the lines that set, complete or hide it.</summary>
    private static MissionPageModel Objective(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var fields = new List<PageField>();
        if (section.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "type")) is { Count: > 0 } type)
        {
            var kind = MissionIndex.Text(type[0]);
            fields.Add(new PageField("Type", kind, "How the game shows the objective: ids (a text), navmarker (a point), " +
                                                   "rep_inst (an object) or base (a base)."));
            foreach (var id in MissionIndex.ObjectiveTextIds(type))
            {
                var text = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                           context.Text(number) is { Length: > 0 } value
                    ? $"{value} ({id})"
                    : id;
                fields.Add(new PageField("Text", text));
            }

            if ((MissionIndex.Is(kind, "navmarker") || MissionIndex.Is(kind, "rep_inst")) && type.Count > 6)
            {
                fields.Add(new PageField("System", SystemText(context, MissionIndex.Text(type[1]))));
                fields.Add(new PageField("Position", string.Join(", ", Enumerable.Range(4, 3).Select(i => MissionIndex.Text(type[i])))));
                if (MissionIndex.Is(kind, "rep_inst") && type.Count > 7)
                    fields.Add(new PageField("Object", MissionIndex.Text(type[7])));
            }
            else if (MissionIndex.Is(kind, "base") && type.Count > 1)
            {
                var baseName = MissionIndex.Text(type[1]);
                fields.Add(new PageField("Base", context.NameOf(MissionValueKind.Base, baseName) is { Length: > 0 } name
                    ? $"{name} ({baseName})"
                    : baseName));
            }
        }

        fields.AddRange(KeyFields(context, section, "state", "hidden"));
        var users = MissionUses.Naming(context.Analysis.Index, section);
        var script = context.Analysis.Script.Path;
        var sections = new List<PageSection>
        {
            new("objective", "Objective", EditorIcons.Bullseye, schema.Help["objective"], fields, []),
            Lines("setBy", "Set by", users.Where(l => MissionIndex.Is(l.Key, "Act_SetNNObj")), script),
            Lines("completedBy", "Completed by", users.Where(l => MissionIndex.Is(l.Key, "Act_SetNNState")), script),
            Lines("hiddenBy", "Hidden by", users.Where(l => MissionIndex.Is(l.Key, "Act_SetNNHidden")), script),
            Problems(context.Analysis, SectionProblems(context.Analysis, section), context.ShowBaseGame),
        };
        return new MissionPageModel(section.Nickname ?? section.Ref.Id.ToString(), "Objective", EditorIcons.Bullseye,
            schema.Help["objective"], null, sections, section.Ref);
    }

    /// <summary>A section of the lines of other triggers, each with a link.</summary>
    private static PageSection Lines(string id, string title, IEnumerable<MissionLine> lines, string script)
    {
        var list = lines.ToList();
        return new PageSection(id, title, EditorIcons.Link, null, list.Select(l => RowField(script, l)).ToList(),
            list.Count == 0 ? ["No line."] : []);
    }

    /// <summary>The page of a conversation (section 10.12 of the M6a design): its completeness, its system, its lines as
    /// a list field, and the lines that start it.</summary>
    private static MissionPageModel Conversation(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var index = context.Analysis.Index;
        // The steps come from the checks of the conversation: comm lines in no voices file (M21), and speakers in another
        // system (M19).
        var problems = SectionProblems(context.Analysis, section);
        var completeness = new CompletenessReport("Conversation",
        [
            MissionCompleteness.RuleStep("Comm lines exist", "The game plays no sound for a missing comm line.",
                problems.Where(p => p.Rule == "M21").ToList()),
            MissionCompleteness.RuleStep("Speakers in the system", "A speaker in another system stops the conversation.",
                problems.Where(p => p.Rule == "M19").ToList()),
        ]) { HiddenMessages = BaseGameMessages.Count(problems) };
        var sections = new List<PageSection>
        {
            new("conversation", "Conversation", EditorIcons.Comments, schema.Help["conversation"],
                KeyFields(context, section, "system"), []),
            new("lines", "Lines", EditorIcons.VolumeUp, null, KeyFields(context, section, "line"), []),
        };
        return ItemPage(context, section, "Conversation", EditorIcons.Comments, schema.Help["conversation"], completeness,
            sections, null, "Started by");
    }

    /// <summary>The page of a random encounter (section 10.16 of the M6a design): the steps and the fields of its Ambush
    /// prefab, and the triggers that arm it, disarm it and react to its spawn.</summary>
    private static MissionPageModel RandomEncounter(PageContext context, MissionSection section)
    {
        var schema = MissionSchema.Retail;
        var analysis = context.Analysis;
        var ambush = analysis.Prefabs?.Instances.FirstOrDefault(i => i.CoreSection == section);
        var fields = KeyFields(context, section, "encounter_type");
        if (ambush != null)
        {
            foreach (var field in ambush.Definition.Fields.Where(f => f.From.Role == "core" && f.From.Key is not (null or "nickname")))
            {
                var values = ambush.Values(field);
                if (values.Count > 0)
                    fields.Add(new PageField(field.Label, string.Join(", ", values.Select(v => v.Text))));
            }
        }

        var users = MissionUses.Naming(analysis.Index, section);
        var script = analysis.Script.Path;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nickname", "encounter_type" };
        known.UnionWith(ambush?.Definition.Fields.Select(f => f.From.Key).OfType<string>() ?? []);
        var advanced = section.Section.Where(e => !known.Contains(e.Name))
            .Select(e => new PageField(e.Name, string.Join(", ", e.Select(MissionIndex.Text)),
                "The editor does not know the meaning of this key yet.")).ToList();
        var sections = new List<PageSection>
        {
            new("encounter", "Encounter", EditorIcons.Crosshairs, schema.Help["randomEncounter"], fields, []),
            Lines("armedBy", "Armed by", users.Where(l => MissionIndex.Is(l.Key, "Act_EnableEnc")), script),
            Lines("disarmedBy", "Disarmed by", users.Where(l => MissionIndex.Is(l.Key, "Act_DisableEnc")), script),
            Lines("onSpawn", "On spawn", users.Where(l => MissionIndex.Is(l.Key, "Cnd_EncLaunched")), script),
            Problems(analysis, SectionProblems(analysis, section), context.ShowBaseGame),
        };
        if (advanced.Count > 0)
            sections.Add(new PageSection("advanced", "Advanced", EditorIcons.Cog, null, advanced, [], Advanced: true));
        return new MissionPageModel(section.Nickname ?? section.Ref.Id.ToString(), "Random encounter", EditorIcons.Crosshairs,
            schema.Help["randomEncounter"], ambush != null ? MissionCompleteness.OfInstance(analysis, ambush) : null, sections,
            section.Ref);
    }

    /// <summary>The page of a label (section 10.15 of the M6a design): its members and the lines that name it.</summary>
    private static MissionPageModel Label(PageContext context, string label)
    {
        var index = context.Analysis.Index;
        var members = index.Labels.TryGetValue(label, out var list) ? list : [];
        var sections = new List<PageSection>
        {
            new("members", $"Members ({members.Count.ToString(CultureInfo.InvariantCulture)})", EditorIcons.Users, null,
                members.Select(m => new PageField(KindOf(MissionRef.KindOf(m.Kind), null, MissionSchema.Retail).Kind,
                    m.Nickname ?? m.Ref.Id.ToString(), null, MissionWorkspace.RefOf(m.Ref, -1))).ToList(), []),
            UsedBy(context, index.ReferencesTo(MissionValueKind.Object, label).Where(r => r.Section == null)
                .Select(r => r.Line).Distinct().ToList()),
        };
        return new MissionPageModel(label, "Label", EditorIcons.Tag, MissionSchema.Retail.Help["label"], null, sections, null);
    }

    /// <summary>The page of an act or of the frame "Not started by the mission" (section 10.17 of the M6a
    /// design).</summary>
    private static MissionPageModel Act(PageContext context, string key)
    {
        var analysis = context.Analysis;
        var acts = analysis.Acts ?? MissionActs.Propose(analysis.Index);
        var script = analysis.Script.Path;
        var number = key == "a:0" ? 0 : int.TryParse(key.AsSpan(2), out var n) ? n : -1;
        var act = acts.Acts.FirstOrDefault(a => a.Number == number);
        var triggers = number == 0 ? analysis.Index.Triggers.Where(t => t.IsDuplicate || acts.ActOf(t) == null).ToList()
            : act?.Triggers ?? [];
        var systemText = act?.System is { } system
            ? context.SystemName?.Invoke(system) is { Length: > 0 } name ? name : system
            : null;
        var title = number == 0 ? "Not started by the mission"
            : systemText != null ? $"Act {number}: {systemText}"
            : $"Act {number}";
        var walkthrough = $"{triggers.Count.ToString(CultureInfo.InvariantCulture)} {(triggers.Count == 1 ? "trigger" : "triggers")}, " +
                          $"{(act?.Checkpoints ?? 0).ToString(CultureInfo.InvariantCulture)} " +
                          $"{(act?.Checkpoints == 1 ? "checkpoint" : "checkpoints")}";
        PageField Link(MissionTrigger t) => new(t.Nickname, t.Condition?.Sentence() ?? "No condition", null,
            new MissionRef(script, MissionItemKind.Trigger, t.Section.Ref));
        var sections = new List<PageSection>
        {
            new("act", number == 0 ? "Frame" : "Act", EditorIcons.Film,
                MissionSchema.Retail.Help[number == 0 ? "notStarted" : "act"],
                [new PageField("Name", title), new PageField("Walkthrough", walkthrough)], []),
            new("triggers", "Triggers", EditorIcons.Bolt, null, triggers.Select(Link).ToList(), []),
            new("checkpoints", "Checkpoints", EditorIcons.Save, null,
                triggers.Where(t => t.ActionsOf("Act_Save").Any()).Select(Link).ToList(), []),
        };
        return new MissionPageModel(title, "Act", EditorIcons.Film, MissionSchema.Retail.Help[number == 0 ? "notStarted" : "act"],
            null, sections, null);
    }

    // ---- The prefab pages (sections 10.4 and 10.5).

    /// <summary>The page of a prefab instance (section 10.4 of the M6a design): its completeness, its named fields, its
    /// triggers with their roles, and the INI of all its triggers.</summary>
    private static MissionPageModel PrefabInstance(PageContext context, MissionPrefabInstance instance)
    {
        var analysis = context.Analysis;
        var script = analysis.Script.Path;
        var definition = instance.Definition;
        var fields = new List<PageField>();
        foreach (var field in definition.Fields)
        {
            var values = instance.Values(field);
            if (field.Spec(MissionSchema.Retail) is { } spec)
            {
                var model = ListFieldModel.Read(spec, values.Where(v => v.Line != null).Select(v => ListFieldLine.Of(v.Line!.Line))
                    .Distinct().ToList());
                fields.Add(new PageField(field.Label, model.Rows.Count.ToString(CultureInfo.InvariantCulture)) { List = model });
            }
            else if (field.List != null)
            {
                fields.Add(new PageField(field.Label, values.Count.ToString(CultureInfo.InvariantCulture)) { Heading = true });
                fields.AddRange(values.Select(v => new PageField(v.Trigger?.Nickname ?? "", v.Text, null, v.Trigger != null
                    ? new MissionRef(script, MissionItemKind.Trigger, v.Trigger.Section.Ref)
                    : null)));
            }
            else
            {
                var value = values.FirstOrDefault();
                fields.Add(new PageField(field.Label, value?.Text ?? "None", null, value?.Trigger != null
                    ? new MissionRef(script, MissionItemKind.Trigger, value.Trigger.Section.Ref)
                    : null));
            }
        }

        var members = new List<PageField>();
        if (instance.Core is { } core)
            members.Add(new PageField("Core", core.Nickname, null, new MissionRef(script, MissionItemKind.Trigger, core.Section.Ref)));
        foreach (var rule in definition.Members)
        {
            foreach (var trigger in instance.Role(rule.Role))
                members.Add(new PageField(rule.Label, trigger.Nickname, instance.Claimed.Contains(trigger) ? null
                        : "Another prefab holds this trigger. It counts for the steps of this prefab too.",
                    new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref)));
        }

        var sections = new List<PageSection>
        {
            new("fields", "Fields", MissionGlyphs.OfPrefab(definition.Id), definition.Help, fields, []),
            new("members", "Triggers in this prefab", EditorIcons.Bolt, null, members, []),
            Problems(analysis, SectionProblems(analysis, instance.Triggers.Select(t => t.Section)
                .Append(instance.CoreSection).OfType<MissionSection>().Distinct().ToArray()), context.ShowBaseGame),
        };
        var ini = instance.Triggers.Select(t => t.Section.Ref).Prepend(instance.CoreSection?.Ref).OfType<SectionRef>().Distinct().ToList();
        return new MissionPageModel($"{definition.Name} {instance.Name}", "Behavior prefab", MissionGlyphs.OfPrefab(definition.Id),
            definition.Help, MissionCompleteness.OfInstance(analysis, instance), sections, null) { IniSections = ini };
    }

    /// <summary>The page of a prefab kind (section 10.5 of the M6a design): what it is, what it writes, its fields, its
    /// steps and its uses.</summary>
    private static MissionPageModel PrefabKind(PageContext context, MissionPrefabDefinition definition)
    {
        var script = context.Analysis.Script.Path;
        var uses = context.Analysis.Prefabs?.OfPrefab(definition.Id).ToList() ?? [];
        var total = context.Result.Analyses.Values.Sum(a => a.Prefabs?.OfPrefab(definition.Id).Count() ?? 0);
        var sections = new List<PageSection>
        {
            new("whatItIs", "What it is", EditorIcons.InfoCircle, null, [], [definition.Help.WhatItIs]),
            new("whatItWrites", "What it writes", EditorIcons.Pen, null, [], [definition.Help.WhatItWrites]),
            new("fields", "Fields", MissionGlyphs.OfPrefab(definition.Id), null, definition.Fields.Select(f => new PageField(
                f.Label, f.List?.Codec switch
                {
                    PrefabListCodec.Lines => $"A list of {f.List.Items}",
                    PrefabListCodec.Triggers => $"A list of {f.List.Items}",
                    _ => "One value",
                })).ToList(), []),
            new("steps", "Steps", EditorIcons.CheckCircle, null,
                definition.Steps.Select(s => new PageField(s.Name, s.Reason)).ToList(), []),
            new("uses", $"Uses ({uses.Count.ToString(CultureInfo.InvariantCulture)})", EditorIcons.Link, null,
                uses.Select(i => new PageField(i.Name, i.Core?.Condition?.Sentence() ?? "", null,
                    new MissionRef(script, MissionItemKind.PrefabInstance, Key: i.Key))).ToList(),
                [total == 1 ? "1 instance in all missions of the browser." : $"{total.ToString(CultureInfo.InvariantCulture)} instances in all missions of the browser."]),
        };
        return new MissionPageModel(definition.Name, "Behavior prefab", MissionGlyphs.OfPrefab(definition.Id), definition.Help,
            null, sections, null);
    }

    // ---- The link pages (section 10.14).

    /// <summary>The page of a cutscene link (section 10.14 of the M6a design): the line that plays a space cutscene,
    /// with its completeness, its file, its anchor, the trigger that ends it, and a button that opens it.</summary>
    private static MissionPageModel CutsceneLink(PageContext context, MissionTrigger trigger, MissionLine line)
    {
        var analysis = context.Analysis;
        var script = analysis.Script.Path;
        var file = line.Value(0) ?? "";
        var fileRef = line.References.FirstOrDefault(r => r.ValueIndex == 0);
        var anchorRef = line.References.FirstOrDefault(r => r.ValueIndex == 1);
        var instance = analysis.Prefabs?.Instances.FirstOrDefault(i => i.Definition.Id == "spaceCutscene" && i.Core == trigger);
        var revert = instance?.Role("revert").FirstOrDefault();
        // The steps come from the checks of the line: a missing file (M22), an anchor that does not resolve (M06 and
        // M23), and no "End camera" after the cutscene (G06).
        var problems = SectionProblems(analysis, trigger.Section).Where(p => p.EntryIndex == line.Entry).ToList();
        var completeness = new CompletenessReport("Cutscene link",
        [
            MissionCompleteness.RuleStep("Cutscene file", "The game plays nothing without the file.",
                problems.Where(p => p.Rule == "M22").ToList()),
            MissionCompleteness.RuleStep("Anchor", "The camera of the cutscene moves with the anchor ship.",
                problems.Where(p => p.Rule is "M06" or "M23").ToList()),
            MissionCompleteness.RuleStep("Camera back", "Without \"End camera\", the camera does not come back to the player.",
                problems.Where(p => p.Rule == "G06").ToList()),
        ]) { HiddenMessages = BaseGameMessages.Count(problems) };
        var fields = new List<PageField> { new("Cutscene file", file, null, new CutsceneLink(file)) };
        if (line.Value(1) is { } anchor)
            fields.Add(new PageField("Anchor", anchorRef?.State == MissionReferenceState.Missing ? $"Missing: {anchor}" : anchor,
                "The ship that the camera of the cutscene moves with.",
                anchorRef?.Section != null ? MissionWorkspace.RefOf(anchorRef.Section.Ref, -1) : null)
            {
                Missing = anchorRef?.State == MissionReferenceState.Missing,
            });
        if (revert != null)
        {
            fields.Add(new PageField("Ends when", revert.Condition?.Sentence() ?? "No condition"));
            fields.Add(new PageField("Camera back", revert.Nickname, null,
                new MissionRef(script, MissionItemKind.Trigger, revert.Section.Ref)));
        }

        fields.Add(new PageField("Open in the Cutscene tab", file, "Open the space cutscene in the Cutscene tab.",
            new CutsceneLink(file)));
        var sections = new List<PageSection>
        {
            new("cutscene", "Cutscene", EditorIcons.Film, MissionSchema.Retail.Help["cutsceneLink"], fields, []),
            Lines("addedBy", "Played by", [line], script),
            Problems(analysis, problems, context.ShowBaseGame),
        };
        return new MissionPageModel($"{trigger.Nickname}: {MissionFiles.Display(file)}", "Cutscene link", EditorIcons.Film,
            MissionSchema.Retail.Help["cutsceneLink"], completeness, sections, trigger.Section.Ref);
    }

    /// <summary>The page of a base scene link (section 10.14 of the M6a design): the file, the repeat, and the lines that
    /// add it, remove it and wait for it.</summary>
    private static MissionPageModel BaseSceneLink(PageContext context, MissionLine line)
    {
        var analysis = context.Analysis;
        var index = analysis.Index;
        var script = analysis.Script.Path;
        var file = line.Value(0) ?? "";
        var users = index.Lines.Where(l => l.Trigger != null && l.Value(0) is { } value &&
                                           value.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        // The steps come from the checks of the line: a missing file (M22), and a path above its buffer (M12).
        var problems = SectionProblems(analysis, line.Section).Where(p => p.EntryIndex == line.Entry).ToList();
        var completeness = new CompletenessReport("Base scene link",
        [
            MissionCompleteness.RuleStep("Scene file", "The game plays nothing without the file.",
                problems.Where(p => p.Rule == "M22").ToList()),
            MissionCompleteness.RuleStep("Path length", "The game cuts a longer path, so it finds no file.",
                problems.Where(p => p.Rule == "M12").ToList()),
        ]) { HiddenMessages = BaseGameMessages.Count(problems) };
        var fields = new List<PageField> { new("Scene file", file) };
        var add = users.FirstOrDefault(l => MissionIndex.Is(l.Key, "Act_AddRTC"));
        if (add?.Value(1) is { } repeats)
            fields.Add(new PageField("Repeats", repeats, "True keeps the scene in the base after the player sees it."));
        var sections = new List<PageSection>
        {
            new("baseScene", "Base scene", EditorIcons.Image, MissionSchema.Retail.Help["baseSceneLink"], fields, []),
            Lines("addedBy", "Added by", users.Where(l => MissionIndex.Is(l.Key, "Act_AddRTC")), script),
            Lines("removedBy", "Removed by", users.Where(l => MissionIndex.Is(l.Key, "Act_RemoveRTC")), script),
            Lines("waitedForBy", "Waited for by", users.Where(l => MissionIndex.Is(l.Key, "Cnd_RTCDone")), script),
        };
        return new MissionPageModel(MissionFiles.Display(file), "Base scene link", EditorIcons.Image,
            MissionSchema.Retail.Help["baseSceneLink"], completeness, sections, line.Section.Ref);
    }
}

/// <summary>Helpers of the lines of the page models.</summary>
internal static class MissionLineKinds
{
    /// <summary>The kind of the name of a line: a condition, an action or an order. A line without a schema name counts
    /// by its prefix.</summary>
    public static MissionNameKind? Kind(this MissionLine line) => line.Name?.Kind ??
        (TriggerRules.IsCondition(line.Key) ? MissionNameKind.Condition
            : TriggerRules.IsAction(line.Key) ? MissionNameKind.Action
            : null);
}

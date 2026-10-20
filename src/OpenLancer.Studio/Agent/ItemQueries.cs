using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The lines of one key of a section (tool <c>get_item</c>).</summary>
/// <param name="Lines">The values of each line of the key, in file order.</param>
/// <param name="Label">The plain label of the list that the key names, or null.</param>
/// <param name="Locked">True when <c>set_fields</c> cannot change the key: the key line, or a section that the editor
/// cannot change.</param>
/// <param name="Unsaved">True when the lines of the key have an edit that the session did not save.</param>
internal sealed record FieldRow(string Key, IReadOnlyList<JsonArray> Lines, string? Label, ValueOrigin Origin, string? Mod,
    bool Locked, bool Unsaved, IReadOnlyList<Problem> Problems);

/// <summary>A component of an object and its problems.</summary>
internal sealed record ComponentRow(ObjectComponent Kind, string Name, IReadOnlyList<Problem> Problems);

/// <summary>A line that names an item.</summary>
/// <param name="Ref">The canonical reference of the section of the line.</param>
/// <param name="Line">The position of the line among the lines of the key in the section, from 0.</param>
/// <param name="Value">The index of the value in the line, from 0.</param>
/// <param name="List">The data list of the value.</param>
internal sealed record ReferenceRow(string Ref, string Key, int Line, int Value, string List);

/// <summary>The text mentions of a nickname in files that the lists do not read.</summary>
/// <param name="Pending">True while the editor builds the text index. Call the tool again in a few seconds.</param>
internal sealed record MentionsView(bool Pending, IReadOnlyList<StoryMention> Files);

/// <summary>A section of an INI file of the edit view (tool <c>get_item</c>).</summary>
/// <param name="File">The install path of the file.</param>
/// <param name="Name">The name that the game shows, or an empty text.</param>
internal sealed record SectionItemView(string Ref, string Type, string File, string Name, ItemOrigin Origin)
{
    /// <summary>Why the editor cannot change the section, or null.</summary>
    public string? ReadOnlyReason { get; init; }

    public IReadOnlyList<FieldRow> Fields { get; init; } = [];

    public IReadOnlyList<ComponentRow> Components { get; init; } = [];

    /// <summary>The objects whose <c>parent</c> line names the object.</summary>
    public IReadOnlyList<string> Children { get; init; } = [];

    /// <summary>The lines that name the item, on request.</summary>
    public IReadOnlyList<ReferenceRow>? UsedBy { get; init; }

    /// <summary>The text mentions of the nickname, on request.</summary>
    public MentionsView? Mentions { get; init; }

    /// <summary>The canonical INI text of the section, on request.</summary>
    public string? Raw { get; init; }

    /// <summary>The problems of the section that name no key.</summary>
    public IReadOnlyList<Problem> Problems { get; init; } = [];
}

/// <summary>A prop group (tool <c>list_prop_groups</c>).</summary>
/// <param name="Ref">The <c>group:</c> reference.</param>
/// <param name="Mod">The mod that has the group.</param>
/// <param name="Editable">True when the group is a group of the active mod.</param>
internal sealed record PropGroupRow(string Ref, string Nickname, string Name, string Mod, bool Editable, int Members,
    IReadOnlyList<string> Problems);

/// <summary>An asset of the Assets panel (tool <c>search_assets</c>).</summary>
/// <param name="Ref">The <c>asset:</c> reference.</param>
/// <param name="Marks">The marks of the tile: <c>added</c>, <c>changed</c> and <c>unsaved</c>.</param>
/// <param name="NotPlaceable">Why the Scene cannot place the asset, or null.</param>
internal sealed record AssetItemRow(string Ref, string Nickname, string Detail, string Category, ValueOrigin Origin,
    string? Mod, IReadOnlyList<string> Marks, string? NotPlaceable);

/// <summary>
/// The result of <c>get_item</c>: the kind of the item and the view of that kind. Exactly one view is not null.
/// </summary>
/// <param name="Kind"><c>section</c>, <c>mission_script</c>, <c>trigger</c>, <c>mission_item</c>,
/// <c>prefab_instance</c>, <c>prefab</c>, <c>cutscene_element</c>, <c>prop_group</c> or <c>asset</c>.</param>
internal sealed record ItemView(string Kind)
{
    public SectionItemView? Section { get; init; }

    public MissionOverview? MissionScript { get; init; }

    public MissionTriggerView? Trigger { get; init; }

    public MissionItemView? MissionItem { get; init; }

    public MissionInstanceView? PrefabInstance { get; init; }

    public MissionPrefabView? Prefab { get; init; }

    public CutsceneElementView? CutsceneElement { get; init; }

    public PropGroupRow? PropGroup { get; init; }

    public AssetItemRow? Asset { get; init; }
}

/// <summary>The result of <c>find_references</c>.</summary>
/// <param name="Lines">The lines of the INI files that name the item.</param>
/// <param name="MissionLines">The lines of the mission scripts that name the item.</param>
/// <param name="Mentions">The text mentions of the nickname, or null for an item without a nickname.</param>
internal sealed record ReferencesView(IReadOnlyList<ReferenceRow> Lines, IReadOnlyList<MissionReferenceView> MissionLines,
    MentionsView? Mentions);

/// <summary>A value of a data list (tool <c>list_values</c>).</summary>
/// <param name="Value">The name that a line writes.</param>
/// <param name="Label">The name that the game shows, or an empty text.</param>
/// <param name="Detail">The short text of the detail keys of the list.</param>
/// <param name="Ref">The reference of the section of the entry, or null for an enum value.</param>
/// <param name="Problem">Why the engine cannot use the entry, or null.</param>
internal sealed record ValueRow(string Value, string Label, string Detail, ValueOrigin Origin, string? Mod, string? Ref,
    bool Unsaved, string? Problem);

/// <summary>One line with problems of a raw text.</summary>
/// <param name="Line">The line, from 1.</param>
/// <param name="BaseGame">True when each problem of the line is a problem of the base game.</param>
/// <param name="Note">True for a note without a problem, for example why the editor cannot change a section.</param>
internal sealed record RawLineMark(int Line, DiagnosticSeverity Severity, string Message, bool BaseGame, bool Note);

/// <summary>The canonical INI text of a file or of a section of W (tool <c>get_raw_ini</c>).</summary>
/// <param name="File">The install path of the file.</param>
/// <param name="Ref">The reference of the section, or null for the whole file.</param>
internal sealed record RawIniView(string File, string? Ref, string Text, IReadOnlyList<RawLineMark> Marks);

/// <summary>The query tools of the items of each kind, the data lists, the assets and the raw INI (section 7.1 of the
/// MCP1 design).</summary>
internal static class ItemQueries
{
    public static ToolStep GetItem(ToolCall call)
    {
        var session = call.Session;
        var text = call.Args.Text("ref")!;
        var reference = ItemRefs.Parse(text);
        var missions = session.Missions.Queries;
        switch (reference.Kind)
        {
            case ItemRefKind.Script:
                return ToolResult.Of(new ItemView("mission_script") { MissionScript = missions.GetMission(ScriptRef.Parse(text)) });
            case ItemRefKind.Trigger:
                return ToolResult.Of(new ItemView("trigger") { Trigger = missions.GetTrigger(TriggerRef.Parse(text)) });
            case ItemRefKind.Label:
                return ToolResult.Of(new ItemView("mission_item") { MissionItem = missions.GetItem(LabelRef.Parse(text)) });
            case ItemRefKind.Instance:
                return ToolResult.Of(new ItemView("prefab_instance") { PrefabInstance = missions.GetPrefabInstance(text) });
            case ItemRefKind.Prefab:
                return ToolResult.Of(new ItemView("prefab") { Prefab = Prefab(missions, reference) });
            case ItemRefKind.Act:
                throw Bad(text, AgentTexts.Error("ref_act"));
            case ItemRefKind.Cutscene:
                throw Bad(text, AgentTexts.Error("ref_cutscene"));
            case ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent:
                return ToolResult.Of(new ItemView("cutscene_element")
                {
                    CutsceneElement = CutsceneQueries.Element(session, ItemRefs.ResolveCutsceneElement(session, reference)),
                });
            case ItemRefKind.Group:
                return ToolResult.Of(new ItemView("prop_group") { PropGroup = GroupRow(session, Group(session, reference)) });
            case ItemRefKind.Asset:
                return ToolResult.Of(new ItemView("asset") { Asset = Asset(session, reference) });
        }

        var section = ItemRefs.ResolveSection(session, reference);
        if (session.Missions.IsMissionFile(section.Path))
        {
            var item = ItemRef.Parse(ItemRefs.Of(section));
            return section.Id.Type.Equals("Trigger", StringComparison.OrdinalIgnoreCase) && section.Id.Key is { } trigger
                ? ToolResult.Of(new ItemView("trigger")
                {
                    Trigger = missions.GetTrigger(new TriggerRef(section.Path, trigger)),
                })
                : ToolResult.Of(new ItemView("mission_item") { MissionItem = missions.GetItem(item) });
        }
        return ToolResult.Of(new ItemView("section")
        {
            Section = SectionItem(call, section, call.Args.Flag("include_raw"), call.Args.Flag("include_used_by")),
        });
    }

    public static ToolStep FindReferences(ToolCall call)
    {
        var session = call.Session;
        var refText = call.Args.Text("ref");
        var name = call.Args.Text("name");
        if (refText == null == (name == null))
            throw Bad(refText ?? name ?? "", AgentTexts.Error("ref_or_name"));
        if (name != null)
        {
            var lines = new List<ReferenceRow>();
            foreach (var list in session.Lists.RuleData.Lists.Values.Where(l => l.Source != null))
            {
                if (session.Lists.IsScoped(list.Id) || session.Lists.List(list.Id)?.Find(name) != null)
                    lines.AddRange(Rows(session, list.Id, session.Lists.ReferencesTo(list.Id, name)));
            }
            return ToolResult.Of(new ReferencesView(lines, [], Mentions(session, name)));
        }

        var reference = ItemRefs.Parse(refText!);
        if (reference.Kind is ItemRefKind.Trigger or ItemRefKind.Label ||
            reference.Kind is ItemRefKind.Section or ItemRefKind.Line && session.Missions.IsMissionFile(reference.Path!))
        {
            var script = reference.Kind == ItemRefKind.Line
                ? ItemRefs.Of(new SectionRef(reference.Path!, reference.Section!))
                : refText!;
            return ToolResult.Of(new ReferencesView([], session.Missions.Queries.FindReferences(script), null));
        }
        var section = ItemRefs.ResolveSection(session, reference);
        var key = section.Id.Key ?? throw Bad(refText!, AgentTexts.Error("ref_no_key"));
        var found = new List<ReferenceRow>();
        foreach (var list in session.Lists.SourcesOf(section.Path, section.Id.Type))
        {
            var scope = session.Lists.IsScoped(list.Id) ? section.Path : null;
            found.AddRange(Rows(session, list.Id, session.Lists.ReferencesTo(list.Id, key, scope)));
        }
        IReadOnlyList<MissionReferenceView> missionLines = reference.Kind is ItemRefKind.Object or ItemRefKind.System or
            ItemRefKind.Base or ItemRefKind.ListEntry
            ? TryMissionLines(session, refText!)
            : [];
        return ToolResult.Of(new ReferencesView(found, missionLines, Mentions(session, key)));
    }

    public static ToolStep ListValues(ToolCall call)
    {
        var session = call.Session;
        var id = call.Args.Text("list")!;
        if (!session.Lists.RuleData.Lists.TryGetValue(id, out var definition))
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, "UnknownList",
                AgentTexts.Error("list_unknown", ("list", id),
                    ("lists", string.Join(", ", session.Lists.RuleData.Lists.Keys.Order(StringComparer.Ordinal)))))
            {
                Field = "list",
            });
        }
        ListView? view;
        if (session.Lists.IsScoped(definition.Id))
        {
            var system = call.Args.Text("system") ?? throw new EditException(new EditError(EditErrorKind.InvalidArgument,
                ToolInput.BadArgument, AgentTexts.Error("list_needs_system", ("list", definition.Id))) { Field = "system" });
            var path = SceneQueries.System(session, system).Path ?? throw new EditException(new EditError(
                EditErrorKind.NotFound, ItemRefs.NotFound, AgentTexts.Error("system_no_file", ("system", system))));
            view = session.Lists.List(definition.Id, path);
        }
        else
            view = session.Lists.List(definition.Id);
        var text = call.Args.Text("text");
        var rows = (view?.Entries ?? [])
            .Select(e => (Entry: e, Label: e.Name ?? (e.Section is { } s ? call.Texts.NameOf(s) : "")))
            .Where(r => ToolCall.Matches(text, r.Entry.Value, r.Label, r.Entry.Detail))
            .ToList();
        return ToolResult.Of(call.Paged(rows, r => new ValueRow(r.Entry.Value, r.Label, r.Entry.Detail, r.Entry.Origin,
            r.Entry.ModId, r.Entry.Section is { } s ? ItemRefs.Canonical(session, s) : null, r.Entry.Unsaved,
            r.Entry.Problem)));
    }

    public static ToolStep SearchAssets(ToolCall call)
    {
        var session = call.Session;
        var catalog = session.Assets;
        var category = call.Args.Text("category") ?? AssetCatalog.AllId;
        if (catalog.Categories.All(c => !c.Id.Equals(category, StringComparison.OrdinalIgnoreCase)))
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, "UnknownCategory",
                AgentTexts.Error("asset_category", ("category", category),
                    ("categories", string.Join(", ", catalog.Categories.Select(c => c.Id))))) { Field = "category" });
        }
        var kind = call.Args.Text("kind");
        var rows = catalog.Search(category, call.Args.Text("text") ?? "", r => r.Nickname)
            .Where(r => kind == null || AgentTools.Snake(r.Asset.Kind.ToString()) == kind)
            .ToList();
        return ToolResult.Of(call.Paged(rows, r => AssetRow(r)));
    }

    public static ToolStep ListPropGroups(ToolCall call)
    {
        var session = call.Session;
        var text = call.Args.Text("text");
        return ToolResult.Of(new PropGroupsView(PropGroups.Of(session)
            .Where(g => ToolCall.Matches(text, g.Nickname, g.Name))
            .Select(g => GroupRow(session, g)).ToList()));
    }

    public static ToolStep GetRawIni(ToolCall call)
    {
        var session = call.Session;
        var refText = call.Args.Text("ref");
        var file = call.Args.Text("file");
        if (refText == null == (file == null))
            throw Bad(refText ?? file ?? "", AgentTexts.Error("ref_or_file"));
        IReadOnlyList<SectionRef> sections;
        string path;
        SectionRef? one = null;
        if (file != null)
        {
            var normal = InstallPaths.Parse(file);
            path = session.OpenDocument(normal)?.Path ?? throw new EditException(new EditError(EditErrorKind.NotFound,
                ItemRefs.NotFound, AgentTexts.Error("file_none", ("file", normal))) { Field = "file" });
            sections = session.Sections(path);
        }
        else
        {
            one = ItemRefs.ResolveSection(session, ItemRefs.Parse(refText!));
            path = one.Path;
            sections = [one];
        }
        var document = session.OpenDocument(path)!;
        var raw = RawText.Canonical(path, sections.Select(s => document.Working[document.IndexOf(s.Id)]).ToList(),
            ids: sections.Select(s => s.Id).ToList());
        var diagnostics = session.Missions.IsMissionFile(path)
            ? session.Missions.Model(path).Problems
            : sections.SelectMany(s => session.Check(s).Concat(session.Lists.Problems(s).Select(p => p.Diagnostic))
                .Select(d => d with { Target = d.Target ?? s }));
        var marks = IniLint.Marks(raw, diagnostics.Where(d => one == null || one.Equals(d.Target)), showBaseGame: false);
        return ToolResult.Of(new RawIniView(path, one == null ? null : ItemRefs.Canonical(session, one), raw.Text,
            marks.Select(m => new RawLineMark(m.Line + 1, m.Severity, m.Message, m.BaseGame, m.Note)).ToList()));
    }

    /// <summary>The view of a section of an INI file that is not a mission file.</summary>
    private static SectionItemView SectionItem(ToolCall call, SectionRef section, bool raw, bool usedBy)
    {
        var session = call.Session;
        var view = session.Get(section)!;
        var schema = session.Schema(section);
        var keyEntry = schema.FirstOrDefault(k => k.IsKeyEntry)?.Name ?? KeyOf(view.Type);
        var saved = session.SavedSection(section);
        var diagnostics = session.Check(section).Concat(session.Lists.Problems(section).Select(p => p.Diagnostic))
            .Select(d => d with { Target = d.Target ?? section }).ToList();
        var fields = view.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var (origin, mod) = session.Origin(section, g.Key);
            var label = session.Lists.Rules(section, g.Key).SelectMany(r => r.Lists)
                .Select(l => session.Lists.RuleData.Lists.TryGetValue(l, out var d) ? d.Label : null).FirstOrDefault(l => l != null);
            var unsaved = saved == null || !SameLines(saved, view, g.Key);
            return new FieldRow(g.Key, g.Select(e => AgentRows.Values(e.Values)).ToList(), label, origin, mod,
                view.ReadOnlyReason != null || g.Key.Equals(keyEntry, StringComparison.OrdinalIgnoreCase), unsaved,
                diagnostics.Where(d => g.Key.Equals(d.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(d => AgentRows.ProblemOf(session, d)).ToList());
        }).ToList();
        var isObject = SceneSections.KindOf(session, section) == SceneKind.Object;
        var nickname = section.Id.Key;
        return new SectionItemView(ItemRefs.Canonical(session, section), view.Type, section.Path,
            call.Texts.TextOf(section, view.Type.Equals("system", StringComparison.OrdinalIgnoreCase) ? "strid_name" : "ids_name"),
            AgentRows.OriginOf(session, section, view, keyEntry))
        {
            ReadOnlyReason = view.ReadOnlyReason,
            Fields = fields,
            Components = isObject
                ? ObjectComponents.Of(session, section).Select(c => new ComponentRow(c.Kind, ObjectComponents.Name(c.Kind),
                    c.Problems.Select(p => AgentRows.ProblemOf(session, p)).ToList())).ToList()
                : [],
            Children = isObject && nickname != null
                ? session.Sections(section.Path)
                    .Where(s => s.Id.Type.Equals(SceneSections.ObjectType, StringComparison.OrdinalIgnoreCase) &&
                                session.Get(s) is { } child &&
                                string.Equals(SceneSections.Last(child, "parent"), nickname, StringComparison.OrdinalIgnoreCase))
                    .Select(s => ItemRefs.Canonical(session, s)).ToList()
                : [],
            UsedBy = usedBy && nickname != null
                ? session.Lists.SourcesOf(section.Path, section.Id.Type).SelectMany(l => Rows(session, l.Id,
                    session.Lists.ReferencesTo(l.Id, nickname, session.Lists.IsScoped(l.Id) ? section.Path : null))).ToList()
                : null,
            Mentions = usedBy && nickname != null ? Mentions(session, nickname) : null,
            Raw = raw ? RawSectionEdit.Format(view) : null,
            Problems = diagnostics.Where(d => d.Key == null).Select(d => AgentRows.ProblemOf(session, d)).ToList(),
        };
    }

    /// <summary>The key line of a section type without a schema, for its origin.</summary>
    private static string KeyOf(string type) => type.ToLowerInvariant() switch
    {
        "nebula" or "asteroids" => "zone",
        _ => "nickname",
    };

    /// <summary>True when a key has the same lines in two sections.</summary>
    private static bool SameLines(Section saved, SectionView view, string key)
    {
        var before = saved.Where(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(e => string.Join(",", e.Select(v => v.ToString()))).ToList();
        var now = view.Lines(key).Select(e => string.Join(",", e.Values.Select(v => v.ToString()))).ToList();
        return before.SequenceEqual(now, StringComparer.Ordinal);
    }

    private static IEnumerable<ReferenceRow> Rows(EditSession session, string list, IEnumerable<ListReference> references) =>
        references.Select(r => new ReferenceRow(ItemRefs.Canonical(session, r.Section), r.Key, r.Line, r.Index, list));

    /// <summary>The text mentions of a nickname. While the text index builds, the result says so and the tool does not
    /// wait for it, because the editor runs the call between two frames.</summary>
    private static MentionsView Mentions(EditSession session, string nickname)
    {
        if (!session.StoryMentionsReady)
        {
            session.StartStoryMentions();
            return new MentionsView(true, []);
        }
        return new MentionsView(false, session.StoryMentions(nickname));
    }

    /// <summary>The mission lines that name a name of the game data, or none when no mission line can name it.</summary>
    private static IReadOnlyList<MissionReferenceView> TryMissionLines(EditSession session, string reference)
    {
        try
        {
            return session.Missions.Queries.FindReferences(reference);
        }
        catch (EditException e) when (e.Error.Kind == EditErrorKind.InvalidArgument)
        {
            return [];
        }
    }

    private static MissionPrefabView Prefab(MissionQueries missions, ItemReference reference)
    {
        var text = reference.ToString();
        return missions.ListPrefabs().FirstOrDefault(p => p.Ref.Equals(text, StringComparison.OrdinalIgnoreCase)) ??
               throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                   AgentTexts.Error("prefab_none", ("ref", text))) { Target = text });
    }

    private static PropGroup Group(EditSession session, ItemReference reference)
    {
        var groups = PropGroups.Of(session);
        return groups.FirstOrDefault(g => g.Nickname.Equals(reference.Name, StringComparison.OrdinalIgnoreCase) &&
                                          (reference.Scope == null ? g.Editable
                                              : string.Equals(g.ModId, reference.Scope, StringComparison.OrdinalIgnoreCase))) ??
               throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                   AgentTexts.Error("group_none", ("ref", reference)) +
                   ItemRefs.Hint(reference.Name, groups.Select(g => g.Nickname))) { Target = reference.ToString() });
    }

    private static PropGroupRow GroupRow(EditSession session, PropGroup group) => new(
        ItemRefs.Format(new ItemReference(ItemRefKind.Group, group.Nickname) { Scope = group.Editable ? null : group.ModId }),
        group.Nickname, group.Name, group.ModId ?? session.ActiveMod?.Id ?? "", group.Editable, group.Members.Count,
        group.Problems.Select(p => p.Message).ToList());

    private static AssetItemRow Asset(EditSession session, ItemReference reference)
    {
        var kind = Enum.GetValues<AssetKind>().Cast<AssetKind?>()
            .FirstOrDefault(k => AgentTools.Snake(k!.Value.ToString()) == reference.Scope?.ToLowerInvariant());
        var row = kind is { } k ? session.Assets.Find(k, reference.Name) : null;
        return row != null
            ? AssetRow(row)
            : throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                AgentTexts.Error("asset_none", ("ref", reference))) { Target = reference.ToString() });
    }

    private static AssetItemRow AssetRow(AssetRow row) => new(
        ItemRefs.Format(new ItemReference(ItemRefKind.Asset, row.Asset.Id) { Scope = AgentTools.Snake(row.Asset.Kind.ToString()) }),
        row.Nickname, row.Detail, row.CategoryId, row.Origin, row.ModId,
        Enum.GetValues<AssetMark>().Where(m => m != AssetMark.None && row.Mark.HasFlag(m))
            .Select(m => AgentTools.Snake(m.ToString())).ToList(),
        row.NotPlaceable);

    private static EditException Bad(string target, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference, message) { Target = target });
}

/// <summary>The result of <c>list_prop_groups</c>.</summary>
internal sealed record PropGroupsView(IReadOnlyList<PropGroupRow> Groups);

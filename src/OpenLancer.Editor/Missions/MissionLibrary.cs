using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Missions;

namespace OpenLancer.Editor.Missions;

/// <summary>The kind of an item of the mission library (section 8.3 of the M6b design).</summary>
internal enum LibraryKind
{
    /// <summary>A named event of the library: a new trigger with the condition of the event.</summary>
    Event,

    Action,

    Condition,

    /// <summary>A behavior prefab: a new linked instance of the prefab (section 8.3 of the M6b design).</summary>
    Prefab,
}

/// <summary>One item of the mission library and of quick add: an event, an action or a condition.</summary>
/// <param name="Id">The id of an event, or the key of an action or a condition.</param>
/// <param name="Name">The plain name, for example "Spawn ship".</param>
/// <param name="Group">The title of the group of the item in the library, for example "Spawn and move".</param>
/// <param name="Key">The key of the line that the item writes, for example <c>Cnd_Timer</c>.</param>
internal sealed record LibraryItem(LibraryKind Kind, string Id, string Name, string Group, char Glyph, HelpText Help,
    string Key)
{
    /// <summary>True when the game data has no example of the line (decision D425). The library shows the item
    /// dim.</summary>
    public bool NoRetailExample { get; init; }

    /// <summary>The OpenLancer mark of the line, for example "OpenLancer: no effect", or null.</summary>
    public string? OpenLancerMark { get; init; }

    /// <summary>The tooltip of the OpenLancer mark, or null.</summary>
    public string? OpenLancerTip { get; init; }

    /// <summary>True for an item that makes a trigger: an event or a condition.</summary>
    public bool MakesTrigger => Kind is LibraryKind.Event or LibraryKind.Condition;

    /// <summary>The prefab of a <see cref="LibraryKind.Prefab"/> item, or null.</summary>
    public PrefabRef? Prefab { get; init; }

    /// <summary>The tooltip of the lock of a prefab of a lower mod, or null. Such a prefab can place, but it cannot
    /// change.</summary>
    public string? Lock { get; init; }

    /// <summary>Why the item cannot place, for example a prefab file with a problem, or null.</summary>
    public string? Problem { get; init; }
}

/// <summary>A group of the library, for example the actions of one action group.</summary>
internal sealed record LibraryGroup(string Title, IReadOnlyList<LibraryItem> Items);

/// <summary>A part of the library: "Events", "Actions" or "Conditions", with its groups.</summary>
internal sealed record LibrarySection(string Title, IReadOnlyList<LibraryGroup> Groups)
{
    public IEnumerable<LibraryItem> Items => Groups.SelectMany(g => g.Items);
}

/// <summary>
/// The items of the mission library strip and of quick add (section 8.3 of the M6b design): the 10 events, the actions
/// by their 17 groups and the conditions by their event groups. Each item has its glyph, its plain name, its info text
/// and its marks. The class also makes the first line of an item: the values that the user then changes in the
/// Inspector. The class makes no ImGui call, so the tests read it.
/// </summary>
internal static class MissionLibrary
{
    /// <summary>The title of the section and the group of the prefabs.</summary>
    public const string PrefabsTitle = "Prefabs";

    /// <summary>
    /// The section "Prefabs" of the library (section 8.3 of the M6b design): the built-in prefabs, then the prefabs of
    /// the active mod, then the prefabs of the lower mods with a lock. A prefab whose file has a problem shows dim and
    /// does not place.
    /// </summary>
    /// <param name="assets">The prefabs in the order of <see cref="MissionEditor.Prefabs"/>.</param>
    public static LibrarySection Prefabs(IReadOnlyList<PrefabAsset> assets) =>
        new(PrefabsTitle, [new LibraryGroup(PrefabsTitle, assets.Select(Prefab).ToList())]);

    /// <summary>The item of a prefab. A drag of a tile of the Assets panel carries it too.</summary>
    public static LibraryItem Prefab(PrefabAsset asset)
    {
        var template = asset.Template;
        var help = template is { Help.Length: > 0 }
            ? new HelpText(asset.Name, template.Help, template.Writes.Length > 0 ? template.Writes : MissionPageModel.AssetHelp.WhatItWrites)
            : MissionPageModel.AssetHelp with { Title = asset.Name };
        var glyph = asset.Ref.Builtin ? MissionGlyphs.OfPrefab(asset.Ref.Id) : EditorIcons.Cubes;
        return new LibraryItem(LibraryKind.Prefab, asset.Ref.ToString(), asset.Name, PrefabsTitle, glyph, help, asset.Ref.RecordText)
        {
            Prefab = asset.Ref,
            Lock = asset.Ref.Builtin || asset.Active ? null : LockTip(asset.ModId ?? ""),
            Problem = template == null
                ? $"The prefab file has a problem, so the prefab cannot place. {asset.Problems.FirstOrDefault()}"
                : null,
        };
    }

    /// <summary>The tooltip of the lock of a prefab of a lower mod (section 8.11 of the M6b design).</summary>
    public static string LockTip(string mod) =>
        $"This prefab belongs to mod {mod}. Only a prefab of the active mod can change.";

    /// <summary>The delay of a new "After a delay" trigger, in seconds (section 8.2 of the M6b design).</summary>
    public const double DefaultDelay = 3;

    /// <summary>The value of a new line for a name that the user picks later. The page shows it as "Missing".</summary>
    public const string NoName = "none";

    private const string NoParams = "no_params";

    // The library of the studio, with and without the OpenLancer marks. The schema and the coverage do not change while
    // the editor runs, so each form is made once.
    private static IReadOnlyList<LibrarySection>? marked;
    private static IReadOnlyList<LibrarySection>? plain;

    /// <summary>The library of the studio: the library strip, quick add of the graph and "Add action" of the Trigger page
    /// use it. The editor makes each form once.</summary>
    /// <param name="marks">True while the OpenLancer column shows: the items have the OpenLancer marks (UI rule
    /// 4).</param>
    public static IReadOnlyList<LibrarySection> Studio(bool marks) =>
        marks ? marked ??= Sections(EngineCoverage.Instance) : plain ??= Sections(null);

    /// <summary>The library with the marks of a coverage.</summary>
    /// <param name="coverage">The OpenLancer marks, or null when the marks do not show (UI rule 4).</param>
    public static IReadOnlyList<LibrarySection> Sections(IEngineCoverage? coverage)
    {
        var schema = MissionSchema.Retail;
        var events = MissionPrefabs.Retail.Events
            .Select(e => Event(schema, e, coverage)).OfType<LibraryItem>().ToList();
        var actions = schema.Groups.OrderBy(g => g.Order)
            .Select(g => new LibraryGroup(g.Section, schema.Actions.Where(a => a.Group == g.Id)
                .Select(a => Name(a, g.Section, MissionGlyphs.OfGroup(g.Id), coverage)).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
        var conditions = schema.Events.OrderBy(e => e.Order)
            .Select(e => new LibraryGroup(e.Label, schema.Conditions.Where(c => c.Group == e.Id)
                .Select(c => Name(c, e.Label, MissionGlyphs.OfEvent(e.Id), coverage)).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
        return
        [
            new LibrarySection("Events", [new LibraryGroup("Events", events)]),
            new LibrarySection("Actions", actions),
            new LibrarySection("Conditions", conditions),
        ];
    }

    /// <summary>The items of the library that match a search: each word of the search is in the name, the key, the
    /// group or the id of the item, without letter case. An empty search gives all items. The id of an event is a word
    /// such as "enterArea", so a search finds the event by it too.</summary>
    /// <param name="fits">Keeps only the items that fit the place of quick add, or null for all items.</param>
    public static IReadOnlyList<LibraryItem> Search(IReadOnlyList<LibrarySection> sections, string text,
        Func<LibraryItem, bool>? fits = null)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return sections.SelectMany(s => s.Items)
            .Where(i => fits == null || fits(i))
            .Where(i => words.All(w => Matches(i, w)))
            .ToList();
    }

    private static bool Matches(LibraryItem item, string word) =>
        item.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
        item.Key.Contains(word, StringComparison.OrdinalIgnoreCase) ||
        item.Group.Contains(word, StringComparison.OrdinalIgnoreCase) ||
        item.Id.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The item of an event: a new trigger with the first condition of the event.</summary>
    private static LibraryItem? Event(MissionSchema schema, MissionEventDefinition definition, IEngineCoverage? coverage)
    {
        if (definition.Conditions.Count == 0 || schema.Name(definition.Conditions[0]) is not { } condition)
            return null;
        var (mark, tip) = Mark(condition.Name, coverage);
        return new LibraryItem(LibraryKind.Event, definition.Id, definition.Label, "Events",
            MissionGlyphs.OfEvent(condition.Group),
            new HelpText(definition.Label,
                $"Adds a trigger that starts on this event: {condition.Plain.ToLowerInvariant()}.",
                $"A [Trigger] section with a {condition.Name} line."),
            condition.Name)
        {
            NoRetailExample = condition.NoRetailExample, OpenLancerMark = mark, OpenLancerTip = tip,
        };
    }

    /// <summary>The item of an action or a condition.</summary>
    private static LibraryItem Name(MissionName name, string group, char glyph, IEngineCoverage? coverage)
    {
        var (mark, tip) = Mark(name.Name, coverage);
        var help = name.Kind == MissionNameKind.Condition
            ? new HelpText(name.Plain, $"A condition: {name.Sentence(v => $"{{{Label(name, v)}}}")}",
                $"A {name.Name} line. A trigger has one condition.")
            : new HelpText(name.Plain, MissionSchema.Retail.Group(name.Group)?.Help.WhatItIs ?? name.Plain,
                $"A {name.Name} line in the trigger.");
        return new LibraryItem(name.Kind == MissionNameKind.Condition ? LibraryKind.Condition : LibraryKind.Action,
            name.Name, name.Plain, group, glyph, help, name.Name)
        {
            NoRetailExample = name.NoRetailExample, OpenLancerMark = mark, OpenLancerTip = tip,
        };
    }

    private static string Label(MissionName name, string value) => name.Value(value)?.Label ?? value;

    private static (string? Mark, string? Tip) Mark(string name, IEngineCoverage? coverage)
    {
        var mark = coverage?.Of(name) ?? EngineMark.Runs;
        return mark == EngineMark.Runs ? (null, null) : ($"OpenLancer: {OpenLancerGap.MarkText(mark)}", OpenLancerGap.MarkTooltip(mark));
    }

    /// <summary>The line that an item writes: the condition of an event or a condition, or an action.</summary>
    public static LineSpec Line(LibraryItem item)
    {
        var name = MissionSchema.Retail.Name(item.Key) ??
                   throw new ArgumentException($"{item.Key} is not a name of the schema.", nameof(item));
        var values = new Dictionary<string, MissionValue>(StringComparer.OrdinalIgnoreCase);
        switch (item.Id)
        {
            case "leaveArea":
                values["side"] = "outside";
                break;
            case "enterArea" when name.Value("side") != null:
                values["side"] = "inside";
                break;
        }
        return Starter(name, values);
    }

    /// <summary>
    /// The first line of a name: each value that the line needs gets a first value that the user then changes. The
    /// schema default and the "none" word of the schema stay to the layer. A number gets 0, and the delay of
    /// <c>Cnd_Timer</c> gets <see cref="DefaultDelay"/>. A word gets the first word of its set, and a bool gets true. A
    /// position gets 0, 0, 0, and a rotation gets the rotation without a turn. A name gets <see cref="NoName"/>, so the
    /// page shows "Missing" until the user picks one. A name without values gets <c>no_params</c>, as retail writes
    /// it.
    /// </summary>
    /// <param name="given">The values that the caller gives. They win over the first values.</param>
    public static LineSpec Starter(MissionName name, IReadOnlyDictionary<string, MissionValue>? given = null)
    {
        if (name.Values.Count == 0)
            return LineSpec.Raw(name.Name, NoParams);
        var values = new Dictionary<string, MissionValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in name.Values.OrderBy(v => v.Index))
        {
            if (given?.TryGetValue(spec.Name, out var value) == true)
            {
                values[spec.Name] = value;
                continue;
            }
            if (spec.Optional || spec.Default != null || spec.NoneToken != null)
                continue;
            values[spec.Name] = First(name.Name, spec);
        }
        foreach (var (key, value) in given ?? new Dictionary<string, MissionValue>())
            values.TryAdd(key, value);
        return new LineSpec(name.Name) { Values = values };
    }

    /// <summary>The first line of a key of a section with named values, by the rules of <see cref="Starter"/>. The page
    /// of an item adds it for a key that the section does not have (section 8.4 of the M6b design).</summary>
    public static LineSpec KeyStarter(MissionKeySpec key) =>
        new(key.Key)
        {
            Values = key.Values.Where(s => !s.Optional && s.Default == null && s.NoneToken == null)
                .ToDictionary(s => s.Name, s => First(key.Key, s), StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>The first value of a value spec of a line of a key.</summary>
    private static MissionValue First(string key, MissionValueSpec spec)
    {
        if (MissionIndex.Is(key, "Cnd_Timer") && spec.Name == "seconds")
            return DefaultDelay;
        var value = spec.Kind switch
        {
            MissionValueKind.Position => MissionValue.Of(0, 0, 0),
            MissionValueKind.Rotation => MissionValue.Of(1, 0, 0, 0),
            MissionValueKind.Number or MissionValueKind.WholeNumber or MissionValueKind.Attitude or
                MissionValueKind.TextId => MissionValue.Of(0),
            MissionValueKind.Bool => MissionValue.Of(true),
            _ when spec.Tokens != null && MissionSchema.Retail.Tokens.GetValueOrDefault(spec.Tokens) is { Values.Count: > 0 } set =>
                MissionValue.Of(set.Values[0]),
            _ => MissionValue.Of(NoName),
        };
        return spec.Rest && value.Type != MissionValueType.List ? MissionValue.List([value]) : value;
    }
}

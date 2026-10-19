using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Data.Missions;

/// <summary>What a behavior prefab groups (section 8.1 of the M6a design).</summary>
public enum MissionPrefabKind
{
    /// <summary>A group of triggers that does one part of the mission.</summary>
    Beat,

    /// <summary>A pattern of an <c>[ObjList]</c>. It claims no trigger.</summary>
    Order,
}

/// <summary>The core match of a prefab: what a core trigger or a core section has (section 14.5 of the M6a
/// design).</summary>
public sealed record PrefabCore
{
    public IReadOnlyList<string> AnyAction { get; init; } = [];

    public IReadOnlyList<string> NoAction { get; init; } = [];

    /// <summary>The key of the condition that counts, or null.</summary>
    public string? Condition { get; init; }

    /// <summary>Value 0 of <see cref="Condition"/>, or null for any value.</summary>
    public string? ConditionValue { get; init; }

    public bool Repeatable { get; init; }

    public bool NoActions { get; init; }

    /// <summary>A section type for a prefab whose core is a section, for example <c>MsnRandEnc</c>, or null.</summary>
    public string? Section { get; init; }

    /// <summary>True for a core trigger that triggers watch with ON and with OFF.</summary>
    public bool WatchedTarget { get; init; }

    /// <summary>An action group that each action other than a start or stop line belongs to, or null.</summary>
    public string? AllNonFlowInGroup { get; init; }

    /// <summary>The keys of the actions other than start and stop lines, or empty.</summary>
    public IReadOnlyList<string> AllNonFlowActions { get; init; } = [];

    /// <summary>An action key and its value 0, or null.</summary>
    public (string Action, string Value)? ActionValue { get; init; }

    /// <summary>The action group of a chain of triggers, for example <c>comms</c>, or null.</summary>
    public string? Chain { get; init; }

    public IReadOnlyList<string> AnyKey { get; init; } = [];

    public (IReadOnlyList<string> Keys, int Count)? MinKeys { get; init; }

    public IReadOnlyList<string> NoKey { get; init; } = [];
}

/// <summary>A member role of a prefab, with the rule that finds its triggers.</summary>
/// <param name="Shared">True when a trigger that an earlier instance claims still counts for this instance.</param>
/// <param name="MaxLinks">The most links of a path rule.</param>
public sealed record PrefabMemberRule(string Role, string Label, string Rule, bool Shared, int MaxLinks);

/// <summary>Where a field of a prefab reads its values.</summary>
/// <param name="Role">The role: <c>core</c>, a member role, or <c>all</c>.</param>
/// <param name="Trigger">True for the nicknames of the triggers of the role.</param>
/// <param name="Condition">True for the sentences of the conditions of the triggers of the role.</param>
/// <param name="Actions">The action keys whose value <paramref name="Value"/> the field reads.</param>
/// <param name="Key">A key of the core section, or null.</param>
/// <param name="Group">An action group whose lines the field lists, or null.</param>
/// <param name="AllActions">True for each action line of the triggers of the role.</param>
/// <param name="Keys">The keys of the core section whose lines the field lists.</param>
public sealed record PrefabSource(string Role, bool Trigger, bool Condition, IReadOnlyList<string> Actions, int Value,
    string? Key, string? Group, bool AllActions, IReadOnlyList<string> Keys);

/// <summary>How a list field of a prefab reads its rows (section 14.5 of the M6a design).</summary>
public enum PrefabListCodec
{
    /// <summary>One row for each line of the field, with the columns of its key (a mixed-keys list field).</summary>
    Lines,

    /// <summary>One row for each trigger of the role of the field, for example the triggers that end a spawn
    /// wave.</summary>
    Triggers,
}

/// <summary>The list data of a list field of a prefab.</summary>
/// <param name="Items">The plural word of the rows, for example "ships".</param>
public sealed record PrefabList(PrefabListCodec Codec, string Items);

/// <summary>A named field of a prefab, for example "Ships".</summary>
/// <param name="List">The list data of a list field, or null for a field with one value.</param>
public sealed record PrefabField(string Id, string Label, PrefabSource From, PrefabList? List)
{
    /// <summary>
    /// The keys of the lines of a <see cref="PrefabListCodec.Lines"/> list field. They are its action keys, the actions of
    /// its group, each action, or its keys of the core section.
    /// </summary>
    public IReadOnlyList<string> LineKeys(MissionSchema schema) =>
        From.Keys.Count > 0 ? From.Keys
        : From.Key != null ? [From.Key]
        : From.Actions.Count > 0 ? From.Actions
        : From.Group != null ? schema.Actions.Where(a => a.Group == From.Group).Select(a => a.Name).ToList()
        : From.AllActions ? schema.Actions.Select(a => a.Name).ToList()
        : [];

    /// <summary>
    /// The list field spec of a <see cref="PrefabListCodec.Lines"/> field (decision D540): a mixed-keys list of the lines
    /// of its keys, with the columns of the schema. Null for a field with one value and for a list of triggers.
    /// </summary>
    public ListFieldSpec? Spec(MissionSchema schema) => List is { Codec: PrefabListCodec.Lines } list
        ? schema.LinesList(Id, Label, LineKeys(schema), list.Items)
        : null;
}

/// <summary>A completeness step of a prefab (section 13.5 of the M6a design).</summary>
/// <param name="Requires">A role that must have a trigger, or null.</param>
/// <param name="Rules">The check rules whose shown problems make the step missing.</param>
/// <param name="Check">A fixed check of <see cref="MissionCompleteness"/>, or null.</param>
public sealed record PrefabStep(string Id, string Name, string? Requires, IReadOnlyList<string> Rules, string? Check,
    string Reason);

/// <summary>One behavior prefab of the catalog (section 8.2 of the M6a design).</summary>
/// <param name="Order">The place of a beat prefab in the order of the matcher, or 0 for an order prefab.</param>
/// <param name="ClaimsCore">False when the core trigger stays free for a later prefab, as the watched trigger of a
/// branch.</param>
public sealed record MissionPrefabDefinition(string Id, string Name, MissionPrefabKind Kind, int Order, HelpText Help,
    PrefabCore Core, IReadOnlyList<PrefabMemberRule> Members, IReadOnlyList<PrefabField> Fields,
    IReadOnlyList<PrefabStep> Steps, bool ClaimsCore);

/// <summary>A named event: a label of a trigger from its condition (section 8.2 of the M6a design). It claims no
/// trigger.</summary>
public sealed record MissionEventDefinition(string Id, string Label, IReadOnlyList<string> Conditions, string? Rule);

/// <summary>One value of a field of an instance, with its line or its trigger.</summary>
public sealed record PrefabFieldValue(string Text, MissionLine? Line, MissionTrigger? Trigger);

/// <summary>One instance of a behavior prefab in a script.</summary>
public sealed class MissionPrefabInstance
{
    internal MissionPrefabInstance(MissionPrefabDefinition definition, MissionTrigger? core, MissionSection? coreSection,
        IReadOnlyDictionary<string, IReadOnlyList<MissionTrigger>> members, IReadOnlyList<MissionTrigger> claimed)
    {
        Definition = definition;
        Core = core;
        CoreSection = coreSection ?? core?.Section;
        Members = members;
        Claimed = claimed;
    }

    public MissionPrefabDefinition Definition { get; }

    /// <summary>The core trigger, or null for a prefab whose core is a section.</summary>
    public MissionTrigger? Core { get; }

    /// <summary>The core section: the section of the core trigger, or the <c>[MsnRandEnc]</c> or <c>[ObjList]</c>.</summary>
    public MissionSection? CoreSection { get; }

    /// <summary>The triggers of each member role. A shared role can hold triggers that another instance claims.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MissionTrigger>> Members { get; }

    /// <summary>The triggers that this instance claims: its frame holds them.</summary>
    public IReadOnlyList<MissionTrigger> Claimed { get; }

    /// <summary>The identity of the instance in its script: the prefab and the core trigger or section. A base-game run
    /// of the script compares instances by it (decision D551).</summary>
    public string Key => $"{Definition.Id}|{Core?.Nickname ?? CoreSection?.Ref.Id.ToString()}";

    /// <summary>The name of the instance: the nickname of the core trigger or section.</summary>
    public string Name => Core?.Nickname ?? CoreSection?.Nickname ?? Definition.Name;

    /// <summary>The triggers of a role: <c>core</c>, a member role or <c>all</c>.</summary>
    public IReadOnlyList<MissionTrigger> Role(string role) => role switch
    {
        "core" => Core is { } core ? [core] : [],
        "all" => (Core is { } c ? new[] { c } : []).Concat(Members.Values.SelectMany(m => m)).Distinct().ToList(),
        _ => Members.GetValueOrDefault(role) ?? [],
    };

    /// <summary>The core trigger and each member trigger, without repeats.</summary>
    public IReadOnlyList<MissionTrigger> Triggers => Role("all");

    /// <summary>The values of a field (section 8.2 of the M6a design).</summary>
    public IReadOnlyList<PrefabFieldValue> Values(PrefabField field)
    {
        var from = field.From;
        var result = new List<PrefabFieldValue>();
        if (from.Key != null || from.Keys.Count > 0)
        {
            if (CoreSection == null)
                return result;
            var keys = from.Key != null ? [from.Key] : from.Keys;
            for (var i = 0; i < CoreSection.Section.Count; i++)
            {
                var entry = CoreSection.Section[i];
                if (!keys.Any(k => MissionIndex.Is(entry.Name, k)))
                    continue;
                var text = from.Key != null
                    ? entry.Count > from.Value ? MissionIndex.Text(entry[from.Value]) : ""
                    : $"{entry.Name}: {string.Join(", ", entry.Select(MissionIndex.Text))}";
                if (text.Length > 0)
                    result.Add(new PrefabFieldValue(text, null, null));
            }
            return result;
        }
        foreach (var trigger in Role(from.Role))
        {
            if (from.Trigger)
                result.Add(new PrefabFieldValue(trigger.Nickname, null, trigger));
            if (from.Condition && trigger.Condition is { } condition)
                result.Add(new PrefabFieldValue(condition.Sentence(), condition, trigger));
            foreach (var action in trigger.Actions)
            {
                if (from.AllActions || from.Group != null && action.Name?.Group == from.Group)
                    result.Add(new PrefabFieldValue($"{action.Name?.Plain ?? action.Key}: {string.Join(", ",
                        Enumerable.Range(0, action.Count).Select(action.Value).OfType<string>())}", action, trigger));
                else if (from.Actions.Any(a => MissionIndex.Is(action.Key, a)) && action.Value(from.Value) is { } value)
                    result.Add(new PrefabFieldValue(value, action, trigger));
            }
        }
        return result;
    }

    public override string ToString() => $"{Definition.Name} {Name}";
}

/// <summary>The prefab instances of one script, and the trigger claims.</summary>
public sealed class MissionPrefabResult(IReadOnlyList<MissionPrefabInstance> instances,
    IReadOnlyDictionary<MissionTrigger, MissionPrefabInstance> claims)
{
    public IReadOnlyList<MissionPrefabInstance> Instances { get; } = instances;

    /// <summary>The instance that claims a trigger, or null for a plain trigger.</summary>
    public MissionPrefabInstance? Of(MissionTrigger trigger) => claims.GetValueOrDefault(trigger);

    /// <summary>The instances of a prefab.</summary>
    public IEnumerable<MissionPrefabInstance> OfPrefab(string id) => Instances.Where(i => i.Definition.Id == id);

    /// <summary>The number of instances of each prefab id.</summary>
    public IReadOnlyDictionary<string, int> Counts =>
        Instances.GroupBy(i => i.Definition.Id).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>
/// The behavior prefabs of mission scripts and their matcher (section 14.5 of the M6a design, decision D524). In M6a the
/// prefabs group and explain the triggers. Prefab mode and overrides come in M6b on the same definitions.
/// </summary>
/// <remarks>The matcher runs the beat prefabs in their order, and the core triggers of one prefab in file order. A trigger
/// that an earlier instance claims cannot be a member of a later one, except in a shared role. The result is the same for
/// the same script.</remarks>
public sealed class MissionPrefabs
{
    private const string ResourceName = "OpenLancer.Data.Missions.mission-prefabs.json";

    /// <summary>The groups of the start and stop lines. The other groups are the work of a trigger.</summary>
    private static readonly HashSet<string> FlowGroups = ["starts", "stops"];

    private static readonly Lazy<MissionPrefabs> RetailPrefabs = new(() =>
    {
        using var stream = typeof(MissionPrefabs).Assembly.GetManifestResourceStream(ResourceName) ??
                           throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    private MissionPrefabs(IReadOnlyList<MissionPrefabDefinition> definitions, IReadOnlyList<MissionEventDefinition> events)
    {
        Definitions = definitions;
        Events = events;
    }

    /// <summary>The catalog that ships with the editor.</summary>
    public static MissionPrefabs Retail => RetailPrefabs.Value;

    /// <summary>The prefabs: the beat prefabs in matcher order, then the order prefabs.</summary>
    public IReadOnlyList<MissionPrefabDefinition> Definitions { get; }

    /// <summary>The ten named events.</summary>
    public IReadOnlyList<MissionEventDefinition> Events { get; }

    /// <summary>The prefab of an id, or null.</summary>
    public MissionPrefabDefinition? Definition(string id) => Definitions.FirstOrDefault(d => d.Id == id);

    /// <summary>The named event of a trigger from its condition, or null.</summary>
    public MissionEventDefinition? EventOf(MissionTrigger trigger)
    {
        if (trigger.Condition is not { } condition)
            return null;
        foreach (var definition in Events)
        {
            if (!definition.Conditions.Any(c => MissionIndex.Is(condition.Key, c)))
                continue;
            var matches = definition.Rule switch
            {
                "firstEnterSystem" => !trigger.Repeats &&
                                      (MissionIndex.Is(condition.Key, "Cnd_SystemEnter") || condition.Value(0) is { } system &&
                                       !MissionIndex.Is(system, "no_params")),
                "inside" => MissionIndex.Is(condition.Key, "Cnd_DistCircle") || MissionIndex.Is(condition.Value(0), "inside"),
                "outside" => MissionIndex.Is(condition.Value(0), "outside"),
                _ => true,
            };
            if (matches)
                return definition;
        }
        return null;
    }

    /// <summary>Reads prefab data.</summary>
    /// <exception cref="InvalidDataException">The data is not valid.</exception>
    public static MissionPrefabs Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            var root = document.RootElement;
            var events = root.GetProperty("events").EnumerateArray().Select(e => new MissionEventDefinition(
                Text(e, "id"), Text(e, "label"), Strings(e, "conditions"), Optional(e, "rule"))).ToList();
            var definitions = root.GetProperty("prefabs").EnumerateArray().Select(ReadPrefab).ToList();
            foreach (var definition in definitions)
            {
                foreach (var member in definition.Members.Where(m => !KnownRules.Contains(m.Rule)))
                    throw new FormatException($"The prefab {definition.Id} has the unknown member rule '{member.Rule}'.");
                foreach (var step in definition.Steps.Where(s => s.Check != null && !MissionCompleteness.Checks.Contains(s.Check)))
                    throw new FormatException($"The prefab {definition.Id} has the unknown step check '{step.Check}'.");
                var roles = definition.Members.Select(m => m.Role).Append("core").Append("all").ToHashSet();
                foreach (var field in definition.Fields.Where(f => !roles.Contains(f.From.Role)))
                    throw new FormatException($"A field of the prefab {definition.Id} reads the unknown role '{field.From.Role}'.");
                foreach (var step in definition.Steps.Where(s => s.Requires != null && !roles.Contains(s.Requires)))
                    throw new FormatException($"A step of the prefab {definition.Id} requires the unknown role '{step.Requires}'.");
            }
            var ordered = definitions.Where(d => d.Kind == MissionPrefabKind.Beat).OrderBy(d => d.Order)
                .Concat(definitions.Where(d => d.Kind == MissionPrefabKind.Order)).ToList();
            return new MissionPrefabs(ordered, events);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or
                                       FormatException or ArgumentException)
        {
            throw new InvalidDataException("The mission prefabs are not valid: " + e.Message, e);
        }
    }

    /// <summary>The member rules that the matcher knows.</summary>
    private static readonly HashSet<string> KnownRules =
    [
        "saveTarget", "msnResponseTriggers", "offerSceneTriggers", "firstRevertOnPath", "watchersOn", "watchersOff",
        "encounterArmers", "encounterReactions", "destroyedWatchersOfSpawned", "destroyedWatchersOfMarked", "linearChain",
        "ordersGivers",
    ];

    /// <summary>Finds the instances of the prefabs in the index of a script.</summary>
    public MissionPrefabResult Match(MissionIndex index)
    {
        var claims = new Dictionary<MissionTrigger, MissionPrefabInstance>();
        var instances = new List<MissionPrefabInstance>();
        var triggers = index.Triggers.Where(t => !t.IsDuplicate).ToList();
        foreach (var definition in Definitions)
        {
            if (definition.Core.Section is { } type)
            {
                foreach (var section in index.Document.Sections.Where(s => MissionIndex.Is(s.Type, type) && SectionMatches(definition.Core, s)))
                {
                    var members = Members(index, definition, null, section, claims);
                    var instance = new MissionPrefabInstance(definition, null, section, members,
                        definition.Kind == MissionPrefabKind.Order ? [] : Claim(definition, null, members, claims));
                    Register(instance, claims);
                    instances.Add(instance);
                }
                continue;
            }
            foreach (var trigger in triggers)
            {
                if (definition.ClaimsCore && claims.ContainsKey(trigger) || !Matches(index, definition.Core, trigger))
                    continue;
                if (definition.Core.Chain is { } chain && !IsChainHead(index, trigger, chain))
                    continue;
                var members = Members(index, definition, trigger, null, claims);
                if (definition.Core.WatchedTarget &&
                    (members.GetValueOrDefault("on") is not { Count: > 0 } || members.GetValueOrDefault("off") is not { Count: > 0 }))
                    continue;
                if (definition.Core.Chain != null && members.GetValueOrDefault("chain") is not { Count: > 0 })
                    continue;
                var instance = new MissionPrefabInstance(definition, trigger, null, members,
                    Claim(definition, trigger, members, claims));
                Register(instance, claims);
                instances.Add(instance);
            }
        }
        return new MissionPrefabResult(instances, claims);
    }

    /// <summary>The triggers that a new instance claims: its core and the members that no earlier instance
    /// claims.</summary>
    private static List<MissionTrigger> Claim(MissionPrefabDefinition definition, MissionTrigger? core,
        IReadOnlyDictionary<string, IReadOnlyList<MissionTrigger>> members, Dictionary<MissionTrigger, MissionPrefabInstance> claims)
    {
        var result = new List<MissionTrigger>();
        if (core != null && definition.ClaimsCore)
            result.Add(core);
        foreach (var trigger in members.Values.SelectMany(m => m))
        {
            if (!claims.ContainsKey(trigger) && !result.Contains(trigger))
                result.Add(trigger);
        }
        return result;
    }

    private static void Register(MissionPrefabInstance instance, Dictionary<MissionTrigger, MissionPrefabInstance> claims)
    {
        foreach (var trigger in instance.Claimed)
            claims.TryAdd(trigger, instance);
    }

    /// <summary>True when a trigger matches the core of a prefab.</summary>
    private static bool Matches(MissionIndex index, PrefabCore core, MissionTrigger trigger)
    {
        if (core.AnyAction.Count > 0 && !trigger.Actions.Any(a => core.AnyAction.Any(k => MissionIndex.Is(a.Key, k))))
            return false;
        if (core.NoAction.Count > 0 && trigger.Actions.Any(a => core.NoAction.Any(k => MissionIndex.Is(a.Key, k))))
            return false;
        if (core.Condition != null && (!trigger.ConditionIs(core.Condition) ||
                                       core.ConditionValue != null && !MissionIndex.Is(trigger.Condition!.Value(0), core.ConditionValue)))
            return false;
        if (core.Repeatable && !trigger.Repeats)
            return false;
        if (core.NoActions && (trigger.Condition == null || trigger.Actions.Count > 0))
            return false;
        if (core.ActionValue is var (action, value) &&
            !trigger.ActionsOf(action).Any(a => MissionIndex.Is(a.Value(0), value)))
            return false;
        var work = trigger.Actions.Where(a => a.Name == null || !FlowGroups.Contains(a.Name.Group)).ToList();
        if (core.AllNonFlowInGroup is { } group && (work.Count == 0 || work.Any(a => a.Name?.Group != group)))
            return false;
        if (core.AllNonFlowActions.Count > 0 &&
            (work.Count == 0 || work.Any(a => !core.AllNonFlowActions.Any(k => MissionIndex.Is(a.Key, k)))))
            return false;
        if (core.WatchedTarget && !index.LinksTo(trigger).Any(l => l.Kind == MissionLinkKind.Watch))
            return false;
        if (core.Chain is { } chain && !OnlyGroup(trigger, chain))
            return false;
        return true;
    }

    /// <summary>True when a section matches the core of a prefab whose core is a section.</summary>
    private static bool SectionMatches(PrefabCore core, MissionSection section)
    {
        var keys = section.Section.Select(e => e.Name).ToList();
        if (core.AnyKey.Count > 0 && !keys.Any(k => core.AnyKey.Any(a => MissionIndex.Is(k, a))))
            return false;
        if (core.MinKeys is var (minKeys, count) && keys.Count(k => minKeys.Any(m => MissionIndex.Is(k, m))) < count)
            return false;
        if (core.NoKey.Count > 0 && keys.Any(k => core.NoKey.Any(n => MissionIndex.Is(k, n))))
            return false;
        return true;
    }

    /// <summary>True when each action of a trigger is in the group or is a start or stop line, and one is in the
    /// group.</summary>
    private static bool OnlyGroup(MissionTrigger trigger, string group) =>
        trigger.Actions.Any(a => a.Name?.Group == group) &&
        trigger.Actions.All(a => a.Name != null && (a.Name.Group == group || FlowGroups.Contains(a.Name.Group)));

    /// <summary>The only trigger that a trigger starts, when it has no other source, or null.</summary>
    private static MissionTrigger? OnlyNext(MissionIndex index, MissionTrigger trigger)
    {
        var starts = index.Links.Where(l => l.Source == trigger && l.Kind == MissionLinkKind.Start).ToList();
        if (starts.Count != 1 || starts[0].Target is not { } next || next == trigger)
            return null;
        return index.StartsOf(next).Select(l => l.Source).Distinct().Count() == 1 ? next : null;
    }

    /// <summary>True when a trigger starts a chain of the group: its source is not a link of such a chain.</summary>
    private static bool IsChainHead(MissionIndex index, MissionTrigger trigger, string group)
    {
        var sources = index.StartsOf(trigger).Select(l => l.Source).Distinct().ToList();
        return !(sources is [var source] && OnlyGroup(source, group) && OnlyNext(index, source) == trigger);
    }

    /// <summary>The triggers of each member role of a new instance.</summary>
    private static Dictionary<string, IReadOnlyList<MissionTrigger>> Members(MissionIndex index,
        MissionPrefabDefinition definition, MissionTrigger? core, MissionSection? section,
        Dictionary<MissionTrigger, MissionPrefabInstance> claims)
    {
        var result = new Dictionary<string, IReadOnlyList<MissionTrigger>>();
        var taken = new HashSet<MissionTrigger>();
        if (core != null)
            taken.Add(core);
        foreach (var member in definition.Members)
        {
            var found = Rule(index, member, core, section)
                .Where(t => member.Shared || !claims.ContainsKey(t) && !taken.Contains(t))
                .Distinct()
                .ToList();
            if (!member.Shared)
                taken.UnionWith(found);
            result[member.Role] = found;
        }
        return result;
    }

    /// <summary>The triggers of a member rule, in file order.</summary>
    private static IEnumerable<MissionTrigger> Rule(MissionIndex index, PrefabMemberRule member, MissionTrigger? core,
        MissionSection? section)
    {
        var triggers = index.Triggers.Where(t => !t.IsDuplicate);
        switch (member.Rule)
        {
            case "saveTarget":
                return core?.ActionsOf("Act_Save").Select(a => a.Value(0)).OfType<string>().Select(index.Trigger)
                    .OfType<MissionTrigger>().Take(1) ?? [];
            case "msnResponseTriggers":
                return triggers.Where(t => t.ConditionIs("Cnd_MsnResponse") && MissionIndex.Is(t.Condition!.Value(0), "reject"));
            case "offerSceneTriggers":
                return triggers.Where(t => t.ActionsOf("Act_AddRTC").Any(a =>
                    a.Value(0)?.Contains("offer", StringComparison.OrdinalIgnoreCase) == true));
            case "firstRevertOnPath":
                return core == null ? [] : FirstOnPath(index, core, t => t.ActionsOf("Act_RevertCam").Any(), member.MaxLinks);
            case "watchersOn":
                return Watchers(index, core, "ON", "ACTIVE");
            case "watchersOff":
                return Watchers(index, core, "OFF", "INACTIVE");
            case "encounterArmers":
                return section?.Nickname is { } armed
                    ? triggers.Where(t => t.Actions.Any(a => (MissionIndex.Is(a.Key, "Act_EnableEnc") ||
                                                              MissionIndex.Is(a.Key, "Act_DisableEnc")) &&
                                                             MissionIndex.Is(a.Value(0), armed)))
                    : [];
            case "encounterReactions":
                return section?.Nickname is { } launched
                    ? triggers.Where(t => t.ConditionIs("Cnd_EncLaunched") && MissionIndex.Is(t.Condition!.Value(0), launched))
                    : [];
            case "destroyedWatchersOfSpawned":
                return core == null ? [] : DestroyedWatchers(index, SpawnedNames(index, core));
            case "destroyedWatchersOfMarked":
                return core == null
                    ? []
                    : DestroyedWatchers(index, core.ActionsOf("Act_MarkObj").Where(a => a.Value(1) != "0")
                        .Select(a => a.Value(0)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));
            case "linearChain":
                return core == null ? [] : Chain(index, core);
            case "ordersGivers":
                return section?.Nickname is { } list
                    ? triggers.Where(t => t.ActionsOf("Act_GiveObjList").Any(a => MissionIndex.Is(a.Value(1), list)))
                    : [];
            default:
                return [];
        }
    }

    /// <summary>The first trigger with a property on the Start paths of a trigger, at most a number of links away, in
    /// breadth-first order.</summary>
    private static IEnumerable<MissionTrigger> FirstOnPath(MissionIndex index, MissionTrigger start,
        Func<MissionTrigger, bool> has, int maxLinks)
    {
        var seen = new HashSet<MissionTrigger> { start };
        var level = new List<MissionTrigger> { start };
        for (var depth = 0; depth < maxLinks && level.Count > 0; depth++)
        {
            var next = new List<MissionTrigger>();
            foreach (var trigger in level)
            {
                foreach (var link in index.Links.Where(l => l.Source == trigger && l.Kind == MissionLinkKind.Start &&
                                                            l.Target != null))
                {
                    if (!seen.Add(link.Target!))
                        continue;
                    if (has(link.Target!))
                        return [link.Target!];
                    next.Add(link.Target!);
                }
            }
            level = next;
        }
        return [];
    }

    /// <summary>The triggers whose condition watches a trigger with one of the states.</summary>
    private static IEnumerable<MissionTrigger> Watchers(MissionIndex index, MissionTrigger? target, params string[] states) =>
        target == null
            ? []
            : index.LinksTo(target).Where(l => l.Kind == MissionLinkKind.Watch && states.Contains(l.State))
                .Select(l => l.Source).OrderBy(t => t.Section.Index);

    /// <summary>The names that the spawn lines of a trigger spawn: the ships and formations, the ships of a spawned
    /// formation, and their labels.</summary>
    internal static HashSet<string> SpawnedNames(MissionIndex index, MissionTrigger trigger)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in trigger.Actions.Where(a => MissionIndex.Is(a.Key, "Act_SpawnShip") ||
                                                        MissionIndex.Is(a.Key, "Act_SpawnFormation")))
        {
            if (line.Value(0) is not { } name)
                continue;
            names.Add(name);
            foreach (var section in index.Items(MissionSectionKind.Formation, name)
                         .Concat(index.Items(MissionSectionKind.Ship, name)))
            {
                foreach (var entry in section.Section.Where(e => e.Count > 0 &&
                                                                 (MissionIndex.Is(e.Name, "ship") || MissionIndex.Is(e.Name, "label"))))
                    names.Add(MissionIndex.Text(entry[0]));
            }
        }
        return names;
    }

    /// <summary>The triggers whose condition <c>Cnd_Destroyed</c> names one of the names.</summary>
    private static IEnumerable<MissionTrigger> DestroyedWatchers(MissionIndex index, HashSet<string> names) =>
        index.Triggers.Where(t => !t.IsDuplicate && t.ConditionIs("Cnd_Destroyed") &&
                                  t.Condition!.Value(0) is { } target && names.Contains(target));

    /// <summary>The links of a chain of comm triggers after its head.</summary>
    private static List<MissionTrigger> Chain(MissionIndex index, MissionTrigger head)
    {
        var chain = new List<MissionTrigger>();
        var seen = new HashSet<MissionTrigger> { head };
        var current = head;
        while (OnlyNext(index, current) is { } next && OnlyGroup(next, "comms") && seen.Add(next))
        {
            chain.Add(next);
            current = next;
        }
        return chain;
    }

    private static MissionPrefabDefinition ReadPrefab(JsonElement e)
    {
        var kind = Text(e, "kind") switch
        {
            "beat" => MissionPrefabKind.Beat,
            "order" => MissionPrefabKind.Order,
            var other => throw new FormatException($"The prefab kind '{other}' is not known."),
        };
        return new MissionPrefabDefinition(Text(e, "id"), Text(e, "name"), kind,
            e.TryGetProperty("order", out var order) ? order.GetInt32() : 0, HelpText.Read(e.GetProperty("help")),
            ReadCore(e.GetProperty("core")),
            e.GetProperty("members").EnumerateArray().Select(m => new PrefabMemberRule(Text(m, "role"), Text(m, "label"),
                Text(m, "rule"), Bool(m, "shared"), m.TryGetProperty("maxLinks", out var links) ? links.GetInt32() : 6)).ToList(),
            e.GetProperty("fields").EnumerateArray().Select(ReadField).ToList(),
            e.GetProperty("steps").EnumerateArray().Select(s => new PrefabStep(Text(s, "id"), Text(s, "name"),
                Optional(s, "requires"), Strings(s, "rules"), Optional(s, "check"), Text(s, "reason"))).ToList(),
            !e.TryGetProperty("claimsCore", out var claims) || claims.GetBoolean());
    }

    private static PrefabCore ReadCore(JsonElement e) => new()
    {
        AnyAction = Strings(e, "anyAction"),
        NoAction = Strings(e, "noAction"),
        Condition = Optional(e, "condition"),
        ConditionValue = Optional(e, "value"),
        Repeatable = Bool(e, "repeatable"),
        NoActions = Bool(e, "noActions"),
        Section = Optional(e, "section"),
        WatchedTarget = Bool(e, "watchedTarget"),
        AllNonFlowInGroup = Optional(e, "allNonFlowInGroup"),
        AllNonFlowActions = Strings(e, "allNonFlowActions"),
        ActionValue = e.TryGetProperty("actionValue", out var av) ? (Text(av, "action"), Text(av, "value")) : null,
        Chain = Optional(e, "chain"),
        AnyKey = Strings(e, "anyKey"),
        MinKeys = e.TryGetProperty("minKeys", out var mk) ? (Strings(mk, "keys"), mk.GetProperty("count").GetInt32()) : null,
        NoKey = Strings(e, "noKey"),
    };

    private static PrefabField ReadField(JsonElement e)
    {
        var from = e.GetProperty("from");
        return new PrefabField(Text(e, "id"), Text(e, "label"), new PrefabSource(Text(from, "role"), Bool(from, "trigger"),
            Bool(from, "condition"), Strings(from, "actions"), from.TryGetProperty("value", out var v) ? v.GetInt32() : 0,
            Optional(from, "key"), Optional(from, "group"), Bool(from, "allActions"), Strings(from, "keys")),
            e.TryGetProperty("list", out var list) ? ReadList(list) : null);
    }

    private static PrefabList ReadList(JsonElement e) => new(Text(e, "codec") switch
    {
        "lines" => PrefabListCodec.Lines,
        "triggers" => PrefabListCodec.Triggers,
        var other => throw new FormatException($"The list codec '{other}' is not known."),
    }, Text(e, "items"));

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"An item has no '{name}'.");

    private static string? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.GetBoolean();

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : [];
}

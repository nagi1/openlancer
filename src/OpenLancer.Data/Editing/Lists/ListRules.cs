using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>What the engine does with a value that is not valid, or with a missing line (section 6.3 of the M21
/// design). Decisions D82 and D124 read it. The values go from the least to the most severe.</summary>
public enum RuleEffect
{
    /// <summary>The engine does not use the value.</summary>
    None,

    /// <summary>The item works with less, for example without its loadout.</summary>
    Degrade,

    /// <summary>The engine drops the item.</summary>
    Drop,

    /// <summary>The game crashes when it uses the item, for example when the player docks. The rule gives the
    /// consequence that tells when.</summary>
    Crash,

    /// <summary>The load of the game data stops.</summary>
    LoadBreak,
}

/// <summary>
/// A rule: one value of one key of one section type, in the files of a role, names an entry of a list.
/// </summary>
/// <param name="Role">The role of the files of the section, for example <c>system</c>.</param>
/// <param name="Section">The section type, for example <c>Object</c>. It compares without letter case.</param>
/// <param name="Key">The key, for example <c>archetype</c>. It compares without letter case.</param>
/// <param name="Index">The index of the value in a line of the key.</param>
/// <param name="Lists">The ids of the lists, in order. The first list that has the name gives the entry.</param>
/// <param name="Closed">True when the engine finds the value only through the list. Then a value of the active mod
/// outside the list is an error (decision D96).</param>
/// <param name="Required">True when the engine drops the item or stops the load without a valid line.</param>
/// <param name="RequiredWith">A key that makes the rule required when the section has a line of it, or null.</param>
/// <param name="Empty">True when an empty value is valid.</param>
/// <param name="Scope">Null, <c>file</c> (the sections of the file of the section), or <c>systemOf:N</c> (the
/// sections of the system file that value N of the line names).</param>
/// <param name="Label">The label of the value in a line with more than one listed value, or null.</param>
/// <param name="Effect">What the engine does with a value that is not valid, or with a missing line.</param>
/// <param name="Consequence">The message about what the engine does, or null.</param>
/// <param name="Evidence">The engine lines that prove the rule.</param>
public sealed record ListRule(string Role, string Section, string Key, int Index, IReadOnlyList<string> Lists,
    bool Closed, bool Required, string? RequiredWith, bool Empty, string? Scope, string? Label,
    RuleEffect Effect, string? Consequence, string? Evidence)
{
    /// <summary>True when no retail line has the key. The retail check then expects no line for the rule.</summary>
    public bool Unused { get; init; }

    /// <summary>
    /// True for the <c>scene</c> line of a room (section 5.6 of the M8b design): the rule reads value 1 of the line, or
    /// value 2 when value 0 is <c>all</c>, whatever its text, as the engine reads it (RoomInfo.cs:27-41). The engine
    /// skips a line with too few values (RoomInfo.cs:46-49), so such a line gets no check.
    /// </summary>
    public bool Scene { get; init; }

    /// <summary>The index of the value that the rule reads in a line, or -1 when the line has no such value. Without
    /// <see cref="Scene"/>, it is <see cref="Index"/>.</summary>
    public int IndexIn(Entry line) => IndexIn(line.Count, line.Count > 0 ? line[0].ToString() : null);

    /// <summary>The index of the value that the rule reads in the values of a line (see <see cref="IndexIn(Entry)"/>).
    /// The Inspector uses it to put a picked value at the place of the rule.</summary>
    public int IndexIn(IReadOnlyList<ValueBase> values) =>
        IndexIn(values.Count, values.Count > 0 ? values[0].ToString() : null);

    private int IndexIn(int count, string? first)
    {
        if (!Scene)
            return Index < count ? Index : -1;
        if (count == 0)
            return -1;
        var at = first?.Trim().Equals(SceneAll, StringComparison.OrdinalIgnoreCase) == true ? 2 : 1;
        return at < count ? at : -1;
    }

    /// <summary>The first value of a <c>scene</c> line for the ambient scripts of all times of day.</summary>
    private const string SceneAll = "all";

    /// <summary>The value index of a <c>systemOf:N</c> scope, or -1.</summary>
    public int SystemValue =>
        Scope != null && Scope.StartsWith(SystemOfPrefix, StringComparison.Ordinal) &&
        int.TryParse(Scope.AsSpan(SystemOfPrefix.Length), out var index)
            ? index
            : -1;

    /// <summary>What the game does with a value that is not valid, in plain words: the consequence, or the text of the
    /// effect.</summary>
    public string ConsequenceText => Consequence ?? Effect switch
    {
        RuleEffect.Degrade => "The game ignores this value.",
        RuleEffect.Drop => "The game drops this item.",
        RuleEffect.Crash => "The game crashes with this value.",
        RuleEffect.LoadBreak => "The game stops loading with this value.",
        _ => "The game does not use this value.",
    };

    /// <summary>What the game does when the key has no valid line, in plain words: the consequence, or the text of the
    /// effect.</summary>
    public string MissingText => Consequence ?? Effect switch
    {
        RuleEffect.LoadBreak => "The game stops loading without this key.",
        _ => ConsequenceText,
    };

    /// <summary>True for an effect that makes the item unusable: the engine drops it, the game crashes with it, or
    /// the load stops. A line that names such an item is then an error (decision D82, as decided on
    /// 2026-10-18).</summary>
    internal static bool MakesUnusable(RuleEffect effect) =>
        effect is RuleEffect.Drop or RuleEffect.Crash or RuleEffect.LoadBreak;

    internal const string SystemOfPrefix = "systemOf:";
}

/// <summary>How the engine finds the files of a role (section 7 of the M21 design).</summary>
/// <param name="Data">The <c>[Data]</c> name of the role in <c>freelancer.ini</c>, or null.</param>
/// <param name="Last">True when the engine reads only the last entry of the name.</param>
/// <param name="Fallback">The path that the engine reads without an entry, relative to the data folder, or null.</param>
/// <param name="Paths">Fixed paths that the engine reads, relative to the data folder.</param>
/// <param name="Engine">False when retail reads the files but the fork does not. A list of such a role is never
/// closed.</param>
/// <param name="Universe">True for <c>universe.ini</c>.</param>
/// <param name="SystemFiles">True for the files that the <c>[system]</c> sections of <c>universe.ini</c> name.</param>
/// <param name="RoomFiles">True for the room files that the <c>[Room]</c> sections of the base files name (decision D135
/// of the M8b design).</param>
internal sealed record RoleDefinition(string Name, string? Data, bool Last, string? Fallback,
    IReadOnlyList<string> Paths, bool Engine, bool Universe, bool SystemFiles, bool RoomFiles);

/// <summary>Where the entries of a list come from (section 6.4 of the M21 design).</summary>
/// <param name="Role">The role of the files, or null for a scoped list.</param>
/// <param name="Sections">The section types whose sections give the entries.</param>
/// <param name="Keys">The keys that give the names, or null for the key entry of the section type.</param>
/// <param name="Where">Key values that a section must have, for example <c>type = FLOAT</c>.</param>
/// <param name="Values">For a values list: the section type, the key and the value index whose distinct values are the
/// entries. Null for a sections list.</param>
/// <param name="Scoped">True when the rule gives the file (a scoped list).</param>
/// <param name="Files">For a files list: the extension of the files whose paths are the entries, for example <c>.thn</c>
/// (section 5.6 of the M8b design). Null for the other lists.</param>
internal sealed record ListSource(string? Role, IReadOnlyList<string> Sections, IReadOnlyList<string>? Keys,
    IReadOnlyDictionary<string, string> Where, (string Section, string Key, int Index)? Values, bool Scoped,
    string? Files = null);

/// <summary>One list of the rule data.</summary>
/// <param name="Kind">The kind of the entries, for the glyph of the editor.</param>
/// <param name="Hash">True when the engine adds the entries to a collection that throws when two names have one
/// FLHash.</param>
/// <param name="Unique">True when the engine finds an entry only when exactly one section has the name.</param>
/// <param name="Requires">The keys that an entry needs to be usable.</param>
/// <param name="Detail">The keys of the entry for the footer of the picker.</param>
/// <param name="Source">Where the entries come from, or null for an enum list.</param>
/// <param name="Enum">The full name of the C# enum of an enum list, or null.</param>
internal sealed record ListDefinition(string Id, string Label, string Kind, bool Hash, bool Unique,
    IReadOnlyList<string> Requires, IReadOnlyList<string> Detail, ListSource? Source, string? Enum);

/// <summary>
/// The rule data of the data lists: roles, lists and rules (decision D112). The editor ships the retail rules as an
/// embedded resource. M25 reads mod rule files in the same format.
/// </summary>
internal sealed class ListRules
{
    private const string ResourceName = "OpenLancer.Data.Editing.Lists.list-rules.json";

    private static readonly Lazy<ListRules> RetailRules = new(() =>
    {
        using var stream = typeof(ListRules).Assembly.GetManifestResourceStream(ResourceName) ??
                           throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    private readonly Dictionary<(string Role, string Section, string Key), List<ListRule>> byAddress;

    private ListRules(IReadOnlyDictionary<string, RoleDefinition> roles, IReadOnlyList<ListDefinition> lists,
        IReadOnlyList<ListRule> rules)
    {
        Roles = roles;
        Lists = lists.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        All = rules;
        byAddress = rules.GroupBy(r => (r.Role.ToLowerInvariant(), r.Section.ToLowerInvariant(), r.Key.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Index).ToList());
        ByRoleAndSection = rules.GroupBy(r => (r.Role.ToLowerInvariant(), r.Section.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ListRule>)g.ToList());
    }

    /// <summary>The rules that ship with the editor.</summary>
    public static ListRules Retail => RetailRules.Value;

    public IReadOnlyDictionary<string, RoleDefinition> Roles { get; }

    public IReadOnlyDictionary<string, ListDefinition> Lists { get; }

    /// <summary>All rules, in the order of the file.</summary>
    public IReadOnlyList<ListRule> All { get; }

    /// <summary>The rules of each (role, section type) in lower case.</summary>
    public IReadOnlyDictionary<(string Role, string Section), IReadOnlyList<ListRule>> ByRoleAndSection { get; }

    /// <summary>The rules of a key of a section type in the files of a role, by value index.</summary>
    public IReadOnlyList<ListRule> Rules(string role, string section, string key) =>
        byAddress.TryGetValue((role.ToLowerInvariant(), section.ToLowerInvariant(), key.ToLowerInvariant()),
            out var rules)
            ? rules
            : [];

    /// <summary>Reads rule data.</summary>
    /// <exception cref="InvalidDataException">The data is not valid.</exception>
    public static ListRules Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            var root = document.RootElement;
            var roles = root.GetProperty("roles").EnumerateObject()
                .Select(p => ReadRole(p.Name, p.Value))
                .ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            var lists = root.GetProperty("lists").EnumerateArray().Select(ReadList).ToList();
            var rules = root.GetProperty("rules").EnumerateArray().SelectMany(ReadRules).ToList();
            return new ListRules(roles, lists, rules);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or
                                       FormatException or ArgumentException)
        {
            throw new InvalidDataException("The list rules are not valid: " + e.Message, e);
        }
    }

    private static RoleDefinition ReadRole(string name, JsonElement e) => new(name,
        String(e, "data"), Bool(e, "last"), String(e, "fallback"), Strings(e, "paths"),
        !e.TryGetProperty("engine", out var engine) || engine.GetBoolean(), Bool(e, "universe"), Bool(e, "systemFiles"),
        Bool(e, "roomFiles"));

    private static ListDefinition ReadList(JsonElement e)
    {
        ListSource? source = null;
        if (e.TryGetProperty("source", out var s))
        {
            (string, string, int)? values = s.TryGetProperty("values", out var v)
                ? (v.GetProperty("section").GetString()!, v.GetProperty("key").GetString()!, v.GetProperty("index").GetInt32())
                : null;
            var where = s.TryGetProperty("where", out var w)
                ? w.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            source = new ListSource(String(s, "role"), Strings(s, "sections"),
                s.TryGetProperty("keys", out _) ? Strings(s, "keys") : null, where, values,
                String(s, "scope") == "file", String(s, "files"));
        }
        return new ListDefinition(e.GetProperty("id").GetString()!, e.GetProperty("label").GetString()!,
            String(e, "kind") ?? "Other", Bool(e, "hash"), Bool(e, "unique"), Strings(e, "requires"),
            Strings(e, "detail"), source, String(e, "enum"));
    }

    /// <summary>The rules of one entry of the rules array: one rule, or one rule for each element of
    /// <c>values</c>.</summary>
    private static IEnumerable<ListRule> ReadRules(JsonElement e)
    {
        var role = e.GetProperty("role").GetString()!;
        var section = e.GetProperty("section").GetString()!;
        var key = e.GetProperty("key").GetString()!;
        if (e.TryGetProperty("values", out var values))
            return values.EnumerateArray().Select(v => ReadRule(role, section, key, v.GetProperty("index").GetInt32(), v))
                .ToList();
        return [ReadRule(role, section, key, 0, e)];
    }

    private static ListRule ReadRule(string role, string section, string key, int index, JsonElement e)
    {
        IReadOnlyList<string> lists = e.TryGetProperty("lists", out _)
            ? Strings(e, "lists")
            : [e.GetProperty("list").GetString()!];
        var effect = String(e, "effect") switch
        {
            null or "none" => RuleEffect.None,
            "degrade" => RuleEffect.Degrade,
            "drop" => RuleEffect.Drop,
            "crash" => RuleEffect.Crash,
            "loadBreak" => RuleEffect.LoadBreak,
            var other => throw new FormatException($"The effect '{other}' of {section} {key} is not known."),
        };
        return new ListRule(role, section, key, index, lists, Bool(e, "closed"), Bool(e, "required"),
            String(e, "requiredWith"), Bool(e, "empty"), String(e, "scope"), String(e, "label"), effect,
            String(e, "consequence"), String(e, "evidence"))
        {
            Unused = Bool(e, "unused"),
            Scene = Bool(e, "scene"),
        };
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.GetBoolean();

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : [];
}

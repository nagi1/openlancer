using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The text of the active mod in the editor: its string table and its infocard table, by key. A table cannot change,
/// so the edit history keeps old tables to undo edits. Keys compare with letter case and sort by ordinal.
/// </summary>
internal sealed class ModText
{
    public static readonly ModText Empty = new(
        ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal));

    private ModText(ImmutableSortedDictionary<string, string> strings, ImmutableSortedDictionary<string, string> infocards)
    {
        Strings = strings;
        Infocards = infocards;
    }

    public ImmutableSortedDictionary<string, string> Strings { get; }

    public ImmutableSortedDictionary<string, string> Infocards { get; }

    public static ModText From(ModStrings strings) => From(strings.Strings, strings.Infocards);

    public static ModText From(IEnumerable<KeyValuePair<string, string>> strings,
        IEnumerable<KeyValuePair<string, string>> infocards) => new(
        ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, strings),
        ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, infocards));

    public string? Get(StringKind kind, string key) => Table(kind).GetValueOrDefault(key);

    /// <summary>The text with the entry set, or removed when <paramref name="text"/> is null.</summary>
    public ModText With(StringKind kind, string key, string? text)
    {
        var table = text == null ? Table(kind).Remove(key) : Table(kind).SetItem(key, text);
        return kind == StringKind.String ? new ModText(table, Infocards) : new ModText(Strings, table);
    }

    /// <summary>True when both have the same entries.</summary>
    public bool SameAs(ModText other) =>
        ReferenceEquals(this, other) || Same(Strings, other.Strings) && Same(Infocards, other.Infocards);

    /// <summary>
    /// The bytes of <c>strings.json</c> with this text, or null when the file must not exist. The file keeps its other
    /// fields, its byte order mark and its line ends. A table without entries leaves the file. The keys are sorted.
    /// </summary>
    /// <param name="path">The strings.json file of the mod. It need not exist.</param>
    /// <exception cref="InvalidDataException">The file has a comment, or is not a JSON object.</exception>
    public byte[]? ToFile(string path)
    {
        var file = File.Exists(path) ? JsonFile.Read(path) : JsonFile.Create();
        SetTable(file.Root, "strings", Strings);
        SetTable(file.Root, "infocards", Infocards);
        return file.Root.Count == 0 ? null : file.ToBytes();
    }

    private ImmutableSortedDictionary<string, string> Table(StringKind kind) =>
        kind == StringKind.String ? Strings : Infocards;

    /// <summary>Sets a field in place, so the field keeps its position. The field name compares without letter case.</summary>
    private static void SetTable(JsonObject root, string name, ImmutableSortedDictionary<string, string> table)
    {
        if (table.Count == 0)
        {
            root.Remove(name);
            return;
        }
        var json = new JsonObject();
        foreach (var (key, text) in table)
            json[key] = text;
        root[name] = json;
    }

    private static bool Same(ImmutableSortedDictionary<string, string> a, ImmutableSortedDictionary<string, string> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var text) && text == e.Value);
}

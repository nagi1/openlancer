using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Prefabs;

/// <summary>The kind of a field of a prefab template (section 5.3 of the M6b design).</summary>
public enum PrefabFieldKind
{
    /// <summary>One value of a schema value kind, for example a number or a formation. A line uses it as
    /// <c>{field}</c>.</summary>
    Value,

    /// <summary>Rows with named columns. A line with the slot <c>{field.column}</c> repeats once for each row.</summary>
    List,

    /// <summary>A list of triggers. A line with the slot <c>{field}</c> repeats once for each trigger.</summary>
    Triggers,

    /// <summary>One condition line with its key and its values. The line <c>{field} =</c> takes it.</summary>
    Condition,

    /// <summary>Lines with their keys and values, for example attitude lines. The line <c>{field} =</c> repeats once
    /// for each row.</summary>
    Lines,
}

/// <summary>A column of a list field: its id in the slots, its plain label and its kind.</summary>
public sealed record PrefabColumn(string Id, string Label, MissionValueKind Kind)
{
    /// <summary>The number of values of the column: 3 for a position, 4 for a rotation, else 1.</summary>
    public int Width => PrefabRow.WidthOf(Kind);
}

/// <summary>
/// One row of the value of a field (section 5.5 of the M6b design): the value texts of a value, a list row or a trigger,
/// or a line with its key for a condition field or a lines field. The text form is <c>a, b, c</c>, or
/// <c>Key = a, b</c> for a line.
/// </summary>
/// <param name="Key">The key of a line, or null for a row of values.</param>
public sealed record PrefabRow(string? Key, IReadOnlyList<string> Texts)
{
    /// <summary>A row of values.</summary>
    public static PrefabRow Of(params string[] texts) => new(null, texts);

    /// <summary>A line with its key.</summary>
    public static PrefabRow Line(string key, params string[] texts) => new(key, texts);

    /// <summary>The text form of the row.</summary>
    public string Format() => Key == null ? string.Join(", ", Texts) : Texts.Count == 0 ? $"{Key} =" : $"{Key} = {string.Join(", ", Texts)}";

    /// <summary>Reads the text form of a row of a field kind.</summary>
    public static PrefabRow Parse(PrefabFieldKind kind, string text)
    {
        text = text.Trim();
        if (kind is PrefabFieldKind.Condition or PrefabFieldKind.Lines)
        {
            var equals = text.IndexOf('=');
            var key = (equals < 0 ? text : text[..equals]).Trim();
            var rest = equals < 0 ? "" : text[(equals + 1)..];
            return new PrefabRow(key, Split(rest));
        }
        return new PrefabRow(null, Split(text));
    }

    /// <summary>The value texts of a text with commas between them. A text without a value gives none.</summary>
    public static IReadOnlyList<string> Split(string text) =>
        text.Trim().Length == 0 ? [] : text.Split(',').Select(t => t.Trim()).ToList();

    /// <summary>The number of values of a kind: 3 for a position, 4 for a rotation, else 1.</summary>
    public static int WidthOf(MissionValueKind kind) => kind switch
    {
        MissionValueKind.Position => 3,
        MissionValueKind.Rotation => 4,
        _ => 1,
    };

    public bool Equals(PrefabRow? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) &&
        Texts.SequenceEqual(other.Texts, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Key?.ToLowerInvariant(), Texts.Count);

    public override string ToString() => Format();
}

/// <summary>A field of a prefab template (section 5.3 of the M6b design).</summary>
/// <param name="Id">The id of the field in the slots and in the instance records.</param>
/// <param name="Label">The plain label, for example "Spawn when".</param>
public sealed record PrefabFieldDef(string Id, string Label, PrefabFieldKind Kind)
{
    /// <summary>The schema kind of a <see cref="PrefabFieldKind.Value"/> field.</summary>
    public MissionValueKind ValueKind { get; init; } = MissionValueKind.Text;

    /// <summary>The id of the word set of a field of the kind <c>token</c>, or null.</summary>
    public string? Tokens { get; init; }

    /// <summary>The columns of a <see cref="PrefabFieldKind.List"/> field.</summary>
    public IReadOnlyList<PrefabColumn> Columns { get; init; } = [];

    /// <summary>The keys that a condition field or a lines field takes. Empty takes each condition or each
    /// action.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>The unit of a number, for example <c>s</c>, or null.</summary>
    public string? Unit { get; init; }

    /// <summary>True when each instance sets the value itself. Such a field never shows as an override, as Unity treats
    /// the root position of an instance (decision D674).</summary>
    public bool PerInstance { get; init; }

    /// <summary>True when a value field can have no value. Each line with its slot then goes away.</summary>
    public bool Optional { get; init; }

    /// <summary>What the field holds, for its tooltip (UI rule 9).</summary>
    public string Help { get; init; } = "";

    /// <summary>The default rows. A value field has one row, or none when it is optional.</summary>
    public IReadOnlyList<PrefabRow> Default { get; init; } = [];

    /// <summary>True for a field whose value has a row for each item: a list, triggers or lines.</summary>
    public bool IsList => Kind is PrefabFieldKind.List or PrefabFieldKind.Triggers or PrefabFieldKind.Lines;

    /// <summary>The text of the kind in the template file.</summary>
    public string KindText => Kind switch
    {
        PrefabFieldKind.List => "list",
        PrefabFieldKind.Triggers => "triggers",
        PrefabFieldKind.Condition => "condition",
        PrefabFieldKind.Lines => "lines",
        _ => KindName(ValueKind),
    };

    /// <summary>The name of a schema value kind in the file, for example <c>wholeNumber</c>.</summary>
    public static string KindName(MissionValueKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public bool Equals(PrefabFieldDef? other) =>
        other is not null && Id == other.Id && Label == other.Label && Kind == other.Kind && ValueKind == other.ValueKind &&
        Tokens == other.Tokens && Columns.SequenceEqual(other.Columns) &&
        Keys.SequenceEqual(other.Keys, StringComparer.OrdinalIgnoreCase) && Unit == other.Unit &&
        PerInstance == other.PerInstance && Optional == other.Optional && Help == other.Help &&
        Default.SequenceEqual(other.Default);

    public override int GetHashCode() => HashCode.Combine(Id, Label, Kind, ValueKind);
}

/// <summary>An output of a prefab instance (section 5.3 of the M6b design). Each outside trigger that the user links to
/// the port gives one line where <c>{port:&lt;id&gt;}</c> stands.</summary>
/// <param name="Role">The role of the template section that has the port lines.</param>
public sealed record PrefabPort(string Id, string Label, string Role);

/// <summary>A line of a template section: the key and the raw text of the values, with the slots.</summary>
/// <param name="Line">The line number in the file, from 1, or 0 for a line that a command made.</param>
public sealed record PrefabLine(string Key, string Value, int Line = 0)
{
    public bool Equals(PrefabLine? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal) &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Key, Value);
}

/// <summary>A template section: a mission section type with its role and its lines with slots.</summary>
/// <param name="Line">The line number of the header in the file, from 1, or 0.</param>
public sealed record PrefabSectionDef(string Type, string Role, IReadOnlyList<PrefabLine> Lines, int Line = 0)
{
    /// <summary>The text of the first <c>nickname</c> line, for example <c>{instance}_spawn</c>, or null.</summary>
    public string? NicknamePattern =>
        Lines.FirstOrDefault(l => l.Key.Equals("nickname", StringComparison.OrdinalIgnoreCase))?.Value;

    public bool Equals(PrefabSectionDef? other) =>
        other is not null && string.Equals(Type, other.Type, StringComparison.OrdinalIgnoreCase) && Role == other.Role &&
        Lines.SequenceEqual(other.Lines);

    public override int GetHashCode() => HashCode.Combine(Type.ToLowerInvariant(), Role, Lines.Count);
}

/// <summary>A problem of the format check of a prefab template, with its line number from 1.</summary>
public sealed record PrefabProblem(int Line, string Message)
{
    public override string ToString() =>
        Line > 0 ? $"Line {Line.ToString(CultureInfo.InvariantCulture)} of the prefab: {Message}" : Message;
}

/// <summary>A slot in the text of a template line: <c>{instance}</c>, <c>{field}</c>, <c>{field.column}</c> or
/// <c>{port:id}</c>.</summary>
/// <param name="Start">The place of the opening brace in the text.</param>
/// <param name="Length">The length of the slot with its braces.</param>
/// <param name="Name">The text between the braces.</param>
public readonly record struct PrefabSlot(int Start, int Length, string Name)
{
    /// <summary>The text that names the instance id.</summary>
    public const string Instance = "instance";

    /// <summary>The start of a port slot.</summary>
    public const string PortPrefix = "port:";

    public bool IsInstance => Name == Instance;

    /// <summary>The id of a port slot, or null.</summary>
    public string? Port => Name.StartsWith(PortPrefix, StringComparison.Ordinal) ? Name[PortPrefix.Length..] : null;

    /// <summary>The field of a field slot, or null.</summary>
    public string? Field => IsInstance || Port != null ? null : Name.Split('.')[0];

    /// <summary>The column of a slot <c>{field.column}</c>, or null.</summary>
    public string? Column => IsInstance || Port != null || !Name.Contains('.') ? null : Name[(Name.IndexOf('.') + 1)..];

    /// <summary>The slots of a text, in order.</summary>
    public static IReadOnlyList<PrefabSlot> Of(string text)
    {
        var result = new List<PrefabSlot>();
        var at = 0;
        while ((at = text.IndexOf('{', at)) >= 0)
        {
            var close = text.IndexOf('}', at);
            if (close < 0)
                break;
            result.Add(new PrefabSlot(at, close - at + 1, text[(at + 1)..close].Trim()));
            at = close + 1;
        }
        return result;
    }

    /// <summary>The field of a line whose key is a slot, for example <c>{spawnWhen}</c>, or null.</summary>
    public static string? KeyField(string key)
    {
        var trimmed = key.Trim();
        return trimmed.Length > 2 && trimmed[0] == '{' && trimmed[^1] == '}' && trimmed.IndexOf('{', 1) < 0
            ? trimmed[1..^1].Trim()
            : null;
    }
}

/// <summary>
/// A behavior prefab template (section 5.3 of the M6b design): the header, the fields, the ports and the template
/// sections with slots. The record never changes: an edit makes a new one. <see cref="Write"/> gives the canonical text,
/// and <see cref="Read"/> reads a text and checks its format.
/// </summary>
/// <param name="Id">The id of the prefab, for example <c>spawnWave</c> or <c>ol_gate_ambush</c>.</param>
/// <param name="Name">The plain name in the library, for example "Spawn wave".</param>
public sealed record PrefabTemplate(string Id, string Name, MissionPrefabKind Kind)
{
    /// <summary>The format of the files that this editor reads and writes.</summary>
    public const int Format = 1;

    /// <summary>The first line of each template file.</summary>
    public const string Header = "; OpenLancer mission prefab, format 1. The game does not read this file.";

    /// <summary>The prefab that this prefab copies, as <c>builtin:&lt;id&gt;</c> or <c>mod:&lt;id&gt;</c>, or
    /// null.</summary>
    public string? VariantOf { get; init; }

    /// <summary>What the prefab is, for its info icon (UI rule 10). Empty when the file has no text.</summary>
    public string Help { get; init; } = "";

    /// <summary>What the prefab writes to the game files, for its info icon. Empty when the file has no text.</summary>
    public string Writes { get; init; } = "";

    public IReadOnlyList<PrefabFieldDef> Fields { get; init; } = [];

    public IReadOnlyList<PrefabPort> Ports { get; init; } = [];

    public IReadOnlyList<PrefabSectionDef> Sections { get; init; } = [];

    /// <summary>The field of an id, or null.</summary>
    public PrefabFieldDef? Field(string id) => Fields.FirstOrDefault(f => f.Id == id);

    /// <summary>The template section of a role, or null.</summary>
    public PrefabSectionDef? Section(string role) => Sections.FirstOrDefault(s => s.Role == role);

    /// <summary>The port of an id, or null.</summary>
    public PrefabPort? Port(string id) => Ports.FirstOrDefault(p => p.Id == id);

    /// <summary>The info text of the prefab (UI rule 10). A template without its own texts gets plain texts.</summary>
    public HelpText HelpText => new(Name,
        Help.Length > 0 ? Help : $"A behavior prefab: a group of triggers with named fields that each instance uses.",
        Writes.Length > 0 ? Writes : "The sections of each instance. The prefab itself is an editor file. The game does not read it.");

    /// <summary>
    /// The hash of the parts of the template that the compile reads: the fields, the ports and the template sections.
    /// The id, the name and the texts of the header do not count, so a rename does not make the instances old (rule
    /// P03). SHA-256, 16 hex digits.
    /// </summary>
    public string ContentHash => Hashes.GetValue(this, t => new StrongBox<string>(Hash(t.Body()))).Value!;

    // The hash of each template. A copy with 'with' is a new key, so a changed template gets its own hash.
    private static readonly ConditionalWeakTable<PrefabTemplate, StrongBox<string>> Hashes = new();

    /// <summary>The canonical text of the template.</summary>
    public string Write()
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        text.Append("[Prefab]\n");
        Line(text, "id", Id);
        Line(text, "name", Name);
        Line(text, "kind", Kind == MissionPrefabKind.Order ? "order" : "beat");
        if (VariantOf != null)
            Line(text, "variant_of", VariantOf);
        if (Help.Length > 0)
            Line(text, "help", Help);
        if (Writes.Length > 0)
            Line(text, "writes", Writes);
        Line(text, "format", Format.ToString(CultureInfo.InvariantCulture));
        text.Append(Body());
        return text.ToString();
    }

    /// <summary>The text of the fields, the ports and the template sections.</summary>
    private string Body()
    {
        var text = new StringBuilder();
        foreach (var field in Fields)
        {
            text.Append("\n[Field]\n");
            Line(text, "id", field.Id);
            Line(text, "label", field.Label);
            Line(text, "kind", field.KindText);
            if (field.Tokens != null)
                Line(text, "tokens", field.Tokens);
            foreach (var key in field.Keys)
                Line(text, "key", key);
            foreach (var column in field.Columns)
                Line(text, "column", $"{column.Id}, {PrefabFieldDef.KindName(column.Kind)}, {column.Label}");
            if (field.Unit != null)
                Line(text, "unit", field.Unit);
            if (field.PerInstance)
                Line(text, "per_instance", "true");
            if (field.Optional)
                Line(text, "optional", "true");
            if (field.Help.Length > 0)
                Line(text, "help", field.Help);
            foreach (var row in field.Default)
                Line(text, "default", row.Format());
        }
        foreach (var port in Ports)
        {
            text.Append("\n[Port]\n");
            Line(text, "id", port.Id);
            Line(text, "label", port.Label);
            Line(text, "role", port.Role);
        }
        foreach (var section in Sections)
        {
            text.Append('\n').Append('[').Append(section.Type).Append("]\n");
            Line(text, "role", section.Role);
            foreach (var line in section.Lines)
                Line(text, line.Key, line.Value);
        }
        return text.ToString();
    }

    private static void Line(StringBuilder text, string key, string value)
    {
        text.Append(key).Append(" =");
        if (value.Length > 0)
            text.Append(' ').Append(value.ReplaceLineEndings(" "));
        text.Append('\n');
    }

    /// <summary>The SHA-256 hash of a text as 16 hex digits.</summary>
    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))), 0, 8).ToLowerInvariant();

    public bool Equals(PrefabTemplate? other) =>
        other is not null && Id == other.Id && Name == other.Name && Kind == other.Kind && VariantOf == other.VariantOf &&
        Help == other.Help && Writes == other.Writes && Fields.SequenceEqual(other.Fields) &&
        Ports.SequenceEqual(other.Ports) && Sections.SequenceEqual(other.Sections);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Kind, Fields.Count, Sections.Count);

    /// <summary>
    /// Reads a template and checks its format (section 5.3 of the M6b design). Each bad line gives one problem with its
    /// line number. An asset with a problem does not place. Null when the text has no <c>[Prefab]</c> section first or
    /// an unknown format.
    /// </summary>
    public static PrefabTemplate? Read(string text, out IReadOnlyList<PrefabProblem> problems)
    {
        var reader = new TemplateReader(text);
        var template = reader.Read();
        problems = reader.Problems;
        return template;
    }

    /// <summary>The template of a text, from a cache of the texts that the editor read, or null with problems.</summary>
    public static (PrefabTemplate? Template, IReadOnlyList<PrefabProblem> Problems) Cached(string text)
    {
        lock (Parsed)
        {
            if (Parsed.TryGetValue(text, out var known))
                return known;
            var template = Read(text, out var problems);
            if (Parsed.Count > 256)
                Parsed.Clear();
            Parsed[text] = (template, problems);
            return (template, problems);
        }
    }

    private static readonly Dictionary<string, (PrefabTemplate?, IReadOnlyList<PrefabProblem>)> Parsed = new(StringComparer.Ordinal);
}

/// <summary>The reader and the format check of a template text (section 5.3 of the M6b design).</summary>
internal sealed class TemplateReader(string text)
{
    private readonly List<PrefabProblem> problems = [];
    private readonly MissionSchema schema = MissionSchema.Retail;

    public IReadOnlyList<PrefabProblem> Problems => problems;

    private sealed record RawSection(string Type, int Line, List<(string Key, string Value, int Line)> Lines);

    public PrefabTemplate? Read()
    {
        var sections = Parse();
        if (sections.Count == 0 || !Is(sections[0].Type, "Prefab"))
        {
            Problem(sections.Count == 0 ? 1 : sections[0].Line, "A prefab file starts with a [Prefab] section.");
            return null;
        }
        var header = sections[0];
        var format = Value(header, "format");
        if (format != PrefabTemplate.Format.ToString(CultureInfo.InvariantCulture))
        {
            Problem(LineOf(header, "format"), format == null
                ? "The [Prefab] section has no 'format' line. Add 'format = 1'."
                : $"The prefab has format {format}. This editor reads only format {PrefabTemplate.Format}.");
            return null;
        }
        var id = Required(header, "id") ?? "";
        if (id.Length > 0 && !IsId(id))
            Problem(LineOf(header, "id"), $"'{id}' is not a valid prefab id. Use letters, digits, '_' and '-'.");
        var name = Required(header, "name") ?? id;
        var kind = MissionPrefabKind.Beat;
        switch (Required(header, "kind"))
        {
            case "beat" or null:
                break;
            case "order":
                kind = MissionPrefabKind.Order;
                break;
            case var other:
                Problem(LineOf(header, "kind"), $"'{other}' is not a prefab kind. Use 'beat' or 'order'.");
                break;
        }
        var fields = new List<PrefabFieldDef>();
        var ports = new List<(PrefabPort Port, int Line)>();
        var templates = new List<PrefabSectionDef>();
        foreach (var section in sections.Skip(1))
        {
            if (Is(section.Type, "Prefab"))
                Problem(section.Line, "A prefab file has one [Prefab] section.");
            else if (Is(section.Type, "Field"))
                ReadField(section, fields);
            else if (Is(section.Type, "Port"))
                ReadPort(section, ports);
            else
                ReadSection(section, templates);
        }
        var template = new PrefabTemplate(id, name, kind)
        {
            VariantOf = Value(header, "variant_of"),
            Help = Value(header, "help") ?? "",
            Writes = Value(header, "writes") ?? "",
            Fields = fields,
            Ports = ports.Select(p => p.Port).ToList(),
            Sections = templates,
        };
        foreach (var (port, line) in ports.Where(p => template.Section(p.Port.Role) == null))
            Problem(line, $"Port '{port.Id}' names the role '{port.Role}', which no template section has.");
        foreach (var section in templates)
            CheckSlots(template, section);
        return template;
    }

    private List<RawSection> Parse()
    {
        var sections = new List<RawSection>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close < 0)
                {
                    Problem(i + 1, "A section header ends with ']'.");
                    continue;
                }
                sections.Add(new RawSection(line[1..close].Trim(), i + 1, []));
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals < 0 || sections.Count == 0)
            {
                Problem(i + 1, "A line has the form 'key = value' inside a section.");
                continue;
            }
            sections[^1].Lines.Add((line[..equals].Trim(), line[(equals + 1)..].Trim(), i + 1));
        }
        return sections;
    }

    private void ReadField(RawSection section, List<PrefabFieldDef> fields)
    {
        var id = Required(section, "id");
        if (id == null)
            return;
        if (!IsId(id))
        {
            Problem(LineOf(section, "id"), $"'{id}' is not a valid field id. Use letters, digits and '_'.");
            return;
        }
        if (fields.Any(f => f.Id == id))
        {
            Problem(LineOf(section, "id"), $"The field id '{id}' repeats.");
            return;
        }
        var label = Required(section, "label") ?? id;
        var kindText = Required(section, "kind") ?? "text";
        var field = new PrefabFieldDef(id, label, PrefabFieldKind.Value);
        switch (kindText)
        {
            case "list":
                field = field with { Kind = PrefabFieldKind.List };
                break;
            case "triggers":
                field = field with { Kind = PrefabFieldKind.Triggers, ValueKind = MissionValueKind.Trigger };
                break;
            case "condition":
                field = field with { Kind = PrefabFieldKind.Condition };
                break;
            case "lines":
                field = field with { Kind = PrefabFieldKind.Lines };
                break;
            default:
                if (!Enum.TryParse<MissionValueKind>(kindText, ignoreCase: true, out var valueKind) ||
                    int.TryParse(kindText, out _))
                {
                    Problem(LineOf(section, "kind"), $"'{kindText}' is not a field kind. Use a value kind of the mission " +
                                                     "schema, list, triggers, condition or lines.");
                    return;
                }
                field = field with { ValueKind = valueKind };
                break;
        }
        var columns = new List<PrefabColumn>();
        foreach (var (key, value, line) in section.Lines)
        {
            switch (key.ToLowerInvariant())
            {
                case "id" or "label" or "kind":
                    break;
                case "tokens":
                    if (!schema.Tokens.ContainsKey(value))
                        Problem(line, $"'{value}' is not a word set of the mission schema.");
                    field = field with { Tokens = value };
                    break;
                case "key":
                    if (field.Kind is not (PrefabFieldKind.Lines or PrefabFieldKind.Condition))
                        Problem(line, "Only a condition field or a lines field has 'key' lines.");
                    else if (schema.Name(value) is not { } name)
                        Problem(line, $"'{value}' is not an action or a condition of the game.");
                    else if (name.Kind != (field.Kind == PrefabFieldKind.Condition ? MissionNameKind.Condition : MissionNameKind.Action))
                        Problem(line, field.Kind == PrefabFieldKind.Condition
                            ? $"{name.Name} is an action. A condition field takes conditions."
                            : $"{name.Name} is a condition. A lines field takes actions.");
                    else
                        field = field with { Keys = [.. field.Keys, name.Name] };
                    break;
                case "column":
                    if (Column(value, line) is { } column)
                    {
                        if (columns.Any(c => c.Id == column.Id))
                            Problem(line, $"The column id '{column.Id}' repeats.");
                        else
                            columns.Add(column);
                    }
                    break;
                case "unit":
                    field = field with { Unit = value };
                    break;
                case "per_instance":
                    field = field with { PerInstance = Bool(value, line) };
                    break;
                case "optional":
                    field = field with { Optional = Bool(value, line) };
                    break;
                case "help":
                    field = field with { Help = value };
                    break;
                case "default":
                    break;
                default:
                    Problem(line, $"A [Field] section has no '{key}' line.");
                    break;
            }
        }
        field = field with { Columns = columns };
        if (field.Kind == PrefabFieldKind.List && columns.Count == 0)
            Problem(section.Line, $"List field '{id}' has no 'column' line. Add one, for example 'column = ship, ship, Ship'.");
        if (field.Kind != PrefabFieldKind.List && columns.Count > 0)
            Problem(section.Line, $"Only a list field has columns. Field '{id}' is not a list.");
        if (field is { Kind: PrefabFieldKind.Value, ValueKind: MissionValueKind.Token, Tokens: null })
            Problem(section.Line, $"Field '{id}' takes a word, so it needs a 'tokens' line with the id of a word set.");
        if (field.Optional && field.Kind != PrefabFieldKind.Value)
            Problem(LineOf(section, "optional"), "Only a value field can be optional. A list field can have no rows.");
        var rows = new List<PrefabRow>();
        foreach (var (key, value, line) in section.Lines.Where(l => Is(l.Key, "default")))
        {
            var row = PrefabRow.Parse(field.Kind, value);
            if (PrefabValues.RowProblem(field, row) is { } problem)
                Problem(line, problem);
            else
                rows.Add(row);
        }
        if (field.Kind == PrefabFieldKind.Value && rows.Count > 1)
            Problem(section.Line, $"Value field '{id}' has more than one 'default' line.");
        if (field.Kind == PrefabFieldKind.Condition && rows.Count > 1)
            Problem(section.Line, $"Condition field '{id}' has more than one 'default' line.");
        if (field.Kind is PrefabFieldKind.Value or PrefabFieldKind.Condition && rows.Count == 0 && !field.Optional)
            Problem(section.Line, $"Field '{id}' has no 'default' line. Add the value that a new instance gets.");
        fields.Add(field with { Default = rows });
    }

    private PrefabColumn? Column(string value, int line)
    {
        var parts = value.Split(',').Select(p => p.Trim()).ToList();
        if (parts.Count < 2 || parts.Count > 3 || parts[0].Length == 0 || !IsId(parts[0]))
        {
            Problem(line, "A column line has the form 'column = id, kind, label'.");
            return null;
        }
        if (!Enum.TryParse<MissionValueKind>(parts[1], ignoreCase: true, out var kind) || int.TryParse(parts[1], out _) ||
            kind == MissionValueKind.Token)
        {
            Problem(line, $"'{parts[1]}' is not a value kind of a column. Use a value kind of the mission schema other " +
                          "than token.");
            return null;
        }
        return new PrefabColumn(parts[0], parts.Count > 2 && parts[2].Length > 0 ? parts[2] : parts[0], kind);
    }

    private void ReadPort(RawSection section, List<(PrefabPort Port, int Line)> ports)
    {
        var id = Required(section, "id");
        var label = Required(section, "label");
        var role = Required(section, "role");
        if (id == null || label == null || role == null)
            return;
        if (!IsId(id))
            Problem(LineOf(section, "id"), $"'{id}' is not a valid port id. Use letters, digits and '_'.");
        else if (ports.Any(p => p.Port.Id == id))
            Problem(LineOf(section, "id"), $"The port id '{id}' repeats.");
        else
            ports.Add((new PrefabPort(id, label, role), section.Line));
    }

    private void ReadSection(RawSection section, List<PrefabSectionDef> templates)
    {
        var type = MissionIndex.Is(section.Type, "NPCShipArch") ? null : schema.Section(section.Type)?.Type;
        if (type == null)
        {
            Problem(section.Line, $"[{section.Type}] is not a section type of a mission script. A template section has " +
                                  "the type of a mission section, for example [Trigger] or [ObjList].");
            return;
        }
        if (section.Lines.Count == 0 || !Is(section.Lines[0].Key, "role"))
        {
            Problem(section.Line, "A template section starts with a 'role' line.");
            return;
        }
        var role = section.Lines[0].Value;
        if (!IsId(role))
        {
            Problem(section.Lines[0].Line, $"'{role}' is not a valid role. Use letters, digits and '_'.");
            return;
        }
        if (templates.Any(t => t.Role == role))
        {
            Problem(section.Lines[0].Line, $"The role '{role}' repeats. Each template section has its own role.");
            return;
        }
        var lines = section.Lines.Skip(1).Select(l => new PrefabLine(l.Key, l.Value, l.Line)).ToList();
        var nickname = lines.FirstOrDefault(l => Is(l.Key, "nickname"));
        if (nickname == null)
            Problem(section.Line, $"Template section '{role}' has no 'nickname' line. Add 'nickname = {{instance}}_{role}'.");
        else if (PrefabSlot.Of(nickname.Value).Any(s => !s.IsInstance))
            Problem(nickname.Line, "A nickname line can hold only the slot {instance}.");
        templates.Add(new PrefabSectionDef(type, role, lines, section.Line));
    }

    /// <summary>Checks the keys and the slots of the lines of a template section.</summary>
    private void CheckSlots(PrefabTemplate template, PrefabSectionDef section)
    {
        foreach (var line in section.Lines)
        {
            if (PrefabSlot.KeyField(line.Key) is { } keyField)
            {
                var field = template.Field(keyField);
                if (field == null)
                    Problem(line.Line, $"The slot {{{keyField}}} names no field of the prefab.");
                else if (field.Kind is not (PrefabFieldKind.Condition or PrefabFieldKind.Lines))
                    Problem(line.Line, $"Field '{keyField}' gives values, not lines. Use it as a value, for example 'key = {{{keyField}}}'.");
                else if (field.Kind == PrefabFieldKind.Condition && !MissionIndex.Is(section.Type, "Trigger"))
                    Problem(line.Line, "A condition field can stand only in a [Trigger] section.");
                if (line.Value.Length > 0)
                    Problem(line.Line, $"The line of field '{keyField}' has no value. Write '{{{keyField}}} ='.");
                continue;
            }
            try
            {
                MissionValues.Key(schema, section.Type, LinePlace.Section, line.Key);
            }
            catch (OpenLancer.Data.Editing.Commands.EditException e)
            {
                Problem(line.Line, e.Error.Message);
            }
            string? list = null;
            foreach (var slot in PrefabSlot.Of(line.Value))
            {
                string? owner = null;
                if (slot.IsInstance)
                    continue;
                if (slot.Port is { } port)
                {
                    if (template.Port(port) == null)
                        Problem(line.Line, $"The slot {{{slot.Name}}} names no port of the prefab.");
                    owner = "port:" + port;
                }
                else if (template.Field(slot.Field!) is not { } field)
                    Problem(line.Line, $"The slot {{{slot.Name}}} names no field of the prefab.");
                else if (field.Kind is PrefabFieldKind.Condition or PrefabFieldKind.Lines)
                    Problem(line.Line, $"Field '{field.Id}' gives whole lines. Write it as a line: '{{{field.Id}}} ='.");
                else if (field.Kind == PrefabFieldKind.List)
                {
                    if (slot.Column == null && field.Columns.Count != 1)
                        Problem(line.Line, $"List field '{field.Id}' has more than one column. Name the column, for example {{{field.Id}.{field.Columns[0].Id}}}.");
                    else if (slot.Column != null && field.Columns.All(c => c.Id != slot.Column))
                        Problem(line.Line, $"List field '{field.Id}' has no column '{slot.Column}'.");
                    owner = field.Id;
                }
                else if (slot.Column != null)
                    Problem(line.Line, $"Field '{field.Id}' is not a list, so it has no column '{slot.Column}'.");
                else if (field.Kind == PrefabFieldKind.Triggers)
                    owner = field.Id;
                if (owner == null)
                    continue;
                if (list != null && list != owner)
                    Problem(line.Line, "A line can repeat for one list or one port only. Split the line.");
                list ??= owner;
            }
        }
    }

    private string? Required(RawSection section, string key)
    {
        var value = Value(section, key);
        if (value is { Length: > 0 })
            return value;
        Problem(section.Line, $"The [{section.Type}] section has no '{key}' line.");
        return null;
    }

    private static string? Value(RawSection section, string key) =>
        section.Lines.FirstOrDefault(l => Is(l.Key, key)) is var (found, value, _) && found != null ? value : null;

    private static int LineOf(RawSection section, string key) =>
        section.Lines.FirstOrDefault(l => Is(l.Key, key)) is var (found, _, line) && found != null ? line : section.Line;

    private bool Bool(string value, int line)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!value.Equals("false", StringComparison.OrdinalIgnoreCase))
            Problem(line, $"'{value}' is not true or false.");
        return false;
    }

    /// <summary>True for an id of a prefab, a field, a column, a port or a role: letters, digits, '_' and '-'.</summary>
    internal static bool IsId(string text) =>
        text.Length is > 0 and <= 47 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private void Problem(int line, string message) => problems.Add(new PrefabProblem(line, message));

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The built-in behavior prefabs (section 5.2 of the M6b design, decision D672): one template for each of the 17 prefabs
/// of M6a, embedded in the data assembly. They use the ids of <c>mission-prefabs.json</c>, so the matcher and the
/// compiler share one definition. They are read-only, as the prefabs of a Unity package are.
/// </summary>
public static class BuiltinPrefabs
{
    private const string ResourcePrefix = "OpenLancer.Data.Missions.Prefabs.";

    private static readonly Lazy<IReadOnlyDictionary<string, (string Text, PrefabTemplate Template)>> Loaded = new(Load);

    /// <summary>The ids of the built-in prefabs, in the order of the matcher.</summary>
    public static IReadOnlyList<string> Ids => MissionPrefabs.Retail.Definitions.Select(d => d.Id).Where(Loaded.Value.ContainsKey).ToList();

    /// <summary>The template of a built-in prefab, or null.</summary>
    public static PrefabTemplate? Template(string id) => Loaded.Value.TryGetValue(id, out var known) ? known.Template : null;

    /// <summary>The text of the template of a built-in prefab, or null.</summary>
    public static string? Text(string id) => Loaded.Value.TryGetValue(id, out var known) ? known.Text : null;

    /// <exception cref="System.IO.InvalidDataException">A template is missing or has a problem.</exception>
    private static IReadOnlyDictionary<string, (string, PrefabTemplate)> Load()
    {
        var result = new Dictionary<string, (string, PrefabTemplate)>(StringComparer.Ordinal);
        var assembly = typeof(BuiltinPrefabs).Assembly;
        foreach (var definition in MissionPrefabs.Retail.Definitions)
        {
            var name = ResourcePrefix + definition.Id + ".ini";
            using var stream = assembly.GetManifestResourceStream(name) ??
                               throw new System.IO.InvalidDataException($"The resource {name} is missing.");
            using var reader = new System.IO.StreamReader(stream);
            var text = reader.ReadToEnd().Replace("\r\n", "\n");
            var template = PrefabTemplate.Read(text, out var problems);
            if (template == null || problems.Count > 0)
                throw new System.IO.InvalidDataException(
                    $"The built-in prefab {definition.Id} is not valid: {string.Join(" ", problems)}");
            if (template.Id != definition.Id)
                throw new System.IO.InvalidDataException($"The built-in prefab {definition.Id} has the id '{template.Id}'.");
            result[definition.Id] = (text, template);
        }
        return result;
    }
}

/// <summary>
/// A behavior prefab that an instance can name (section 5.2 of the M6b design): a built-in prefab, an asset of the active
/// mod, or an asset of a lower mod. The asset has its text, and its template when the text has no problem.
/// </summary>
/// <param name="Ref">The prefab.</param>
/// <param name="Text">The text of the template file.</param>
/// <param name="ModId">The mod of the asset, or null for a built-in prefab.</param>
/// <param name="Active">True for an asset of the active mod. Only such an asset can change.</param>
public sealed record PrefabAsset(Editing.PrefabRef Ref, string Text, string? ModId, bool Active)
{
    /// <summary>The template of the text and its problems, from the cache of the texts that the editor read.</summary>
    private (PrefabTemplate? Template, IReadOnlyList<PrefabProblem> Problems) Parsed => PrefabTemplate.Cached(Text);

    /// <summary>The template, or null when the format check found a problem.</summary>
    public PrefabTemplate? Template => Parsed.Problems.Count == 0 ? Parsed.Template : null;

    /// <summary>The problems of the format check.</summary>
    public IReadOnlyList<PrefabProblem> Problems => Parsed.Problems;

    /// <summary>The plain name of the prefab, or its id when the file has a problem.</summary>
    public string Name => Parsed.Template?.Name is { Length: > 0 } name ? name : Ref.Id;

    /// <summary>The path of an asset of a mod, relative to the mod folder (decision D673).</summary>
    public static string PathOf(string id) => $@"prefabs\missions\{id}.ini";

    /// <summary>The folder of the assets of a mod, relative to the mod folder.</summary>
    public const string Folder = @"prefabs\missions\";
}

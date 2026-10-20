using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenLancer.Data.Missions;

/// <summary>The place of a node of the mission graph that the user moved or placed (section 6.8 of the M6b design): the
/// trigger, and the top-left corner of the node in canvas points.</summary>
/// <param name="Trigger">The nickname of the trigger.</param>
public sealed record GraphNodePlace(string Trigger, float X, float Y);

/// <summary>One act of the graph file (section 6.8 of the M6b design): its nickname, its name, its walkthrough text and
/// its triggers in order. An act without a name shows the title that the editor gives a proposed act, for example "Act 2:
/// {system name}".</summary>
public sealed record GraphAct(string Nickname, string Name, string Text, IReadOnlyList<string> Members)
{
    public bool Equals(GraphAct? other) =>
        other is not null && Nickname == other.Nickname && Name == other.Name && Text == other.Text &&
        Members.SequenceEqual(other.Members);

    public override int GetHashCode() => HashCode.Combine(Nickname, Name, Text, Members.Count);
}

/// <summary>A section of the graph file that this editor keeps as it is, for example a <c>[Node]</c> record of a prefab
/// instance: its type and its lines.</summary>
public sealed record GraphRawSection(string Type, IReadOnlyList<(string Key, string Value)> Lines);

/// <summary>A member of a linked prefab instance: the role of its template section and the nickname of its section in
/// the script.</summary>
public sealed record InstanceMember(string Role, string Nickname);

/// <summary>The value of a field of a linked prefab instance: the text form of each row (see
/// <see cref="Prefabs.PrefabRow"/>). A list field can have no row.</summary>
public sealed record InstanceValue(string Field, IReadOnlyList<string> Rows)
{
    public bool Equals(InstanceValue? other) =>
        other is not null && Field == other.Field && Rows.SequenceEqual(other.Rows, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Field, Rows.Count);
}

/// <summary>A link of a port of a linked prefab instance to an outside trigger.</summary>
public sealed record InstancePort(string Port, string Trigger);

/// <summary>
/// The <c>[Instance]</c> record of a linked prefab instance in the graph file (section 5.5 of the M6b design): the
/// prefab, its hash at the last compile, the members, the per-instance values, the overrides, the removed members, the
/// port links and the hash of the compiled lines. The record never changes: an edit makes a new one.
/// </summary>
/// <param name="Id">The id of the instance in the script.</param>
/// <param name="Prefab">The prefab: <c>builtin:&lt;id&gt;</c> or <c>mod:&lt;id&gt;</c>.</param>
public sealed record InstanceRecord(string Id, string Prefab)
{
    /// <summary>The hash of the prefab at the last compile (<c>prefab_hash</c>), or null.</summary>
    public string? PrefabHash { get; init; }

    public IReadOnlyList<InstanceMember> Members { get; init; } = [];

    /// <summary>The values of the per-instance fields.</summary>
    public IReadOnlyList<InstanceValue> Values { get; init; } = [];

    /// <summary>The fields whose value differs from the default of the prefab.</summary>
    public IReadOnlyList<InstanceValue> Overrides { get; init; } = [];

    /// <summary>The roles of the members that the user deleted: the removed-member overrides (decision D676 c, the rule
    /// of Unity 2022.2 and later).</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];

    public IReadOnlyList<InstancePort> Ports { get; init; } = [];

    /// <summary>The hash of the compiled lines (<c>lines_hash</c>), or null. Other lines of the members are a hand
    /// edit (rule P01).</summary>
    public string? LinesHash { get; init; }

    /// <summary>The nickname of the member of a role, or null.</summary>
    public string? Member(string role) => Members.FirstOrDefault(m => m.Role == role)?.Nickname;

    /// <summary>The value or the override of a field, or null.</summary>
    public InstanceValue? ValueOf(string field) =>
        Values.FirstOrDefault(v => v.Field == field) ?? Overrides.FirstOrDefault(v => v.Field == field);

    public bool Equals(InstanceRecord? other) =>
        other is not null && Id == other.Id && Prefab == other.Prefab && PrefabHash == other.PrefabHash &&
        LinesHash == other.LinesHash && Members.SequenceEqual(other.Members) && Values.SequenceEqual(other.Values) &&
        Overrides.SequenceEqual(other.Overrides) && Removed.SequenceEqual(other.Removed) && Ports.SequenceEqual(other.Ports);

    public override int GetHashCode() => HashCode.Combine(Id, Prefab, Members.Count, Overrides.Count);
}

/// <summary>
/// The graph file of a mission script, <c>graphs\&lt;install path&gt;.ini</c> in the mod folder (section 6.8 of the M6b
/// design, decisions D404 and D679). It holds the nodes that the user moved or placed, the acts after the first act
/// edit, the records of the linked prefab instances, and the records of other kinds. The game, the merge and the
/// packages ignore the file. The record never changes: an edit makes a new one.
/// </summary>
public sealed record GraphFile(IReadOnlyList<GraphNodePlace> Nodes, IReadOnlyList<GraphAct> Acts,
    IReadOnlyList<GraphRawSection> Others)
{
    /// <summary>The format of the files that this editor writes.</summary>
    public const int Format = 1;

    /// <summary>The first line of each graph file.</summary>
    public const string Header = "; OpenLancer mission graph, format 1. The game does not read this file.";

    /// <summary>A graph file without records.</summary>
    public static readonly GraphFile Empty = new([], [], []);

    /// <summary>The records of the linked prefab instances of the script (section 5.5 of the M6b design).</summary>
    public IReadOnlyList<InstanceRecord> Instances { get; init; } = [];

    /// <summary>The record of a linked instance, without letter case, or null.</summary>
    public InstanceRecord? Instance(string id) =>
        Instances.FirstOrDefault(i => i.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why the editor cannot write the file, or null. A file of another format reads as no records, and a
    /// command does not write over it (review finding 12 of M6b-2).</summary>
    public string? Unreadable { get; init; }

    /// <summary>True when the file has a record. A file without a record is not written (section 6.8 of the M6b
    /// design).</summary>
    public bool HasRecords => Nodes.Count + Acts.Count + Instances.Count + Others.Count > 0;

    /// <summary>The path of the graph file of a script, relative to the mod folder.</summary>
    /// <param name="script">The install path of the script.</param>
    public static string PathOf(string script) => @"graphs\" + script;

    /// <summary>The place of the node of a trigger, or null.</summary>
    public GraphNodePlace? Place(string trigger) =>
        Nodes.FirstOrDefault(n => n.Trigger.Equals(trigger, StringComparison.OrdinalIgnoreCase));

    /// <summary>The act of a nickname, or null.</summary>
    public GraphAct? Act(string nickname) =>
        Acts.FirstOrDefault(a => a.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));

    /// <summary>The act that has a trigger as a member, or null.</summary>
    public GraphAct? ActOf(string trigger) =>
        Acts.FirstOrDefault(a => a.Members.Contains(trigger, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a graph file. A line that the editor cannot read gives a problem and stays out. A file of another format
    /// gives a problem and no records, and <see cref="Unreadable"/> tells why. Null text gives <see cref="Empty"/>.
    /// </summary>
    /// <param name="problems">The problems of the text, each with its line.</param>
    public static GraphFile Read(string? text, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        problems = found;
        if (string.IsNullOrWhiteSpace(text))
            return Empty;
        var sections = new List<(string Type, int Line, List<(string Key, string Value, int Line)> Lines)>();
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
                    found.Add(Problem(i, "A section header ends with ']'."));
                    continue;
                }
                sections.Add((line[1..close].Trim(), i, []));
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals < 0 || sections.Count == 0)
            {
                found.Add(Problem(i, "A line has the form 'key = value' inside a section."));
                continue;
            }
            sections[^1].Lines.Add((line[..equals].Trim(), line[(equals + 1)..].Trim(), i));
        }

        if (sections.FirstOrDefault(s => Is(s.Type, "Graph")) is { Lines: { } graph } &&
            graph.FirstOrDefault(l => Is(l.Key, "format")) is var (_, format, formatLine) && format != null &&
            format != Format.ToString(CultureInfo.InvariantCulture))
        {
            found.Add(Problem(formatLine, $"The graph file has format {format}. This editor reads format {Format}."));
            return Empty with
            {
                Unreadable = $"The graph file has format {format}. This editor reads only format {Format}, so it does not " +
                             "change the file.",
            };
        }

        var nodes = new List<GraphNodePlace>();
        var acts = new List<GraphAct>();
        var instances = new List<InstanceRecord>();
        var others = new List<GraphRawSection>();
        foreach (var (type, line, entries) in sections)
        {
            if (Is(type, "Graph"))
                continue;
            if (Is(type, "Node"))
            {
                var trigger = Value(entries, "trigger");
                var pos = Value(entries, "pos")?.Split(',');
                if (trigger is { Length: > 0 } && pos is { Length: 2 } &&
                    float.TryParse(pos[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(pos[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    nodes.Add(new GraphNodePlace(trigger, x, y));
                    continue;
                }
                // This editor places a prefab instance by the nodes of its member triggers. So it keeps the node of an
                // instance from another editor as it is.
                if (Value(entries, "instance") != null)
                {
                    others.Add(new GraphRawSection(type, entries.Select(e => (e.Key, e.Value)).ToList()));
                    continue;
                }
                found.Add(Problem(line, "A [Node] record has a 'trigger' line and a 'pos = x, y' line."));
                continue;
            }
            if (Is(type, "Act"))
            {
                if (Value(entries, "nickname") is not { Length: > 0 } nickname)
                {
                    found.Add(Problem(line, "An [Act] record has a 'nickname' line."));
                    continue;
                }
                acts.Add(new GraphAct(nickname, Value(entries, "name") ?? "", Value(entries, "text") ?? "",
                    entries.Where(e => Is(e.Key, "member") && e.Value.Length > 0).Select(e => e.Value).ToList()));
                continue;
            }
            if (Is(type, "Instance"))
            {
                if (ReadInstance(entries, line, found) is { } record)
                {
                    if (instances.Any(i => Is(i.Id, record.Id)))
                        found.Add(Problem(line, $"The id '{record.Id}' of an [Instance] record repeats. The editor keeps the first record."));
                    else
                        instances.Add(record);
                }
                continue;
            }
            others.Add(new GraphRawSection(type, entries.Select(e => (e.Key, e.Value)).ToList()));
        }
        return new GraphFile(nodes, acts, others) { Instances = instances };
    }

    /// <summary>Reads an <c>[Instance]</c> record (section 5.5 of the M6b design), or null with a problem.</summary>
    private static InstanceRecord? ReadInstance(List<(string Key, string Value, int Line)> entries, int line, List<string> found)
    {
        var id = Value(entries, "id");
        var prefab = Value(entries, "prefab");
        if (id is not { Length: > 0 } || prefab is not { Length: > 0 })
        {
            found.Add(Problem(line, "An [Instance] record has an 'id' line and a 'prefab' line."));
            return null;
        }
        var members = new List<InstanceMember>();
        var values = new List<InstanceValue>();
        var overrides = new List<InstanceValue>();
        var removed = new List<string>();
        var ports = new List<InstancePort>();
        foreach (var (key, value, at) in entries)
        {
            switch (key.ToLowerInvariant())
            {
                case "id" or "prefab" or "prefab_hash" or "lines_hash":
                    break;
                case "member" when Pair(value) is var (role, nickname) && nickname.Length > 0:
                    members.Add(new InstanceMember(role, nickname));
                    break;
                case "value":
                    AddRow(values, value);
                    break;
                case "override":
                    AddRow(overrides, value);
                    break;
                case "removed" when value.Length > 0:
                    removed.Add(value);
                    break;
                case "port" when Pair(value) is var (port, trigger) && trigger.Length > 0:
                    ports.Add(new InstancePort(port, trigger));
                    break;
                default:
                    found.Add(Problem(at, $"An [Instance] record has no '{key} = {value}' line. The editor drops the line."));
                    break;
            }
        }
        return new InstanceRecord(id, prefab)
        {
            PrefabHash = Value(entries, "prefab_hash"),
            LinesHash = Value(entries, "lines_hash"),
            Members = members,
            Values = values,
            Overrides = overrides,
            Removed = removed,
            Ports = ports,
        };
    }

    /// <summary>The two parts of a value 'a, b': the text before the first comma and the rest.</summary>
    private static (string First, string Second) Pair(string value)
    {
        var comma = value.IndexOf(',');
        return comma < 0 ? (value.Trim(), "") : (value[..comma].Trim(), value[(comma + 1)..].Trim());
    }

    /// <summary>Adds the row of a 'value' or an 'override' line to the value of its field. A line without a row gives a
    /// field without rows.</summary>
    private static void AddRow(List<InstanceValue> values, string text)
    {
        var (field, row) = Pair(text);
        if (field.Length == 0)
            return;
        var at = values.FindIndex(v => v.Field == field);
        if (at < 0)
            values.Add(new InstanceValue(field, text.Contains(',') ? [row] : []));
        else if (text.Contains(','))
            values[at] = values[at] with { Rows = [.. values[at].Rows, row] };
    }

    /// <summary>The text of the file for a script, or null for a file without a record. A save then deletes the
    /// file.</summary>
    public string? Write(string script)
    {
        if (!HasRecords)
            return null;
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        text.Append("[Graph]\n");
        text.Append("script = ").Append(script).Append('\n');
        text.Append("format = ").Append(Format.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var node in Nodes)
        {
            text.Append("\n[Node]\n");
            text.Append("trigger = ").Append(node.Trigger).Append('\n');
            text.Append("pos = ").Append(Number(node.X)).Append(", ").Append(Number(node.Y)).Append('\n');
        }
        foreach (var act in Acts)
        {
            text.Append("\n[Act]\n");
            text.Append("nickname = ").Append(act.Nickname).Append('\n');
            if (act.Name.Length > 0)
                text.Append("name = ").Append(OneLine(act.Name)).Append('\n');
            if (act.Text.Length > 0)
                text.Append("text = ").Append(OneLine(act.Text)).Append('\n');
            foreach (var member in act.Members)
                text.Append("member = ").Append(member).Append('\n');
        }
        foreach (var record in Instances)
        {
            text.Append("\n[Instance]\n");
            text.Append("id = ").Append(record.Id).Append('\n');
            text.Append("prefab = ").Append(record.Prefab).Append('\n');
            if (record.PrefabHash != null)
                text.Append("prefab_hash = ").Append(record.PrefabHash).Append('\n');
            foreach (var member in record.Members)
                text.Append("member = ").Append(member.Role).Append(", ").Append(member.Nickname).Append('\n');
            WriteValues(text, "value", record.Values);
            WriteValues(text, "override", record.Overrides);
            foreach (var role in record.Removed)
                text.Append("removed = ").Append(role).Append('\n');
            foreach (var port in record.Ports)
                text.Append("port = ").Append(port.Port).Append(", ").Append(port.Trigger).Append('\n');
            if (record.LinesHash != null)
                text.Append("lines_hash = ").Append(record.LinesHash).Append('\n');
        }
        foreach (var section in Others)
        {
            text.Append('\n').Append('[').Append(section.Type).Append("]\n");
            foreach (var (key, value) in section.Lines)
                text.Append(key).Append(" = ").Append(value).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The 'value' or 'override' lines of values: one line for each row, or one line without a row for a field
    /// without rows.</summary>
    private static void WriteValues(StringBuilder text, string key, IReadOnlyList<InstanceValue> values)
    {
        foreach (var value in values)
        {
            if (value.Rows.Count == 0)
                text.Append(key).Append(" = ").Append(value.Field).Append('\n');
            foreach (var row in value.Rows)
                text.Append(key).Append(" = ").Append(value.Field).Append(", ").Append(OneLine(row)).Append('\n');
        }
    }

    /// <summary>
    /// The notes of the records that name a trigger that the script does not have (section 6.8 of the M6b design). The
    /// Console shows them when the mission opens. Each such record goes at the next write of the file.
    /// </summary>
    /// <param name="hasTrigger">Tells whether the script has a trigger, without letter case.</param>
    public IReadOnlyList<string> Notes(string script, Func<string, bool> hasTrigger)
    {
        var notes = new List<string>();
        foreach (var node in Nodes.Where(n => !hasTrigger(n.Trigger)))
            notes.Add($"The graph file of {script} has a node of trigger '{node.Trigger}', which the script does not have. " +
                      "The editor removes the node when it writes the file again.");
        foreach (var act in Acts)
        {
            foreach (var member in act.Members.Where(m => !hasTrigger(m)))
                notes.Add($"Act '{(act.Name.Length > 0 ? act.Name : act.Nickname)}' in the graph file of {script} names " +
                          $"trigger '{member}', which the script " +
                          "does not have. The editor removes the name when it writes the file again.");
        }
        return notes;
    }

    /// <summary>The file without the nodes and the act members of the triggers that the script does not have.</summary>
    public GraphFile Pruned(Func<string, bool> hasTrigger) => this with
    {
        Nodes = Nodes.Where(n => hasTrigger(n.Trigger)).ToList(),
        Acts = Acts.Select(a => a with { Members = a.Members.Where(hasTrigger).ToList() }).ToList(),
    };

    public bool Equals(GraphFile? other) =>
        other is not null && Nodes.SequenceEqual(other.Nodes) && Acts.SequenceEqual(other.Acts) &&
        Instances.SequenceEqual(other.Instances) && Others.Count == other.Others.Count && Others.Zip(other.Others).All(p =>
            p.First.Type == p.Second.Type && p.First.Lines.SequenceEqual(p.Second.Lines));

    public override int GetHashCode() => HashCode.Combine(Nodes.Count, Acts.Count, Others.Count);

    private static string? Value(IEnumerable<(string Key, string Value, int Line)> lines, string key) =>
        lines.FirstOrDefault(l => Is(l.Key, key)) is var (found, value, _) && found != null ? value : null;

    private static string Number(float value) =>
        MathF.Round(value) == value ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A text on one line: a line end becomes a space.</summary>
    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Problem(int line, string message) =>
        $"Line {(line + 1).ToString(CultureInfo.InvariantCulture)} of the graph file: {message}";

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

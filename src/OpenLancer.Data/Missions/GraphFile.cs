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
/// its triggers in order.</summary>
public sealed record GraphAct(string Nickname, string Name, string Text, IReadOnlyList<string> Members)
{
    public bool Equals(GraphAct? other) =>
        other is not null && Nickname == other.Nickname && Name == other.Name && Text == other.Text &&
        Members.SequenceEqual(other.Members);

    public override int GetHashCode() => HashCode.Combine(Nickname, Name, Text, Members.Count);
}

/// <summary>A section of the graph file that this editor keeps as it is, for example an <c>[Instance]</c> record of
/// milestone M6b-3: its type and its lines.</summary>
public sealed record GraphRawSection(string Type, IReadOnlyList<(string Key, string Value)> Lines);

/// <summary>
/// The graph file of a mission script, <c>graphs\&lt;install path&gt;.ini</c> in the mod folder (section 6.8 of the M6b
/// design, decisions D404 and D679). It holds the nodes that the user moved or placed, the acts after the first act
/// edit, and the records of other kinds. The game, the merge and the packages ignore the file. The record never changes:
/// an edit makes a new one.
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

    /// <summary>True when the file has a record. A file without a record is not written (section 6.8 of the M6b
    /// design).</summary>
    public bool HasRecords => Nodes.Count + Acts.Count + Others.Count > 0;

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
    /// gives a problem and no records. Null text gives <see cref="Empty"/>.
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
            return Empty;
        }

        var nodes = new List<GraphNodePlace>();
        var acts = new List<GraphAct>();
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
                // A node of a prefab instance comes with milestone M6b-3, so this editor keeps it as it is.
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
                acts.Add(new GraphAct(nickname, Value(entries, "name") ?? nickname, Value(entries, "text") ?? "",
                    entries.Where(e => Is(e.Key, "member") && e.Value.Length > 0).Select(e => e.Value).ToList()));
                continue;
            }
            others.Add(new GraphRawSection(type, entries.Select(e => (e.Key, e.Value)).ToList()));
        }
        return new GraphFile(nodes, acts, others);
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
            text.Append("name = ").Append(OneLine(act.Name)).Append('\n');
            if (act.Text.Length > 0)
                text.Append("text = ").Append(OneLine(act.Text)).Append('\n');
            foreach (var member in act.Members)
                text.Append("member = ").Append(member).Append('\n');
        }
        foreach (var section in Others)
        {
            text.Append('\n').Append('[').Append(section.Type).Append("]\n");
            foreach (var (key, value) in section.Lines)
                text.Append(key).Append(" = ").Append(value).Append('\n');
        }
        return text.ToString();
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
                notes.Add($"Act '{act.Name}' in the graph file of {script} names trigger '{member}', which the script " +
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
        Others.Count == other.Others.Count && Others.Zip(other.Others).All(p =>
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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Run;

/// <summary>How a waiting condition line of the world panel reacts (section 4.3 of the M6c design).</summary>
public enum WorldLineKind
{
    /// <summary>The line waits for an event: the row has the button "Happens".</summary>
    Event,

    /// <summary>The game tests the line at each heartbeat: the row has the check box "Is true".</summary>
    Fact,

    /// <summary>A fact that also listens to events (<c>Cnd_NPCSystemEnter</c>): the row has both.</summary>
    EventAndFact,
}

/// <summary>Why a waiting line is likely to come next: the run itself sent its comm line or spawned its
/// object.</summary>
/// <param name="Spawned">True for a spawned object, false for a sent comm line.</param>
/// <param name="Step">The number of the step that sent or spawned it.</param>
public readonly record struct WorldCause(bool Spawned, int Step)
{
    /// <summary>The row note: "Sent at step {n}" or "Spawned at step {n}".</summary>
    public string Text => Spawned ? $"Spawned at step {StepText}" : $"Sent at step {StepText}";

    private string StepText => Step.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One distinct condition line that active triggers wait for (section 4.3 of the M6c design): one row of the
/// world panel.</summary>
/// <param name="Key">The key of the line (<see cref="WorldEvents.KeyOf"/>).</param>
/// <param name="Line">The first line with the key, in the order of the active list.</param>
/// <param name="Triggers">The active triggers that wait for the line, in the order of the active list.</param>
/// <param name="Count">The matches so far and the matches that a counting condition needs, or null.</param>
/// <param name="Cause">Why the line is likely to come next, or null.</param>
public sealed record WaitingLine(string Key, MissionLine Line, WorldLineKind Kind, IReadOnlyList<MissionTrigger> Triggers,
    (int Count, int Needed)? Count, WorldCause? Cause)
{
    /// <summary>The condition sentence of the M6a schema, for example "Starts when comm line {line} ends.", with the
    /// count of a counting condition.</summary>
    public string Sentence => Count is var (count, needed)
        ? $"{Line.Sentence()} ({count.ToString(CultureInfo.InvariantCulture)} of {needed.ToString(CultureInfo.InvariantCulture)})"
        : Line.Sentence();

    /// <summary>The event of the line.</summary>
    public WorldEvent Event => new(Key);
}

/// <summary>
/// The world events and facts of a dry run (section 4.3 of the M6c design, decision D755). Each distinct condition line
/// that waits for the world is one event or one fact. Lines compare by name and values without letter case, except the
/// words of a case-sensitive word set (TR16). The schema marks the event conditions with a <c>wake</c> list and the
/// fact conditions with <c>polls</c>.
/// </summary>
public static class WorldEvents
{
    private const char Separator = '\u001f';

    private static readonly ConditionalWeakTable<MissionLine, string> Keys = new();

    /// <summary>The key of a condition line: its condition name and its value texts by position. Two lines with one key
    /// are one event. A word of a case-sensitive word set keeps its letter case, and each other value compares without
    /// letter case (TR16).</summary>
    public static string KeyOf(MissionLine line) => Keys.GetValue(line, MakeKey);

    /// <summary>The event of a condition line.</summary>
    public static WorldEvent Of(MissionLine line) => new(KeyOf(line));

    private static string MakeKey(MissionLine line)
    {
        var texts = Enumerable.Range(0, line.Count).Select(i => Normal(line, i)).ToList();
        while (texts.Count > 0 && texts[^1].Length == 0)
            texts.RemoveAt(texts.Count - 1);
        return (line.Name?.Name ?? line.Key.Trim()).ToLowerInvariant() + Separator + string.Join(Separator, texts);
    }

    /// <summary>The value text of a key: the exact text of a word of a case-sensitive set, else the text in lower
    /// case.</summary>
    private static string Normal(MissionLine line, int index) =>
        Normal(line.Value(index) ?? "", line.Name?.ValueAt(index));

    private static string Normal(string text, MissionValueSpec? spec)
    {
        foreach (var id in new[] { spec?.Tokens, spec?.Also })
        {
            if (id != null && MissionSchema.Retail.Tokens.TryGetValue(id, out var set) && set.CaseSensitive &&
                set.Values.Contains(text, StringComparer.Ordinal))
                return text;
        }
        return text.ToLowerInvariant();
    }

    /// <summary>The kind of a condition line in the world panel, or null for a condition that the machine tests itself
    /// (<c>Cnd_True</c>, <c>Cnd_Timer</c> and <c>Cnd_WatchTrigger</c>) and for a line without a known condition.</summary>
    public static WorldLineKind? KindOf(MissionLine? condition) => KindOf(condition?.Name);

    /// <summary>The kind of a condition of the schema in the world panel, or null for a condition that the machine tests
    /// itself and for another name.</summary>
    public static WorldLineKind? KindOf(MissionName? name) => MachineProgram.KindOf(name) switch
    {
        ConditionKind.Event or ConditionKind.StartTest => WorldLineKind.Event,
        ConditionKind.Poll => WorldLineKind.Fact,
        ConditionKind.PollAndEvent => WorldLineKind.EventAndFact,
        _ => null,
    };

    /// <summary>
    /// The distinct lines that the active triggers of a machine wait for, one row for each key (section 4.3 of the M6c
    /// design). The rows that the run itself makes likely come first, latest step first. The other rows keep the order
    /// of the active list.
    /// </summary>
    /// <param name="world">The world of the run, for the rows that the run makes likely, or null.</param>
    public static IReadOnlyList<WaitingLine> Waiting(TriggerMachine machine, DryRunWorld? world = null)
    {
        var rows = new List<WaitingLine>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var trigger in machine.Active)
        {
            if (trigger.Condition is not { } condition || KindOf(condition) is not { } kind)
                continue;
            var key = KeyOf(condition);
            if (byKey.TryGetValue(key, out var at))
            {
                rows[at] = rows[at] with { Triggers = [.. rows[at].Triggers, trigger] };
                continue;
            }
            byKey[key] = rows.Count;
            rows.Add(new WaitingLine(key, condition, kind, [trigger], machine.CountOf(trigger), world?.CauseOf(condition)));
        }
        return
        [
            .. rows.Where(r => r.Cause != null).OrderByDescending(r => r.Cause!.Value.Step),
            .. rows.Where(r => r.Cause == null),
        ];
    }

    /// <summary>The distinct lines that the active triggers of a machine wait for as an event, in the order of the active
    /// list. An event of the dry run names one of them.</summary>
    internal static IReadOnlyList<MissionLine> EventLines(TriggerMachine machine)
    {
        var lines = new List<MissionLine>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in machine.Active)
        {
            if (trigger.Condition is { } condition && KindOf(condition) is WorldLineKind.Event or WorldLineKind.EventAndFact &&
                keys.Add(KeyOf(condition)))
                lines.Add(condition);
        }
        return lines;
    }

    /// <summary>The distinct lines of a script that the game tests at each heartbeat for the world, in file order. A fact
    /// of the dry run names one of them, also before its trigger waits.</summary>
    public static IReadOnlyList<MissionLine> FactLines(MissionIndex index) =>
        index.Triggers.Where(t => !t.IsDuplicate).Select(t => t.Condition)
            .OfType<MissionLine>()
            .Where(l => KindOf(l) is WorldLineKind.Fact or WorldLineKind.EventAndFact)
            .DistinctBy(KeyOf, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The lines that a line of a request matches (section 7.3 of the M6c design): the lines with the same condition
    /// name whose values are the values that the request gives. A value that the request does not give matches any
    /// value. When two or more lines match, the lines that the request gives in full come first.
    /// </summary>
    /// <param name="lines">The distinct lines to match, in their order.</param>
    public static IReadOnlyList<MissionLine> Match(LineSpec pattern, IEnumerable<MissionLine> lines)
    {
        var matches = lines.Where(l => Matches(pattern, l)).ToList();
        if (matches.Count < 2)
            return matches;
        var full = matches.Where(l => GivesAll(pattern, l)).ToList();
        return full.Count == 1 ? full : matches;
    }

    /// <summary>True when the line has the condition of a pattern and each value of the pattern.</summary>
    public static bool Matches(LineSpec pattern, MissionLine line)
    {
        if (line.Name is not { } name || !name.Name.Equals(pattern.Key.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var (valueName, value) in pattern.Values)
        {
            MissionValueSpec? spec = null;
            foreach (var candidate in name.Values)
            {
                if (candidate.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase))
                {
                    spec = candidate;
                    break;
                }
            }
            if (spec == null || !Same(value, line, spec))
                return false;
        }
        return true;
    }

    /// <summary>True when the pattern gives each named value that the line has.</summary>
    private static bool GivesAll(LineSpec pattern, MissionLine line) =>
        line.Name!.Values.Where(v => line.ValueOf(v) != null)
            .All(v => pattern.Values.Keys.Contains(v.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The pattern of a line: its key and each named value that it has. A row of the world panel makes its
    /// event and its fact with it.</summary>
    public static LineSpec PatternOf(MissionLine line) => new(line.Name?.Name ?? line.Key.Trim())
    {
        Values = (line.Name?.Values ?? []).Select(v => (v.Name, Text: line.ValueOf(v)))
            .Where(p => p.Text != null)
            .ToDictionary(p => p.Name, p => MissionValue.Of(p.Text!), StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>The text of a value of a pattern: a single value as a line writes it, the items of a list with ", "
    /// between them.</summary>
    internal static string TextOf(MissionValue value) =>
        value.Scalar ?? string.Join(", ", value.Items.Select(TextOf));

    /// <summary>True when a value of a pattern is the value of a line: the same numbers, or the same words by the rule
    /// of the key. A position or a value that repeats compares each part.</summary>
    private static bool Same(MissionValue value, MissionLine line, MissionValueSpec spec)
    {
        if (spec is { Width: 1, Rest: false } && value.Scalar is { } scalar)
            return line.Value(spec.Index) is { } text && SamePart(scalar, text, spec);
        if (line.ValueOf(spec) is not { } joined)
            return false;
        var a = TextOf(value).Split(',');
        var b = joined.Split(',');
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!SamePart(a[i].Trim(), b[i].Trim(), spec))
                return false;
        }
        return true;
    }

    /// <summary>True when two parts of a value are the same number, or the same word. A word of a case-sensitive word
    /// set compares with letter case (TR16), and each other word without.</summary>
    private static bool SamePart(string a, string b, MissionValueSpec spec)
    {
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return x == y;
        return Normal(a, spec) == Normal(b, spec);
    }
}

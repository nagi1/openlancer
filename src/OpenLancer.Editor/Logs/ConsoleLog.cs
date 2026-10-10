using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Logs;

internal enum ConsoleSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One message of the Console.</summary>
/// <param name="Source">What wrote the message, for example "Editor", a mod or an engine component.</param>
internal sealed record ConsoleEntry(ConsoleSeverity Severity, string Source, string Message, DateTime Time)
{
    /// <summary>The section that the message is about, or null. A click on the entry selects it.</summary>
    public SectionRef? Target { get; init; }

    /// <summary>The first line of the message. A list row shows only this line.</summary>
    public string FirstLine { get; } = Message.Split('\n', 2)[0].TrimEnd('\r');

    /// <summary>The entry as one line of text, for example "[Error 12:34:56] Mods: message".</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Severity} {Time:HH:mm:ss}] {Source}: {Message}");
}

/// <summary>
/// The messages of the Console: the editor messages, the mod diagnostics and the engine warnings and errors. It keeps
/// the newest <see cref="Capacity"/> entries and drops the oldest ones.
/// </summary>
internal sealed partial class ConsoleLog
{
    public const int Capacity = 10_000;

    /// <summary>The source of an engine line that has no component.</summary>
    public const string EngineSource = "Engine";

    private readonly List<ConsoleEntry> entries = [];
    private readonly int[] counts = new int[3];

    // Each engine line that the log added: its severity, source and message.
    private readonly HashSet<(ConsoleSeverity, string, string)> engineLines = [];

    /// <summary>The entries from the oldest to the newest.</summary>
    public IReadOnlyList<ConsoleEntry> Entries => entries;

    /// <summary>A number that changes each time an entry is added or removed.</summary>
    public int Version { get; private set; }

    /// <summary>The newest warning or error, or null when the log has none.</summary>
    public ConsoleEntry? Last { get; private set; }

    /// <summary>The warnings since the user last looked at the Console.</summary>
    public int UnseenWarnings { get; private set; }

    /// <summary>The errors since the user last looked at the Console.</summary>
    public int UnseenErrors { get; private set; }

    /// <summary>
    /// True while the game data loads again after a save. That load logs the lines of the first load again, so the
    /// log then adds only the engine lines that it did not add before.
    /// </summary>
    public bool SkipKnownEngineLines { get; set; }

    /// <summary>The number of entries with the severity.</summary>
    public int Count(ConsoleSeverity severity) => counts[(int)severity];

    /// <param name="target">The section that the message is about, or null.</param>
    public ConsoleEntry Add(ConsoleSeverity severity, string source, string message, SectionRef? target = null)
    {
        var entry = new ConsoleEntry(severity, source, message, DateTime.Now) { Target = target };
        if (entries.Count == Capacity)
        {
            counts[(int)entries[0].Severity]--;
            entries.RemoveAt(0);
        }

        entries.Add(entry);
        counts[(int)severity]++;
        Version++;
        if (severity == ConsoleSeverity.Warning)
        {
            UnseenWarnings++;
            Last = entry;
        }
        else if (severity == ConsoleSeverity.Error)
        {
            UnseenErrors++;
            Last = entry;
        }

        return entry;
    }

    /// <summary>Adds a diagnostic of the mods, with its section.</summary>
    /// <param name="prefix">Text before the message, for example the number of a save, or an empty text.</param>
    public ConsoleEntry Add(Diagnostic diagnostic, string prefix = "") =>
        Add(diagnostic.Severity == DiagnosticSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning,
            diagnostic.Source, prefix + diagnostic.Message, diagnostic.Target);

    /// <summary>
    /// Adds a line that FLLog wrote, without its "[Warning: 12:34:56] " prefix. The component of the line becomes the
    /// source. Debug and info lines are not added, because the engine writes many of them.
    /// </summary>
    public void AddEngineLine(string line, LogSeverity severity)
    {
        if (severity < LogSeverity.Warning)
        {
            return;
        }

        var (source, message) = ParseEngineLine(line);
        var consoleSeverity = severity == LogSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning;
        if (engineLines.Add((consoleSeverity, source, message)) || !SkipKnownEngineLines)
        {
            Add(consoleSeverity, source, message);
        }
    }

    /// <summary>
    /// Splits an FLLog line "[Warning: 12:34:56] Component: message" into the component and the message. A line in
    /// another form gives <see cref="EngineSource"/> and the full line.
    /// </summary>
    internal static (string Source, string Message) ParseEngineLine(string line)
    {
        var match = EngineLine().Match(line);
        return match.Success
            ? (match.Groups["source"].Value, match.Groups["message"].Value)
            : (EngineSource, line);
    }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        entries.Clear();
        Array.Clear(counts);
        Last = null;
        MarkSeen();
        Version++;
    }

    /// <summary>Sets the unseen counts to 0. Call it while the Console shows.</summary>
    public void MarkSeen() => (UnseenWarnings, UnseenErrors) = (0, 0);

    // FLLog.Write: "[{severity}: {time}] {component}: {message}". The time separator depends on the culture. A
    // message can have more lines, for example an exception with its stack trace.
    [GeneratedRegex(@"^\[\w+: [^\]]*\] (?<source>[^:\r\n]+): (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex EngineLine();
}

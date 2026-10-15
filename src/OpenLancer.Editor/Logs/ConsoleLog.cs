using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;

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

    /// <summary>The place in a cutscene that the message is about, or null. A click on the entry opens the cutscene
    /// and selects the element.</summary>
    public ThnLocation? Cutscene { get; init; }

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
    /// True while the game data loads again after a save, and during an action of <see cref="SkipKnownEngineLinesOf"/>.
    /// That load logs the lines of the first load again, so the log then adds only the engine lines that it did not add
    /// before.
    /// </summary>
    public bool SkipKnownEngineLines { get; set; }

    /// <summary>The number of entries with the severity.</summary>
    public int Count(ConsoleSeverity severity) => counts[(int)severity];

    /// <param name="target">The section that the message is about, or null.</param>
    public ConsoleEntry Add(ConsoleSeverity severity, string source, string message, SectionRef? target = null) =>
        Add(new ConsoleEntry(severity, source, message, DateTime.Now) { Target = target });

    /// <summary>Adds an entry. When the log is full, the oldest entry goes.</summary>
    private ConsoleEntry Add(ConsoleEntry entry)
    {
        var severity = entry.Severity;
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

    /// <summary>Adds a diagnostic of the mods, with its section or its place in a cutscene.</summary>
    /// <param name="prefix">Text before the message, for example the number of a save, or an empty text.</param>
    public ConsoleEntry Add(Diagnostic diagnostic, string prefix = "") =>
        Add(new ConsoleEntry(diagnostic.Severity == DiagnosticSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning,
            diagnostic.Source, prefix + diagnostic.Message, DateTime.Now)
        {
            Target = diagnostic.Target,
            Cutscene = diagnostic.Cutscene,
        });

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
    /// Runs an action on the UI thread, and adds only the engine lines of the action that the log did not add before.
    /// For example, a model that loads again logs the lines of its first load again. FLLog gives its lines to the UI
    /// thread later, in order. So the skip starts after the lines that FLLog has now, which the log adds as before, and it
    /// ends one step after the lines of the action, as at the end of a reload.
    /// </summary>
    /// <param name="ui">The UI thread that FLLog gives its lines to.</param>
    /// <param name="skipAfter">Gives <see cref="SkipKnownEngineLines"/> after the action, for example true while a
    /// reload runs.</param>
    public void SkipKnownEngineLinesOf(Action action, IUIThread ui, Func<bool> skipAfter)
    {
        FLLog.Flush();
        ui.QueueUIThread(() => SkipKnownEngineLines = true);
        action();
        FLLog.Flush();
        ui.QueueUIThread(() => ui.QueueUIThread(() => SkipKnownEngineLines = skipAfter()));
    }

    /// <summary>
    /// Adds a warning or an error line of the game process of Play (decision D57). The game loads the same data as the
    /// editor and writes many of the same lines. So the log adds a line only when it did not add the same engine line
    /// before: the same severity, component and message. The entry has the source of the caller, and its message starts
    /// with the component.
    /// </summary>
    /// <param name="line">A line of <see cref="ParseGameLine"/>.</param>
    /// <param name="source">The source of the entry, for example "Play".</param>
    public void AddGameLine((ConsoleSeverity Severity, string Source, string Message) line, string source)
    {
        if (engineLines.Add(line))
        {
            Add(line.Severity, source, $"{line.Source}: {line.Message}");
        }
    }

    /// <summary>
    /// The severity, the component and the message of a warning or an error line of FLLog, for example
    /// "[Warning: 12:34:56] Ini: message". Null for a line of another severity or in another form.
    /// </summary>
    internal static (ConsoleSeverity Severity, string Source, string Message)? ParseGameLine(string line)
    {
        var match = EngineLine().Match(line);
        ConsoleSeverity? severity = match.Groups["severity"].Value switch
        {
            nameof(LogSeverity.Warning) => ConsoleSeverity.Warning,
            nameof(LogSeverity.Error) => ConsoleSeverity.Error,
            _ => null,
        };
        return match.Success && severity is { } known
            ? (known, match.Groups["source"].Value, match.Groups["message"].Value)
            : null;
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
    [GeneratedRegex(@"^\[(?<severity>\w+): [^\]]*\] (?<source>[^:\r\n]+): (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex EngineLine();
}

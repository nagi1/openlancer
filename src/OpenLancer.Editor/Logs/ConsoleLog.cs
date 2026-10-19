using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>The section that the message is about, or null. A click on the entry selects it. The origin rules can
    /// give the section of an engine line when the line resolves.</summary>
    public SectionRef? Target { get; internal set; }

    /// <summary>The place in a cutscene that the message is about, or null. A click on the entry opens the cutscene
    /// and selects the element.</summary>
    public ThnLocation? Cutscene { get; init; }

    /// <summary>Who causes the message (decision D500 of the console filter design). An engine line of a load has the
    /// origin pending until it resolves.</summary>
    public MessageOrigin Origin { get; internal set; } = MessageOrigin.OpenLancer;

    /// <summary>The kind of an engine line, or null. The details pane shows its text.</summary>
    public EngineLineKind? Kind { get; init; }

    /// <summary>The first line of the message. A list row shows only this line.</summary>
    public string FirstLine { get; } = Message.Split('\n', 2)[0].TrimEnd('\r');

    /// <summary>The number of the entry in the order of arrival.</summary>
    internal long Sequence { get; set; }

    /// <summary>True after the entry left the log: <see cref="ConsoleLog.Clear"/>, or the capacity dropped it.</summary>
    internal bool Removed { get; set; }

    /// <summary>The entry as one line of text, for example "[Error 12:34:56] Mods: message".</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Severity} {Time:HH:mm:ss}] {Source}: {Message}");
}

/// <summary>The engine lines of one Play run (decision D514 of the console filter design): the count of each key, and
/// the lines that wait for the base run.</summary>
internal sealed class GameRunLines
{
    internal Dictionary<string, int> Seen { get; } = new(StringComparer.Ordinal);

    internal List<(EngineLine Line, int Occurrence, ConsoleEntry Entry)> Waiting { get; } = [];

    /// <summary>True after the run gave its notice of the slow game server (decision D507).</summary>
    internal bool Noticed { get; set; }
}

/// <summary>
/// The messages of the Console: the editor messages, the mod diagnostics and the engine warnings and errors. It keeps
/// the newest <see cref="Capacity"/> entries and drops the oldest ones.
/// </summary>
/// <remarks>
/// <para>Each entry has an origin (section 3 of the console filter design). The engine lines of a known load form a
/// batch: the data load of the open, a reload after a save, the load of a system in the Scene, and a Play run. A batch
/// starts and ends with a sentinel line through FLLog, so its lines are the lines between the two (see
/// <see cref="BeginBatch"/>). When a batch ends and the base run of the profile is known, <see cref="Origins"/> gives
/// each line its origin. Until then, the entries of the batch are pending.</para>
/// <para>Base-game entries and pending entries never change the unseen counts or <see cref="Last"/>, so the tab badges
/// and the status bar never show them. A pending entry that resolves to another origin counts as a new entry.</para>
/// </remarks>
internal sealed class ConsoleLog
{
    public const int Capacity = 10_000;

    /// <summary>The source of an engine line that has no component.</summary>
    public const string EngineSource = EngineLine.NoComponent;

    // The sentinel lines of a batch, with its id after the text.
    private const string BatchComponent = "OpenLancer";
    private const string BatchStart = "Console batch starts: ";
    private const string BatchEnd = "Console batch ends: ";

    // The groups of the counts: the entries that show, the base-game entries and the pending entries.
    private const int Shown = 0;
    private const int BaseGame = 1;
    private const int Pending = 2;

    private readonly List<ConsoleEntry> entries = [];
    private readonly int[,] counts = new int[3, 3];

    // Each engine line that the log added: its severity, source and message.
    private readonly HashSet<(ConsoleSeverity, string, string)> engineLines = [];

    // The batches whose start line came, by id, and the batches that ended and wait for the base run.
    private readonly Dictionary<string, Batch> openBatches = new(StringComparer.Ordinal);
    private readonly List<Batch> openOrder = [];
    private readonly List<Batch> endedBatches = [];
    private readonly List<GameRunLines> gameRuns = [];

    private EngineLineOrigins? origins;
    private long sequence;

    /// <summary>The entries from the oldest to the newest.</summary>
    public IReadOnlyList<ConsoleEntry> Entries => entries;

    /// <summary>A number that changes each time an entry is added, removed or resolved.</summary>
    public int Version { get; private set; }

    /// <summary>The newest warning, error or status entry (see <see cref="AddStatus"/>) that is not a base-game or a
    /// pending entry, or null when the log has none. The status bar shows it.</summary>
    public ConsoleEntry? Last { get; private set; }

    /// <summary>The warnings since the user last looked at the Console.</summary>
    public int UnseenWarnings { get; private set; }

    /// <summary>The errors since the user last looked at the Console.</summary>
    public int UnseenErrors { get; private set; }

    /// <summary>
    /// True while the game data loads again after a save, and during an action of <see cref="SkipKnownEngineLinesOf"/>.
    /// That load logs the lines of the first load again, so the log then adds only the engine lines that it did not add
    /// before. The batch of the load still counts each line for the comparison with the base run.
    /// </summary>
    public bool SkipKnownEngineLines { get; set; }

    /// <summary>
    /// The origin rules of the open profile, or null while no profile is open. A change drops the batches of the
    /// profile before: their pending entries show with the origin unknown.
    /// </summary>
    public EngineLineOrigins? Origins
    {
        get => origins;
        set
        {
            foreach (var entry in PendingEntries())
                Resolve(entry, new ResolvedLine(MessageOrigin.Unknown, null));
            openBatches.Clear();
            openOrder.Clear();
            endedBatches.Clear();
            gameRuns.Clear();
            origins = value;
        }
    }

    /// <summary>The number of entries with the severity that show while the base-game messages are hidden.</summary>
    public int Count(ConsoleSeverity severity) => counts[(int)severity, Shown];

    /// <summary>The number of entries with the severity that show.</summary>
    /// <param name="baseGame">True when the base-game messages show.</param>
    public int Count(ConsoleSeverity severity, bool baseGame) =>
        counts[(int)severity, Shown] + (baseGame ? counts[(int)severity, BaseGame] : 0);

    /// <summary>The number of base-game entries.</summary>
    public int BaseGameCount => Sum(BaseGame);

    /// <summary>The number of entries that wait for the base run.</summary>
    public int PendingCount => Sum(Pending);

    /// <param name="target">The section that the message is about, or null.</param>
    /// <param name="cutscene">The place in a cutscene that the message is about, or null.</param>
    /// <param name="origin">Who causes the message, or null for OpenLancer.</param>
    public ConsoleEntry Add(ConsoleSeverity severity, string source, string message, SectionRef? target = null,
        ThnLocation? cutscene = null, MessageOrigin? origin = null) =>
        Add(new ConsoleEntry(severity, source, message, DateTime.Now)
        {
            Target = target,
            Cutscene = cutscene,
            Origin = origin ?? MessageOrigin.OpenLancer,
        });

    /// <summary>
    /// Adds the result of an action of the user, for example a save. The status bar shows the entry also when it is an
    /// info entry, until a newer warning, error or status entry comes.
    /// </summary>
    public ConsoleEntry AddStatus(ConsoleSeverity severity, string source, string message)
    {
        var entry = Add(severity, source, message);
        Last = entry;
        return entry;
    }

    /// <summary>Adds an entry. When the log is full, the oldest entry goes.</summary>
    private ConsoleEntry Add(ConsoleEntry entry)
    {
        if (entries.Count == Capacity)
        {
            var oldest = entries[0];
            counts[(int)oldest.Severity, Group(oldest)]--;
            oldest.Removed = true;
            entries.RemoveAt(0);
        }

        entry.Sequence = ++sequence;
        entries.Add(entry);
        counts[(int)entry.Severity, Group(entry)]++;
        Version++;
        if (Group(entry) == Shown)
        {
            Note(entry);
        }

        return entry;
    }

    /// <summary>Counts a new entry that shows: a warning or an error adds to the unseen counts and becomes
    /// <see cref="Last"/> when it is newer.</summary>
    private void Note(ConsoleEntry entry)
    {
        if (entry.Severity == ConsoleSeverity.Info)
        {
            return;
        }

        if (entry.Severity == ConsoleSeverity.Warning)
        {
            UnseenWarnings++;
        }
        else
        {
            UnseenErrors++;
        }

        if (Last == null || entry.Sequence > Last.Sequence)
        {
            Last = entry;
        }
    }

    /// <summary>Adds a diagnostic of the mods, with its section, its place in a cutscene and its origin.</summary>
    /// <param name="prefix">Text before the message, for example the number of a save, or an empty text.</param>
    public ConsoleEntry Add(Diagnostic diagnostic, string prefix = "") =>
        Add(new ConsoleEntry(diagnostic.Severity == DiagnosticSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning,
            diagnostic.Source, prefix + diagnostic.Message, DateTime.Now)
        {
            Target = diagnostic.Target,
            Cutscene = diagnostic.Cutscene,
            Origin = diagnostic.Origin ?? MessageOrigin.OpenLancer,
        });

    /// <summary>
    /// Starts a batch of engine lines: the lines that FLLog gives after this call, up to the end line of
    /// <see cref="EndBatch"/>. Call it on the thread that starts the load, before the load. Each batch must end.
    /// </summary>
    /// <returns>The id of the batch for <see cref="EndBatch"/>.</returns>
    public static string BeginBatch()
    {
        var id = Guid.NewGuid().ToString("N");
        FLLog.Info(BatchComponent, BatchStart + id);
        return id;
    }

    /// <summary>Ends a batch after the last line of its load. Call it on the thread of the load, after the load, also when
    /// the load fails.</summary>
    public static void EndBatch(string id) => FLLog.Info(BatchComponent, BatchEnd + id);

    /// <summary>The start line of a batch as FLLog gives it, without the time. The tests give it to
    /// <see cref="AddEngineLine"/>.</summary>
    internal static string BatchStartLine(string id) => $"[Info: 00:00:00] {BatchComponent}: {BatchStart}{id}";

    /// <summary>The end line of a batch as FLLog gives it, without the time.</summary>
    internal static string BatchEndLine(string id) => $"[Info: 00:00:00] {BatchComponent}: {BatchEnd}{id}";

    /// <summary>
    /// Adds a line that FLLog wrote, without its "[Warning: 12:34:56] " prefix. The component of the line becomes the
    /// source. Debug and info lines are not added, because the engine writes many of them. The rules table drops the
    /// lines about the state of the engine process (decision D506).
    /// </summary>
    public void AddEngineLine(string line, LogSeverity severity)
    {
        if (BatchLine(line))
        {
            return;
        }

        if (severity < LogSeverity.Warning)
        {
            return;
        }

        var parsed = EngineLine.Parse(line, severity);
        var kind = EngineLineRules.Match(parsed);
        if (kind?.Disposition == EngineLineDisposition.Drop)
        {
            return;
        }

        var consoleSeverity = severity == LogSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning;
        var batch = openOrder.Count > 0 ? openOrder[^1] : null;
        ConsoleEntry? entry = null;
        if (engineLines.Add((consoleSeverity, parsed.Source, parsed.Message)) || !SkipKnownEngineLines)
        {
            var origin = kind?.OpenLancerOrigin == true || origins == null ? MessageOrigin.OpenLancer
                : batch == null ? MessageOrigin.Unknown
                : origins.NeedsBaseRun ? MessageOrigin.Pending
                : MessageOrigin.BaseGame;
            entry = Add(new ConsoleEntry(consoleSeverity, parsed.Source, parsed.Message, DateTime.Now)
            {
                Kind = kind,
                Origin = origin,
            });
        }

        batch?.Add(parsed, entry is { Origin.Kind: OriginKind.Pending } ? entry : null);
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

    /// <summary>Starts the lines of a Play run. Its lines resolve when they arrive (section 3.4 of the console filter
    /// design).</summary>
    public GameRunLines BeginGameRun()
    {
        var run = new GameRunLines();
        gameRuns.Add(run);
        return run;
    }

    /// <summary>
    /// Adds a warning or an error line of the game process of Play (decision D57). The game loads the same data as the
    /// editor and writes many of the same lines. So the log adds a line only when it did not add the same engine line
    /// before: the same severity, component and message. The entry has the source of the caller, and its message starts
    /// with the component. The rules table drops the process lines, and the lines of a slow game server give one info
    /// entry for each run (decisions D506 and D507).
    /// </summary>
    /// <param name="line">A line of <see cref="ParseGameLine"/>.</param>
    /// <param name="source">The source of the entry, for example "Play".</param>
    public void AddGameLine(GameRunLines run, EngineLine line, string source)
    {
        var kind = EngineLineRules.Match(line);
        if (kind?.Disposition == EngineLineDisposition.Drop)
        {
            return;
        }

        if (kind?.Disposition == EngineLineDisposition.Notice)
        {
            if (!run.Noticed && EngineLineRules.SlowUpdate(line) is { } ms)
            {
                run.Noticed = true;
                Add(ConsoleSeverity.Info, source, SlowServerNotice(ms));
            }

            return;
        }

        var occurrence = run.Seen[line.Key] = run.Seen.GetValueOrDefault(line.Key) + 1;
        var severity = line.Severity == LogSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning;
        if (!engineLines.Add((severity, line.Source, line.Message)))
        {
            return;
        }

        var resolved = kind?.OpenLancerOrigin == true || origins == null
            ? new ResolvedLine(MessageOrigin.OpenLancer, null)
            : origins.CanResolve ? origins.ResolveRunning(line, occurrence)
            : new ResolvedLine(MessageOrigin.Pending, null);
        var entry = Add(new ConsoleEntry(severity, source, $"{line.Source}: {line.Message}", DateTime.Now)
        {
            Kind = kind,
            Origin = resolved.Origin,
            Target = resolved.Target,
        });
        if (entry.Origin.Kind == OriginKind.Pending)
        {
            run.Waiting.Add((line, occurrence, entry));
        }
    }

    /// <summary>The info entry of a Play run with a slow game server (decision D507).</summary>
    internal static string SlowServerNotice(string ms) =>
        $"The game server was slow. An update took {ms} ms. The Play log has each slow update.";

    /// <summary>A warning or an error line of FLLog with a component, for example "[Warning: 12:34:56] Ini: message".
    /// Null for a line of another severity or in another form.</summary>
    internal static EngineLine? ParseGameLine(string line) =>
        EngineLine.Parse(line) is { Severity: LogSeverity.Warning or LogSeverity.Error } parsed ? parsed : null;

    /// <summary>Gives the result of the base run of the open profile to the origin rules, and resolves the entries that
    /// wait.</summary>
    public void BaseRunEnded(IReadOnlyDictionary<string, int> counts)
    {
        origins?.BaseRunEnded(counts);
        ResolveWaiting();
    }

    /// <summary>Takes the failure of the base run of the open profile. The entries that wait show with the origin
    /// unknown (decision D517).</summary>
    public void BaseRunFailed(string reason)
    {
        origins?.BaseRunFailed(reason);
        ResolveWaiting();
    }

    /// <summary>Removes all entries. The batches that wait for the base run go too. A batch that did not end keeps its
    /// lines for the comparison, but not their entries.</summary>
    public void Clear()
    {
        foreach (var entry in entries)
        {
            entry.Removed = true;
        }

        entries.Clear();
        Array.Clear(counts);
        endedBatches.Clear();
        foreach (var run in gameRuns)
        {
            run.Waiting.Clear();
        }

        Last = null;
        MarkSeen();
        Version++;
    }

    /// <summary>Sets the unseen counts to 0. Call it while the Console shows.</summary>
    public void MarkSeen() => (UnseenWarnings, UnseenErrors) = (0, 0);

    /// <summary>True for a start or an end line of a batch. A start opens the batch. An end closes it, and its lines
    /// resolve when the base run is known.</summary>
    private bool BatchLine(string line)
    {
        if (!line.Contains(BatchComponent + ": Console batch ", StringComparison.Ordinal))
        {
            return false;
        }

        if (After(line, BatchStart) is { } started)
        {
            var batch = new Batch();
            openBatches[started] = batch;
            openOrder.Add(batch);
        }
        else if (After(line, BatchEnd) is { } ended && openBatches.Remove(ended, out var batch))
        {
            openOrder.Remove(batch);
            endedBatches.Add(batch);
            ResolveWaiting();
        }

        return true;
    }

    private static string? After(string line, string text) =>
        line.IndexOf(text, StringComparison.Ordinal) is >= 0 and var at ? line[(at + text.Length)..].Trim() : null;

    /// <summary>Resolves the batches that ended and the lines of the Play runs, when the origin rules can.</summary>
    private void ResolveWaiting()
    {
        if (origins is not { CanResolve: true } rules)
        {
            return;
        }

        foreach (var batch in endedBatches)
        {
            var results = rules.Resolve(batch.Lines);
            for (var i = 0; i < results.Count; i++)
            {
                if (batch.Entries[i] is { Removed: false } entry)
                {
                    Resolve(entry, results[i]);
                }
            }
        }

        endedBatches.Clear();
        foreach (var run in gameRuns)
        {
            foreach (var (line, occurrence, entry) in run.Waiting)
            {
                if (!entry.Removed)
                {
                    Resolve(entry, rules.ResolveRunning(line, occurrence));
                }
            }

            run.Waiting.Clear();
        }
    }

    /// <summary>Gives a pending entry its origin. An entry that then shows counts as a new entry.</summary>
    private void Resolve(ConsoleEntry entry, ResolvedLine resolved)
    {
        if (entry.Origin.Kind != OriginKind.Pending || entry.Removed)
        {
            return;
        }

        counts[(int)entry.Severity, Pending]--;
        entry.Origin = resolved.Origin;
        entry.Target ??= resolved.Target;
        counts[(int)entry.Severity, Group(entry)]++;
        Version++;
        if (Group(entry) == Shown)
        {
            Note(entry);
        }
    }

    /// <summary>The entries that wait for the base run.</summary>
    private IEnumerable<ConsoleEntry> PendingEntries()
    {
        foreach (var entry in entries)
        {
            if (entry.Origin.Kind == OriginKind.Pending)
            {
                yield return entry;
            }
        }
    }

    private static int Group(ConsoleEntry entry) => entry.Origin.Kind switch
    {
        OriginKind.BaseGame => BaseGame,
        OriginKind.Pending => Pending,
        _ => Shown,
    };

    private int Sum(int group) => counts[0, group] + counts[1, group] + counts[2, group];

    /// <summary>The engine lines of one load, with the entry of each line that waits, or null for a line that the log
    /// did not add.</summary>
    private sealed class Batch
    {
        public List<EngineLine> Lines { get; } = [];

        public List<ConsoleEntry?> Entries { get; } = [];

        public void Add(EngineLine line, ConsoleEntry? entry)
        {
            Lines.Add(line);
            Entries.Add(entry);
        }
    }
}

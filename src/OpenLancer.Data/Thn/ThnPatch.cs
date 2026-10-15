using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace OpenLancer.Data.Thn;

/// <summary>
/// A THN patch: the changes of a mod to a cutscene script, as Lua text in <c>patches/&lt;install path&gt;.thn</c>
/// (MOD_FORMAT.md, "Cutscenes"). The engine VM evaluates the text, and <see cref="Apply"/> applies it to the script as
/// the lower mods leave it.
/// </summary>
/// <remarks>
/// <para>An entry changes, deletes or adds one element. It finds its element by key (see <see cref="ThnKeys"/>), not
/// by index, because an index changes when a lower mod deletes or adds an element.</para>
/// <para>The merge rules:</para>
/// <list type="number">
/// <item>All keys resolve against the script before the patch. Then the removes, the sets and the deletes run. Then
/// the new elements go at the end, in patch order. So a delete does not move a later match, and a new event runs after
/// the events of the same time (ThnScript.cs:150-152).</item>
/// <item>A key without an element, or whose number of elements differs from <c>ol_count</c>, is an error. Any error
/// stops the whole patch, and the script stays as it was.</item>
/// <item>A set into a value that is not a record is an error. A remove of a missing key is a warning.</item>
/// <item>Two mods that change the same path, or that both set the duration: the later mod wins, with a warning (see
/// <see cref="ThnLedger"/>).</item>
/// <item>A rename sets <c>entity_name</c>, so the next patch in load order uses the new name.</item>
/// <item>An unknown <c>ol_</c> key is an error.</item>
/// <item>An entry with <c>ol_nth</c> into a key whose elements a lower mod deleted or added gets a warning, because
/// the place can name another element (see <see cref="ThnLedger"/>).</item>
/// </list>
/// <para>In one entry, the removes run before the sets. So an entry can move a key to the end of its record.</para>
/// </remarks>
internal sealed class ThnPatch
{
    public const string VersionGlobal = "ol_thn_patch";
    public const int Version = 1;

    internal const string KeyPrefix = "ol_";
    internal const string MatchKey = "ol_match";
    internal const string NthKey = "ol_nth";
    internal const string CountKey = "ol_count";
    internal const string SetKey = "ol_set";
    internal const string RemoveKey = "ol_remove";
    internal const string DeleteKey = "ol_delete";
    internal const string NewKey = "ol_new";

    private static readonly string[] EntryKeys = [MatchKey, NthKey, CountKey, SetKey, RemoveKey, DeleteKey, NewKey];

    /// <exception cref="ArgumentException">A line of the header is not empty and does not start with <c>--</c>.</exception>
    public ThnPatch(string header, float? duration, ImmutableArray<ThnPatchEntry> entities, ImmutableArray<ThnPatchEntry> events)
    {
        if (header.Length > 0 && !header.Split('\n').All(IsCommentLine))
            throw new ArgumentException("Each line of the header must be empty or a Lua comment.", nameof(header));
        Header = header;
        Duration = duration is { } value ? new ThnNumber(value).Value : null;
        Entities = entities;
        Events = events;
    }

    /// <summary>The comment block at the top of the patch, with <c>\n</c> line ends and no line end at its end, or
    /// "". Each line is empty or starts with <c>--</c>.</summary>
    public string Header { get; }

    /// <summary>The new length of the script, or null.</summary>
    public float? Duration { get; }

    public ImmutableArray<ThnPatchEntry> Entities { get; }

    public ImmutableArray<ThnPatchEntry> Events { get; }

    /// <summary>True when the patch changes nothing.</summary>
    public bool IsEmpty => Duration == null && Entities.IsEmpty && Events.IsEmpty;

    /// <summary>
    /// Makes the patch that changes <paramref name="lower"/> into <paramref name="working"/>. An element of the working
    /// script finds its lower element by its <see cref="ThnElement.Origin"/>.
    /// </summary>
    /// <remarks>
    /// <para>The working script must keep the order of the lower elements and have its new elements at the end,
    /// because the merge keeps that order and adds new elements at the end.</para>
    /// <para>A pinned path gets an entry also when its working value equals the lower value: a set of the working
    /// value, or a remove when the working element has no value there. So the mod keeps its value when a lower mod
    /// changes it. A pinned set takes the place of the entries for the paths in its value. A pin on a deleted or new
    /// element changes nothing, because the patch always deletes and adds such elements.</para>
    /// </remarks>
    /// <param name="header">The comment block of the patch (see <see cref="Header"/>).</param>
    /// <param name="pins">The pinned paths (see <see cref="ThnPin"/>), or null for none.</param>
    /// <exception cref="ArgumentException">A patch cannot give the working script: its other globals differ, its
    /// elements are in another order, a changed or deleted lower element has no key, or an element changes in a way
    /// that no path can name (see <see cref="ThnElementPaths.Diff"/>).</exception>
    public static ThnPatch Make(ThnScriptModel lower, ThnScriptModel working,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols, string header = "", IReadOnlySet<ThnPin>? pins = null)
    {
        if (!lower.OtherGlobals.Equals(working.OtherGlobals))
            throw new ArgumentException("The globals other than duration, entities and events differ. A patch cannot change them.");
        pins ??= ImmutableHashSet<ThnPin>.Empty;
        var duration = lower.Duration.Equals(working.Duration) && !pins.Contains(ThnPin.Duration) ? (float?)null : working.Duration;
        return new ThnPatch(header, duration,
            MakeEntries(ThnElementKind.Entity, lower.Entities, working.Entities, symbols, pins),
            MakeEntries(ThnElementKind.Event, lower.Events, working.Events, symbols, pins));
    }

    /// <summary>The patch as Lua text: the header, then the canonical text of <see cref="ToGlobals"/>.</summary>
    public string Write() => (Header.Length > 0 ? Header + "\n" : "") + ThnWriter.Write(ToGlobals());

    /// <summary>The globals of the patch: <c>ol_thn_patch</c>, then <c>duration</c>, <c>entities</c> and
    /// <c>events</c> when the patch has them.</summary>
    public ThnTable ToGlobals()
    {
        var fields = ImmutableArray.CreateBuilder<ThnField>();
        fields.Add(new ThnField(VersionGlobal, new ThnNumber(Version)));
        if (Duration is { } duration)
            fields.Add(new ThnField(ThnScriptModel.DurationGlobal, new ThnNumber(duration)));
        if (!Entities.IsEmpty)
            fields.Add(new ThnField(ThnScriptModel.EntitiesGlobal, new ThnTable([.. Entities.Select(e => (ThnValue)e.ToTable())], [])));
        if (!Events.IsEmpty)
            fields.Add(new ThnField(ThnScriptModel.EventsGlobal, new ThnTable([.. Events.Select(e => (ThnValue)e.ToTable())], [])));
        return new ThnTable([], fields.ToImmutable());
    }

    /// <summary>Reads a patch file: ASCII Lua text that starts with its comment block and then
    /// <c>ol_thn_patch = 1</c>.</summary>
    /// <param name="name">The name of the file, for messages.</param>
    /// <exception cref="ThnPatchException">The file is not a valid patch. The exception lists each problem.</exception>
    public static ThnPatch Read(ReadOnlySpan<byte> text, string name, IThnEvaluator evaluator)
    {
        foreach (var b in text)
        {
            if (!IsTextByte(b))
                throw new ThnPatchException(["A THN patch must be ASCII text."]);
        }
        ThnTable globals;
        try
        {
            globals = evaluator.Evaluate(text, name);
        }
        catch (ThnEvaluationException e)
        {
            throw new ThnPatchException([e.Message]);
        }
        return Read(globals, HeaderOf(Encoding.ASCII.GetString(text)));
    }

    /// <summary>Reads the evaluated globals of a patch.</summary>
    /// <param name="header">The comment block of the patch file (see <see cref="Header"/>).</param>
    /// <exception cref="ThnPatchException">The globals are not a valid patch. The exception lists each problem.</exception>
    public static ThnPatch Read(ThnTable globals, string header)
    {
        var problems = new List<string>();
        if (globals.Record.Length == 0 || globals.Record[0].Key != VersionGlobal)
            problems.Add($"The first statement of a THN patch must be '{VersionGlobal} = {Version}'.");
        float? duration = null;
        var entities = ImmutableArray<ThnPatchEntry>.Empty;
        var events = ImmutableArray<ThnPatchEntry>.Empty;
        foreach (var (key, value) in globals.Record)
        {
            switch (key)
            {
                case VersionGlobal:
                    if (!value.Equals(new ThnNumber(Version)))
                        problems.Add($"This OpenLancer reads THN patch format {Version}. The patch has {VersionGlobal} = {ThnWriter.Value(value)}.");
                    break;
                case ThnScriptModel.DurationGlobal:
                    if (value is ThnNumber number)
                        duration = number.Value;
                    else
                        problems.Add($"'{key}' must be a number.");
                    break;
                case ThnScriptModel.EntitiesGlobal:
                    entities = ReadEntries(ThnElementKind.Entity, key, value, problems);
                    break;
                case ThnScriptModel.EventsGlobal:
                    events = ReadEntries(ThnElementKind.Event, key, value, problems);
                    break;
                default:
                    problems.Add(key.StartsWith(KeyPrefix, StringComparison.Ordinal)
                        ? $"Unknown key '{key}'."
                        : $"The global '{key}' is not part of a THN patch. A patch has {VersionGlobal}, duration, entities and events.");
                    break;
            }
        }
        if (problems.Count > 0)
            throw new ThnPatchException(problems);
        return new ThnPatch(header, duration, entities, events);
    }

    /// <summary>
    /// Applies the patch to a script, with the merge rules of <see cref="ThnPatch"/>. A kept element keeps its id and
    /// its origin. A new element gets a new id and the origin -1.
    /// </summary>
    /// <param name="symbols">The names of the THN environment, for the event keys.</param>
    /// <param name="ids">Gives a new id for each new element.</param>
    /// <returns>The result. Its model is null when an error stops the patch.</returns>
    public ThnPatchResult Apply(ThnScriptModel model, IReadOnlyDictionary<string, ThnSymbolInfo> symbols,
        Func<ThnElementId> ids)
    {
        var run = new ApplyRun(symbols, Duration != null);
        // Rule 1: all keys resolve before any change.
        var entityPositions = run.Resolve(ThnElementKind.Entity, model.Entities, Entities);
        var eventPositions = run.Resolve(ThnElementKind.Event, model.Events, Events);
        if (run.Errors.Count > 0)
            return run.Result(null);
        var entities = run.Change(ThnElementKind.Entity, model.Entities, Entities, entityPositions, ids);
        var events = run.Change(ThnElementKind.Event, model.Events, Events, eventPositions, ids);
        if (run.Errors.Count > 0)
            return run.Result(null);
        return run.Result(model with { Duration = Duration ?? model.Duration, Entities = entities, Events = events });
    }

    /// <summary>The comment block at the top of a text: the lines up to the first line that is not empty and is not a
    /// Lua comment, without the empty lines at its end. Lua 3.2 has only line comments.</summary>
    internal static string HeaderOf(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var end = 0;
        for (var i = 0; i < lines.Length && IsCommentLine(lines[i]); i++)
        {
            if (lines[i].Trim(' ', '\t').Length > 0)
                end = i + 1;
        }
        return string.Join('\n', lines.Take(end));
    }

    /// <summary>
    /// True when a text has a Lua comment after its comment block (see <see cref="HeaderOf"/>). <see cref="Write"/>
    /// keeps only the comment block, so a save drops such a comment (decision D63).
    /// </summary>
    /// <remarks>The text <c>--</c> in a string (<c>"..."</c>, <c>'...'</c> or <c>[[...]]</c>) is not a comment
    /// (llex.c:230-262, :280-285).</remarks>
    internal static bool HasInnerComments(string text)
    {
        // The comment block is the first lines of the text.
        var start = 0;
        var headerLines = HeaderOf(text) is { Length: > 0 } header ? header.Split('\n').Length : 0;
        for (var line = 0; line < headerLines && start < text.Length; line++)
        {
            var end = text.IndexOf('\n', start);
            start = end < 0 ? text.Length : end + 1;
        }
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"' or '\'':
                    var quote = text[i];
                    for (i++; i < text.Length && text[i] != quote && text[i] != '\n'; i++)
                    {
                        if (text[i] == '\\')
                            i++;
                    }
                    break;
                case '[' when i + 1 < text.Length && text[i + 1] == '[':
                    // Long strings nest in Lua 3.2.
                    var depth = 1;
                    for (i += 2; i < text.Length && depth > 0; i++)
                    {
                        if (text[i] == '[' && i + 1 < text.Length && text[i + 1] == '[')
                            (depth, i) = (depth + 1, i + 1);
                        else if (text[i] == ']' && i + 1 < text.Length && text[i + 1] == ']')
                            (depth, i) = (depth - 1, i + 1);
                    }
                    i--;
                    break;
                case '-' when i + 1 < text.Length && text[i + 1] == '-':
                    return true;
            }
        }
        return false;
    }

    /// <summary>True for a line of a comment block: an empty line, or a Lua comment.</summary>
    private static bool IsCommentLine(string line)
    {
        var text = line.Trim(' ', '\t');
        return text.Length == 0 || text.StartsWith("--", StringComparison.Ordinal);
    }

    /// <summary>True for a byte of ASCII text: a printable character, a tab or a line end.</summary>
    private static bool IsTextByte(byte b) => b is (>= 0x20 and < 0x7F) or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private static ImmutableArray<ThnPatchEntry> MakeEntries(ThnElementKind kind, ImmutableList<ThnElement> lower,
        ImmutableList<ThnElement> working, IReadOnlyDictionary<string, ThnSymbolInfo> symbols, IReadOnlySet<ThnPin> pins)
    {
        var plural = Plural(kind);
        var kept = new ThnElement?[lower.Count];
        var added = new List<ThnPatchEntry>();
        var last = -1;
        foreach (var element in working)
        {
            if (element.Origin < 0)
            {
                added.Add(ThnPatchEntry.Addition(element.Table));
                continue;
            }
            if (added.Count > 0 || element.Origin <= last || element.Origin >= lower.Count)
                throw new ArgumentException($"The working {plural} are not in the order of the lower {plural}. A patch keeps " +
                                            $"the order of the lower {plural} and adds new {plural} at the end.");
            kept[element.Origin] = element;
            last = element.Origin;
        }

        var pinned = pins.Where(p => p.Element != null).ToLookup(p => p.Element!.Value, p => p.Path);
        var keys = ThnKeys.Of(kind, lower.Select(e => e.Table), symbols);
        var entries = ImmutableArray.CreateBuilder<ThnPatchEntry>();
        for (var i = 0; i < lower.Count; i++)
        {
            var sets = new List<(ThnPath Path, ThnValue Value)>();
            var removes = new List<ThnPath>();
            if (kept[i] is { } element)
            {
                ThnElementPaths.Diff(kind, lower[i].Table, element.Table, sets, removes);
                AddPins(kind, element.Table, pinned[element.Id], sets, removes);
                if (sets.Count == 0 && removes.Count == 0)
                    continue;
            }
            if (keys[i] is not { } key)
                throw new ArgumentException($"The lower {Singular(kind)} {i + 1} has no key, so a patch cannot change it.");
            var match = ThnKeys.Match(kind, lower[i].Table)!;
            var (nth, count) = key.Count > 1 ? (key.Nth, key.Count) : ((int?)null, (int?)null);
            entries.Add(kept[i] == null
                ? ThnPatchEntry.Deletion(match, nth, count)
                : ThnPatchEntry.Change(match, nth, count, [.. sets], [.. removes]));
        }
        entries.AddRange(added);
        return entries.ToImmutable();
    }

    /// <summary>
    /// Adds an entry for each pinned path of a kept element that the diff does not name: a set of the working value,
    /// or a remove when the working element has no value there. The paths go in ordinal order, so equal states give
    /// equal text.
    /// </summary>
    /// <remarks>
    /// The patch stays exact. The diff names each path whose lower value differs from the working value, or a path
    /// that holds it. So a pinned value that the diff does not name is in the lower element too, in the same place,
    /// and a set of it changes nothing. A pinned remove that the diff does not name removes a value that the lower
    /// element does not have.
    /// </remarks>
    private static void AddPins(ThnElementKind kind, ThnTable working, IEnumerable<ThnPath> pinned,
        List<(ThnPath Path, ThnValue Value)> sets, List<ThnPath> removes)
    {
        foreach (var path in pinned.OrderBy(p => p.Text, StringComparer.Ordinal))
        {
            if (sets.Any(s => path.IsWithin(s.Path)) || removes.Any(path.IsWithin))
                continue;
            if (ThnElementPaths.Get(kind, working, path) is { } value)
            {
                // The set gives the whole value, so the entries in it are not needed.
                sets.RemoveAll(s => s.Path.IsWithin(path));
                removes.RemoveAll(r => r.IsWithin(path));
                sets.Add((path, value));
            }
            else
                removes.Add(path);
        }
    }

    private static ImmutableArray<ThnPatchEntry> ReadEntries(ThnElementKind kind, string global, ThnValue value,
        List<string> problems)
    {
        if (value is not ThnTable { Record.IsEmpty: true } list)
        {
            problems.Add($"'{global}' must be a list of entries.");
            return [];
        }
        var entries = ImmutableArray.CreateBuilder<ThnPatchEntry>(list.List.Length);
        for (var i = 0; i < list.List.Length; i++)
        {
            if (ReadEntry(kind, EntryLabel(kind, i), list.List[i], problems) is { } entry)
                entries.Add(entry);
        }
        return entries.ToImmutable();
    }

    /// <summary>Reads one entry, or adds its problems and returns null.</summary>
    private static ThnPatchEntry? ReadEntry(ThnElementKind kind, string label, ThnValue value, List<string> problems)
    {
        if (value is not ThnTable { List.IsEmpty: true } entry)
        {
            problems.Add($"{label} must be a record, for example {{ {MatchKey} = ..., {SetKey} = {{ ... }} }}.");
            return null;
        }
        var count = problems.Count;
        foreach (var (key, _) in entry.Record)
        {
            if (!EntryKeys.Contains(key))
                problems.Add(key.StartsWith(KeyPrefix, StringComparison.Ordinal)
                    ? $"{label}: unknown key '{key}'."
                    : $"{label}: '{key}' is not a key of an entry. An entry has {string.Join(", ", EntryKeys)}.");
        }

        if (entry[NewKey] is { } added)
        {
            if (entry.Record.Length > 1)
                problems.Add($"{label}: {NewKey} cannot have other keys.");
            if (added is not ThnTable table || !IsElement(kind, table))
            {
                problems.Add(kind == ThnElementKind.Entity
                    ? $"{label}: {NewKey} must be the record of the new entity."
                    : $"{label}: {NewKey} must be the list {{ time, TYPE, {{ targets }}, {{ props }} }} of the new event.");
                return null;
            }
            return problems.Count > count ? null : ThnPatchEntry.Addition(table);
        }

        var match = entry[MatchKey];
        if (match == null)
            problems.Add($"{label} needs {MatchKey} or {NewKey}.");
        else if (!IsMatch(kind, match))
            problems.Add(kind == ThnElementKind.Entity
                ? $"{label}: {MatchKey} must be the name of the entity."
                : $"{label}: {MatchKey} must be the key {{ time, type, {{ targets }} }} of the event.");
        var (nth, entryCount) = ReadPlace(label, entry, problems);
        var delete = false;
        if (entry[DeleteKey] is { } deleteValue)
        {
            if (deleteValue is ThnBool { Value: true })
                delete = true;
            else
                problems.Add($"{label}: write {DeleteKey} = Y to delete the {Singular(kind)}.");
            if (entry[SetKey] != null || entry[RemoveKey] != null)
                problems.Add($"{label}: {DeleteKey} cannot have {SetKey} or {RemoveKey}.");
        }
        var sets = ReadSets(kind, label, entry[SetKey], problems);
        var removes = ReadRemoves(kind, label, entry[RemoveKey], problems);
        if (problems.Count > count)
            return null;
        return delete ? ThnPatchEntry.Deletion(match!, nth, entryCount) : ThnPatchEntry.Change(match!, nth, entryCount, sets, removes);
    }

    /// <summary>True for a table that has the form of an element: a record for an entity, and a list of at least
    /// time, type and targets for an event.</summary>
    private static bool IsElement(ThnElementKind kind, ThnTable table) => kind == ThnElementKind.Entity
        ? table.IsRecord
        : table.Record.IsEmpty && table.List.Length >= 3;

    private static bool IsMatch(ThnElementKind kind, ThnValue match) => kind == ThnElementKind.Entity
        ? match is ThnText
        : match is ThnTable { Record.IsEmpty: true, List: [ThnNumber, ThnNumber or ThnSymbol, ThnTable { Record.IsEmpty: true } targets] } &&
          targets.List.All(t => t is ThnText);

    private static (int? Nth, int? Count) ReadPlace(string label, ThnTable entry, List<string> problems)
    {
        var nthValue = entry[NthKey];
        var countValue = entry[CountKey];
        if (nthValue == null && countValue == null)
            return (null, null);
        if (WholeNumber(nthValue) is { } nth && WholeNumber(countValue) is { } count && nth <= count)
            return (nth, count);
        problems.Add($"{label}: {NthKey} and {CountKey} go together. Each is a whole number from 1, and {NthKey} is at most {CountKey}.");
        return (null, null);
    }

    private static int? WholeNumber(ThnValue? value) =>
        value is ThnNumber { Value: >= 1 and <= int.MaxValue } number && number.Value == MathF.Floor(number.Value)
            ? (int)number.Value
            : null;

    private static ImmutableArray<(ThnPath Path, ThnValue Value)> ReadSets(ThnElementKind kind, string label, ThnValue? value,
        List<string> problems)
    {
        if (value == null)
            return [];
        if (value is not ThnTable { List.IsEmpty: true } record)
        {
            problems.Add($"{label}: {SetKey} must be a record of paths and values, for example {{ [\"cameraprops.fovh\"] = 20 }}.");
            return [];
        }
        var sets = ImmutableArray.CreateBuilder<(ThnPath, ThnValue)>(record.Record.Length);
        foreach (var (key, setValue) in record.Record)
        {
            if (ReadPath(kind, label, key, remove: false, problems) is { } path)
                sets.Add((path, setValue));
        }
        return sets.ToImmutable();
    }

    private static ImmutableArray<ThnPath> ReadRemoves(ThnElementKind kind, string label, ThnValue? value, List<string> problems)
    {
        if (value == null)
            return [];
        if (value is not ThnTable { Record.IsEmpty: true } list || list.List.Any(p => p is not ThnText))
        {
            problems.Add($"{label}: {RemoveKey} must be a list of paths, for example {{ \"cameraprops.fovh\" }}.");
            return [];
        }
        var removes = ImmutableArray.CreateBuilder<ThnPath>(list.List.Length);
        foreach (ThnText text in list.List)
        {
            if (ReadPath(kind, label, text.Value, remove: true, problems) is { } path)
                removes.Add(path);
        }
        return removes.ToImmutable();
    }

    private static ThnPath? ReadPath(ThnElementKind kind, string label, string text, bool remove, List<string> problems)
    {
        if (ThnPath.Problem(text) is { } textProblem)
        {
            problems.Add($"{label}: {textProblem}");
            return null;
        }
        var path = new ThnPath(text);
        if (ThnElementPaths.Problem(kind, path, remove) is not { } problem)
            return path;
        problems.Add($"{label}: {problem}");
        return null;
    }

    /// <summary>The name of an entry in messages, for example "events entry 2".</summary>
    private static string EntryLabel(ThnElementKind kind, int index) => $"{Plural(kind)} entry {index + 1}";

    internal static string Singular(ThnElementKind kind) => kind == ThnElementKind.Entity ? "entity" : "event";

    internal static string Plural(ThnElementKind kind) => kind == ThnElementKind.Entity ? "entities" : "events";

    /// <summary>The name of an element in messages, for example <c>entity "Cam_02"</c>.</summary>
    private static string ElementLabel(ThnElementKind kind, ThnValue? match) =>
        $"{Singular(kind)} {(match == null ? "without a key" : ThnKeys.Text(match))}";

    /// <summary>The state of one <see cref="Apply"/>: the problems and the records of the changes.</summary>
    /// <param name="setsDuration">True when the patch sets the duration of the script.</param>
    private sealed class ApplyRun(IReadOnlyDictionary<string, ThnSymbolInfo> symbols, bool setsDuration)
    {
        public List<string> Errors { get; } = [];
        private readonly List<string> warnings = [];
        private readonly List<ThnPatchChange> changes = [];
        private readonly List<ThnGroupChange> groups = [];
        private readonly List<ThnPlace> places = [];

        public ThnPatchResult Result(ThnScriptModel? model) =>
            new(model, [.. Errors], [.. warnings], [.. changes], [.. groups], [.. places], setsDuration);

        /// <summary>The index of the element of each entry, or -1 for a new element. Adds an error for each key that
        /// does not name one element.</summary>
        public int[] Resolve(ThnElementKind kind, ImmutableList<ThnElement> elements, ImmutableArray<ThnPatchEntry> entries)
        {
            var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var keys = ThnKeys.Of(kind, elements.Select(e => e.Table), symbols);
            for (var i = 0; i < keys.Length; i++)
            {
                if (keys[i] is not { } key)
                    continue;
                if (!positions.TryGetValue(key.Signature, out var list))
                    positions[key.Signature] = list = [];
                list.Add(i);
            }

            var result = new int[entries.Length];
            var claimed = new Dictionary<int, int>();
            for (var i = 0; i < entries.Length; i++)
            {
                result[i] = -1;
                var entry = entries[i];
                if (entry.Match == null)
                    continue;
                var label = EntryLabel(kind, i);
                var key = ThnKeys.Text(entry.Match);
                if (ThnKeys.Signature(kind, entry.Match, symbols) is not { } signature)
                {
                    Errors.Add($"{label}: the type of the key {key} is not a THN name.");
                    continue;
                }
                var hits = positions.GetValueOrDefault(signature) ?? [];
                var expected = entry.Count ?? 1;
                if (hits.Count == 0)
                    Errors.Add($"{label}: no {Singular(kind)} has the key {key}.");
                else if (hits.Count != expected)
                    Errors.Add(entry.Count == null
                        ? $"{label}: {hits.Count} {Plural(kind)} have the key {key}. Give {NthKey} and {CountKey} to name one of them."
                        : $"{label}: {hits.Count} {Plural(kind)} have the key {key}, but the entry expects {expected} ({CountKey}).");
                else if (claimed.TryGetValue(hits[(entry.Nth ?? 1) - 1], out var other))
                    Errors.Add($"{label} names the same {Singular(kind)} as {EntryLabel(kind, other)}.");
                else
                {
                    result[i] = hits[(entry.Nth ?? 1) - 1];
                    claimed[result[i]] = i;
                    if (entry.Nth is { } nth)
                        places.Add(new ThnPlace(kind, signature, label, key, nth, expected));
                }
            }
            return result;
        }

        /// <summary>Applies the removes, the sets and the deletes, then adds the new elements.</summary>
        public ImmutableList<ThnElement> Change(ThnElementKind kind, ImmutableList<ThnElement> elements,
            ImmutableArray<ThnPatchEntry> entries, int[] positions, Func<ThnElementId> ids)
        {
            var tables = elements.Select(e => e.Table).ToArray();
            var deleted = new bool[elements.Count];
            for (var i = 0; i < entries.Length; i++)
            {
                var position = positions[i];
                if (position < 0)
                    continue;
                var entry = entries[i];
                var element = elements[position];
                var label = ElementLabel(kind, entry.Match);
                var before = Signature(kind, tables[position]);
                if (entry.Delete)
                {
                    deleted[position] = true;
                    changes.Add(new ThnPatchChange(kind, element.Id, label, ThnPatchChangeKind.Delete, null));
                    Group(kind, before);
                    continue;
                }
                var table = tables[position];
                try
                {
                    foreach (var path in entry.Removes)
                    {
                        if (ThnElementPaths.Remove(kind, table, path) is { } removed)
                            table = removed;
                        else
                            warnings.Add($"{label}: '{path}' has no value to remove.");
                        changes.Add(new ThnPatchChange(kind, element.Id, label, ThnPatchChangeKind.Remove, path));
                    }
                    foreach (var (path, value) in entry.Sets)
                    {
                        table = ThnElementPaths.Set(kind, table, path, value);
                        changes.Add(new ThnPatchChange(kind, element.Id, label, ThnPatchChangeKind.Set, path));
                    }
                }
                catch (ThnPathException e)
                {
                    Errors.Add($"{EntryLabel(kind, i)}: {e.Message}");
                    continue;
                }
                tables[position] = table;
                if (Signature(kind, table) is var after && after != before)
                {
                    Group(kind, before);
                    Group(kind, after);
                }
            }

            var result = ImmutableList.CreateBuilder<ThnElement>();
            for (var i = 0; i < elements.Count; i++)
            {
                if (!deleted[i])
                    result.Add(ReferenceEquals(tables[i], elements[i].Table) ? elements[i] : elements[i] with { Table = tables[i] });
            }
            foreach (var entry in entries)
            {
                if (entry.New is not { } table)
                    continue;
                var element = new ThnElement(ids(), -1, table);
                result.Add(element);
                changes.Add(new ThnPatchChange(kind, element.Id, ElementLabel(kind, ThnKeys.Match(kind, table)),
                    ThnPatchChangeKind.New, null));
                Group(kind, Signature(kind, table));
            }
            return result.ToImmutable();
        }

        private string? Signature(ThnElementKind kind, ThnTable table) =>
            ThnKeys.Signature(kind, ThnKeys.Match(kind, table), symbols);

        private void Group(ThnElementKind kind, string? signature)
        {
            if (signature != null)
                groups.Add(new ThnGroupChange(kind, signature));
        }
    }
}

/// <summary>One entry of a <see cref="ThnPatch"/>: a change, a delete or a new element.</summary>
internal sealed class ThnPatchEntry
{
    private ThnPatchEntry(ThnValue? match, int? nth, int? count, ImmutableArray<(ThnPath Path, ThnValue Value)> sets,
        ImmutableArray<ThnPath> removes, bool delete, ThnTable? added)
    {
        Match = match;
        Nth = nth;
        Count = count;
        Sets = sets;
        Removes = removes;
        Delete = delete;
        New = added;
    }

    /// <summary>The key of the element (see <see cref="ThnKeys.Match"/>), or null for a new element.</summary>
    public ThnValue? Match { get; }

    /// <summary>The place of the element among the elements with its key, from 1, or null when the key is unique.</summary>
    public int? Nth { get; }

    /// <summary>The number of elements with the key, or null when the key is unique.</summary>
    public int? Count { get; }

    /// <summary>The values to set, in order. They run after <see cref="Removes"/>.</summary>
    public ImmutableArray<(ThnPath Path, ThnValue Value)> Sets { get; }

    public ImmutableArray<ThnPath> Removes { get; }

    public bool Delete { get; }

    /// <summary>The table of a new element, or null.</summary>
    public ThnTable? New { get; }

    public static ThnPatchEntry Change(ThnValue match, int? nth, int? count, ImmutableArray<(ThnPath Path, ThnValue Value)> sets,
        ImmutableArray<ThnPath> removes) => new(match, nth, count, sets, removes, false, null);

    public static ThnPatchEntry Deletion(ThnValue match, int? nth, int? count) => new(match, nth, count, [], [], true, null);

    public static ThnPatchEntry Addition(ThnTable table) => new(null, null, null, [], [], false, table);

    /// <summary>The entry as the table that a patch file holds.</summary>
    public ThnTable ToTable()
    {
        if (New != null)
            return ThnTable.FromRecord(new ThnField(ThnPatch.NewKey, New));
        var fields = ImmutableArray.CreateBuilder<ThnField>();
        fields.Add(new ThnField(ThnPatch.MatchKey, Match!));
        if (Nth is { } nth && Count is { } count)
        {
            fields.Add(new ThnField(ThnPatch.NthKey, new ThnNumber(nth)));
            fields.Add(new ThnField(ThnPatch.CountKey, new ThnNumber(count)));
        }
        if (!Sets.IsEmpty)
            fields.Add(new ThnField(ThnPatch.SetKey, new ThnTable([], [.. Sets.Select(s => new ThnField(s.Path.Text, s.Value))])));
        if (!Removes.IsEmpty)
            fields.Add(new ThnField(ThnPatch.RemoveKey, new ThnTable([.. Removes.Select(p => (ThnValue)new ThnText(p.Text))], [])));
        if (Delete)
            fields.Add(new ThnField(ThnPatch.DeleteKey, new ThnBool(true)));
        return new ThnTable([], fields.ToImmutable());
    }
}

/// <summary>A patch file is not a valid THN patch.</summary>
internal sealed class ThnPatchException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

internal enum ThnPatchChangeKind
{
    Set,
    Remove,
    Delete,
    New,
}

/// <summary>A change of one element by a patch.</summary>
/// <param name="Label">The name of the element in messages, for example <c>entity "Cam_02"</c>.</param>
/// <param name="Path">The path of a set or a remove, else null.</param>
internal readonly record struct ThnPatchChange(ThnElementKind Kind, ThnElementId Element, string Label,
    ThnPatchChangeKind What, ThnPath? Path);

/// <summary>A key whose elements a patch changed: the patch deleted or added an element with the key, or changed the
/// key of an element.</summary>
internal readonly record struct ThnGroupChange(ThnElementKind Kind, string Signature);

/// <summary>An entry that names its element by its place among the elements with the same key (<c>ol_nth</c>).</summary>
/// <param name="Entry">The name of the entry, for example "events entry 2".</param>
/// <param name="Key">The key as text.</param>
internal readonly record struct ThnPlace(ThnElementKind Kind, string Signature, string Entry, string Key, int Nth, int Count);

/// <summary>The result of <see cref="ThnPatch.Apply"/>.</summary>
/// <param name="Model">The changed script, or null when an error stopped the patch.</param>
/// <param name="Changes">Each element change, in patch order.</param>
/// <param name="Groups">Each key whose elements the patch changed.</param>
/// <param name="Places">Each entry with <c>ol_nth</c>.</param>
/// <param name="SetsDuration">True when the patch sets the duration of the script.</param>
internal sealed record ThnPatchResult(ThnScriptModel? Model, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    IReadOnlyList<ThnPatchChange> Changes, IReadOnlyList<ThnGroupChange> Groups, IReadOnlyList<ThnPlace> Places,
    bool SetsDuration);

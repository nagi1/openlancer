using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>A problem of the text of a raw edit.</summary>
/// <param name="Line">The line of the text, from 1, or 0 for the whole text.</param>
public sealed record RawEditProblem(int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"Line {Line}: {Message}" : Message;
}

/// <summary>The batch of a raw edit of mission sections, or the problems that stop it, and the notes for the
/// Console. No batch and no problem: nothing changed.</summary>
public sealed record RawMissionPlan(MissionBatch? Batch, IReadOnlyList<RawEditProblem> Problems, IReadOnlyList<string> Notes);

/// <summary>A problem of the mission checks of a draft text, with its line in the text.</summary>
/// <param name="Line">The line of the text, from 1, or 0 for the whole text.</param>
public sealed record RawDraftMark(int Line, Diagnostic Problem);

/// <summary>
/// The checks of a draft text while the user types (section 8.8 of the M6b design). They are the problems of the text,
/// or a function that runs the mission checks on the parsed text. The function reads only data that does not change, so
/// a worker can run it.
/// </summary>
/// <param name="Checks">The function, or null when the text has a problem or the file is not a mission script.</param>
public sealed record RawMissionDraft(IReadOnlyList<RawEditProblem> Problems, Func<IReadOnlyList<RawDraftMark>>? Checks);

/// <summary>
/// The plan of a raw INI edit of a mission file (section 8.8 of the M6b design, decision D678): the "Raw INI" view of a
/// whole script, or the INI block of a page. The plan compares the text at the start of the edit with the text at its
/// end, section by section (type, key and ordinal), and makes one batch of mission commands.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A changed body gives <see cref="ReplaceSectionBody"/>. A new section gives <see cref="RestoreSection"/> at its
/// place. A removed section gives <see cref="RemoveSection"/>, and the lines that name it show "Missing".</item>
/// <item>A ship or a formation section in another place gives <see cref="MoveSection"/>. A section of another type
/// keeps its place, with a note.</item>
/// <item>The plan stops when W of a changed or removed section changed after the start, when a first nickname changes,
/// and for a problem of the text. A value outside a list does not stop the plan. It becomes a problem of the
/// checks.</item>
/// </list>
/// </remarks>
public static class RawMissionEdit
{
    public const string ChangedWhileTyping = "The section changed while you typed. Copy your text, and edit again.";

    public const string NicknameChanged =
        "Use Rename (F2) to change a nickname, so that the lines that name it change too.";

    public const string CommentNote = "The editor does not keep comments inside a mission section.";

    /// <summary>The note of a moved section of a type whose place has no effect.</summary>
    public static string MoveNote(string type) =>
        $"The place of a {type} section in the file has no effect in the game. The editor kept its place.";

    /// <summary>The plan of an edit of the whole text of a mission file.</summary>
    /// <param name="file">The install path of the script or of its ship build file.</param>
    public static RawMissionPlan PlanFile(EditSession session, string file, string startText, string endText) =>
        Plan(session, file, null, startText, endText);

    /// <summary>The plan of an edit of the INI block of a page: the text of some sections of a file.</summary>
    /// <param name="sections">The identities in the file of the sections of the start text, in text order.</param>
    public static RawMissionPlan PlanBlock(EditSession session, string file, IReadOnlyList<SectionIdentity> sections,
        string startText, string endText) =>
        Plan(session, file, sections, startText, endText);

    /// <summary>The problems of a text that stop a raw edit, for the marks while the user types.</summary>
    public static IReadOnlyList<RawEditProblem> TextProblems(string file, string text) => Parse(file, text).Problems;

    /// <summary>The sections of a text of mission sections as the engine text parser reads them, for example the text
    /// of a paste of triggers (decision D689). None when the text has a problem of <see cref="TextProblems"/>.</summary>
    public static IReadOnlyList<Section> Sections(string file, string text) => Parse(file, text).Sections;

    /// <summary>
    /// The checks of a draft text while the user types (section 8.8 of the M6b design, UI rule 11 of AGENTS.md). The
    /// text is the whole file or the INI block of a page. A text problem gives its problem at once. Else the checks run
    /// on W with the parsed sections in place of the sections of the text. Each problem of a section of the text maps
    /// to its line.
    /// </summary>
    /// <param name="block">The identities in the file of the sections of the block, in text order, or null for the
    /// whole file.</param>
    public static RawMissionDraft Draft(EditSession session, string file, IReadOnlyList<SectionIdentity>? block, string text)
    {
        var parsed = Parse(file, text);
        if (parsed.Problems.Count > 0)
            return new RawMissionDraft(parsed.Problems, null);
        if (block != null && BlockProblem(block, parsed) is { } problem)
            return new RawMissionDraft([problem], null);
        if (session.OpenDocument(file) is not { HoldsScript: true, State.Exists: true } document)
            return new RawMissionDraft([], null);
        // The index in the sections of the draft of each section of the text.
        List<Section> sections;
        var places = new int[parsed.Sections.Count];
        if (block == null)
        {
            sections = parsed.Sections;
            for (var k = 0; k < places.Length; k++)
                places[k] = k;
        }
        else
        {
            sections = document.Working.ToList();
            for (var k = 0; k < places.Length; k++)
            {
                if (document.IndexOf(block[k]) is not (>= 0 and var at))
                    return new RawMissionDraft([], null);
                sections[at] = parsed.Sections[k];
                places[k] = at;
            }
        }
        var work = session.Missions.DraftWork(document, sections);
        return new RawMissionDraft([], () =>
        {
            var analysis = work();
            var ids = SectionIdentity.Identify(sections);
            var result = new List<RawDraftMark>();
            foreach (var diagnostic in analysis.Problems)
            {
                var at = diagnostic.Target is { } target ? Array.IndexOf(ids, target.Id) : -1;
                var k = Array.IndexOf(places, at);
                if (k < 0)
                {
                    // A problem of the whole file shows on the first line of the whole text.
                    if (block == null && diagnostic.Target == null)
                        result.Add(new RawDraftMark(0, diagnostic));
                    continue;
                }
                var entry = diagnostic.EntryIndex >= 0 && diagnostic.EntryIndex < parsed.Sections[k].Count ? diagnostic.EntryIndex : -1;
                result.Add(new RawDraftMark(parsed.Line(k, entry), diagnostic));
            }
            return result;
        });
    }

    /// <summary>
    /// Why the end text of the INI block of a page cannot be planned, or null (section 8.8 of the M6b design). The block
    /// holds the sections of the page. A header cannot go in or out of it, and the type of each section stays, as in the
    /// raw block of other files (MOD_FORMAT "Raw INI blocks").
    /// </summary>
    private static RawEditProblem? BlockProblem(IReadOnlyList<SectionIdentity> block, ParsedText end)
    {
        if (end.Sections.Count != block.Count)
        {
            var count = block.Count == 1 ? "one section" : $"{block.Count} sections";
            return new RawEditProblem(end.Sections.Count > block.Count ? end.Line(block.Count, -1) : 0,
                $"This block holds {count}, so a header cannot go in or out of it. Add or remove a section in the Raw INI " +
                "view of the whole script.");
        }
        for (var k = 0; k < block.Count; k++)
        {
            if (!end.Sections[k].Name.Equals(block[k].Type, StringComparison.OrdinalIgnoreCase))
                return new RawEditProblem(end.Line(k, -1),
                    $"The header of this section must stay [{block[k].Type}]. Make a section of another type in the Raw INI " +
                    "view of the whole script.");
        }
        return null;
    }

    private static RawMissionPlan Plan(EditSession session, string file, IReadOnlyList<SectionIdentity>? block,
        string startText, string endText)
    {
        if (string.Equals(startText.Replace("\r\n", "\n"), endText.Replace("\r\n", "\n"), StringComparison.Ordinal))
            return new RawMissionPlan(null, [], []);
        if (session.OpenDocument(file) is not { } document)
            return Stop(0, $"The edit view has no file {file}.");
        var end = Parse(file, endText);
        if (end.Problems.Count > 0)
            return new RawMissionPlan(null, end.Problems, []);
        var start = Parse(file, startText);
        if (start.Problems.Count > 0)
            return Stop(0, "The text at the start of the edit does not parse. Copy your text, and edit again.");

        // The identity of each section of the start text in its file.
        var startIds = Identify(start.Sections);
        if (block != null && block.Count != startIds.Length)
            return Stop(0, ChangedWhileTyping);
        if (block != null && BlockProblem(block, end) is { } blockProblem)
            return new RawMissionPlan(null, [blockProblem], []);
        var fileIdOf = new Dictionary<SectionIdentity, SectionIdentity>();
        for (var k = 0; k < startIds.Length; k++)
            fileIdOf[startIds[k]] = block?[k] ?? startIds[k];
        var endIds = Identify(end.Sections);
        var startIndex = startIds.Select((id, k) => (id, k)).ToDictionary(p => p.id, p => p.k);
        var endIndex = endIds.Select((id, k) => (id, k)).ToDictionary(p => p.id, p => p.k);

        var removed = startIds.Where(id => !endIndex.ContainsKey(id)).ToList();
        var added = endIds.Where(id => !startIndex.ContainsKey(id)).ToList();
        // A first nickname that changed gives a removed and a new section of one type at one place.
        foreach (var id in added)
        {
            var k = endIndex[id];
            if (removed.Any(r => r.Type.Equals(id.Type, StringComparison.OrdinalIgnoreCase) &&
                                 SamePlace(startIds, startIndex[r], endIds, k, startIndex)))
                return Stop(end.Line(k, -1), NicknameChanged);
        }

        var notes = new List<string>();
        var commands = new List<MissionCommand>();
        var common = endIds.Where(startIndex.ContainsKey).ToList();
        foreach (var id in common.Concat(removed))
        {
            var before = start.Sections[startIndex[id]];
            var changed = !endIndex.TryGetValue(id, out var k) || !Same(before, end.Sections[k]);
            if (!changed)
                continue;
            var at = document.IndexOf(fileIdOf[id]);
            if (at < 0 || !Same(before, document.Working[at]))
                return Stop(endIndex.TryGetValue(id, out var line) ? end.Line(line, -1) : 0, ChangedWhileTyping);
        }

        // Bodies first: they keep each identity. A member of a linked prefab instance takes no line edit (section 8.8 of
        // the M6b design). A delete of a member is a removed-member override (decision D676 c), so it passes.
        foreach (var id in common.Where(id => !Same(start.Sections[startIndex[id]], end.Sections[endIndex[id]])))
        {
            if (session.Missions.LinkedInstanceOf(new SectionRef(document.Path, fileIdOf[id])) is { } linked)
                return Stop(end.Line(endIndex[id], FirstChange(start.Sections[startIndex[id]], end.Sections[endIndex[id]])),
                    PrefabLinks.LineEditMessage(PrefabLinks.PrefabNameOf(linked)));
            commands.Add(new ReplaceSectionBody(new ItemRef(document.Path, fileIdOf[id]),
                MissionDraft.RawOf(end.Sections[endIndex[id]])));
            if (end.HasComment[endIndex[id]])
                Note(notes, CommentNote);
        }

        // Moves: a section that is in both texts and not in their longest common order.
        var kept = LongestCommonOrder(startIds.Where(endIndex.ContainsKey).ToList(), common);
        var order = document.Working.Select((_, i) => document.IdentityAt(i)).ToList();
        foreach (var id in common.Where(id => !kept.Contains(id)))
        {
            var type = end.Sections[endIndex[id]].Name;
            if (!MissionIndex.Is(type, "MsnShip") && !MissionIndex.Is(type, "MsnFormation"))
            {
                Note(notes, MoveNote(type));
                continue;
            }
            var anchor = endIds.Skip(endIndex[id] + 1).FirstOrDefault(kept.Contains);
            var beforeId = anchor != null ? fileIdOf[anchor] : AfterBlock(order, block);
            commands.Add(new MoveSection(new ItemRef(document.Path, fileIdOf[id]))
            {
                Before = beforeId == null ? null : new ItemRef(document.Path, beforeId),
            });
            order.Remove(fileIdOf[id]);
            order.Insert(beforeId == null ? order.Count : order.IndexOf(beforeId), fileIdOf[id]);
        }

        // Removes from the end of the file, so the ordinal of an earlier section stays.
        foreach (var id in removed.OrderByDescending(id => order.IndexOf(fileIdOf[id])))
        {
            commands.Add(new RemoveSection(new ItemRef(document.Path, fileIdOf[id])));
            order.Remove(fileIdOf[id]);
        }

        // New sections at their places: after the section before them in the text. A new section of a patched file
        // whose place has no effect goes to the end of the file, as the command does (section 6.6 of the M6b design).
        var places = order.Cast<object>().ToList();
        var news = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var id in added)
        {
            var k = endIndex[id];
            var index = PlaceOf(places, endIds, k, fileIdOf, block, order);
            if (MissionDraft.MovesBaseSections(document, end.Sections[k],
                    places.Skip(index).Where(p => !news.Contains(p)).Cast<SectionIdentity>()))
            {
                index = places.Count;
                Note(notes, MissionDraft.NewPlaceNote(end.Sections[k].Name));
            }
            places.Insert(index, id);
            news.Add(id);
            commands.Add(new RestoreSection(new ScriptRef(document.Path), index,
                new RawSection(end.Sections[k].Name, MissionDraft.RawOf(end.Sections[k]))));
            if (end.HasComment[k])
                Note(notes, CommentNote);
        }

        if (commands.Count == 0)
            return new RawMissionPlan(null, [], notes);
        var label = block is [var one] ? $"Edit INI of {one}" : $"Edit INI of {System.IO.Path.GetFileName(document.Path)}";
        return new RawMissionPlan(new MissionBatch(label, commands), [], notes);
    }

    /// <summary>The index in the file of a new section: after the nearest section before it in the end text, else
    /// before the nearest section after it, else after the block, else at the end of the file.</summary>
    private static int PlaceOf(List<object> places, SectionIdentity[] endIds, int k,
        Dictionary<SectionIdentity, SectionIdentity> fileIdOf, IReadOnlyList<SectionIdentity>? block,
        List<SectionIdentity> order)
    {
        for (var p = k - 1; p >= 0; p--)
        {
            var at = IndexOf(places, endIds[p], fileIdOf);
            if (at >= 0)
                return at + 1;
        }
        for (var p = k + 1; p < endIds.Length; p++)
        {
            var at = IndexOf(places, endIds[p], fileIdOf);
            if (at >= 0)
                return at;
        }
        return AfterBlock(order, block) is { } after ? places.IndexOf(after) : places.Count;
    }

    /// <summary>The place of a section of the end text in the list of places: a section of the file by its file
    /// identity, or a new section of this plan by its identity in the end text.</summary>
    private static int IndexOf(List<object> places, SectionIdentity id,
        Dictionary<SectionIdentity, SectionIdentity> fileIdOf) =>
        fileIdOf.TryGetValue(id, out var fileId) && places.IndexOf(fileId) is var i and >= 0 ? i
        : places.FindIndex(p => ReferenceEquals(p, id));

    /// <summary>The section of the file after the last section of a block, or null for the end of the file or for the
    /// whole file.</summary>
    private static SectionIdentity? AfterBlock(List<SectionIdentity> order, IReadOnlyList<SectionIdentity>? block)
    {
        if (block == null)
            return null;
        var last = block.Select(id => order.IndexOf(id)).DefaultIfEmpty(-1).Max();
        return last >= 0 && last + 1 < order.Count ? order[last + 1] : null;
    }

    /// <summary>True when a removed section of the start text and a new section of the end text are at one place: the
    /// same section of both texts comes before them, or both are first.</summary>
    private static bool SamePlace(SectionIdentity[] startIds, int s, SectionIdentity[] endIds, int e,
        Dictionary<SectionIdentity, int> startIndex)
    {
        var previous = e > 0 ? endIds[e - 1] : null;
        if (previous == null)
            return s == 0;
        return startIndex.TryGetValue(previous, out var p) && p == s - 1;
    }

    /// <summary>The sections of the longest common order of two lists of the same sections.</summary>
    private static HashSet<SectionIdentity> LongestCommonOrder(List<SectionIdentity> a, List<SectionIdentity> b)
    {
        var (kept, _) = CommonOrder.Kept(a, b, EqualityComparer<SectionIdentity>.Default);
        return a.Where((_, i) => kept[i]).ToHashSet();
    }

    /// <summary>True when two sections have the same type and the same lines, by the text of their values in the raw
    /// text.</summary>
    private static bool Same(Section a, Section b) =>
        a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && a.Count == b.Count && a.Zip(b).All(p => Same(p.First, p.Second));

    /// <summary>True when two lines have the same key and the same values, by the text of their values.</summary>
    private static bool Same(Entry a, Entry b) =>
        a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) &&
        a.Select(RetailText.Of).SequenceEqual(b.Select(RetailText.Of), StringComparer.Ordinal);

    /// <summary>The first line of the end section that differs from the start section, or -1 for its header when the end
    /// section only lost lines at its end.</summary>
    private static int FirstChange(Section start, Section end)
    {
        var count = Math.Min(start.Count, end.Count);
        for (var i = 0; i < count; i++)
        {
            if (!Same(start[i], end[i]))
                return i;
        }
        return count < end.Count ? count : -1;
    }

    private static SectionIdentity[] Identify(IReadOnlyList<Section> sections) => SectionIdentity.Identify(sections);

    private static void Note(List<string> notes, string note)
    {
        if (!notes.Contains(note))
            notes.Add(note);
    }

    private static RawMissionPlan Stop(int line, string message) => new(null, [new RawEditProblem(line, message)], []);

    /// <summary>The sections of a text as the engine text parser reads them, the line of each section and entry, and
    /// the problems of the text.</summary>
    private static ParsedText Parse(string path, string text)
    {
        var problems = new List<RawEditProblem>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var schema = MissionSchema.Retail;
        var headers = new List<int>();
        var comments = new List<bool>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var number = i + 1;
            if (line.Length == 0)
                continue;
            if (line[0] is ';' or '@' || line.StartsWith("//", StringComparison.Ordinal))
            {
                if (comments.Count > 0)
                    comments[^1] = true;
                continue;
            }
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                var type = close > 0 ? line[1..close].Trim() : "";
                if (close < 0)
                    problems.Add(new RawEditProblem(number, "A section header ends with ']'. Correct the header."));
                else if (schema.Section(type) == null && !MissionIndex.Is(type, "NPCShipArch"))
                    problems.Add(new RawEditProblem(number,
                        $"[{type}] is not a section type of a mission file. The game ignores the section. Correct the type."));
                headers.Add(i);
                comments.Add(false);
                continue;
            }
            if (headers.Count == 0)
            {
                problems.Add(new RawEditProblem(number,
                    "This line is before the first section header, so the game ignores it. Move it into a section, or " +
                    "remove it."));
                continue;
            }
            var commentAt = line.IndexOf(';');
            if (commentAt >= 0)
            {
                comments[^1] = true;
                line = line[..commentAt].Trim();
                if (line.Length == 0)
                    continue;
            }
            if (!line.Contains('=') && !KnownKey(schema, line))
                problems.Add(new RawEditProblem(number,
                    $"'{line}' has no '='. Write 'key = values'. Only a known key of the game can be without values."));
        }
        if (problems.Count > 0)
            return new ParsedText([], [], [], problems);
        List<Section> sections;
        try
        {
            sections = IniDocument.Parse(path, IniDocument.TextEncoding.GetBytes(string.Join("\r\n", lines))).Sections;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ParsedText([], [], [], [new RawEditProblem(0,
                "The game cannot read the text. Correct the section headers and the lines, then edit again.")]);
        }
        var map = RawIni.Scan(IniDocument.TextEncoding.GetBytes(string.Join("\n", lines)), out _);
        if (sections.Count != headers.Count)
            return new ParsedText([], [], [], [new RawEditProblem(0, "The game reads another number of sections. Correct the headers.")]);
        return new ParsedText(sections, map?.Sections.Count == sections.Count ? map.Sections : null, comments, []);
    }

    /// <summary>True for a key that the schema knows: a condition, an action, an order or a key of a section type.</summary>
    private static bool KnownKey(MissionSchema schema, string key) =>
        schema.Name(key) != null || schema.Directive(key) != null ||
        schema.Sections.Append(schema.ShipBuild).Any(s => s.Key(key) != null);

    private sealed record ParsedText(List<Section> Sections, List<Export.RawSection>? Map, List<bool> HasComment,
        List<RawEditProblem> Problems)
    {
        /// <summary>The line of a section header, or of one of its entries, from 1. 0 when the map is missing.</summary>
        public int Line(int section, int entry) =>
            Map == null ? 0 : (entry < 0 ? Map[section].Header : Map[section].Entries[entry]) + 1;
    }
}

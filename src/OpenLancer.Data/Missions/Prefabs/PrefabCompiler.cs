using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Prefabs;

/// <summary>
/// What the compile of a linked instance reads (section 5.4 of the M6b design): the instance id, the value of each field,
/// the outside triggers of each port, the member nicknames of the record and the removed members.
/// </summary>
/// <param name="Id">The id of the instance. The slot <c>{instance}</c> gives it.</param>
public sealed record PrefabInput(string Id)
{
    /// <summary>The rows of each field that the instance sets: the per-instance values and the overrides. A field
    /// without rows here takes the default of the template.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PrefabRow>> Values { get; init; } =
        new Dictionary<string, IReadOnlyList<PrefabRow>>();

    /// <summary>The outside triggers of each port, in order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Ports { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The nickname of each role that the record holds. A role without a nickname here takes the nickname of
    /// its template line.</summary>
    public IReadOnlyDictionary<string, string> Members { get; init; } = new Dictionary<string, string>();

    /// <summary>The roles of the removed-member overrides (decision D676 c). The compile leaves them out.</summary>
    public IReadOnlySet<string> Removed { get; init; } = new HashSet<string>();
}

/// <summary>One section of the compile: its role, its type, its nickname and its lines as value texts.</summary>
public sealed record CompiledSection(string Role, string Type, string Nickname, IReadOnlyList<RawEntry> Entries)
{
    /// <summary>The index of the template line of each line, in the lines of the template section.</summary>
    public IReadOnlyList<int> Sources { get; init; } = [];
}

/// <summary>
/// The compile of a linked prefab instance (section 5.4 of the M6b design): a pure function of the template, the instance
/// id, the field values and the port links. Each run gives the same sections.
/// </summary>
public static class PrefabCompiler
{
    /// <summary>
    /// Compiles an instance: for each template section in order, it fills the slots, repeats the lines of list fields
    /// and ports, and leaves out the role. A line whose list has no rows goes away. Each line gets the checks of the
    /// schema, as a command does.
    /// </summary>
    /// <exception cref="EditException">A field value has the wrong kind, a line is not valid, or a member nickname is not
    /// a valid name. The error names the field or the line, and the reason.</exception>
    public static IReadOnlyList<CompiledSection> Compile(PrefabTemplate template, PrefabInput input)
    {
        var values = new Dictionary<string, IReadOnlyList<PrefabRow>>();
        foreach (var field in template.Fields)
        {
            var rows = input.Values.GetValueOrDefault(field.Id) ?? field.Default;
            if (PrefabValues.ValueProblem(field, rows) is { } problem)
                throw new EditException(MissionErrors.FieldValueError(field.Label, problem));
            values[field.Id] = rows;
        }

        // The nickname of each role: the record, or the template line with the instance id.
        var patterns = new Dictionary<string, string>();
        var names = new Dictionary<string, string>();
        foreach (var section in template.Sections)
        {
            var pattern = Fill(section.NicknamePattern ?? "", input.Id, null, null, values, input.Ports);
            patterns[section.Role] = pattern;
            names[section.Role] = input.Members.GetValueOrDefault(section.Role) ?? pattern;
        }

        var result = new List<CompiledSection>();
        foreach (var section in template.Sections.Where(s => !input.Removed.Contains(s.Role)))
        {
            var name = names[section.Role];
            if (!input.Members.ContainsKey(section.Role) && !MissionNames.FormOk(name, MissionNames.MaxFor(section.Type)))
                throw new EditException(MissionErrors.NameInvalidError(name, MissionNames.MaxFor(section.Type)) with
                {
                    Message = $"The prefab gives section '{section.Role}' the name '{name}'. It is not a valid name: use " +
                              $"letters, digits, '_' and '-', with at most {MissionNames.MaxFor(section.Type)} characters. " +
                              "Give the instance a shorter id.",
                });
            var entries = new List<RawEntry>();
            var sources = new List<int>();
            var nickname = true;
            for (var at = 0; at < section.Lines.Count; at++)
            {
                var line = section.Lines[at];
                if (PrefabSlot.KeyField(line.Key) is { } keyField)
                    entries.AddRange(values[keyField].Select(r => new RawEntry(r.Key!, r.Texts)));
                else if (nickname && line.Key.Equals("nickname", StringComparison.OrdinalIgnoreCase))
                {
                    nickname = false;
                    entries.Add(new RawEntry(line.Key, [name]));
                }
                else
                {
                    foreach (var texts in Expand(template, line, input, values))
                        entries.Add(new RawEntry(line.Key, Remap(texts, patterns, names)));
                }
                sources.AddRange(Enumerable.Repeat(at, entries.Count - sources.Count));
            }
            CheckLines(section, entries);
            result.Add(new CompiledSection(section.Role, section.Type, name, entries) { Sources = sources });
        }
        return result;
    }

    /// <summary>
    /// The role and the line of the first line of a compile that holds the value of a field, or null when no line holds
    /// it, for example a list without rows. "Show in raw INI" of a field of a linked instance goes to that line (section
    /// 8.6 of the M6b design).
    /// </summary>
    public static (string Role, int Entry)? FieldLine(PrefabTemplate template, IReadOnlyList<CompiledSection> compiled, string field)
    {
        foreach (var section in compiled)
        {
            if (template.Section(section.Role) is not { } source)
                continue;
            for (var entry = 0; entry < section.Sources.Count; entry++)
            {
                var line = source.Lines[section.Sources[entry]];
                if (PrefabSlot.KeyField(line.Key) == field || PrefabSlot.Of(line.Value).Any(s => s.Field == field))
                    return (section.Role, entry);
            }
        }
        return null;
    }

    /// <summary>The nickname that the template gives the section of a role in a new instance with an id, for example
    /// <c>ol_wave_1_spawn</c>.</summary>
    public static string MadeName(PrefabSectionDef section, string id) =>
        Fill(section.NicknamePattern ?? "", id, null, null, new Dictionary<string, IReadOnlyList<PrefabRow>>(),
            new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>
    /// A made id of a new instance (section 4.9 of the M6b design): the id of the prefab in lowercase and a number. No
    /// record of the script has the id, and the script has no section with a name that the template gives a section of
    /// the instance.
    /// </summary>
    /// <param name="idTaken">True for an id that a record of the script has.</param>
    /// <param name="nameTaken">True for a name that the script has for a section type, or that is not valid.</param>
    public static string NewInstanceId(PrefabTemplate template, Func<string, bool> idTaken, Func<string, string, bool> nameTaken)
    {
        var stem = new string(template.Id.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')
            .ToArray());
        if (stem.Length > 20)
            stem = stem[..20];
        for (var n = 1;; n++)
        {
            var id = $"{stem}_{n.ToString(CultureInfo.InvariantCulture)}";
            if (!idTaken(id) && template.Sections.All(s => !nameTaken(s.Type, MadeName(s, id))))
                return id;
        }
    }

    /// <summary>The value texts of each copy of a template line: one copy, one for each row of its list or each link of
    /// its port, or none when the list is empty or an optional field has no value.</summary>
    private static IEnumerable<IReadOnlyList<string>> Expand(PrefabTemplate template, PrefabLine line, PrefabInput input,
        IReadOnlyDictionary<string, IReadOnlyList<PrefabRow>> values)
    {
        var slots = PrefabSlot.Of(line.Value);
        string? listField = null;
        string? port = null;
        foreach (var slot in slots)
        {
            if (slot.Port is { } p)
                port = p;
            else if (slot.Field is { } f && template.Field(f) is { } field)
            {
                if (field.IsList)
                    listField = f;
                else if (field.Optional && values[f].Count == 0)
                    yield break;
            }
        }
        var count = port != null ? input.Ports.GetValueOrDefault(port)?.Count ?? 0
            : listField != null ? values[listField].Count
            : 1;
        for (var i = 0; i < count; i++)
        {
            var text = Fill(line.Value, input.Id, listField != null ? (listField, i) : null, port != null ? (port, i) : null,
                values, input.Ports, template);
            var texts = PrefabRow.Split(text).ToList();
            // A slot of an optional value at the end of a line leaves an empty value. The line ends before it.
            while (texts.Count > 0 && texts[^1].Length == 0)
                texts.RemoveAt(texts.Count - 1);
            yield return texts;
        }
    }

    /// <summary>The text of a template line with its slots filled. The row of a list or the link of a port gives the
    /// slots of that list or port.</summary>
    private static string Fill(string text, string id, (string Field, int Row)? row, (string Port, int Link)? link,
        IReadOnlyDictionary<string, IReadOnlyList<PrefabRow>> values, IReadOnlyDictionary<string, IReadOnlyList<string>> ports,
        PrefabTemplate? template = null)
    {
        var slots = PrefabSlot.Of(text);
        if (slots.Count == 0)
            return text;
        var result = new StringBuilder();
        var at = 0;
        foreach (var slot in slots)
        {
            result.Append(text, at, slot.Start - at);
            at = slot.Start + slot.Length;
            if (slot.IsInstance)
                result.Append(id);
            else if (slot.Port is { } port)
                result.Append(link is { } l && l.Port == port ? ports[port][l.Link] : "");
            else if (template?.Field(slot.Field!) is { } field && values.TryGetValue(field.Id, out var rows))
            {
                var value = field.IsList
                    ? row is { } r && r.Field == field.Id ? rows[r.Row] : null
                    : rows.FirstOrDefault();
                if (value == null)
                    continue;
                result.Append(field.Kind == PrefabFieldKind.List
                    ? string.Join(", ", ColumnTexts(field, value, slot.Column ?? field.Columns[0].Id))
                    : string.Join(", ", value.Texts));
            }
        }
        result.Append(text, at, text.Length - at);
        return result.ToString();
    }

    /// <summary>The texts of one column of a row of a list field.</summary>
    internal static IEnumerable<string> ColumnTexts(PrefabFieldDef field, PrefabRow row, string column)
    {
        var at = 0;
        foreach (var c in field.Columns)
        {
            if (c.Id == column)
                return row.Texts.Skip(at).Take(c.Width);
            at += c.Width;
        }
        return [];
    }

    /// <summary>The texts of a line with each made nickname of a member in place of the nickname that the record holds.
    /// So a member that keeps its own nickname, for example after "Save as prefab", gets the lines that name
    /// it.</summary>
    private static IReadOnlyList<string> Remap(IReadOnlyList<string> texts, IReadOnlyDictionary<string, string> patterns,
        IReadOnlyDictionary<string, string> names)
    {
        var result = texts.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            foreach (var (role, pattern) in patterns)
            {
                if (pattern.Length > 0 && result[i].Equals(pattern, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(names[role], pattern, StringComparison.Ordinal))
                {
                    result[i] = names[role];
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>Checks the lines of a compiled section with the schema, as a command does.</summary>
    /// <exception cref="EditException">A line is not valid. The message names the section and the line.</exception>
    private static void CheckLines(PrefabSectionDef section, IReadOnlyList<RawEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (PrefabValues.LineProblem(section.Type, entry.Key, entry.Values) is { } problem)
                throw new EditException(MissionErrors.TemplateLineError(section.Role, entry.ToString(), problem));
        }
    }

    /// <summary>
    /// The hash of compiled lines (<c>lines_hash</c> of section 5.5 of the M6b design): SHA-256 of the engine form of
    /// each section in order, 16 hex digits. The sections of W of the members give the same hash when their lines are
    /// the lines of the compile.
    /// </summary>
    public static string LinesHash(IEnumerable<Section> sections)
    {
        var text = new StringBuilder();
        foreach (var section in sections)
            text.Append(section.Name.ToLowerInvariant()).Append(':').Append(SectionHash.Of(section)).Append('\n');
        return PrefabTemplate.Hash(text.ToString());
    }

    /// <summary>The sections of compiled lines as the merge reads them, for <see cref="LinesHash"/>.</summary>
    /// <exception cref="EditException">A line does not read back as its values.</exception>
    public static IReadOnlyList<Section> Sections(IEnumerable<CompiledSection> compiled) =>
        compiled.Select(c =>
        {
            var section = new Section(c.Type);
            foreach (var entry in c.Entries)
                section.Add(MissionValues.Entry(section, entry.Key, entry.Values, null, binary: false));
            return section;
        }).ToList();
}

/// <summary>The checks of the values of prefab fields and of template lines (sections 5.3 and 5.4 of the M6b
/// design).</summary>
public static class PrefabValues
{
    /// <summary>Why the rows of a field are not a valid value of the field, or null.</summary>
    public static string? ValueProblem(PrefabFieldDef field, IReadOnlyList<PrefabRow> rows)
    {
        switch (field.Kind)
        {
            case PrefabFieldKind.Value when rows.Count == 0 && !field.Optional:
                return $"{field.Label} needs a value.";
            case PrefabFieldKind.Value or PrefabFieldKind.Condition when rows.Count > 1:
                return $"{field.Label} takes one value.";
            case PrefabFieldKind.Condition when rows.Count == 0:
                return $"{field.Label} needs a condition.";
        }
        return rows.Select(r => RowProblem(field, r)).FirstOrDefault(p => p != null);
    }

    /// <summary>Why a row is not a valid row of a field, or null.</summary>
    public static string? RowProblem(PrefabFieldDef field, PrefabRow row)
    {
        var schema = MissionSchema.Retail;
        switch (field.Kind)
        {
            case PrefabFieldKind.Lines when schema.Directive(row.Key ?? "") is { } order:
                return field.Keys.Count > 0 && !field.Keys.Contains(order.Name, StringComparer.OrdinalIgnoreCase)
                    ? $"{field.Label} takes only these lines: {string.Join(", ", field.Keys)}."
                    : LineProblem("ObjList", order.Name, row.Texts);
            case PrefabFieldKind.Condition or PrefabFieldKind.Lines:
            {
                var key = row.Key ?? "";
                var name = schema.Name(key);
                var condition = field.Kind == PrefabFieldKind.Condition;
                if (name == null)
                    return $"'{key}' is not {(condition ? "a condition" : "an action")} of the game.";
                if (name.Kind != (condition ? MissionNameKind.Condition : MissionNameKind.Action))
                    return condition ? $"{name.Name} is an action. {field.Label} takes a condition."
                        : $"{name.Name} is a condition. {field.Label} takes actions.";
                if (field.Keys.Count > 0 && !field.Keys.Contains(name.Name, StringComparer.OrdinalIgnoreCase))
                    return $"{field.Label} takes only these lines: {string.Join(", ", field.Keys)}.";
                return LineProblem("Trigger", name.Name, row.Texts);
            }
            case PrefabFieldKind.List:
            {
                var width = field.Columns.Sum(c => c.Width);
                if (row.Key != null || row.Texts.Count != width)
                    return $"A row of {field.Label} has {width} values: {string.Join(", ", field.Columns.Select(c => c.Label))}.";
                var at = 0;
                foreach (var column in field.Columns)
                {
                    if (TextsProblem(column.Label, column.Kind, null, row.Texts.Skip(at).Take(column.Width).ToList()) is { } p)
                        return p;
                    at += column.Width;
                }
                return null;
            }
            case PrefabFieldKind.Triggers:
                return row.Key != null || row.Texts.Count != 1
                    ? $"A row of {field.Label} is the nickname of one trigger."
                    : TextsProblem(field.Label, MissionValueKind.Trigger, null, row.Texts);
            default:
                return row.Key != null
                    ? $"{field.Label} takes a value, not a line."
                    : TextsProblem(field.Label, field.ValueKind, field.Tokens, row.Texts);
        }
    }

    /// <summary>Why the texts of one value of a kind are not valid, or null.</summary>
    private static string? TextsProblem(string label, MissionValueKind kind, string? tokens, IReadOnlyList<string> texts)
    {
        var width = PrefabRow.WidthOf(kind);
        if (texts.Count != width)
            return width == 1 ? $"{label} takes one value." : $"{label} takes {width} numbers.";
        if (texts.Any(t => t.Length == 0))
            return $"{label} needs a value.";
        var spec = new MissionValueSpec(0, label, label, kind) { Width = width, Tokens = tokens };
        if (width > 1 && texts.Any(t => !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return $"{label} takes {width} numbers.";
        var value = width > 1
            ? MissionValue.Of(texts.Select(t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray())
            : MissionValue.Of(texts[0]);
        try
        {
            MissionValues.Convert(MissionSchema.Retail, spec, value, []);
            return null;
        }
        catch (EditException e)
        {
            return e.Error.Message;
        }
    }

    /// <summary>
    /// Why a line of a section type is not valid, or null: the section type does not read the key, a value has the wrong
    /// kind, a value that a later value needs is empty, or a value cannot stand in a line. Values after the values of the
    /// schema stay as they are.
    /// </summary>
    public static string? LineProblem(string sectionType, string key, IReadOnlyList<string> texts)
    {
        var schema = MissionSchema.Retail;
        try
        {
            var (_, specs) = MissionValues.Key(schema, sectionType, LinePlace.Section, key);
            foreach (var text in texts)
                MissionValues.CheckText(text);
            var last = texts.Count - 1;
            for (var i = 0; i < last; i++)
            {
                if (texts[i].Length == 0)
                    return $"{specs.FirstOrDefault(s => s.Covers(i))?.Label ?? $"Value {i + 1}"} needs a value, because a " +
                           "later value is set.";
            }
            foreach (var spec in specs.OrderBy(s => s.Index))
            {
                if (spec.Index >= texts.Count)
                {
                    if (!spec.Optional && spec.Default == null && spec.NoneToken == null)
                        return $"{spec.Label} needs a value.";
                    break;
                }
                var end = spec.Rest ? texts.Count : Math.Min(texts.Count, spec.Index + spec.Width);
                var part = texts.Skip(spec.Index).Take(end - spec.Index).ToList();
                MissionValue value;
                if (spec.Width > 1)
                {
                    if (part.Count != spec.Width ||
                        part.Any(t => !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                        return $"{spec.Label} takes {spec.Width} numbers.";
                    value = MissionValue.Of(part.Select(t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray());
                }
                else
                    value = spec.Rest ? MissionValue.List(part.Select(t => MissionValue.Of(t))) : MissionValue.Of(part[0]);
                MissionValues.Convert(schema, spec, value, []);
            }
            return null;
        }
        catch (EditException e)
        {
            return e.Error.Message;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The matched prefab instances in the draft: the write rules of their fields (section 5.8 of the M6b design)
/// and "Save as prefab" (section 5.9).</summary>
internal sealed partial class MissionDraft
{
    /// <summary>The matched instance of a reference in W now.</summary>
    /// <exception cref="EditException"><c>InstanceNotFound</c>.</exception>
    public (EditDocument Script, MissionPrefabInstance Instance) Matched(InstanceRef reference)
    {
        var document = Script(reference.Script);
        var instances = Editor.ModelOf(document, document.State).Prefabs?.Instances.Where(i => i.Linked == null).ToList() ?? [];
        return instances.FirstOrDefault(i => i.Id.Equals(reference.Id.Trim(), StringComparison.OrdinalIgnoreCase)) is { } instance
            ? (document, instance)
            : throw Refuse(MissionErrors.InstanceNotFoundError(document.Path, reference.Id,
                ItemRefs.Hint(reference.Id, instances.Select(i => i.Id))) with { Target = reference.ToString() });
    }

    /// <summary>
    /// Writes a field of a matched instance by its write rule (section 5.8 of the M6b design): a value of an action, the
    /// condition of a role trigger, a value of a key of the core section, or the lines of a list. A field without a
    /// rule refuses with <c>FieldNotWritable</c>. The rows have the text form of <see cref="PrefabRow"/>.
    /// </summary>
    public void WriteMatchedField(InstanceRef reference, string fieldId, IReadOnlyList<string> rows)
    {
        var (document, instance) = Matched(reference);
        var definition = instance.Definition;
        var field = definition.Fields.FirstOrDefault(f => f.Id == fieldId.Trim()) ??
                    throw Refuse(MissionErrors.FieldNotFoundError(definition.Name, fieldId, definition.Fields.Select(f => f.Id)));
        var from = field.From;
        switch (field.Write)
        {
            case PrefabWrite.Condition:
            {
                var triggers = instance.Role(from.Role);
                var lines = rows.Select(r => Line(field, r, condition: true)).ToList();
                if (field.List == null ? lines.Count != 1 || triggers.Count == 0 : lines.Count != triggers.Count)
                    throw Refuse(MissionErrors.FieldValueError(field.Label, field.List == null
                        ? "The field takes one condition."
                        : $"The field has one condition for each trigger of the role, so it takes {triggers.Count} rows."));
                for (var i = 0; i < lines.Count; i++)
                    Inverses.AddRange(RunInner(new SetCondition(new TriggerRef(document.Path, triggers[i].Nickname),
                        LineSpec.Raw(lines[i].Key!, [.. lines[i].Texts]))));
                break;
            }
            case PrefabWrite.Value when field.List == null:
                WriteValue(document, instance, field, rows);
                break;
            case PrefabWrite.Value or PrefabWrite.Lines:
            {
                var (section, matches) = LinesOf(instance, field);
                var lines = rows.Select(r => Line(field, r, condition: false)).ToList();
                foreach (var line in lines.Where(l => !matches(l.Key!)))
                    throw Refuse(MissionErrors.FieldValueError(field.Label,
                        $"{line.Key} is not a line of {field.Label}. Pick a line that the field holds."));
                ReplaceLines(document, section.Index, matches, lines);
                break;
            }
            case PrefabWrite.Key:
                WriteKey(document, instance, field, rows);
                break;
            default:
                throw Refuse(MissionErrors.FieldNotWritableError(field.Label));
        }
        Changes.Add(reference.ToString());
    }

    /// <summary>A row of a field as a line with its key, after the checks of the line.</summary>
    private PrefabRow Line(PrefabField field, string text, bool condition)
    {
        var row = PrefabRow.Parse(PrefabFieldKind.Lines, text);
        var name = Schema.Name(row.Key ?? "");
        var problem = row.Key is not { Length: > 0 } ? $"'{text}' is not a line. Write the key, '=' and the values."
            : condition && name?.Kind != MissionNameKind.Condition ? $"'{row.Key}' is not a condition of the game."
            : PrefabValues.LineProblem(field.From.Keys.Count > 0 ? "ObjList" : "Trigger", row.Key, row.Texts);
        if (problem != null)
            throw Refuse(MissionErrors.FieldValueError(field.Label, problem));
        return row with { Key = name?.Name ?? Schema.Directive(row.Key!)?.Name ?? row.Key };
    }

    /// <summary>The section of the lines of a list field and the keys that it holds: the one trigger of the role, or the
    /// core section for keys of an order list.</summary>
    /// <exception cref="EditException"><c>FieldNotWritable</c> when the role has more than one trigger.</exception>
    private (MissionSection Section, Func<string, bool> Matches) LinesOf(MissionPrefabInstance instance, PrefabField field)
    {
        var from = field.From;
        if (from.Keys.Count > 0)
            return instance.CoreSection is { } core
                ? (core, key => from.Keys.Any(k => MissionIndex.Is(key, k)))
                : throw Refuse(MissionErrors.FieldNotWritableError(field.Label));
        var triggers = instance.Role(from.Role);
        if (triggers.Count != 1)
            throw Refuse(MissionErrors.FieldNotWritableError(field.Label) with
            {
                Message = $"{field.Label} holds the lines of {triggers.Count} triggers. Change each line in its trigger.",
            });
        Func<string, bool> matches = from.AllActions ? TriggerRules.IsAction
            : from.Group is { } group ? key => Schema.Name(key) is { Kind: MissionNameKind.Action } name && name.Group == group
            : key => field.LineKeys(Schema).Any(k => MissionIndex.Is(key, k));
        return (triggers[0].Section, matches);
    }

    /// <summary>
    /// Puts the lines of a list field into a section. Row n takes the place of the line n of the keys that the field
    /// holds, so a row that did not change keeps its line (section 8.5 of the M6b design). More rows go after the last
    /// such line, or to the end of the section. Fewer rows remove the last lines.
    /// </summary>
    private void ReplaceLines(EditDocument document, int index, Func<string, bool> matches, IReadOnlyList<PrefabRow> rows)
    {
        var section = document.Working[index];
        var entries = section.Select(e => new RawEntry(e.Name, MissionValues.TextsOf(e))).ToList();
        var nickname = MissionIndex.Is(section.Name, "Trigger") ? TriggerRules.NicknameLine(section) : -1;
        var condition = MissionIndex.Is(section.Name, "Trigger") ? TriggerRules.EffectiveCondition(section.ToList()) : -1;
        var lines = Enumerable.Range(0, entries.Count).Where(i => i > nickname && i != condition && matches(entries[i].Key)).ToList();
        var same = Math.Min(rows.Count, lines.Count);
        for (var i = 0; i < same; i++)
            entries[lines[i]] = new RawEntry(rows[i].Key!, rows[i].Texts);
        if (rows.Count > lines.Count)
            entries.InsertRange(lines.Count > 0 ? lines[^1] + 1 : entries.Count,
                rows.Skip(same).Select(r => new RawEntry(r.Key!, r.Texts)));
        else
        {
            foreach (var row in lines.Skip(same).Reverse())
                entries.RemoveAt(row);
        }
        Inverses.AddRange(RunInner(new ReplaceSectionBody(ItemOf(document, index), entries)));
    }

    /// <summary>Writes one value of the first action line of the keys of a field in the role trigger. A trigger without
    /// such a line gets one with the value, and the defaults of the schema in the gaps.</summary>
    private void WriteValue(EditDocument document, MissionPrefabInstance instance, PrefabField field, IReadOnlyList<string> rows)
    {
        var from = field.From;
        var triggers = instance.Role(from.Role);
        if (triggers.Count == 0)
            throw Refuse(MissionErrors.FieldNotWritableError(field.Label) with
            {
                Message = $"The instance has no trigger for {field.Label}. Add the trigger in the graph first.",
            });
        if (rows.Count != 1)
            throw Refuse(MissionErrors.FieldValueError(field.Label, "The field takes one value."));
        var texts = PrefabRow.Split(rows[0]);
        var trigger = triggers.FirstOrDefault(t => t.Actions.Any(a => from.Actions.Any(k => MissionIndex.Is(a.Key, k)))) ?? triggers[0];
        var line = trigger.Actions.FirstOrDefault(a => from.Actions.Any(k => MissionIndex.Is(a.Key, k)));
        var name = Schema.Name(line?.Key ?? from.Actions[0]);
        var spec = name?.ValueAt(from.Value) ?? throw Refuse(MissionErrors.FieldNotWritableError(field.Label));
        var value = ValueOf(spec, texts, field.Label);
        Inverses.AddRange(line != null
            ? RunInner(new SetActionValue(RowOf(document, line.Section.Index, line.Entry), spec.Name, value))
            : RunInner(new AddAction(new TriggerRef(document.Path, trigger.Nickname), new LineSpec(name!.Name)
            {
                Values = new Dictionary<string, MissionValue>(StringComparer.OrdinalIgnoreCase) { [spec.Name] = value },
            })));
    }

    /// <summary>Writes one value of the first line of a key of the core section. A section without the key gets the line,
    /// when the value is the first value of the line.</summary>
    private void WriteKey(EditDocument document, MissionPrefabInstance instance, PrefabField field, IReadOnlyList<string> rows)
    {
        var from = field.From;
        if (instance.CoreSection is not { } core || rows.Count != 1)
            throw Refuse(MissionErrors.FieldValueError(field.Label, "The field takes one value."));
        var texts = PrefabRow.Split(rows[0]);
        if (texts.Count != 1 || texts[0].Length == 0)
            throw Refuse(MissionErrors.FieldValueError(field.Label, "The field takes one value."));
        var at = document.IndexOf(core.Ref.Id);
        var section = document.Working[at];
        var row = section.ToList().FindIndex(e => MissionIndex.Is(e.Name, from.Key!));
        if (row >= 0)
        {
            if (row == section.ToList().FindIndex(e => MissionIndex.Is(e.Name, "nickname")) && from.Value == 0)
                throw Refuse(MissionErrors.KeyLineError(from.Key!));
            Inverses.AddRange(RunInner(new SetLineValue(RowOf(document, at, row), texts[0]) { Index = from.Value }));
        }
        else if (from.Value == 0)
            Inverses.AddRange(RunInner(new SetItemKey(ItemOf(document, at), from.Key!, [LineSpec.Raw(from.Key!, texts[0])])));
        else
            throw Refuse(MissionErrors.FieldNotWritableError(field.Label) with
            {
                Message = $"The section has no {from.Key} line, so {field.Label} cannot change. Add the line in the INI block first.",
            });
    }

    /// <summary>The value of a spec from its texts: one text, or the numbers of a position or a rotation.</summary>
    private MissionValue ValueOf(MissionValueSpec spec, IReadOnlyList<string> texts, string label)
    {
        if (spec.Width == 1)
            return texts.Count == 1 ? MissionValue.Of(texts[0]) : throw Refuse(MissionErrors.FieldValueError(label, "The field takes one value."));
        var numbers = new double[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            if (!double.TryParse(texts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                throw Refuse(MissionErrors.FieldValueError(label, $"The field takes {spec.Width} numbers."));
        }
        return MissionValue.Of(numbers);
    }

    // ---- Save as prefab ----

    /// <summary>
    /// Makes a prefab asset of the active mod from a matched instance ("Save as prefab", section 5.9 of the M6b design):
    /// the template sections are the member sections, and each value that a writable field reads becomes a slot with the
    /// value of the instance as its default. The instance then becomes a linked instance of the new asset with no
    /// override, and its members keep their nicknames.
    /// </summary>
    public void SaveAsPrefab(InstanceRef reference, string id, string? name)
    {
        var (document, instance) = Matched(reference);
        var definition = instance.Definition;
        var members = instance.Claimed.Select(t => t.Section).Append(instance.CoreSection).OfType<MissionSection>()
            .Distinct().OrderBy(s => s.Index).ToList();
        var roles = RolesOf(instance, members);
        var lines = members.ToDictionary(s => s, s => s.Section.Select(e => (Key: e.Name, Texts: MissionValues.TextsOf(e).ToList())).ToList());
        var names = members.Select(NicknameOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fields = new List<PrefabFieldDef>();
        foreach (var field in definition.Fields.Where(f => f.Write != PrefabWrite.None))
        {
            if (Slot(instance, field, lines, names) is { } made)
                fields.Add(made);
        }
        // Each nickname and each value that names a member get the made nickname of the member.
        var patterns = members.ToDictionary(s => NicknameOf(s)!, s => $"{{{PrefabSlot.Instance}}}_{roles[s]}",
            StringComparer.OrdinalIgnoreCase);
        var sections = members.Select(s => new PrefabSectionDef(s.Type, roles[s], lines[s].Select(l => new PrefabLine(l.Key,
            string.Join(", ", l.Texts.Select(t => patterns.GetValueOrDefault(t) ?? t)))).ToList())).ToList();
        var template = new PrefabTemplate(id, name?.Trim() is { Length: > 0 } given ? given : definition.Name, definition.Kind)
        {
            VariantOf = PrefabRef.BuiltinOf(definition.Id).RecordText,
            Help = definition.Help.WhatItIs,
            Writes = definition.Help.WhatItWrites,
            Fields = fields,
            Sections = sections,
        };
        var text = template.Write();
        var read = PrefabTemplate.Read(text, out var problems);
        if (read == null || problems.Count > 0)
            throw Refuse(MissionErrors.PrefabProblemsError(template.Name, problems.Count, problems.FirstOrDefault()?.ToString() ?? ""));
        SetPrefabText(id, text);
        Inverse(new RemovePrefabAsset(PrefabRef.ModOf(id)));
        var record = new InstanceRecord(RecordId(document, instance), PrefabRef.ModOf(id).RecordText)
        {
            Members = members.Select(s => new InstanceMember(roles[s], NicknameOf(s)!)).ToList(),
        };
        Compile(document, record, read);
        Changes.Add(new InstanceRef(document.Path, record.Id).ToString());
        Select = PrefabRef.ModOf(id).ToString();
    }

    /// <summary>The role of each member section: <c>core</c> for the core, the role of the matcher for a member, with a
    /// number when a role has more than one trigger.</summary>
    private static Dictionary<MissionSection, string> RolesOf(MissionPrefabInstance instance, IReadOnlyList<MissionSection> members)
    {
        var roles = new Dictionary<MissionSection, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in members)
        {
            var role = section == instance.CoreSection ? "core"
                : instance.Definition.Members.FirstOrDefault(m => instance.Role(m.Role).Any(t => t.Section == section))?.Role ?? "member";
            var unique = role;
            for (var n = 2; !used.Add(unique); n++)
                unique = $"{role}_{n.ToString(CultureInfo.InvariantCulture)}";
            roles[section] = unique;
        }
        return roles;
    }

    /// <summary>The nickname of a member section: the first nickname line of a trigger, or the key of another
    /// section.</summary>
    private static string? NicknameOf(MissionSection section) =>
        section.Kind == MissionSectionKind.Trigger ? TriggerRules.Nickname(section.Section) : section.Nickname;

    /// <summary>A made id of the record of a saved instance: the name of the instance when it is a valid name that no
    /// record has, else the prefab id and a number.</summary>
    private string RecordId(EditDocument script, MissionPrefabInstance instance)
    {
        var graph = Graph(script);
        if (MissionNames.FormOk(instance.Name, MissionNames.MaxName) && graph.Instance(instance.Name) == null)
            return instance.Name;
        for (var n = 1;; n++)
        {
            var id = $"{instance.Definition.Id.ToLowerInvariant()}_{n.ToString(CultureInfo.InvariantCulture)}";
            if (graph.Instance(id) == null)
                return id;
        }
    }

    /// <summary>
    /// The template field of a writable field of a matched instance, and its slot in the lines of the members: the value
    /// of an action or of a key, the condition of the role trigger, or the lines of a list. Null when the instance has no
    /// value for the field, when the field reads the lines of more than one trigger, or when its value names a member:
    /// such a value stays a line of the template, so a new instance names its own member.
    /// </summary>
    /// <param name="names">The nicknames of the members.</param>
    private PrefabFieldDef? Slot(MissionPrefabInstance instance, PrefabField field,
        Dictionary<MissionSection, List<(string Key, List<string> Texts)>> lines, IReadOnlySet<string> names)
    {
        var from = field.From;
        var slot = $"{{{field.Id}}}";
        var made = new PrefabFieldDef(field.Id, field.Label, PrefabFieldKind.Value) { Help = field.Help };
        switch (field.Write)
        {
            case PrefabWrite.Condition when field.List == null && instance.Role(from.Role) is [var trigger] &&
                                            lines.TryGetValue(trigger.Section, out var own) && trigger.Condition is { } condition:
                if (own[condition.Entry].Texts.Any(names.Contains))
                    return null;
                own[condition.Entry] = (slot, []);
                return made with
                {
                    Kind = PrefabFieldKind.Condition,
                    Default = [PrefabRow.Line(condition.Key, [.. MissionValues.TextsOf(condition.Line)])],
                };
            case PrefabWrite.Value when field.List == null && instance.Role(from.Role) is [var trigger] &&
                                        lines.TryGetValue(trigger.Section, out var own):
            {
                var line = trigger.Actions.FirstOrDefault(a => from.Actions.Any(k => MissionIndex.Is(a.Key, k)));
                if (line?.Name?.ValueAt(from.Value) is not { } spec || spec.Index + spec.Width > line.Count || spec.Rest)
                    return null;
                var texts = own[line.Entry].Texts;
                var value = texts.Skip(spec.Index).Take(spec.Width).ToList();
                if (value.Any(names.Contains))
                    return null;
                texts.RemoveRange(spec.Index, spec.Width);
                texts.Insert(spec.Index, slot);
                return made with { ValueKind = spec.Kind, Tokens = spec.Tokens, Default = [new PrefabRow(null, value)] };
            }
            case PrefabWrite.Key when instance.CoreSection is { } core && lines.TryGetValue(core, out var own):
            {
                var row = own.FindIndex(l => MissionIndex.Is(l.Key, from.Key!));
                var nickname = own.FindIndex(l => MissionIndex.Is(l.Key, "nickname"));
                var spec = (Schema.Section(core.Type)?.Key(from.Key!)?.ValueSpecs ?? Schema.Directive(from.Key!)?.Values)
                    ?.FirstOrDefault(s => s.Covers(from.Value));
                if (row < 0 || row == nickname || spec == null || from.Value >= own[row].Texts.Count || spec.Width != 1 ||
                    names.Contains(own[row].Texts[from.Value]))
                    return null;
                var value = own[row].Texts[from.Value];
                own[row].Texts[from.Value] = slot;
                return made with { ValueKind = spec.Kind == MissionValueKind.Token && spec.Tokens == null ? MissionValueKind.Text : spec.Kind,
                    Tokens = spec.Tokens, Default = [PrefabRow.Of(value)] };
            }
            case PrefabWrite.Value or PrefabWrite.Lines:
            {
                MissionSection section;
                Func<string, bool> matches;
                try
                {
                    (section, matches) = LinesOf(instance, field);
                }
                catch (EditException)
                {
                    return null;
                }
                if (!lines.TryGetValue(section, out var own))
                    return null;
                var nickname = section.Kind == MissionSectionKind.Trigger ? TriggerRules.NicknameLine(section.Section) : -1;
                var condition = section.Kind == MissionSectionKind.Trigger ? TriggerRules.EffectiveCondition(section.Section.ToList()) : -1;
                var rows = Enumerable.Range(0, own.Count).Where(i => i > nickname && i != condition && matches(own[i].Key)).ToList();
                // The lines of a list field stand together in the template, so only lines that stand together in the
                // script become a field. Else the compile would change their order.
                if (rows.Count == 0 || rows.Any(i => own[i].Texts.Any(names.Contains)) ||
                    rows.Zip(rows.Skip(1)).Any(p => p.Second != p.First + 1))
                    return null;
                var defaults = rows.Select(i => PrefabRow.Line(own[i].Key, [.. own[i].Texts])).ToList();
                foreach (var i in rows.Skip(1).Reverse())
                    own.RemoveAt(i);
                own[rows[0]] = (slot, []);
                // The keys that the field takes: the keys of its order lines, the actions of its group or of its keys,
                // or each action.
                IReadOnlyList<string> keys = from.Keys.Count > 0 ? from.Keys
                    : from.AllActions ? []
                    : from.Group is { } group ? Schema.Actions.Where(a => a.Group == group).Select(a => a.Name).ToList()
                    : field.LineKeys(Schema);
                return made with { Kind = PrefabFieldKind.Lines, Keys = keys, Default = defaults };
            }
            default:
                return null;
        }
    }
}

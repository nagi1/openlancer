using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>Adds a field to the template of a prefab asset of the active mod in prefab mode (section 5.7 of the M6b
/// design). The template file holds it as a [Field] section. Each linked instance compiles again.</summary>
/// <param name="FieldKind">The kind of the field: a value kind of the mission schema, list, triggers, condition or
/// lines. JSON names it <c>field_kind</c>, because <c>kind</c> names the command.</param>
public sealed record AddPrefabField(PrefabRef Prefab, string Id, string Label, string FieldKind) : MissionCommand
{
    /// <summary>The columns of a list field, each as <c>id, kind, label</c>.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The keys that a condition field or a lines field takes, or none for each one.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>The word set of a field of the kind token, or null.</summary>
    public string? Tokens { get; init; }

    public string? Unit { get; init; }

    public bool PerInstance { get; init; }

    public bool Optional { get; init; }

    public string Help { get; init; } = "";

    /// <summary>The default rows in their text form.</summary>
    public IReadOnlyList<string> Default { get; init; } = [];

    /// <summary>The place of the field among the fields, or null for the end.</summary>
    public int? Index { get; init; }

    protected override string Describe() => $"Add field {Label}";

    internal override void Run(MissionDraft d)
    {
        var template = d.Script(Prefab.Script);
        var id = Id.Trim();
        if (!TemplateReader.IsId(id))
            throw d.Refuse(MissionErrors.NameInvalidError(id, 47));
        if (MissionDraft.FieldIds(template).Contains(id))
            throw d.Refuse(MissionErrors.NameTakenError(Prefab.ToString(), "field", id));
        var section = new Section("Field");
        void Add(string key, string value) => section.Add(PrefabMode.Entry(section, key, [value]));
        Add("id", id);
        Add("label", Label.Trim());
        Add("kind", FieldKind.Trim());
        if (Tokens != null)
            Add("tokens", Tokens.Trim());
        foreach (var key in Keys)
            Add("key", key.Trim());
        foreach (var column in Columns)
            Add("column", column.Trim());
        if (Unit != null)
            Add("unit", Unit.Trim());
        if (PerInstance)
            Add("per_instance", "true");
        if (Optional)
            Add("optional", "true");
        if (Help.Trim().Length > 0)
            Add("help", Help.Trim());
        foreach (var row in Default)
            Add("default", row.Trim());
        var fields = Enumerable.Range(0, template.Working.Count)
            .Where(i => template.Working[i].Name.Equals("Field", StringComparison.OrdinalIgnoreCase)).ToList();
        var at = Index is { } index && index >= 0 && index < fields.Count ? fields[index]
            : fields.Count > 0 ? fields[^1] + 1
            : 1;
        d.Insert(template, at, section);
        d.Adds.Add($"{Prefab}#field/{id}");
        d.Inverse(new RemovePrefabField(Prefab, id));
    }
}

/// <summary>Removes a field from the template of a prefab asset in prefab mode. A field that a line of the template
/// uses refuses: make each such line a fixed value first (<see cref="UnbindValue"/>).</summary>
public sealed record RemovePrefabField(PrefabRef Prefab, string Field) : MissionCommand
{
    protected override string Describe() => $"Remove field {Field}";

    internal override void Run(MissionDraft d)
    {
        var (template, index) = d.FieldSection(Prefab, Field);
        var label = d.FieldOf(template, index).Label;
        if (MissionDraft.SlotUses(template, Field.Trim()) is > 0 and var uses)
            throw d.Refuse(MissionErrors.FieldInUseError(label, uses));
        var removed = d.Remove(template, index);
        d.Deletes.Add(new ResultDelete($"{Prefab}#field/{Field.Trim()}", DeleteReason.Target, null));
        d.Inverse(new RestoreSection(new ScriptRef(Prefab.Script), index, MissionDraft.RawSection(removed)));
    }
}

/// <summary>Sets the default rows of a field of the template of a prefab asset in prefab mode. Each linked instance
/// without an override of the field gets the new value when it compiles.</summary>
public sealed record SetPrefabFieldDefault(PrefabRef Prefab, string Field, IReadOnlyList<string> Rows) : MissionCommand
{
    protected override string Describe() => $"Set the default of {Field}";

    internal override void Run(MissionDraft d)
    {
        var (template, index) = d.FieldSection(Prefab, Field);
        var field = d.FieldOf(template, index);
        var rows = d.Rows(field, Rows);
        var section = template.Working[index];
        var entries = section.ToList();
        var at = entries.FindIndex(e => MissionIndex.Is(e.Name, "default"));
        entries.RemoveAll(e => MissionIndex.Is(e.Name, "default"));
        entries.InsertRange(at < 0 ? entries.Count : Math.Min(at, entries.Count),
            rows.Select(r => PrefabMode.Entry(section, "default", [r.Format()])));
        d.SetLines(template, index, entries);
    }
}

/// <summary>
/// Makes a value of a line of a template the slot of a field in prefab mode (section 5.7 of the M6b design): "Field:
/// {label}" in the value choice. Without a value name, the whole line becomes the line of a condition field or of a
/// lines field. Each linked instance compiles again.
/// </summary>
public sealed record BindValue(RowRef Row, string Field) : MissionCommand
{
    /// <summary>The name of the value in the schema, or null for the whole line.</summary>
    public string? Value { get; init; }

    protected override string Describe() => $"Bind to {Field}";

    internal override void Run(MissionDraft d)
    {
        var (template, index, row) = d.Row(Row);
        if (!MissionDraft.IsTemplateDocument(template))
            throw d.Refuse(MissionErrors.NotATemplateError("Only a line of a prefab template has fields."));
        var prefab = PrefabRef.TryParse(template.Path)!;
        var (_, fieldIndex) = d.FieldSection(prefab, Field);
        var field = d.FieldOf(template, fieldIndex);
        var section = template.Working[index];
        var entry = section[row];
        var slot = $"{{{field.Id}}}";
        Entry made;
        if (Value == null)
        {
            if (field.Kind is not (PrefabFieldKind.Condition or PrefabFieldKind.Lines))
                throw d.Refuse(MissionErrors.FieldValueError(field.Label, "Only a condition field or a lines field takes a whole line."));
            made = PrefabMode.Entry(section, slot, []);
        }
        else
        {
            var specs = d.SpecsOf(section.Name, entry.Name);
            var spec = specs.FirstOrDefault(s => s.Name.Equals(Value, StringComparison.OrdinalIgnoreCase)) ??
                       throw d.Refuse(MissionErrors.NoSuchValueError(entry.Name, Value, specs.Select(s => s.Name)));
            if (!FieldFits(field, spec.Kind))
                throw d.Refuse(MissionErrors.FieldValueError(field.Label,
                    $"The field has the kind {field.KindText}, and {spec.Label} needs a value of the kind {PrefabFieldDef.KindName(spec.Kind)}."));
            var texts = MissionValues.TextsOf(entry).ToList();
            while (texts.Count < spec.Index)
                texts.Add(d.Gap(section.Name, entry.Name, texts.Count));
            var end = Math.Min(texts.Count, spec.Index + spec.Width);
            if (spec.Index < texts.Count)
                texts.RemoveRange(spec.Index, end - spec.Index);
            texts.Insert(spec.Index, field.Kind == PrefabFieldKind.List
                ? $"{{{field.Id}.{field.Columns.First(c => Fits(c.Kind, spec.Kind)).Id}}}"
                : slot);
            made = PrefabMode.Entry(section, entry.Name, texts);
        }
        var entries = section.ToList();
        entries[row] = made;
        if (!d.SetLines(template, index, entries, inverse: false))
            return;
        var reference = MissionDraft.RowOf(template, index, row);
        d.Changes.Add(reference.Reference);
        d.Inverse(new SetLine(reference, MissionDraft.RawLine(entry)));
    }

    /// <summary>True when a field can give a value of a kind of the schema. The value choice of prefab mode offers only
    /// such fields.</summary>
    public static bool FieldFits(PrefabFieldDef field, MissionValueKind wanted) => field.Kind switch
    {
        PrefabFieldKind.Value => Fits(field.ValueKind, wanted),
        PrefabFieldKind.Triggers => wanted == MissionValueKind.Trigger,
        PrefabFieldKind.List => field.Columns.Any(c => Fits(c.Kind, wanted)),
        _ => false,
    };

    /// <summary>True when a value of a kind can stand where the schema wants another kind: the same kind, a ship or a
    /// formation for a group, a name of the label rule for an object, and a whole number for a number.</summary>
    internal static bool Fits(MissionValueKind given, MissionValueKind wanted) =>
        given == wanted ||
        wanted == MissionValueKind.Group && given is MissionValueKind.Ship or MissionValueKind.Formation ||
        wanted == MissionValueKind.Object && given is MissionValueKind.Ship or MissionValueKind.Formation or
            MissionValueKind.Group or MissionValueKind.Solar or MissionValueKind.Loot ||
        wanted is MissionValueKind.Number or MissionValueKind.Attitude && given is MissionValueKind.Number or
            MissionValueKind.WholeNumber;
}

/// <summary>
/// Makes the slot of a field in a line of a template a fixed value in prefab mode: "Fixed value" in the value choice.
/// Without texts, the value takes the default of the field. A line of a condition field or of a lines field takes the
/// default rows of the field. Each linked instance compiles again.
/// </summary>
public sealed record UnbindValue(RowRef Row) : MissionCommand
{
    /// <summary>The name of the value in the schema, or null for the first slot of the line, or for a line of a
    /// field.</summary>
    public string? Value { get; init; }

    /// <summary>The fixed value texts, or null for the default of the field.</summary>
    public IReadOnlyList<string>? Texts { get; init; }

    protected override string Describe() => "Fixed value";

    internal override void Run(MissionDraft d)
    {
        var (template, index, row) = d.Row(Row);
        if (!MissionDraft.IsTemplateDocument(template))
            throw d.Refuse(MissionErrors.NotATemplateError("Only a line of a prefab template has fields."));
        var prefab = PrefabRef.TryParse(template.Path)!;
        var section = template.Working[index];
        var entry = section[row];
        var entries = section.ToList();
        if (PrefabSlot.KeyField(entry.Name) is { } keyField)
        {
            var (_, fieldIndex) = d.FieldSection(prefab, keyField);
            IReadOnlyList<PrefabRow> rows = Texts != null
                ? [PrefabRow.Parse(PrefabFieldKind.Lines, string.Join(", ", Texts))]
                : d.FieldOf(template, fieldIndex).Default;
            entries.RemoveAt(row);
            entries.InsertRange(row, rows.Select(r => PrefabMode.Entry(section, r.Key ?? "", r.Texts)));
        }
        else
        {
            var texts = MissionValues.TextsOf(entry).ToList();
            var at = Value != null
                ? d.SpecsOf(section.Name, entry.Name).FirstOrDefault(s => s.Name.Equals(Value, StringComparison.OrdinalIgnoreCase))?.Index ?? -1
                : texts.FindIndex(t => PrefabSlot.Of(t).Any(s => s.Field != null));
            if (at < 0 || at >= texts.Count || PrefabSlot.Of(texts[at]).FirstOrDefault(s => s.Field != null) is not { Field: { } field } slot)
                throw d.Refuse(MissionErrors.NotATemplateError($"Line {row} has no field at value '{Value}'."));
            var (_, fieldIndex) = d.FieldSection(prefab, field);
            var definition = d.FieldOf(template, fieldIndex);
            IReadOnlyList<string> value = Texts ?? (definition.Kind == PrefabFieldKind.List
                ? definition.Default.Select(r => PrefabCompiler.ColumnTexts(definition, r, slot.Column ?? definition.Columns[0].Id).ToList())
                    .FirstOrDefault() ?? []
                : definition.Default.FirstOrDefault()?.Texts ?? []);
            if (value.Count == 0)
                throw d.Refuse(MissionErrors.FieldValueError(definition.Label, "The field has no default. Give a fixed value."));
            texts.RemoveAt(at);
            texts.InsertRange(at, value);
            entries[row] = PrefabMode.Entry(section, entry.Name, texts);
        }
        if (!d.SetLines(template, index, entries, inverse: false))
            return;
        d.Changes.Add(MissionDraft.RowOf(template, index, row).Reference);
        d.Inverse(new ReplaceSectionBody(MissionDraft.ItemOf(template, index), MissionDraft.RawOf(section)));
    }
}

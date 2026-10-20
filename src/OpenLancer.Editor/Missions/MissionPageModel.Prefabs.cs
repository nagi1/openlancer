using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Editor.Ui;
using static OpenLancer.Editor.Missions.MissionCommandRefs;

namespace OpenLancer.Editor.Missions;

/// <summary>A link that opens a prefab in prefab mode (section 8.6 of the M6b design).</summary>
internal sealed record PrefabModeLink(PrefabRef Prefab);

/// <summary>A link that shows the asset of a prefab in the Assets panel: "Select prefab asset".</summary>
internal sealed record PrefabAssetLink(PrefabRef Prefab);

/// <summary>A link that opens the dialog "Save as prefab" of a matched instance (section 5.9 of the M6b
/// design).</summary>
/// <param name="Name">The name that the dialog proposes.</param>
/// <param name="Id">The file name that the dialog proposes.</param>
internal sealed record SavePrefabLink(InstanceRef Instance, string Name, string Id);

/// <summary>
/// The pages of the behavior prefabs (sections 5.6, 5.8, 5.9 and 8.6 of the M6b design): a linked instance with its
/// fields, overrides and removed members, a matched instance with the fields that it can write, the Prefab page of
/// prefab mode, and the value choice of a line of a template.
/// </summary>
internal sealed partial record MissionPageModel
{
    // The texts of section 8.11 of the M6b design.
    public const string RevertTip = "Use the value of the prefab.";
    public const string ApplyTip = "Make this value the value of the prefab. Each instance without an override of this field gets it.";
    public const string VariantTip = "Copy the prefab into the active mod, with this value.";
    public const string UnpackTip = "Keep the triggers and remove the link to the prefab.";
    public const string MakeVariantTip = "Copy this prefab into the active mod.";
    public const string FixedValueTip = "Each instance gets this value.";
    public const string FieldValueTip = "Each instance can set this value.";

    // Texts that the design does not give. They follow the same form.
    public const string RemovedTip = "You deleted this trigger of the instance. The prefab still has it. Revert brings it back.";
    public const string RemovedText = "Removed";
    public const string SaveAsPrefabTip = "Make a prefab asset from the triggers of this instance. The instance then uses it.";
    public const string OpenPrefabTip = "Open the prefab in prefab mode. A change there changes each instance.";
    public const string SelectAssetTip = "Show the asset of the prefab in the Assets panel.";
    public const string PerInstanceText = "Per instance";
    public const string PerInstanceTip = "Each instance sets this value. It never shows as an override.";

    /// <summary>The info text of an override (section 8.11 of the M6b design).</summary>
    public static readonly HelpText OverrideHelp = new("Override",
        "A field of this instance that differs from the prefab. Only this instance has the value.",
        "The lines of this instance. The prefab does not change until you apply the value.");

    /// <summary>The info text of a behavior prefab asset.</summary>
    public static readonly HelpText AssetHelp = new("Behavior prefab asset",
        "A group of triggers with named fields that you can use again. Each instance in a mission uses it.",
        "The [Trigger] sections of each instance. The prefab itself is an editor file. The game does not read it.");

    /// <summary>The info text of prefab mode.</summary>
    public static readonly HelpText PrefabModeHelp = new("Prefab mode",
        "The edit view of one prefab. A change here changes each instance of the prefab in the active mod.",
        "The prefab file, and the [Trigger] sections of each instance.");

    /// <summary>The tooltip of the violet dot of a collapsed linked instance with overrides.</summary>
    public static string OverridesDotTip(int count) =>
        count == 1 ? "This instance has 1 override." : $"This instance has {Count(count)} overrides.";

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The text of a line of a template in prefab mode, with the label of the field of each slot in brackets (section 8.6
    /// of the M6b design). For example, <c>Spawn formation: {formation}</c> shows as "Spawn formation: [Enemy
    /// formation]". The instance id and a port show as "[Instance]" and "[Port: {label}]".
    /// </summary>
    public static string SlotText(string text, PrefabTemplate template)
    {
        var slots = PrefabSlot.Of(text);
        if (slots.Count == 0)
            return text;
        var result = new System.Text.StringBuilder();
        var at = 0;
        foreach (var slot in slots)
        {
            result.Append(text, at, slot.Start - at);
            var label = slot.IsInstance ? "Instance"
                : slot.Port is { } port ? $"Port: {template.Port(port)?.Label ?? port}"
                : template.Field(slot.Field ?? "") is { } field
                    ? slot.Column is { } column ? $"{field.Label}: {field.Columns.FirstOrDefault(c => c.Id == column)?.Label ?? column}" : field.Label
                    : slot.Name;
            result.Append('[').Append(label).Append(']');
            at = slot.Start + slot.Length;
        }

        result.Append(text, at, text.Length - at);
        return result.ToString();
    }

    /// <summary>The page of a prefab instance: a linked instance, or a matched instance.</summary>
    private static MissionPageModel PrefabInstance(PageContext context, MissionPrefabInstance instance) =>
        instance.Linked is { } linked ? LinkedInstancePage(context, instance, linked) : MatchedInstancePage(context, instance);

    // ---- Linked instances (section 5.6) ----

    /// <summary>
    /// The page of a linked instance (sections 5.6 and 8.6 of the M6b design): each field of the prefab with the widget
    /// of its kind, the overrides in bold with the violet bar and their menu, the members and the removed members, and
    /// the page menu "Overrides ({n})". A field edit gives <see cref="SetPrefabField"/>, so the instance compiles.
    /// </summary>
    private static MissionPageModel LinkedInstancePage(PageContext context, MissionPrefabInstance instance, LinkedInstance linked)
    {
        var analysis = context.Analysis;
        var script = analysis.Script.Path;
        var record = linked.Record;
        var reference = new InstanceRef(script, record.Id);
        var prefab = PrefabRef.TryParse(record.Prefab);
        var name = linked.Asset?.Name ?? prefab?.Id ?? record.Prefab;
        var readOnly = context.ScriptReadOnly(script) ?? (linked.Missing ? PrefabLinks.MissingMessage(record.Id, name) : null);
        var editable = readOnly == null;
        var canApply = linked.Asset is { Active: true };
        var unsaved = context.UnsavedOverrides(reference);

        var fields = new List<PageField>();
        foreach (var value in linked.Fields)
        {
            var field = value.Field;
            var view = PrefabValueField(context, field, value.Rows, readOnly,
                rows => One($"Set {field.Label}", new SetPrefabField(reference, field.Id, rows)));
            fields.Add(view with
            {
                Help = field.PerInstance ? Join(view.Help, PerInstanceTip) : view.Help,
                Override = value.Override,
                Changed = value.Override && unsaved.Fields.Contains(field.Id),
                Actions = value.Override && editable
                    ? OverrideMenu(context, reference, prefab, name, canApply, field.Id, field.Label)
                    : [],
            });
        }

        var members = new List<PageField>();
        // A removed member shows as an override after the members (decision D676 c).
        foreach (var member in record.Members.Where(m => !record.Removed.Contains(m.Role)))
        {
            var section = linked.Sections.GetValueOrDefault(member.Role);
            members.Add(new PageField(member.Role, member.Nickname, null,
                section != null ? MissionRef.OfSection(section.Ref, -1) : null) { Missing = section == null });
        }

        foreach (var role in record.Removed)
        {
            members.Add(new PageField(role, RemovedText, RemovedTip)
            {
                Override = true,
                Changed = unsaved.Members.Contains(role),
                Actions = editable ? RemovedMenu(reference, name, canApply, role) : [],
            });
        }

        var buttons = new List<PageAction>();
        if (prefab != null && linked.Asset != null)
            buttons.Add(new PageAction("Open prefab", null, new PrefabModeLink(prefab), OpenPrefabTip));
        if (prefab is { Builtin: false } && linked.Asset != null)
            buttons.Add(new PageAction("Select prefab asset", null, new PrefabAssetLink(prefab), SelectAssetTip));
        if (editable || linked.Missing && context.ScriptReadOnly(script) == null)
            buttons.Add(new PageAction("Unpack", One($"Unpack {record.Id}", new UnpackPrefabInstance(reference)), Tooltip: UnpackTip));

        var template = linked.Template;
        var help = template is { Help.Length: > 0 }
            ? new HelpText(name, template.Help, template.Writes.Length > 0 ? template.Writes : AssetHelp.WhatItWrites)
            : AssetHelp with { Title = name };
        var sections = new List<PageSection>
        {
            new("fields", "Fields", MissionGlyphs.OfPrefab(instance.Definition.Id), help, fields,
                fields.Count == 0 ? [linked.Missing ? "The prefab is missing, so its fields do not show." : "The prefab has no fields."] : [])
            {
                Actions = buttons,
            },
            new("members", "Triggers in this prefab", EditorIcons.Bolt, MissionSchema.Retail.Help["prefabMembers"], members, []),
            Problems(analysis, SectionProblems(analysis, linked.Sections.Values.Distinct().ToArray())
                .Concat(analysis.Problems.Where(p => p.Subject?.EndsWith("|" + record.Id, StringComparison.Ordinal) == true &&
                                                     p.Target == null)).ToList(), context.ShowBaseGame),
        };
        var ini = record.Members.Select(m => linked.Sections.GetValueOrDefault(m.Role)?.Ref).OfType<SectionRef>().ToList();
        return new MissionPageModel($"{name} {record.Id}", "Behavior prefab", MissionGlyphs.OfPrefab(instance.Definition.Id),
            help, MissionCompleteness.OfInstance(analysis, instance), sections, null)
        {
            IniSections = ini,
            IniTitle = linked.Sections.Values.All(s => s.Kind == MissionSectionKind.Trigger) ? "INI (all triggers)" : "INI (all sections)",
            Lead = prefab != null && linked.Asset != null
                ? [new PageField("Prefab", name, OpenPrefabTip, new PrefabModeLink(prefab))]
                : [],
            ReadOnly = readOnly != FieldNote ? readOnly : null,
            PageActions = editable ? OverridesMenu(context, reference, prefab, name, canApply, linked) : [],
        };
    }

    /// <summary>The kind of a prefab field in plain words, for example "Whole number" or "Condition".</summary>
    internal static string KindLabel(PrefabFieldDef field)
    {
        var text = field.KindText;
        var words = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (char.IsUpper(c) && words.Length > 0)
                words.Append(' ').Append(char.ToLowerInvariant(c));
            else
                words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : c);
        }

        return words.ToString();
    }

    /// <summary>Two help texts as one, or the one that is not null.</summary>
    private static string? Join(string? first, string second) => first is { Length: > 0 } ? $"{first}\n{second}" : second;

    /// <summary>The field menu of an override (section 8.11 of the M6b design): "Revert", and "Apply to prefab '{name}'"
    /// or, for a prefab that cannot change, "Make a variant with this value".</summary>
    private static IReadOnlyList<PageAction> OverrideMenu(PageContext context, InstanceRef instance, PrefabRef? prefab,
        string prefabName, bool canApply, string field, string label)
    {
        var menu = new List<PageAction>
        {
            new("Revert", One($"Revert {label}", new RevertOverride(instance) { Field = field }), Tooltip: RevertTip),
        };
        if (canApply)
            menu.Add(new PageAction($"Apply to prefab '{prefabName}'",
                One($"Apply {label} to prefab {prefabName}", new ApplyOverrideToPrefab(instance) { Field = field }),
                Tooltip: ApplyTip));
        else if (prefab != null)
            menu.Add(new PageAction("Make a variant with this value", One($"Make a variant of {prefabName}",
                new CreatePrefabAsset(context.FreePrefabId((prefab.Id + "_variant").ToLowerInvariant()))
                {
                    VariantOf = prefab, Source = instance, Fields = [field],
                }), Tooltip: VariantTip));
        return menu;
    }

    /// <summary>The menu of a removed member (decision D676 c): "Revert" brings the member back, and "Apply to prefab"
    /// removes its section from the prefab.</summary>
    private static IReadOnlyList<PageAction> RemovedMenu(InstanceRef instance, string prefabName, bool canApply, string role)
    {
        var menu = new List<PageAction>
        {
            new("Revert", One($"Revert the removed {role}", new RevertOverride(instance) { Member = role }),
                Tooltip: "Bring the trigger back, with the lines of the prefab."),
        };
        if (canApply)
            menu.Add(new PageAction($"Apply to prefab '{prefabName}'",
                One($"Remove {role} from prefab {prefabName}", new ApplyOverrideToPrefab(instance) { Member = role }),
                Tooltip: "Remove this trigger from the prefab. Each instance of the prefab loses it."));
        return menu;
    }

    /// <summary>
    /// The page menu of a linked instance (section 5.6 of the M6b design): the heading "Overrides ({n})", "Revert" and
    /// "Apply" for each override, "Revert all" and "Apply all", as the Overrides list of Unity. Then "Unpack" and
    /// "Delete instance".
    /// </summary>
    private static IReadOnlyList<PageAction> OverridesMenu(PageContext context, InstanceRef instance, PrefabRef? prefab,
        string prefabName, bool canApply, LinkedInstance linked)
    {
        var record = linked.Record;
        var menu = new List<PageAction> { new($"Overrides ({Count(linked.OverrideCount)})", null) };
        foreach (var value in linked.Fields.Where(f => f.Override))
        {
            var label = value.Field.Label;
            menu.Add(new PageAction($"Revert {label}", One($"Revert {label}", new RevertOverride(instance) { Field = value.Field.Id }),
                Tooltip: RevertTip));
            if (canApply)
                menu.Add(new PageAction($"Apply {label}", One($"Apply {label} to prefab {prefabName}",
                    new ApplyOverrideToPrefab(instance) { Field = value.Field.Id }), Tooltip: ApplyTip));
        }

        foreach (var role in record.Removed)
            menu.Add(new PageAction($"Revert the removed {role}",
                One($"Revert the removed {role}", new RevertOverride(instance) { Member = role }), Tooltip: RevertTip));
        if (linked.OverrideCount > 0)
        {
            menu.Add(new PageAction("Revert all", One($"Revert all of {record.Id}", new RevertOverride(instance)),
                Tooltip: "Use the values of the prefab for each field, and bring back each removed trigger."));
            if (canApply)
                menu.Add(new PageAction("Apply all", One($"Apply the overrides of {record.Id} to prefab {prefabName}",
                    new ApplyOverrideToPrefab(instance)), Tooltip: "Make each override the value of the prefab."));
            else if (prefab != null)
                menu.Add(new PageAction("Make a variant with these values", One($"Make a variant of {prefabName}",
                    new CreatePrefabAsset(context.FreePrefabId((prefab.Id + "_variant").ToLowerInvariant())) { VariantOf = prefab, Source = instance }),
                    Tooltip: "Copy the prefab into the active mod, with the values of the overrides."));
        }

        menu.Add(new PageAction("Unpack", One($"Unpack {record.Id}", new UnpackPrefabInstance(instance)), Tooltip: UnpackTip));
        menu.Add(new PageAction("Delete instance", One($"Delete instance {record.Id}", new RemovePrefabInstance(instance)),
            Tooltip: "Remove the triggers of the instance and its link to the prefab."));
        return menu;
    }

    /// <summary>
    /// The field of a value of a prefab field (section 5.5 of the M6b design): a value field has the widget of its kind,
    /// for example a picker for a formation. A list field, a trigger list, a condition and lines have the list widget,
    /// with one row for each row of the value. One gesture gives one batch of the rows in their text form.
    /// </summary>
    /// <param name="make">Makes the batch of the new rows of the value.</param>
    internal static PageField PrefabValueField(PageContext context, PrefabFieldDef field, IReadOnlyList<PrefabRow> rows,
        string? readOnly, Func<IReadOnlyList<string>, MissionBatch?> make)
    {
        var help = field.Help.Length > 0 ? field.Help : null;
        var scope = context.Analysis.Script.Path;
        if (field.Kind == PrefabFieldKind.Value)
        {
            var spec = new MissionValueSpec(0, field.Id, field.Label, field.ValueKind)
            {
                Tokens = field.Tokens, Width = PrefabRow.WidthOf(field.ValueKind), Optional = field.Optional,
            };
            var (widget, words) = WidgetOf(spec);
            var value = rows.FirstOrDefault()?.Texts is { } texts ? string.Join(", ", texts) : "";
            var shown = value.Length == 0 ? "None" : field.Unit != null ? $"{value} {field.Unit}" : value;
            return new PageField(field.Label, shown, help)
            {
                Edit = new FieldEdit(widget, $"Set {field.Label}")
                {
                    Value = value,
                    Spec = spec,
                    List = widget == EditWidget.Picker ? MissionSchema.ListOf(spec.Kind) : null,
                    Scope = scope,
                    NoneWord = field.Optional ? "" : null,
                    Choices = words.Distinct(StringComparer.OrdinalIgnoreCase).Select(w => (w, w, (string?)null)).ToList(),
                    Make = v => make(v.Type != MissionValueType.List && v.Scalar is not { Length: > 0 } ? [] : [string.Join(", ", TextsOf(v))]),
                    PickInScene = widget == EditWidget.Position,
                    ReadOnly = readOnly,
                },
            };
        }

        var lines = field.Kind is PrefabFieldKind.Condition or PrefabFieldKind.Lines;
        var list = PrefabListSpec(field);
        var model = ListFieldModel.Read(list, rows.Select(r => new ListFieldLine(r.Key ?? field.Id,
            r.Texts.Select(t => (ValueBase)new StringValue(t)).ToList())).ToList());
        return new PageField(field.Label, Count(model.Rows.Count), help)
        {
            List = model,
            Edit = new FieldEdit(EditWidget.List, $"Set {field.Label}")
            {
                MakeList = m => make(m.Write().Select(l => new PrefabRow(lines ? l.Key : null, l.Texts).Format()).ToList()),
                Scope = scope,
                ReadOnly = readOnly,
            },
        };
    }

    /// <summary>The list field spec of a prefab field that is not a value field: the columns of a list field, one trigger
    /// for each row of a trigger list, and the lines of the keys of a condition or a lines field.</summary>
    internal static ListFieldSpec PrefabListSpec(PrefabFieldDef field)
    {
        var schema = MissionSchema.Retail;
        var help = field.Help.Length > 0 ? field.Help : null;
        switch (field.Kind)
        {
            case PrefabFieldKind.List:
                var columns = new List<ListColumn>();
                var index = 0;
                foreach (var column in field.Columns)
                {
                    columns.Add(schema.Column(new MissionValueSpec(index, column.Id, column.Label, column.Kind) { Width = column.Width }));
                    index += column.Width;
                }

                return new ListFieldSpec(field.Id, field.Label, help, ListFieldCodec.RepeatedKey, [field.Id], columns) { Items = "rows" };
            case PrefabFieldKind.Triggers:
                return new ListFieldSpec(field.Id, field.Label, help, ListFieldCodec.RepeatedKey, [field.Id],
                    [schema.Column(new MissionValueSpec(0, "trigger", "Trigger", MissionValueKind.Trigger))]) { Items = "triggers" };
            default:
                var condition = field.Kind == PrefabFieldKind.Condition;
                var keys = field.Keys.Count > 0 ? field.Keys
                    : (condition ? schema.Conditions : schema.Actions).Select(n => n.Name).ToList();
                return schema.LinesList(field.Id, field.Label, keys, condition ? "conditions" : "lines") with
                {
                    AddLabel = condition ? "Set condition" : "Add line",
                };
        }
    }

    // ---- Matched instances (sections 5.8 and 5.9) ----

    /// <summary>
    /// The page of a matched instance (section 10.4 of the M6a design, sections 5.8 and 5.9 of the M6b design): its
    /// completeness, its named fields and its triggers with their roles. A field with a write rule takes edits through
    /// <see cref="SetPrefabField"/>. The page menu has "Save as prefab...", and "Open prefab" when a built-in prefab has
    /// the same id.
    /// </summary>
    private static MissionPageModel MatchedInstancePage(PageContext context, MissionPrefabInstance instance)
    {
        var analysis = context.Analysis;
        var script = analysis.Script.Path;
        var definition = instance.Definition;
        var reference = new InstanceRef(script, instance.Id);
        var readOnly = context.ScriptReadOnly(script);
        var fields = new List<PageField>();
        foreach (var field in definition.Fields)
        {
            var values = instance.Values(field);
            var writes = readOnly == null && field.Write != PrefabWrite.None;
            MissionBatch? Make(IReadOnlyList<string> rows) => One($"Set {field.Label}", new SetPrefabField(reference, field.Id, rows));
            if (field.Spec(MissionSchema.Retail) is { } spec)
            {
                var model = ListFieldModel.Read(spec, values.Where(v => v.Line != null).Select(v => ListFieldLine.Of(v.Line!.Line))
                    .Distinct().ToList());
                fields.Add(new PageField(field.Label, Count(model.Rows.Count), field.Help)
                {
                    List = model,
                    Edit = writes
                        ? new FieldEdit(EditWidget.List, $"Set {field.Label}")
                        {
                            MakeList = m => Make(m.Write().Select(l => PrefabRow.Line(l.Key, [.. l.Texts]).Format()).ToList()),
                            Scope = script,
                        }
                        : null,
                });
            }
            else if (field.List != null)
            {
                fields.Add(new PageField(field.Label, Count(values.Count), field.Help)
                {
                    Heading = true,
                });
                fields.AddRange(values.Select(v => new PageField(v.Trigger?.Nickname ?? "", v.Text, null, v.Trigger != null
                    ? new MissionRef(script, MissionItemKind.Trigger, v.Trigger.Section.Ref)
                    : null)));
            }
            else
            {
                fields.Add(MatchedValueField(context, reference, field, values.FirstOrDefault(), writes, Make));
            }
        }

        var members = new List<PageField>();
        if (instance.Core is { } core)
            members.Add(new PageField(definition.CoreLabel, core.Nickname, null,
                new MissionRef(script, MissionItemKind.Trigger, core.Section.Ref)));
        foreach (var rule in definition.Members)
        {
            foreach (var trigger in instance.Role(rule.Role))
                members.Add(new PageField(rule.Label, trigger.Nickname, instance.Claimed.Contains(trigger) ? null
                        : MissionSchema.Retail.Notes["sharedMember"],
                    new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref)));
        }

        var buttons = new List<PageAction>();
        var builtin = BuiltinPrefabs.Text(definition.Id) != null ? PrefabRef.BuiltinOf(definition.Id) : null;
        if (builtin != null)
            buttons.Add(new PageAction("Open prefab", null, new PrefabModeLink(builtin), OpenPrefabTip));
        if (readOnly == null && instance.Core != null)
            buttons.Add(new PageAction("Save as prefab...", null,
                new SavePrefabLink(reference, definition.Name, context.FreePrefabId(definition.Id.ToLowerInvariant())), SaveAsPrefabTip));

        var sections = new List<PageSection>
        {
            new("fields", "Fields", MissionGlyphs.OfPrefab(definition.Id), definition.Help, fields, [])
            {
                Actions = buttons,
            },
            new("members", "Triggers in this prefab", EditorIcons.Bolt, MissionSchema.Retail.Help["prefabMembers"], members,
                []),
            Problems(analysis, SectionProblems(analysis, instance.Triggers.Select(t => t.Section)
                .Append(instance.CoreSection).OfType<MissionSection>().Distinct().ToArray()), context.ShowBaseGame),
        };
        var ini = instance.Triggers.Select(t => t.Section.Ref).Prepend(instance.CoreSection?.Ref).OfType<SectionRef>().Distinct().ToList();
        // An order prefab and an ambush have a core section that is not a trigger.
        var onlyTriggers = instance.CoreSection is null or { Kind: MissionSectionKind.Trigger };
        return new MissionPageModel($"{definition.Name} {instance.Name}", "Behavior prefab", MissionGlyphs.OfPrefab(definition.Id),
            definition.Help, MissionCompleteness.OfInstance(analysis, instance), sections, null)
        {
            IniSections = ini,
            IniTitle = onlyTriggers ? "INI (all triggers)" : "INI (all sections)",
            PageActions = buttons,
        };
    }

    /// <summary>
    /// The field of a value of a matched instance (section 5.8 of the M6b design). A value of an action has the widget of
    /// its value spec, a condition has a text field of its line, and a key of the core section has a text field of its
    /// values. A field without a write rule is read-only and goes to its trigger.
    /// </summary>
    private static PageField MatchedValueField(PageContext context, InstanceRef instance, PrefabField field,
        PrefabFieldValue? value, bool writes, Func<IReadOnlyList<string>, MissionBatch?> make)
    {
        var script = context.Analysis.Script.Path;
        var link = value?.Trigger != null ? new MissionRef(script, MissionItemKind.Trigger, value.Trigger.Section.Ref) : null;
        var shown = new PageField(field.Label, value?.Text ?? "None", field.Help, link);
        if (!writes || value?.Line is not { } line)
            return shown;
        switch (field.Write)
        {
            case PrefabWrite.Value when line.Name?.Values.FirstOrDefault(s => s.Index == field.From.Value) is { } spec:
                return ValueField(context, line, spec, field.Label, v => new SetPrefabField(instance, field.Id,
                    [string.Join(", ", TextsOf(v))])) with { Help = field.Help };
            case PrefabWrite.Condition:
                var text = MissionEditor.LineText(line.Line);
                return shown with
                {
                    Value = text,
                    Edit = new FieldEdit(EditWidget.Text, $"Set {field.Label}")
                    {
                        Value = text, Make = v => v.Scalar is { Length: > 0 } row ? make([row]) : null,
                    },
                };
            case PrefabWrite.Key:
                var values = string.Join(", ", line.Line.Select(MissionIndex.Text));
                return shown with
                {
                    Edit = new FieldEdit(EditWidget.Text, $"Set {field.Label}")
                    {
                        Value = values, Make = v => v.Scalar is { } row ? make([row]) : null,
                    },
                };
            default:
                return shown;
        }
    }

    // ---- Prefab mode (sections 5.7 and 8.6) ----

    /// <summary>
    /// The Prefab page of prefab mode (section 8.6 of the M6b design): the name, the kind, the source of a variant, the
    /// fields with their kind, default and "Per instance", the ports, the template sections and the problems. A built-in
    /// prefab or a prefab of a lower mod is read-only, with its banner and "Make variant".
    /// </summary>
    /// <param name="key">The row of the Hierarchy, for example <c>field:delay</c>: the page opens its section.</param>
    internal static MissionPageModel PrefabPage(PageContext context, PrefabModeModel model, string? key = null)
    {
        var asset = model.Asset;
        var prefab = asset.Ref;
        var template = model.Template;
        var readOnly = model.ReadOnlyReason ?? context.ScriptReadOnly(prefab.Script);
        var editable = readOnly == null && template != null;
        var sections = new List<PageSection>();

        var about = new List<PageField>
        {
            new("Name", asset.Name),
            new("File", prefab.Builtin ? "Built-in prefab of the editor" : PrefabAsset.PathOf(prefab.Id),
                prefab.Builtin ? "The editor has this prefab. It cannot change." : "The prefab file in the folder of the mod."),
            new("Mod", asset.ModId ?? "None"),
        };
        if (template != null)
        {
            about.Insert(1, new PageField("Kind", template.Kind == MissionPrefabKind.Order ? "Order" : "Beat"));
            if (template.VariantOf is { } source)
                about.Add(new PageField("Variant of", PrefabRef.TryParse(source)?.Id ?? source,
                    "The prefab that this prefab copies. A change of that prefab does not change this one (decision D672).",
                    PrefabRef.TryParse(source) is { } from ? new PrefabModeLink(from) : null));
        }

        if (!prefab.Builtin && model.ReadOnlyReason == null)
            about.Add(new PageField("Instances", model.BarText(asset.ModId)));
        var makeVariant = new PageAction("Make variant", One($"Make a variant of {asset.Name}",
            new CreatePrefabAsset(context.FreePrefabId((prefab.Id + "_variant").ToLowerInvariant())) { VariantOf = prefab }), Tooltip: MakeVariantTip);
        var canVariant = model.ReadOnlyReason != null && context.ScriptReadOnly(prefab.Script) == null && template != null;
        sections.Add(new PageSection("prefab", "Prefab", EditorIcons.Cubes, AssetHelp, about, [])
        {
            Actions = canVariant ? [makeVariant] : [],
        });

        if (template != null)
        {
            var fields = new List<PageField>();
            foreach (var field in template.Fields)
            {
                var view = PrefabValueField(context, field, field.Default, editable ? null : readOnly ?? FieldNote,
                    rows => One($"Set the default of {field.Label}", new SetPrefabFieldDefault(prefab, field.Id, rows)));
                var note = $"Kind: {KindLabel(field)}.{(field.PerInstance ? $" {PerInstanceText}." : "")}";
                fields.Add(view with
                {
                    Help = Join(view.Help, note),
                    Actions = editable
                        ? [new PageAction("Remove field", One($"Remove field {field.Label}", new RemovePrefabField(prefab, field.Id)),
                            Tooltip: "Remove the field. Each line that uses it must have a fixed value first.")]
                        : [],
                });
            }

            sections.Add(new PageSection("fields", "Fields", EditorIcons.List, null, fields,
                fields.Count == 0 ? ["The prefab has no fields. Pick \"Field\" in the value choice of a line to make one."] : []));
            sections.Add(new PageSection("ports", "Ports", EditorIcons.Link, null,
                template.Ports.Select(p => new PageField(p.Label, $"From {p.Role}",
                    "An output of the prefab. Each instance can start triggers of its mission with it.")).ToList(),
                template.Ports.Count == 0 ? ["The prefab has no ports."] : []));
            var document = model.Analysis?.Document;
            sections.Add(new PageSection("sections", "Sections", EditorIcons.Th, null, template.Sections.Select(s =>
                new PageField(s.Role, s.Type, null, document?.Sections.FirstOrDefault(d => d.Nickname == s.Role) is { } found
                    ? MissionRef.OfSection(found.Ref, -1)
                    : null)).ToList(), []));
        }

        var problems = asset.Problems.Select(p => new PageField($"Line {Count(p.Line)}", p.Message, null,
            Severity: DiagnosticSeverity.Error)).ToList();
        if (model.Analysis is { } analysis)
            problems.AddRange(Problems(analysis, analysis.Problems, context.ShowBaseGame).Fields);
        sections.Add(new PageSection("problems", "Problems", EditorIcons.ExclamationTriangle, MissionSchema.Retail.Help["problems"],
            problems, problems.Count == 0 ? ["The prefab has no problem."] : []));

        var help = template is { Help.Length: > 0 }
            ? new HelpText(asset.Name, template.Help, template.Writes.Length > 0 ? template.Writes : AssetHelp.WhatItWrites)
            : AssetHelp with { Title = asset.Name };
        return new MissionPageModel(asset.Name, "Behavior prefab", EditorIcons.Cubes, help, null, sections, null)
        {
            ReadOnly = model.ReadOnlyReason ?? (readOnly != FieldNote ? readOnly : null),
            PageActions = canVariant ? [makeVariant] : [],
            FocusSection = key?.Split(':')[0] switch
            {
                "field" => "fields",
                "port" => "ports",
                _ => null,
            },
        };
    }

    /// <summary>The lines of a trigger of a template whose key is the slot of a field of a kind, for example
    /// <c>{spawnWhen} =</c>.</summary>
    private static IEnumerable<MissionLine> SlotLines(PrefabTemplate template, MissionIndex index, MissionTrigger trigger,
        PrefabFieldKind kind) =>
        index.LinesOf(trigger.Section).Where(l => PrefabSlot.KeyField(l.Key) is { } id && template.Field(id)?.Kind == kind);

    /// <summary>The field of a line whose key is the slot of a field: "Field: {label}", with "Fixed value" in its menu
    /// (<see cref="UnbindValue"/>). The line takes the default rows of the field.</summary>
    private static PageField SlotLineField(PrefabTemplate template, MissionLine line, string label, bool editable)
    {
        var field = template.Field(PrefabSlot.KeyField(line.Key)!);
        return new PageField(label, $"Field: {field?.Label ?? line.Key}", FieldValueTip)
        {
            Actions = editable
                ? [new PageAction("Fixed value", One($"Fixed value of {label}", new UnbindValue(RowOf(line))), Tooltip: FixedValueTip)]
                : [],
        };
    }

    /// <summary>The line choice of a whole line of a template (section 8.6 of the M6b design): "Field: {label}" for each
    /// condition field of a condition, or for each lines field that takes the key of an action
    /// (<see cref="BindValue"/> without a value).</summary>
    private static IEnumerable<PageAction> LineChoices(PrefabTemplate template, MissionLine line, PrefabFieldKind kind) =>
        template.Fields.Where(f => f.Kind == kind && (f.Keys.Count == 0 || f.Keys.Any(k => MissionIndex.Is(k, line.Key))))
            .Select(f => new PageAction($"Field: {f.Label}", One($"Bind the line to {f.Label}", new BindValue(RowOf(line), f.Id)),
                Tooltip: FieldValueTip));

    /// <summary>
    /// The value choice of a value of a line of a template in prefab mode (section 8.6 of the M6b design): "Fixed value"
    /// (<see cref="UnbindValue"/>) or "Field: {label}" (<see cref="BindValue"/>) for each field that can give the value.
    /// A value with the slot of a field shows "Field: {label}".
    /// </summary>
    private static PageField ValueChoice(PageContext context, PrefabTemplate template, MissionLine line, MissionValueSpec spec,
        string label, PageField field)
    {
        if (!context.Editable(line.Section.Ref))
            return field;
        var row = RowOf(line);
        var text = spec.Index < line.Count ? line.Value(spec.Index) : null;
        var bound = text != null ? PrefabSlot.Of(text).Select(s => s.Field).FirstOrDefault(f => f != null) : null;
        var choices = template.Fields.Where(f => f.Id != bound && BindValue.FieldFits(f, spec.Kind))
            .Select(f => new PageAction($"Field: {f.Label}",
                One($"Bind {label} to {f.Label}", new BindValue(row, f.Id) { Value = spec.Name }), Tooltip: FieldValueTip))
            .ToList();
        if (bound == null)
            return field with { Actions = choices };
        var boundLabel = template.Field(bound)?.Label ?? bound;
        return new PageField(label, $"Field: {boundLabel}", FieldValueTip)
        {
            Changed = field.Changed,
            Actions =
            [
                new PageAction("Fixed value", One($"Fixed value of {label}", new UnbindValue(row) { Value = spec.Name }),
                    Tooltip: FixedValueTip),
                .. choices,
            ],
        };
    }
}

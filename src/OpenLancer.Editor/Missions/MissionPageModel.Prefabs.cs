using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
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

/// <summary>The text of a prefab file on the Prefab page, read-only, with the marks of the format check (UI rule
/// 11).</summary>
internal sealed record PrefabFileView(string Path, RawText Text, IReadOnlyList<RawMark> Marks);

/// <summary>
/// The pages of the behavior prefabs (sections 5.6, 5.8, 5.9 and 8.6 of the M6b design): a linked instance with its
/// fields, outputs, overrides and removed members, a matched instance with the fields that it can write, the Prefab page
/// of prefab mode, and the value choice of a line of a template.
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
    public const string RemovedTip = "You deleted this trigger of the instance. The prefab still has it. Revert adds it again.";
    public const string RemovedText = "Removed";
    public const string SaveAsPrefabTip = "Make a prefab asset from the triggers of this instance. The instance then uses it.";
    public const string OpenPrefabTip = "Open the prefab in prefab mode. A change there changes each instance.";
    public const string SelectAssetTip = "Show the asset of the prefab in the Assets panel.";
    public const string PerInstanceText = "Per instance";
    public const string PerInstanceTip = "Each instance sets this value. It never shows as an override.";
    public const string OutputsTip = "The triggers of this mission that the output starts. The instance writes one line for each trigger.";
    public const string NewFieldText = "New field from this value";
    public const string NewFieldTip = "Make a field of the prefab with this value as its default. Each instance can then set the value.";
    public const string NewLineFieldText = "New field from this line";
    public const string MissingPrefabKind = MissionGraph.MissingPrefab;

    /// <summary>The info text of a behavior prefab asset.</summary>
    public static readonly HelpText AssetHelp = new("Behavior prefab asset",
        "A group of triggers with named fields that you can use again. Each instance in a mission uses it.",
        "The [Trigger] sections of each instance. The prefab itself is an editor file. The game does not read it.");

    /// <summary>The info text of prefab mode.</summary>
    public static readonly HelpText PrefabModeHelp = new("Prefab mode",
        "The edit view of one prefab. A change here changes each instance of the prefab in the active mod.",
        "The prefab file, and the [Trigger] sections of each instance.");

    /// <summary>The info text of a prefab: the texts of its template, or the texts of a prefab asset.</summary>
    internal static HelpText PrefabHelp(string name, PrefabTemplate? template) =>
        template is { Help.Length: > 0 }
            ? new HelpText(name, template.Help, template.Writes.Length > 0 ? template.Writes : AssetHelp.WhatItWrites)
            : AssetHelp with { Title = name };

    /// <summary>The tooltip of the violet dot of a collapsed linked instance with overrides.</summary>
    public static string OverridesDotTip(int count) => $"This instance has {MissionChecks.Count(count, "override", "overrides")}.";

    /// <summary>
    /// The batch of "Make variant" and of "Make a variant with this value" (sections 5.6 and 5.9 of the M6b design): a copy
    /// of a prefab in the active mod with a free file name. The command gives the variant a free name. Without an
    /// instance, the result selects the new prefab, and the Mission tab opens it in prefab mode. With an instance, the
    /// instance links to the variant, the values of its overrides of the fields become the defaults of the variant, and
    /// the selection stays on the instance.
    /// </summary>
    /// <param name="fields">The fields whose values become defaults, or null for all overrides of the instance.</param>
    internal static MissionBatch VariantBatch(Func<string, string> freeId, PrefabRef prefab, string name,
        InstanceRef? instance = null, IReadOnlyList<string>? fields = null) =>
        One($"Make a variant of {name}", new CreatePrefabAsset(freeId(prefab.Id + "_variant"))
        {
            VariantOf = prefab, Source = instance, Fields = fields,
        });

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
    /// of its kind, the outputs of the ports, the overrides in bold with the violet bar and their menu, the members and
    /// the removed members, and the page menu "Overrides ({n})". A field edit gives <see cref="SetPrefabField"/>, so the
    /// instance compiles. An instance of a prefab that no mod has shows "Missing prefab", as Unity does.
    /// </summary>
    private static MissionPageModel LinkedInstancePage(PageContext context, MissionPrefabInstance instance, LinkedInstance linked)
    {
        var analysis = context.Analysis;
        var script = analysis.Script.Path;
        var record = linked.Record;
        var reference = new InstanceRef(script, record.Id);
        var prefab = PrefabRef.TryParse(record.Prefab);
        var name = linked.Asset?.Name ?? instance.Definition.Name;
        var readOnly = context.ScriptReadOnly(script) ?? (linked.Missing ? PrefabLinks.MissingMessage(record.Id, name) : null);
        var editable = readOnly == null;
        var canApply = linked.Asset is { Active: true };
        var unsaved = context.UnsavedOverrides(reference);

        var compiled = Compiled(linked);
        var fields = new List<PageField>();
        foreach (var value in linked.Fields)
        {
            var field = value.Field;
            var view = PrefabValueField(context, field, value.Rows, readOnly,
                rows => One($"Set {field.Label}", new SetPrefabField(reference, field.Id, rows)));
            IReadOnlyList<PageAction> menu = value.Override && editable
                ? OverrideMenu(context, reference, prefab, name, canApply, field.Id, field.Label)
                : [];
            if (RawLinkOf(linked, compiled, field.Id) is { } raw)
                menu = [.. menu, new PageAction("Show in raw INI", null, raw)];
            fields.Add(view with
            {
                Help = field.PerInstance ? Join(view.Help, PerInstanceTip) : view.Help,
                Override = value.Override,
                Changed = value.Override && unsaved.Fields.Contains(field.Id),
                Actions = menu,
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

        var help = PrefabHelp(name, linked.Template);
        var sections = new List<PageSection>
        {
            new("fields", "Fields", MissionGlyphs.OfPrefab(instance.Definition.Id), help, fields,
                fields.Count == 0 ? [linked.Missing ? "The prefab is missing, so its fields do not show." : "The prefab has no fields."] : [])
            {
                Actions = buttons,
            },
        };
        if (linked.Template is { Ports.Count: > 0 } template)
            sections.Add(new PageSection("outputs", "Outputs", EditorIcons.Link, null,
                template.Ports.Select(p => OutputField(context, reference, record, p, readOnly)).ToList(), []));
        sections.Add(new PageSection("members", "Triggers in this prefab", EditorIcons.Bolt, MissionSchema.Retail.Help["prefabMembers"],
            members, []));
        sections.Add(Problems(analysis, SectionProblems(analysis, linked.Sections.Values.Distinct().ToArray())
            .Concat(analysis.Problems.Where(p => p.Subject?.EndsWith("|" + record.Id, StringComparison.Ordinal) == true &&
                                                 p.Target == null)).ToList(), context.ShowBaseGame));
        var ini = record.Members.Select(m => linked.Sections.GetValueOrDefault(m.Role)?.Ref).OfType<SectionRef>().ToList();
        return new MissionPageModel($"{name} {record.Id}", linked.Asset == null ? MissingPrefabKind : "Behavior prefab",
            MissionGlyphs.OfPrefab(instance.Definition.Id), help, MissionCompleteness.OfInstance(analysis, instance), sections, null)
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

    /// <summary>
    /// The field of an output of a linked instance (sections 5.3 and 5.5 of the M6b design): the list of the triggers of
    /// the mission that the port starts, with a trigger picker in each row. The list widget gives
    /// <see cref="SetPrefabPort"/>, so the instance writes one line for each trigger. A link drag from the member
    /// trigger of the port in the graph gives the same command.
    /// </summary>
    private static PageField OutputField(PageContext context, InstanceRef instance, InstanceRecord record, PrefabPort port,
        string? readOnly)
    {
        var field = new PrefabFieldDef(port.Id, port.Label, PrefabFieldKind.Triggers) { Help = OutputsTip };
        var rows = record.Ports.Where(p => p.Port == port.Id).Select(p => PrefabRow.Of(p.Trigger)).ToList();
        return PrefabValueField(context, field, rows, readOnly,
            triggers => One($"Set {port.Label}", new SetPrefabPort(instance, port.Id, triggers)));
    }

    /// <summary>The compile of a linked instance now, or null when the prefab is missing or the compile refuses.</summary>
    private static IReadOnlyList<CompiledSection>? Compiled(LinkedInstance linked)
    {
        if (linked.Template is not { } template)
            return null;
        try
        {
            return PrefabCompiler.Compile(template, LinkedInstance.InputOf(linked.Record, template));
        }
        catch (EditException)
        {
            return null;
        }
    }

    /// <summary>The link of "Show in raw INI" of a field of a linked instance (section 8.6 of the M6b design): the first
    /// line of a member that holds the value of the field. Null when no line holds it.</summary>
    private static RawLineLink? RawLinkOf(LinkedInstance linked, IReadOnlyList<CompiledSection>? compiled, string field) =>
        compiled != null && PrefabCompiler.FieldLine(linked.Template!, compiled, field) is var (role, entry) &&
        linked.Sections.GetValueOrDefault(role) is { } section && entry < section.Section.Count
            ? new RawLineLink(section.Ref, entry)
            : null;

    /// <summary>The kind of a prefab field in plain words, for example "Whole number", "Condition" or "Order
    /// list".</summary>
    internal static string KindLabel(PrefabFieldDef field) => field.Kind switch
    {
        PrefabFieldKind.List => "List",
        PrefabFieldKind.Triggers => "Trigger list",
        PrefabFieldKind.Condition => "Condition",
        PrefabFieldKind.Lines => "Lines",
        _ => field.ValueKind switch
        {
            MissionValueKind.Object => "Ship, solar or label",
            MissionValueKind.Group => "Ship or formation",
            MissionValueKind.Ship => "Mission ship",
            MissionValueKind.Position => "Position",
            MissionValueKind.Rotation => "Rotation",
            MissionValueKind.Number => "Number",
            MissionValueKind.WholeNumber => "Whole number",
            MissionValueKind.Attitude => "Attitude",
            MissionValueKind.TextId => "Text",
            MissionValueKind.Bool => "Yes or no",
            MissionValueKind.Token => "Word",
            MissionValueKind.Text => "Text",
            MissionValueKind.Head or MissionValueKind.Body or MissionValueKind.Accessory => "Costume part",
            MissionValueKind.MetaBehavior => "Meta behavior",
            MissionValueKind.AmbientScript => "Room ambient",
            MissionValueKind.Sound => "Sound",
            MissionValueKind.Room => "Room",
            MissionValueKind.Fuse => "Fuse",
            MissionValueKind.File => "File",
            var kind => ValueKindText(kind),
        },
    };

    /// <summary>The text of a port of a template in plain words: the trigger of the template whose lines start the
    /// triggers of the port.</summary>
    internal static string PortText(PrefabPort port) => $"When trigger '{port.Role}' runs";

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
            menu.Add(new PageAction("Make a variant with this value",
                VariantBatch(context.FreePrefabId, prefab, prefabName, instance, [field]), Tooltip: VariantTip));
        return menu;
    }

    /// <summary>The menu of a removed member (decision D676 c): "Revert" adds the member again, and "Apply to prefab"
    /// removes its section from the prefab.</summary>
    private static IReadOnlyList<PageAction> RemovedMenu(InstanceRef instance, string prefabName, bool canApply, string role)
    {
        var menu = new List<PageAction>
        {
            new("Revert", One($"Revert the removed {role}", new RevertOverride(instance) { Member = role }),
                Tooltip: "Add the trigger again, with the lines of the prefab."),
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
        var menu = new List<PageAction> { new($"Overrides ({Number(linked.OverrideCount)})", null) };
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
                Tooltip: "Use the values of the prefab for each field, and add each removed trigger again."));
            if (canApply)
                menu.Add(new PageAction("Apply all", One($"Apply the overrides of {record.Id} to prefab {prefabName}",
                    new ApplyOverrideToPrefab(instance)), Tooltip: "Make each override the value of the prefab."));
            else if (prefab != null)
                menu.Add(new PageAction("Make a variant with these values", VariantBatch(context.FreePrefabId, prefab, prefabName, instance),
                    Tooltip: "Copy the prefab into the active mod, with the values of the overrides."));
        }

        menu.Add(new PageAction("Unpack", One($"Unpack {record.Id}", new UnpackPrefabInstance(instance)), Tooltip: UnpackTip));
        menu.Add(new PageAction("Delete instance", One($"Delete instance {record.Id}", new RemovePrefabInstance(instance)),
            Tooltip: "Remove the triggers of the instance and its link to the prefab."));
        return menu;
    }

    /// <summary>
    /// The field of a value of a prefab field (section 5.5 of the M6b design): a value field has the widget of its kind,
    /// for example a picker for a formation or a dropdown for a word. A list field, a trigger list, a condition and lines
    /// have the list widget, with one row for each row of the value. One gesture gives one batch of the rows in their
    /// text form.
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
        return new PageField(field.Label, Number(model.Rows.Count), help)
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
                fields.Add(new PageField(field.Label, Number(model.Rows.Count), field.Help)
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
                fields.Add(new PageField(field.Label, Number(values.Count), field.Help)
                {
                    Heading = true,
                });
                fields.AddRange(values.Select(v => new PageField(v.Trigger?.Nickname ?? "", v.Text, null, v.Trigger != null
                    ? new MissionRef(script, MissionItemKind.Trigger, v.Trigger.Section.Ref)
                    : null)));
            }
            else
            {
                fields.Add(MatchedValueField(context, instance, field, values.FirstOrDefault(), writes, Make));
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
                new SavePrefabLink(reference, definition.Name, context.FreePrefabId(definition.Id)), SaveAsPrefabTip));

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
    /// The field of a value of a matched instance (section 5.8 of the M6b design), with the widget of what its write rule
    /// writes (UI rules 2 and 3): a value of an action or of a key of the core section has the widget of its value spec,
    /// for example a picker for a faction or a dropdown for a word. A condition has the list widget of one condition. A
    /// value without its line has the same widget, and the write adds the line. A field without a write rule is read-only
    /// and goes to its trigger.
    /// </summary>
    private static PageField MatchedValueField(PageContext context, MissionPrefabInstance instance, PrefabField field,
        PrefabFieldValue? value, bool writes, Func<IReadOnlyList<string>, MissionBatch?> make)
    {
        var script = context.Analysis.Script.Path;
        var link = value?.Trigger != null ? new MissionRef(script, MissionItemKind.Trigger, value.Trigger.Section.Ref) : null;
        var shown = new PageField(field.Label, value?.Text ?? "None", field.Help, link);
        if (!writes)
            return shown;
        var line = value?.Line;
        if (field.Write == PrefabWrite.Value && line?.Name?.Values.FirstOrDefault(s => s.Index == field.From.Value) is { } spec)
            return ValueField(context, line, spec, field.Label, v => new SetPrefabField(new InstanceRef(script, instance.Id), field.Id,
                [string.Join(", ", TextsOf(v))])) with { Help = field.Help };
        if (WriteFieldOf(instance, field, line) is not { } definition)
            return shown;
        IReadOnlyList<PrefabRow> rows = field.Write switch
        {
            PrefabWrite.Condition when line != null => [PrefabRow.Line(line.Key, [.. line.Line.Select(MissionIndex.Text)])],
            // A key field reads one value of its line of the core section.
            PrefabWrite.Key when value?.Text is { Length: > 0 } text => [PrefabRow.Of(text)],
            _ => [],
        };
        return PrefabValueField(context, definition, rows, null, make) with { Link = link };
    }

    /// <summary>The field that a write rule of a matched instance writes, as a prefab field: a condition, or one value with
    /// the kind of its spec in the schema. Null when the schema has no spec of one value for the field.</summary>
    /// <param name="line">The line of the value now, or null when the instance does not have it.</param>
    private static PrefabFieldDef? WriteFieldOf(MissionPrefabInstance instance, PrefabField field, MissionLine? line)
    {
        var schema = MissionSchema.Retail;
        var from = field.From;
        MissionValueSpec? spec;
        switch (field.Write)
        {
            case PrefabWrite.Condition:
                return new PrefabFieldDef(field.Id, field.Label, PrefabFieldKind.Condition) { Help = field.Help };
            case PrefabWrite.Key:
                var type = line?.Section.Type ?? instance.CoreSection?.Type;
                spec = (type != null ? schema.Section(type)?.Key(from.Key!)?.ValueSpecs : null)?.FirstOrDefault(s => s.Covers(from.Value)) ??
                       schema.Directive(from.Key!)?.Values.FirstOrDefault(s => s.Covers(from.Value));
                break;
            case PrefabWrite.Value:
                spec = schema.Name(line?.Key ?? from.Actions.FirstOrDefault() ?? "")?.ValueAt(from.Value);
                break;
            default:
                return null;
        }
        if (spec is not { Width: 1, Rest: false })
            return null;
        return new PrefabFieldDef(field.Id, field.Label, PrefabFieldKind.Value)
        {
            ValueKind = spec.Kind == MissionValueKind.Token && spec.Tokens == null ? MissionValueKind.Text : spec.Kind,
            Tokens = spec.Tokens,
            Help = field.Help,
        };
    }

    // ---- Prefab mode (sections 5.7 and 8.6) ----

    /// <summary>
    /// The Prefab page of prefab mode (section 8.6 of the M6b design): the steps, the name, the kind, the source of a
    /// variant, the fields with their kind, default and "Per instance", the ports, the template sections, the problems
    /// and the text of the file. A built-in prefab or a prefab of a lower mod is read-only, with its banner and "Make
    /// variant". A prefab whose file has a problem shows the problems and the text of the file, so the user finds the
    /// line.
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

        var name = new PageField("Name", asset.Name, "The name of the prefab in the library.");
        if (editable)
            name = name with
            {
                Edit = new FieldEdit(EditWidget.Text, "Rename prefab")
                {
                    Value = asset.Name,
                    Make = v => v.Scalar?.Trim() is { Length: > 0 } text && text != asset.Name
                        ? One($"Rename prefab {asset.Name} to {text}", new RenamePrefabAsset(prefab, prefab.Id) { NewName = text })
                        : null,
                },
            };
        var about = new List<PageField>
        {
            name,
            new("File", prefab.Builtin ? "Built-in prefab of the editor" : PrefabAsset.PathOf(prefab.Id),
                prefab.Builtin
                    ? "The editor has this prefab. It cannot change."
                    : "The prefab file in the folder of the mod. To change the file name, use \"Rename file...\" in the menu " +
                      "of its tile in the Assets panel."),
            new("Mod", asset.ModId ?? "None"),
        };
        if (template != null)
        {
            about.Insert(1, new PageField("Kind", template.Kind == MissionPrefabKind.Order ? "Order" : "Beat"));
            if (template.VariantOf is { } source)
                about.Add(new PageField("Variant of", PrefabRef.TryParse(source)?.Id ?? source,
                    "The prefab that this prefab copies. A change of that prefab does not change this one.",
                    PrefabRef.TryParse(source) is { } from ? new PrefabModeLink(from) : null));
        }

        if (!prefab.Builtin && model.ReadOnlyReason == null)
            about.Add(new PageField("Instances", model.BarText(asset.ModId)));
        // A built-in prefab and a prefab of a lower mod cannot change, so they offer a variant in the active mod.
        var canVariant = model.ReadOnlyReason != null && context.SessionReadOnly == null && template != null;
        var makeVariant = canVariant
            ? new PageAction("Make variant", VariantBatch(context.FreePrefabId, prefab, asset.Name), Tooltip: MakeVariantTip)
            : null;
        sections.Add(new PageSection("prefab", "Prefab", EditorIcons.Cubes, AssetHelp, about, [])
        {
            Actions = makeVariant != null ? [makeVariant] : [],
        });

        if (template != null)
        {
            var fields = new List<PageField>();
            foreach (var field in template.Fields)
            {
                // The kind and "Per instance" show on the heading of the field (section 8.6 of the M6b design).
                var kind = field.PerInstance ? $"{KindLabel(field)}, {PerInstanceText.ToLowerInvariant()}" : KindLabel(field);
                fields.Add(new PageField(field.Label, kind, field.PerInstance ? PerInstanceTip : null) { Heading = true });
                var view = PrefabValueField(context, field, field.Default, editable ? null : readOnly ?? FieldNote,
                    rows => One($"Set the default of {field.Label}", new SetPrefabFieldDefault(prefab, field.Id, rows)));
                fields.Add(view with
                {
                    Label = "Default",
                    Help = Join(view.Help, $"The value of {field.Label} in each instance without an override."),
                    Actions = editable
                        ? [new PageAction("Remove field", One($"Remove field {field.Label}", new RemovePrefabField(prefab, field.Id)),
                            Tooltip: "Remove the field. Each line that uses it must have a fixed value first.")]
                        : [],
                });
            }

            sections.Add(new PageSection("fields", "Fields", EditorIcons.List, null, fields,
                fields.Count == 0
                    ? [$"The prefab has no fields. Pick \"{NewFieldText}\" in the menu of a value of a template line to make one."]
                    : []));
            sections.Add(new PageSection("ports", "Ports", EditorIcons.Link, null,
                template.Ports.Select(p => new PageField(p.Label, PortText(p),
                    "An output of the prefab. Each instance can start triggers of its mission with it.")).ToList(),
                template.Ports.Count == 0 ? ["The prefab has no ports."] : []));
            var document = model.Analysis?.Document;
            sections.Add(new PageSection("sections", "Sections", EditorIcons.Th, null, template.Sections.Select(s =>
                new PageField(s.Role, s.Type, null, document?.Sections.FirstOrDefault(d => d.Nickname == s.Role) is { } found
                    ? MissionRef.OfSection(found.Ref, -1)
                    : null)).ToList(), []));
        }

        var problems = asset.Problems.Select(p => new PageField($"Line {Number(p.Line)}", p.Message, null,
            Severity: DiagnosticSeverity.Error)).ToList();
        if (model.Analysis is { } analysis)
            problems.AddRange(Problems(analysis, analysis.Problems, context.ShowBaseGame).Fields);
        sections.Add(new PageSection("problems", "Problems", EditorIcons.ExclamationTriangle, MissionSchema.Retail.Help["problems"],
            problems, problems.Count == 0 ? ["The prefab has no problem."] : []));

        return new MissionPageModel(asset.Name, "Behavior prefab", EditorIcons.Cubes, PrefabHelp(asset.Name, template),
            MissionCompleteness.OfPrefab(model), sections, null)
        {
            ReadOnly = model.ReadOnlyReason ?? (readOnly != FieldNote ? readOnly : null),
            PageActions = makeVariant != null ? [makeVariant] : [],
            PrefabFile = FileView(asset),
            FocusSection = key?.Split(':')[0] switch
            {
                "field" => "fields",
                "port" => "ports",
                "file" => FileSection,
                _ => null,
            },
        };
    }

    /// <summary>The id of the section of the text of a prefab file on the Prefab page.</summary>
    public const string FileSection = "prefabFile";

    /// <summary>The text of a prefab file with a mark on each line with a problem of the format check (UI rule 11).</summary>
    private static PrefabFileView FileView(PrefabAsset asset)
    {
        var text = RawText.Plain(asset.Text);
        var marks = asset.Problems.Where(p => p.Line > 0 && p.Line <= text.Lines.Count)
            .GroupBy(p => p.Line).OrderBy(g => g.Key)
            .Select(g => new RawMark(g.Key - 1, DiagnosticSeverity.Error, string.Join("\n", g.Select(p => p.Message)), false))
            .ToList();
        return new PrefabFileView(asset.Ref.Builtin ? asset.Ref.ToString() : PrefabAsset.PathOf(asset.Ref.Id), text, marks);
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

    /// <summary>
    /// The line choice of a whole line of a template (section 8.6 of the M6b design): "Field: {label}" for each condition
    /// field of a condition, or for each lines field that takes the key of an action (<see cref="BindValue"/> without a
    /// value). "New field from this line" makes such a field with the line as its default, and binds the line to it.
    /// </summary>
    private static IEnumerable<PageAction> LineChoices(PrefabRef prefab, PrefabTemplate template, MissionLine line, PrefabFieldKind kind)
    {
        foreach (var field in template.Fields.Where(f => f.Kind == kind && (f.Keys.Count == 0 || f.Keys.Any(k => MissionIndex.Is(k, line.Key)))))
            yield return new PageAction($"Field: {field.Label}", One($"Bind the line to {field.Label}", new BindValue(RowOf(line), field.Id)),
                Tooltip: FieldValueTip);
        var texts = line.Line.Select(MissionIndex.Text).ToList();
        if (!CanBeDefault(template, texts))
            yield break;
        var condition = kind == PrefabFieldKind.Condition;
        var label = FreeFieldLabel(template, condition ? "Condition" : line.Name?.Plain ?? line.Key);
        var id = FreeFieldId(template, condition ? "condition" : line.Key);
        yield return new PageAction(NewLineFieldText, new MissionBatch($"New field {label}",
        [
            new AddPrefabField(prefab, id, label, condition ? "condition" : "lines")
            {
                Keys = condition ? [] : [line.Key],
                Default = [PrefabRow.Line(line.Key, [.. texts]).Format()],
            },
            new BindValue(RowOf(line), id),
        ]), Tooltip: NewFieldTip);
    }

    /// <summary>
    /// The value choice of a value of a line of a template in prefab mode (section 8.6 of the M6b design): "Fixed value"
    /// (<see cref="UnbindValue"/>) or "Field: {label}" (<see cref="BindValue"/>) for each field that can give the value.
    /// A value with the slot of a field shows "Field: {label}". "New field from this value" makes a field with the value
    /// as its default, and binds the value to it (<see cref="AddPrefabField"/>, section 5.7).
    /// </summary>
    private static PageField ValueChoice(PageContext context, PrefabTemplate template, MissionLine line, MissionValueSpec spec,
        string label, PageField field)
    {
        if (!context.Editable(line.Section.Ref) || context.Prefab?.Asset.Ref is not { } prefab)
            return field;
        var row = RowOf(line);
        var text = spec.Index < line.Count ? line.Value(spec.Index) : null;
        var bound = text != null ? PrefabSlot.Of(text).Select(s => s.Field).FirstOrDefault(f => f != null) : null;
        var choices = template.Fields.Where(f => f.Id != bound && BindValue.FieldFits(f, spec.Kind))
            .Select(f => new PageAction($"Field: {f.Label}",
                One($"Bind {label} to {f.Label}", new BindValue(row, f.Id) { Value = spec.Name }), Tooltip: FieldValueTip))
            .ToList();
        if (bound == null)
        {
            if (NewValueField(prefab, template, line, spec, label) is { } made)
                choices.Add(made);
            return field with { Actions = choices };
        }

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

    /// <summary>"New field from this value": a field of the kind of the value with the value as its default, and the
    /// bind of the value to it, in one batch. Null for a value without a text, for example a value that the line does
    /// not have.</summary>
    private static PageAction? NewValueField(PrefabRef prefab, PrefabTemplate template, MissionLine line, MissionValueSpec spec,
        string label)
    {
        if (spec.Rest || spec.Index + spec.Width > line.Count)
            return null;
        var texts = Enumerable.Range(spec.Index, spec.Width).Select(i => line.Value(i) ?? "").ToList();
        if (texts.Any(t => t.Length == 0) || !CanBeDefault(template, texts))
            return null;
        var kind = spec.Kind == MissionValueKind.Token && spec.Tokens == null ? MissionValueKind.Text : spec.Kind;
        var fieldLabel = FreeFieldLabel(template, label);
        var id = FreeFieldId(template, spec.Name);
        return new PageAction(NewFieldText, new MissionBatch($"New field {fieldLabel}",
        [
            new AddPrefabField(prefab, id, fieldLabel, PrefabFieldDef.KindName(kind))
            {
                Tokens = kind == MissionValueKind.Token ? spec.Tokens : null,
                Default = [string.Join(", ", texts)],
            },
            new BindValue(RowOf(line), id) { Value = spec.Name },
        ]), Tooltip: NewFieldTip);
    }

    /// <summary>True when value texts of a line of the template can be the default of a new field: they have no slot,
    /// and they name no section of the template. A default that names a member would name the same text in each
    /// instance, not the member of the instance.</summary>
    private static bool CanBeDefault(PrefabTemplate template, IEnumerable<string> texts) =>
        texts.All(t => PrefabSlot.Of(t).Count == 0 && template.Sections.All(s => !s.Role.Equals(t.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>A free id of a new field of a template: the wanted text with only letters, digits, '_' and '-', and a
    /// number after it when the template has the id.</summary>
    private static string FreeFieldId(PrefabTemplate template, string wanted)
    {
        var stem = new string(wanted.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        if (stem.Length == 0)
            stem = "field";
        stem = stem.Length > 40 ? stem[..40] : stem;
        var id = stem;
        for (var n = 2; template.Field(id) != null; n++)
            id = $"{stem}{Number(n)}";
        return id;
    }

    /// <summary>A free label of a new field of a template: the wanted label, and a number after it when a field of the
    /// template has the label (UI rule 9).</summary>
    private static string FreeFieldLabel(PrefabTemplate template, string wanted)
    {
        var label = wanted;
        for (var n = 2; template.Fields.Any(f => f.Label.Equals(label, StringComparison.OrdinalIgnoreCase)); n++)
            label = $"{wanted} {Number(n)}";
        return label;
    }
}

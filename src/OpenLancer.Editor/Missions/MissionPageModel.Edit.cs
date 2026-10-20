using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The edit bindings of the mission pages (section 8.4 of the M6b design). Each binding makes the mission command of one
/// gesture of its widget. The page model makes no change, so the tests check the commands.
/// </summary>
internal sealed partial record MissionPageModel
{
    internal static ItemRef ItemOf(MissionSection section) => new(section.Ref.Path, section.Ref.Id);

    internal static RowRef RowOf(MissionLine line) => new(ItemOf(line.Section), line.Entry) { ExpectKey = line.Key };

    internal static TriggerRef TriggerRefOf(MissionTrigger trigger) => new(trigger.Section.Ref.Path, trigger.Nickname);

    private static MissionBatch One(string label, MissionCommand command) => new(label, [command]);

    /// <summary>
    /// The binding of one value of a line by the kind of its spec (section 8.4 of the M6b design): a picker for a name, a
    /// dropdown for a word, a number field, three numbers with "Pick in Scene" for a position, a check box for a bool, a
    /// text id field, or a text field.
    /// </summary>
    /// <param name="make">Makes the command of a new value.</param>
    internal static FieldEdit ValueEdit(PageContext context, MissionLine line, MissionValueSpec spec, string label,
        Func<MissionValue, MissionCommand> make)
    {
        var schema = MissionSchema.Retail;
        var setLabel = $"Set {label}";
        var words = spec.Tokens != null ? schema.Tokens.GetValueOrDefault(spec.Tokens)?.Values ?? [] : [];
        if (spec.Also != null)
            words = [.. words, .. schema.Tokens.GetValueOrDefault(spec.Also)?.Values ?? []];
        var widget = spec.Kind switch
        {
            MissionValueKind.Position => EditWidget.Position,
            MissionValueKind.Rotation => EditWidget.Rotation,
            MissionValueKind.Number or MissionValueKind.Attitude => EditWidget.Number,
            MissionValueKind.WholeNumber => EditWidget.WholeNumber,
            MissionValueKind.Bool => EditWidget.Bool,
            MissionValueKind.TextId => EditWidget.TextId,
            _ when words.Count > 0 => EditWidget.Words,
            _ when MissionSchema.ListOf(spec.Kind) != null => EditWidget.Picker,
            _ => EditWidget.Text,
        };
        return new FieldEdit(widget, setLabel)
        {
            Value = line.ValueOf(spec) ?? "",
            Spec = spec,
            List = widget == EditWidget.Picker ? MissionSchema.ListOf(spec.Kind) : null,
            Scope = context.Analysis.Script.Path,
            NoneWord = spec.NoneToken,
            Choices = words.Distinct(StringComparer.OrdinalIgnoreCase).Select(w => (w, w, (string?)null)).ToList(),
            Make = value => One(setLabel, make(value)),
            MakeText = widget == EditWidget.TextId
                ? text => One(setLabel, new SetLineText(RowOf(line), spec.Name, text))
                : null,
            PickInScene = widget == EditWidget.Position,
            ReadOnly = context.ReadOnlyOf(line.Section.Ref),
        };
    }

    /// <summary>The command of a new value of a section key: <see cref="SetLineValue"/> by the name of the value, or by
    /// its index for a key without named values.</summary>
    private static Func<MissionValue, MissionCommand> KeyValue(MissionLine line, MissionValueSpec spec) =>
        line.Specs.Count > 0
            ? value => new SetLineValue(RowOf(line), value) { Name = spec.Name }
            : value => new SetLineValue(RowOf(line), value) { Index = spec.Index };

    /// <summary>A check box of a trigger: "Starts with the mission" or "Repeats".</summary>
    private static FieldEdit BoolEdit(PageContext context, MissionSection section, string label, bool on,
        Func<bool, MissionCommand> make) =>
        new(EditWidget.Bool, $"Set {label}")
        {
            Value = on ? "true" : "false",
            Make = value => One($"Set {label}", make(value.Type == MissionValueType.Bool ? value.Bool : value.Scalar == "true")),
            ReadOnly = context.ReadOnlyOf(section.Ref),
        };

    /// <summary>
    /// The field "Event" of the Trigger page (section 8.4 of the M6b design): a dropdown of the 44 conditions by event
    /// group, with their plain names. A new event sets the condition with the first values of the new condition.
    /// </summary>
    private static FieldEdit EventEdit(PageContext context, MissionTrigger trigger)
    {
        var schema = MissionSchema.Retail;
        var groups = schema.Events.ToDictionary(e => e.Id, e => e.Label);
        return new FieldEdit(EditWidget.Words, "Set the event")
        {
            Value = trigger.Condition?.Name?.Name ?? trigger.Condition?.Key ?? "",
            Choices = schema.Events.OrderBy(e => e.Order)
                .SelectMany(e => schema.Conditions.Where(c => c.Group == e.Id))
                .Select(c => (c.Name, c.Plain, (string?)groups.GetValueOrDefault(c.Group))).ToList(),
            Make = value => schema.Name(value.Text) is { Kind: MissionNameKind.Condition } name
                ? One("Set the event", new SetCondition(TriggerRefOf(trigger), MissionLibrary.Starter(name)))
                : null,
            ReadOnly = context.ReadOnlyOf(trigger.Section.Ref),
        };
    }

    /// <summary>The row menu of an action line of the Trigger page (section 8.4 of the M6b design): "Move up", "Move
    /// down", "Duplicate", "Remove" and "Show in raw INI".</summary>
    private static IReadOnlyList<PageAction> ActionMenu(PageContext context, MissionTrigger trigger, MissionLine action)
    {
        var raw = new PageAction("Show in raw INI", null, new RawLineLink(trigger.Section.Ref, action.Entry));
        if (context.ReadOnlyOf(trigger.Section.Ref) != null)
            return [raw];
        var actions = trigger.Actions;
        var at = actions.ToList().IndexOf(action);
        var row = RowOf(action);
        var copy = LineSpec.Raw(action.Key, [.. action.Line.Select(MissionIndex.Text)]);
        int? AfterNext() => at + 2 < actions.Count ? actions[at + 2].Entry : null;
        var result = new List<PageAction>();
        if (at > 0)
            result.Add(new PageAction("Move up", One("Move action", new ReorderAction(row) { Before = actions[at - 1].Entry })));
        if (at >= 0 && at + 1 < actions.Count)
            result.Add(new PageAction("Move down", One("Move action", new ReorderAction(row) { Before = AfterNext() })));
        result.Add(new PageAction("Duplicate", One($"Duplicate {action.Name?.Plain ?? action.Key}",
            new AddAction(TriggerRefOf(trigger), copy) { Before = at + 1 < actions.Count ? actions[at + 1].Entry : null })));
        result.Add(new PageAction("Remove", One($"Remove {action.Name?.Plain ?? action.Key}", new RemoveAction(row))));
        result.Add(raw);
        return result;
    }

    /// <summary>The field that adds a trigger to "Started by" or "Stopped by": the picked trigger starts or stops this
    /// trigger with a new line at its end.</summary>
    private static PageField AddLinkField(PageContext context, MissionTrigger trigger, LinkKind kind, string label) =>
        new(label, "", kind == LinkKind.Start
            ? "Pick a trigger. It gets a line that starts this trigger."
            : "Pick a trigger. It gets a line that stops this trigger.")
        {
            Edit = new FieldEdit(EditWidget.Triggers, label)
            {
                List = MissionSchema.ListOf(MissionValueKind.Trigger),
                Scope = context.Analysis.Script.Path,
                Add = name => One($"Link {name} to {trigger.Nickname}",
                    new LinkTriggers(new TriggerRef(context.Analysis.Script.Path, name), trigger.Nickname, kind)),
                ReadOnly = context.ReadOnlyOf(trigger.Section.Ref),
            },
        };

    /// <summary>The binding of a list field of the lines of one key of a section (section 8.5 of the M6b design): one
    /// gesture of the list widget sets all lines of the key in one command.</summary>
    private static FieldEdit KeyListEdit(PageContext context, MissionSection section, string key, string label)
    {
        var item = ItemOf(section);
        return new FieldEdit(EditWidget.List, $"Set {label}")
        {
            MakeList = model =>
            {
                var lines = model.Write();
                MissionCommand command = MissionSection.KindOf(section.Type) == MissionSectionKind.Formation &&
                                         MissionIndex.Is(key, "ship")
                    ? new SetFormationMembers(item, lines.Select(l => l.Texts.FirstOrDefault() ?? "").ToList())
                    : MissionSection.KindOf(section.Type) == MissionSectionKind.OrderList
                        ? new SetOrders(item, lines.Select(l => LineSpec.Raw(l.Key, [.. l.Texts])).ToList())
                        : new SetItemKey(item, key, lines.Select(l => LineSpec.Raw(l.Key, [.. l.Texts])).ToList());
                return One($"Set {label}", command);
            },
            Scope = context.Analysis.Script.Path,
            ReadOnly = context.ReadOnlyOf(section.Ref),
        };
    }

    /// <summary>The binding of a list of values in one line (a value list, section 8.5 of the M6b design): the line
    /// gets the values of the rows.</summary>
    private static FieldEdit ValueListEdit(PageContext context, MissionLine line, MissionValueSpec spec, string label,
        Func<MissionValue, MissionCommand> make) =>
        new(EditWidget.List, $"Set {label}")
        {
            MakeList = model => One($"Set {label}",
                make(MissionValue.List(model.Write()[0].Texts.Skip(spec.Index).Select(MissionValue.Of)))),
            Scope = context.Analysis.Script.Path,
            ReadOnly = context.ReadOnlyOf(line.Section.Ref),
        };

    /// <summary>The number of ships of the script whose NPC names a ship build, for the chip "Used by {n} ships".</summary>
    internal static int ShipsOfBuild(PageContext context, NpcShipBuild build) =>
        context.Analysis.Index.Document.OfKind(MissionSectionKind.Ship)
            .Count(s => MissionCompleteness.BuildOf(context.Result.ShipBuilds, context.Analysis.Index, s) is { } b &&
                        b.Ref.Equals(build.Ref));

    /// <summary>The text of the chip of a ship build (section 8.11 of the M6b design).</summary>
    internal static string UsedByText(int ships) =>
        ships == 1 ? "Used by 1 ship" : $"Used by {ships.ToString(CultureInfo.InvariantCulture)} ships";

    /// <summary>The tooltip of the chip of a ship build.</summary>
    internal static string UsedByTip(int ships) => ships == 1
        ? "This ship alone uses this ship build."
        : "Other ships use this ship build too. A change here changes all of them.";
}

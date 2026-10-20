using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The edit widgets of the mission pages (section 8.4 of the M6b design). Each widget draws the binding of its field
/// (<see cref="FieldEdit"/>), and one gesture runs one batch through the mission editor of the session. A drag of a
/// number and a pick in an open picker are one transaction, so one history entry (section 4.11 of the M6b design). The
/// row menus, the page menu, the "+" of the action groups and "Pick in Scene" are here too.
/// </summary>
internal sealed partial class MissionPage
{
    private const string PickTip = "Click a point in the Scene to set this position. Esc cancels.";

    private readonly Dictionary<string, ValuePicker> pickers = [];
    private readonly QuickAdd quickAdd = new();
    private (string Field, MissionTransaction Transaction)? gesture;
    private string? lastRefusal;
    private readonly Dictionary<string, string> texts = [];
    private string renameText = "";
    private bool renameOpen;

    /// <summary>Runs the batch of one gesture.</summary>
    private void Run(MissionBatch? batch) => window.Missions.Run(batch);

    /// <summary>One step of a gesture with many steps: the first step opens a transaction, and each step takes the place
    /// of the step before it (section 4.11 of the M6b design).</summary>
    private void Step(string field, MissionBatch? batch)
    {
        if (batch == null || window.Session?.Edits is not { } edits)
        {
            return;
        }

        if (gesture is not var (owner, transaction) || owner != field || !transaction.IsOpen)
        {
            EndGesture();
            try
            {
                transaction = edits.Missions.Begin(batch.Label);
            }
            catch (EditException e)
            {
                window.Log.Add(ConsoleSeverity.Warning, MissionChecks.Source, e.Error.Message);
                return;
            }

            gesture = (field, transaction);
        }

        var result = transaction.Apply(batch);
        if (result.Status == EditStatus.Refused && result.Summary is { } message && message != lastRefusal)
        {
            lastRefusal = message;
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, message);
        }
    }

    /// <summary>Ends the open gesture: one history entry when its steps changed data.</summary>
    private void EndGesture()
    {
        if (gesture is not var (_, transaction))
        {
            return;
        }

        gesture = null;
        lastRefusal = null;
        if (!transaction.IsOpen)
        {
            return;
        }

        var result = transaction.Commit();
        foreach (var error in result.Errors)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, error.Message);
        }

        foreach (var note in result.Notes)
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, note);
        }
    }

    /// <summary>The edit widget of a field in its row. Returns false for a field without a widget that the session can
    /// change now, so the page draws it read-only.</summary>
    private bool EditField(string id, PageField field)
    {
        if (field.Edit is not { ReadOnly: null } edit || window.Missions.ReadOnlyNote != null || edit.Widget == EditWidget.List)
        {
            return false;
        }

        switch (edit.Widget)
        {
            case EditWidget.Picker:
                grid.Row(field.Label, () => Picker(id, field, edit));
                break;
            case EditWidget.Words:
                grid.Row(field.Label, () => Words(field, edit));
                break;
            case EditWidget.Number or EditWidget.WholeNumber:
                grid.Row(field.Label, () => Number(id, edit));
                break;
            case EditWidget.Position or EditWidget.Rotation:
                grid.Row(field.Label, () => Numbers(id, field, edit));
                break;
            case EditWidget.Bool:
                grid.Row(field.Label, () =>
                {
                    var on = edit.Value == "true";
                    if (ImGui.Checkbox("##bool", ref on))
                    {
                        Run(edit.Make?.Invoke(MissionValue.Of(on)));
                    }
                });
                break;
            case EditWidget.TextId:
                grid.Row(field.Label, () => TextId(id, field, edit));
                break;
            case EditWidget.Text:
                grid.Row(field.Label, () => Text(id, edit));
                break;
            case EditWidget.Triggers:
                grid.Row(field.Label, () => AddPicker(id, edit));
                break;
        }

        return true;
    }

    /// <summary>A name: a button with the name text, and the list picker with the "Missing" and "None" rows.</summary>
    private void Picker(string id, PageField field, FieldEdit edit)
    {
        var text = field.Missing ? $"{EditorIcons.ExclamationTriangle} {field.Value}"
            : edit.Value.Length == 0 ? "None"
            : field.Value;
        if (field.Missing)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
        }

        var open = ImGui.Button($"{text}##pick", new Vector2(ImGui.GetContentRegionAvail().X, 0));
        if (field.Missing)
        {
            ImGui.PopStyleColor();
        }

        ImGui.SetItemTooltip("Click to pick another value.");
        if (!pickers.TryGetValue(id, out var picker))
        {
            pickers[id] = picker = new ValuePicker();
        }

        var picked = picker.Draw("##picker", open, edit.Value, () => Entries(edit), edit.NoneWord != null,
            new ObjectKind(edit.Spec?.Label ?? field.Label, MissionGlyphs.OfValue(edit.Spec?.Kind ?? MissionValueKind.Text),
                EditorTheme.Label));
        if (picked != null)
        {
            var value = picked.Length == 0 && edit.NoneWord is { Length: > 0 } none ? none : picked;
            Step(id, edit.Make?.Invoke(MissionValue.Of(value)));
        }

        if (!picker.IsOpen && gesture is var (owner, _) && owner == id)
        {
            EndGesture();
        }
    }

    /// <summary>The entries of the lists of a picker, with their display names. A mission list has the entries of the
    /// script.</summary>
    private IReadOnlyList<(ListEntry Entry, string Name)> Entries(FieldEdit edit)
    {
        if (window.Session is not { } session || edit.List == null)
        {
            return [];
        }

        var result = new List<(ListEntry, string)>();
        foreach (var id in edit.List.Split('+'))
        {
            var list = id.StartsWith("mission_", StringComparison.Ordinal) && edit.Scope != null
                ? session.Edits.Lists.List(id, edit.Scope)
                : session.Edits.Lists.List(id);
            foreach (var entry in list?.Entries ?? [])
            {
                var name = entry.Section != null && session.SectionName(entry.Section) is { Length: > 0 } text
                    ? text
                    : entry.Name ?? entry.Value;
                result.Add((entry, name));
            }
        }

        return result;
    }

    /// <summary>True when a list of the lists of a picker has a value. A mission list has the names of the script.</summary>
    private bool Resolves(string ids, string? scope, string value)
    {
        if (window.Session is not { } session)
        {
            return true;
        }

        foreach (var id in ids.Split('+'))
        {
            var list = id.StartsWith("mission_", StringComparison.Ordinal) && scope != null
                ? session.Edits.Lists.List(id, scope)
                : session.Edits.Lists.List(id);
            // A list that is not available checks nothing.
            if (list == null || list.Find(value) != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A dropdown of the words of a set, or of the conditions of the field "Event" by event group.</summary>
    private void Words(PageField field, FieldEdit edit)
    {
        var current = edit.Choices.FirstOrDefault(c => string.Equals(c.Value, edit.Value, StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##words", current.Label ?? (edit.Value.Length > 0 ? edit.Value : field.Value)))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        string? group = null;
        foreach (var (value, label, choiceGroup) in edit.Choices)
        {
            if (choiceGroup != null && choiceGroup != group)
            {
                group = choiceGroup;
                ImGui.SeparatorText(group);
            }

            if (ImGui.Selectable($"{label}##{value}", string.Equals(value, edit.Value, StringComparison.OrdinalIgnoreCase)))
            {
                Run(edit.Make?.Invoke(MissionValue.Of(value)));
            }

            if (!string.Equals(label, value, StringComparison.Ordinal))
            {
                ImGui.SetItemTooltip(value);
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndCombo();
    }

    /// <summary>A number drag (M20). The drag is one transaction, and typed text ends with the field.</summary>
    private void Number(string id, FieldEdit edit)
    {
        ImGui.SetNextItemWidth(-1);
        if (edit.Widget == EditWidget.WholeNumber)
        {
            var value = (int)Parse(edit.Value);
            if (ImGui.DragInt("##number", ref value, 1))
            {
                Step(id, edit.Make?.Invoke(MissionValue.Of(value)));
            }
        }
        else
        {
            var value = (float)Parse(edit.Value);
            if (ImGui.DragFloat("##number", ref value, Math.Max(0.01f, Math.Abs(value) / 100), 0, 0, "%g"))
            {
                Step(id, edit.Make?.Invoke(MissionValue.Of((double)value)));
            }
        }

        if (ImGui.IsItemDeactivated())
        {
            EndGesture();
        }
    }

    /// <summary>Three numbers of a position with "Pick in Scene", or four numbers of a rotation.</summary>
    private void Numbers(string id, PageField field, FieldEdit edit)
    {
        var parts = edit.Value.Split(',').Select(p => (float)Parse(p)).ToList();
        var count = edit.Widget == EditWidget.Rotation ? 4 : 3;
        while (parts.Count < count)
        {
            parts.Add(edit.Widget == EditWidget.Rotation && parts.Count == 0 ? 1 : 0);
        }

        var pick = edit.PickInScene ? ImGui.CalcTextSize("Pick in Scene").X + ImGui.GetStyle().FramePadding.X * 2 +
                                      ImGui.GetStyle().ItemSpacing.X : 0;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - pick);
        var changed = false;
        if (count == 3)
        {
            var vector = new Vector3(parts[0], parts[1], parts[2]);
            changed = ImGui.DragFloat3("##position", ref vector, 1, 0, 0, "%g");
            parts = [vector.X, vector.Y, vector.Z];
        }
        else
        {
            var vector = new Vector4(parts[0], parts[1], parts[2], parts[3]);
            changed = ImGui.DragFloat4("##rotation", ref vector, 0.01f, 0, 0, "%g");
            parts = [vector.X, vector.Y, vector.Z, vector.W];
        }

        if (changed)
        {
            Step(id, edit.Make?.Invoke(MissionValue.Of([.. parts.Select(p => (double)p)])));
        }

        if (ImGui.IsItemDeactivated())
        {
            EndGesture();
        }

        if (edit.PickInScene)
        {
            ImGui.SameLine();
            var waiting = window.Missions.PendingPick?.Label == field.Label;
            if (ImGui.Button(waiting ? "Picking...##pick" : "Pick in Scene##pick"))
            {
                if (waiting)
                {
                    window.Missions.CancelPick();
                }
                else
                {
                    window.Missions.StartPick(field.Label, point =>
                        Run(edit.Make?.Invoke(MissionValue.Of(point.X, point.Y, point.Z))));
                }
            }

            ImGui.SetItemTooltip(PickTip);
        }
    }

    /// <summary>A text id: the number, and own text that the editor writes into the text of the mod.</summary>
    private void TextId(string id, PageField field, FieldEdit edit)
    {
        var number = (int)Parse(edit.Value);
        ImGui.SetNextItemWidth(Math.Min(120 * ImGuiHelper.Scale, ImGui.GetContentRegionAvail().X));
        ImGui.InputInt("##id", ref number, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Run(edit.Make?.Invoke(MissionValue.Of(number)));
        }

        ImGui.SetItemTooltip(field.Value);
        if (edit.MakeText == null)
        {
            return;
        }

        ImGui.SameLine();
        var own = texts.GetValueOrDefault(id + ".own") ?? "";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##own", "Own text", ref own, 1024, ImGuiInputTextFlags.EnterReturnsTrue) &&
            own.Trim().Length > 0)
        {
            Run(edit.MakeText(own.Trim()));
            own = "";
        }

        ImGui.SetItemTooltip("Type the text of this line. The editor writes it into the text of the mod. Enter sets it.");
        texts[id + ".own"] = own;
    }

    /// <summary>A text: the edit runs when the field ends with a change.</summary>
    private void Text(string id, FieldEdit edit)
    {
        // The field keeps the typed text while it is active. Else it shows the value of the line.
        var text = texts.TryGetValue(id, out var typed) ? typed : edit.Value;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##text", ref text, 512);
        if (ImGui.IsItemActive())
        {
            texts[id] = text;
            return;
        }

        texts.Remove(id);
        if (ImGui.IsItemDeactivatedAfterEdit() && text.Trim() != edit.Value)
        {
            Run(edit.Make?.Invoke(MissionValue.Of(text.Trim())));
        }
    }

    /// <summary>The "Add" of a list of triggers or members: a picker whose pick adds the item.</summary>
    private void AddPicker(string id, FieldEdit edit)
    {
        var open = ImGui.Button($"{EditorIcons.Plus}  Add##add");
        if (!pickers.TryGetValue(id, out var picker))
        {
            pickers[id] = picker = new ValuePicker();
        }

        var picked = picker.Draw("##add-picker", open, "", () => Entries(edit), false,
            new ObjectKind("Item", EditorIcons.Bolt, EditorTheme.Label));
        // Each pick of the open picker moves the selection. The last pick adds its item when the picker closes. Esc
        // gives the empty value of the opening, so it adds nothing.
        if (picked != null)
        {
            pendingAdd[id] = picked;
        }

        if (!picker.IsOpen && pendingAdd.Remove(id, out var last) && last.Length > 0)
        {
            Run(edit.Add?.Invoke(last));
        }
    }

    // The last pick of an open "Add" picker: the item goes in when the picker closes.
    private readonly Dictionary<string, string> pendingAdd = [];

    /// <summary>A list field of the page: one gesture of the widget gives one batch.</summary>
    private void ListEdit(string key, PageField field, ListFieldModel list)
    {
        if (!lists.TryGetValue(key, out var widget))
        {
            lists[key] = widget = new ListField();
        }

        var edit = field.Edit;
        var readOnly = window.Missions.ReadOnlyNote ?? edit?.ReadOnly ?? (edit?.MakeList == null ? MissionPageModel.FieldNote : null);
        // The cell checks show at once (section 8.5 of the M6b design): a name outside its lists, a number outside its
        // range, a fraction in a whole number and a word in the wrong letter case.
        var problems = readOnly == null ? list.Check((ids, value) => Resolves(ids, edit?.Scope, value)) : null;
        grid.FullRow(() =>
        {
            var changed = widget.Draw(key, list, null, readOnly, problems,
                entries: id => Entries(new FieldEdit(EditWidget.Picker, "") { List = id, Scope = edit?.Scope }));
            if (changed != null && edit?.MakeList is { } make)
            {
                Run(make(changed));
            }
        });
    }

    /// <summary>The small menu button of a row with actions, for example "Move up" of an action line.</summary>
    private void RowMenu(string id, IReadOnlyList<PageAction> actions)
    {
        if (actions.Count == 0)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"{EditorIcons.EllipsisV}##menu-{id}"))
        {
            ImGui.OpenPopup($"##row-menu-{id}");
        }

        ImGui.SetItemTooltip("Line menu");
        Actions($"##row-menu-{id}", actions);
    }

    /// <summary>The popup of a menu of actions.</summary>
    private void Actions(string popup, IReadOnlyList<PageAction> actions)
    {
        if (!ImGui.BeginPopup(popup))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        foreach (var action in actions)
        {
            if (ImGui.MenuItem(action.Label))
            {
                if (action.Batch != null)
                {
                    Run(action.Batch);
                }
                else if (action.Link != null)
                {
                    GoTo(action.Link);
                }
            }

            if (action.Tooltip != null)
            {
                ImGui.SetItemTooltip(action.Tooltip);
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The page menu at the right of the header: "Rename" and the actions of the page.</summary>
    private void PageMenu(MissionPageModel model)
    {
        if (model.PageActions.Count == 0 && model.Rename == null || window.Missions.ReadOnlyNote != null)
        {
            return;
        }

        if (ImGui.SmallButton($"{EditorIcons.EllipsisV}##page-menu"))
        {
            ImGui.OpenPopup("##page-menu");
        }

        ImGui.SetItemTooltip("Page menu");
        if (ImGui.BeginPopup("##page-menu"))
        {
            EditorTheme.PushMenuColors();
            if (model.Rename != null && ImGui.MenuItem("Rename", "F2"))
            {
                renameText = model.Title;
                renameOpen = true;
            }

            foreach (var action in model.PageActions)
            {
                if (ImGui.MenuItem(action.Label))
                {
                    Run(action.Batch);
                }
            }

            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        if (renameOpen)
        {
            ImGui.OpenPopup("##rename");
            renameOpen = false;
        }

        if (ImGui.BeginPopup("##rename"))
        {
            ImGui.Text("New name");
            if (ImGui.IsWindowAppearing())
            {
                ImGui.SetKeyboardFocusHere();
            }

            if (ImGui.InputText("##rename-text", ref renameText, 128, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                Run(model.Rename?.Invoke(renameText));
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    /// <summary>The button "Add action" at the end of a Trigger page, and quick add of the page. Quick add of the "+" of
    /// a group shows only the actions of the group.</summary>
    private void AddAction(MissionPageModel model)
    {
        if (model.PageTrigger is not { } trigger || model.ReadOnly != null || window.Missions.ReadOnlyNote != null)
        {
            return;
        }

        if (ImGui.Button($"{EditorIcons.Plus}  Add action"))
        {
            quickAdd.Open(new QuickAddContext(null, trigger, QuickAddFilter.Actions), ImGui.GetItemRectMin());
        }

        ImGui.SetItemTooltip("Add an action at the end of the trigger.");
        if (quickAdd.Draw(MissionLibrary.Sections(null)) is var (_, item))
        {
            Run(GraphEditModel.AddTo(trigger, item));
        }
    }

    /// <summary>Opens quick add of the actions of one group, for the "+" of a group of the Trigger page.</summary>
    private void AddToGroup(MissionTrigger trigger, string group) =>
        quickAdd.Open(new QuickAddContext(null, trigger, QuickAddFilter.Actions, group), ImGui.GetMousePos());

    /// <summary>The buttons of the actions of a section, for example "This ship only".</summary>
    private void SectionActions(PageSection section)
    {
        if (section.Actions.Count == 0 || window.Missions.ReadOnlyNote != null)
        {
            return;
        }

        grid.FullRow(() =>
        {
            foreach (var action in section.Actions)
            {
                if (ImGui.Button(action.Label))
                {
                    Run(action.Batch);
                }

                if (action.Tooltip != null)
                {
                    ImGui.SetItemTooltip(action.Tooltip);
                }

                ImGui.SameLine();
            }

            ImGui.NewLine();
        });
    }

    private static double Parse(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Inspector;

namespace OpenLancer.Editor.Ui.Fields;

/// <summary>
/// The list field (AGENTS.md UI rule 3, section 11.1 of the M6a design): a list with one row for each entry, with named
/// cells, as the ReorderableList of Unity. The editable state uses the shared <see cref="ReorderableList"/> with Add,
/// Remove and reorder. Each cell has the editor of its column. A name of a data list gets the list picker (UI rule 2),
/// and a word of a set gets a dropdown. A number gets a drag field, and a position or a rotation gets one drag field
/// for each number. The read-only state shows the rows with their cells, and a right-click copies a cell. The model
/// (<see cref="ListFieldModel"/>) writes the retail lines.
/// </summary>
/// <remarks>One field object keeps the state of the picker of its cells, so each list field of a page has its own
/// object.</remarks>
internal sealed class ListField
{
    /// <summary>Above this number of rows, the read-only list draws only the rows in view.</summary>
    private const int ClipRows = 50;

    /// <summary>The tooltip end of a read-only cell.</summary>
    internal const string CopyHint = "Right-click to copy the value.";

    private readonly ValuePicker picker = new();

    /// <summary>
    /// Draws a list field. Returns the model after an edit of the user, or null without an edit. A read-only field never
    /// changes. One gesture gives one model, so the page makes one command of it.
    /// </summary>
    /// <param name="id">The id of the field. Two fields in one window need different ids.</param>
    /// <param name="help">The help text of the field, or null.</param>
    /// <param name="readOnly">The reason why the field cannot change, or null for an editable field.</param>
    /// <param name="problems">The problems of the cells, for example of <see cref="ListFieldModel.Check"/>.</param>
    /// <param name="entries">Gives the entries of the data lists of a reference column, with their display names. It
    /// takes the id of <see cref="ListColumn.List"/>. Null when the page has no lists. A reference cell then has a text
    /// field.</param>
    /// <param name="changed">True when the active mod sets the list, also with an edit that is not saved. The label then
    /// gets the blue bar.</param>
    public ListFieldModel? Draw(string id, ListFieldModel model, HelpText? help, string? readOnly,
        IReadOnlyList<ListCellProblem>? problems = null,
        Func<string, IReadOnlyList<(ListEntry Entry, string Name)>>? entries = null, bool changed = false)
    {
        ImGui.PushID(id);
        var spec = model.Spec;
        // A list that the active mod sets has the label in the accent color and the blue bar (UI rule 7).
        if (changed)
        {
            PropertyGrid.ChangedBar(ImGui.GetCursorScreenPos());
        }

        ImGui.PushStyleColor(ImGuiCol.Text, changed ? EditorTheme.AccentText : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        ImGui.Text(string.Create(CultureInfo.InvariantCulture, $"{spec.Label} ({model.Rows.Count})"));
        ImGui.PopStyleColor();
        if (spec.Help != null)
        {
            ImGui.SetItemTooltip(spec.Help);
        }

        if (help != null)
        {
            ImGui.SameLine();
            InfoIcon.Draw("##help", help);
        }

        ListFieldModel? result = null;
        if (model.Rows.Count == 0)
        {
            ImGui.TextDisabled($"No {spec.Items}.");
            if (readOnly == null)
            {
                ImGui.TextDisabled("Click Add to add one.");
            }
        }

        if (readOnly != null)
        {
            ReadOnlyRows(model, problems);
        }
        else
        {
            var mixed = spec.Codec == ListFieldCodec.MixedKeys;
            var edited = ReorderableList.Draw("##rows", model.Rows.Select((r, i) => new EditRow(r, i)).ToList(),
                (index, item, width) => item with { Row = Edit(model, item.Row, index, width, entries) },
                mixed ? null : () => new EditRow(new ListFieldRow(spec.Keys[0], [], null) { Changed = true }, -1),
                AddLabel(spec));
            if (edited != null)
            {
                result = Rebuild(model, edited.Select(r => (r.Row, r.Index)).ToList());
            }

            // A mixed-keys field adds a row of a key that the user picks from a menu, for example an order.
            if (mixed && AddMenu(spec) is { } key)
            {
                result = (result ?? model).Add(int.MaxValue, new ListFieldRow(key, [], null));
            }
        }

        ImGui.PopID();
        return result;
    }

    /// <summary>The text of the Add button: the label of the spec, or "Add".</summary>
    internal static string AddLabel(ListFieldSpec spec) => spec.AddLabel ?? "Add";

    /// <summary>The Add button of a mixed-keys field and its menu of keys. Returns the key that the user picks, or
    /// null.</summary>
    private static string? AddMenu(ListFieldSpec spec)
    {
        if (ImGui.Button($"{EditorIcons.Plus}  {AddLabel(spec)}"))
        {
            ImGui.OpenPopup("##add-keys");
        }

        if (!ImGui.BeginPopup("##add-keys"))
        {
            return null;
        }

        string? picked = null;
        EditorTheme.PushMenuColors();
        foreach (var key in spec.Keys)
        {
            if (ImGui.MenuItem(spec.KeyLabel(key)))
            {
                picked = key;
            }

            ImGui.SetItemTooltip(key);
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
        return picked;
    }

    /// <summary>A row of the editable list, with its index in the model, or -1 for a new row.</summary>
    private sealed record EditRow(ListFieldRow Row, int Index);

    /// <summary>The model for the rows of the list after an edit, through the operations of the model: a removed row
    /// goes, an added row comes, a moved row moves, and a changed row keeps its cells.</summary>
    internal static ListFieldModel Rebuild(ListFieldModel model, IReadOnlyList<(ListFieldRow Row, int Index)> rows)
    {
        var result = model;
        var kept = rows.Where(r => r.Index >= 0).Select(r => r.Index).ToHashSet();
        // Remove the rows that are gone, from the end, so that the indexes of the other rows stay.
        for (var i = model.Rows.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(i))
            {
                result = result.Remove(i);
            }
        }

        // The model index of each row of the result, in order. A new row has -1.
        var order = Enumerable.Range(0, model.Rows.Count).Where(kept.Contains).ToList();
        for (var target = 0; target < rows.Count; target++)
        {
            var (row, index) = rows[target];
            if (index < 0)
            {
                result = result.Add(target, row);
                order.Insert(target, -1);
                continue;
            }

            var from = order.IndexOf(index);
            if (from != target)
            {
                result = result.Move(from, target);
                order.RemoveAt(from);
                order.Insert(target, index);
            }

            for (var v = 0; v < row.Cells.Count; v++)
            {
                result = result.Set(target, v, row.Cells[v]);
            }
        }

        return result;
    }

    /// <summary>The cells of one editable row, each with the editor of its column. A mixed-keys row starts with the
    /// plain name of its key.</summary>
    private ListFieldRow Edit(ListFieldModel model, ListFieldRow row, int index, float width,
        Func<string, IReadOnlyList<(ListEntry Entry, string Name)>>? entries)
    {
        var spec = model.Spec;
        if (spec.Codec == ListFieldCodec.MixedKeys)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(spec.KeyLabel(row.Key));
            ImGui.SameLine();
            width = MathF.Max(40, ImGui.GetContentRegionAvail().X);
        }

        var columns = spec.ColumnsOf(row.Key);
        var cells = row.Cells.ToList();
        var count = columns.Sum(c => c.End(cells.Count) - c.Index);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cellWidth = MathF.Max(40, width / Math.Max(1, count) - spacing);
        var first = true;
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            for (var v = column.Index; v < column.End(cells.Count); v++)
            {
                while (cells.Count <= v)
                {
                    cells.Add("");
                }

                if (!first)
                {
                    ImGui.SameLine();
                }

                first = false;
                ImGui.PushID(v);
                ImGui.SetNextItemWidth(cellWidth);
                cells[v] = Cell(column, cells[v], cellWidth, entries) ?? cells[v];
                ImGui.PopID();
            }
        }

        return cells.SequenceEqual(row.Cells) ? row : row with { Cells = cells, Changed = true };
    }

    /// <summary>The editor of one value of a cell. Returns the new text after an edit, or null.</summary>
    private string? Cell(ListColumn column, string text, float width,
        Func<string, IReadOnlyList<(ListEntry Entry, string Name)>>? entries)
    {
        string? result = null;
        switch (column.Kind)
        {
            case ListColumnKind.Reference when column.List is { } list && entries != null:
                var known = entries(list).Any(e => e.Entry.Value.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
                var missing = text.Trim().Length > 0 && !known;
                if (missing)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
                }

                var open = ImGui.Button($"{(text.Trim().Length == 0 ? "None" : missing ? $"Missing: {text}" : text)}##pick",
                    new Vector2(width, 0));
                if (missing)
                {
                    ImGui.PopStyleColor();
                }

                result = picker.Draw("##picker", open, text, () => entries(list), none: true, ObjectKinds.OtherObject);
                break;
            case ListColumnKind.Token when column.Tokens is { Count: > 0 } words:
                if (ImGui.BeginCombo("##token", text.Length == 0 ? column.Name : text))
                {
                    EditorTheme.PushMenuColors();
                    foreach (var word in words)
                    {
                        if (ImGui.Selectable(word, word.Equals(text, StringComparison.Ordinal)))
                        {
                            result = word;
                        }
                    }

                    EditorTheme.PopMenuColors();
                    ImGui.EndCombo();
                }

                break;
            case ListColumnKind.WholeNumber:
                var whole = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d
                    : 0;
                if (ImGui.DragInt("##number", ref whole, 1))
                {
                    result = whole.ToString(CultureInfo.InvariantCulture);
                }

                break;
            case ListColumnKind.Number or ListColumnKind.Position or ListColumnKind.Rotation:
                var number = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? (float)parsed
                    : 0f;
                // The step grows with the value, as the label drag of the Inspector: 10 for a position.
                var speed = column.Kind == ListColumnKind.Position ? 10f : MathF.Max(0.01f, MathF.Abs(number) / 100);
                if (ImGui.DragFloat("##number", ref number, speed, 0, 0, "%g"))
                {
                    result = Math.Round((double)number, 3).ToString("0.###", CultureInfo.InvariantCulture);
                }

                break;
            default:
                var value = text;
                if (ImGui.InputTextWithHint("##text", column.Name, ref value, 256))
                {
                    result = value;
                }

                break;
        }

        ItemTooltip.Show(column.Help != null ? $"{column.Name}\n{column.Help}" : column.Name);
        return result;
    }

    private static void ReadOnlyRows(ListFieldModel model, IReadOnlyList<ListCellProblem>? problems)
    {
        var rows = model.Rows;
        if (rows.Count <= ClipRows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                ReadOnlyRow(model, rows[i], i, problems);
            }

            return;
        }

        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ReadOnlyRow(model, rows[i], i, problems);
            }
        }

        clipper.End();
    }

    private static void ReadOnlyRow(ListFieldModel model, ListFieldRow row, int index,
        IReadOnlyList<ListCellProblem>? problems)
    {
        ImGui.PushID(index);
        ImGui.TextDisabled((index + 1).ToString(CultureInfo.InvariantCulture));
        if (model.Spec.Codec == ListFieldCodec.MixedKeys)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(model.Spec.KeyLabel(row.Key));
        }

        var columns = model.Spec.ColumnsOf(row.Key);
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            ImGui.SameLine();
            var text = CellText(column, row);
            var problem = problems?.FirstOrDefault(p => p.Row == index && p.Column == c);
            if (problem != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text,
                    problem.Severity == DiagnosticSeverity.Error ? EditorTheme.Error : EditorTheme.Warning);
            }

            ImGui.Text(text.Length == 0 ? "-" : text);
            if (problem != null)
            {
                ImGui.PopStyleColor();
            }

            ItemTooltip.Show(problem != null
                ? $"{column.Name}: {text}\n{problem.Message}\n{CopyHint}"
                : $"{column.Name}: {text}\n{CopyHint}");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                ImGui.SetClipboardText(text);
            }
        }

        ImGui.PopID();
    }

    /// <summary>The text of a cell of a read-only row: its values, without empty values.</summary>
    internal static string CellText(ListColumn column, ListFieldRow row) =>
        string.Join(", ", Enumerable.Range(column.Index, column.End(row.Cells.Count) - column.Index).Select(row.Cell)
            .Where(t => t.Length > 0));
}

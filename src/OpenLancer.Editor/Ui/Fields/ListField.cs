using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Editor.Ui.Fields;

/// <summary>
/// The list field (AGENTS.md UI rule 3, section 11.1 of the M6a design): a list with one row for each entry, with named
/// cells, as the ReorderableList of Unity. The editable state uses the shared <see cref="ReorderableList"/> with Add,
/// Remove and reorder. The read-only state shows the rows with their cells, and a right-click copies a cell. The model
/// (<see cref="ListFieldModel"/>) writes the retail lines.
/// </summary>
internal static class ListField
{
    /// <summary>Above this number of rows, the read-only list draws only the rows in view.</summary>
    private const int ClipRows = 50;

    /// <summary>
    /// Draws a list field. Returns the model after an edit of the user, or null without an edit. A read-only field never
    /// changes.
    /// </summary>
    /// <param name="id">The id of the field. Two fields in one window need different ids.</param>
    /// <param name="items">The plural word of the rows for the empty state, for example "ships".</param>
    /// <param name="help">The help text of the field, or null.</param>
    /// <param name="readOnly">The reason why the field cannot change, or null for an editable field.</param>
    /// <param name="problems">The problems of the cells, for example of <see cref="ListFieldModel.Check"/>.</param>
    public static ListFieldModel? Draw(string id, ListFieldModel model, string items, HelpText? help, string? readOnly,
        IReadOnlyList<ListCellProblem>? problems = null)
    {
        ImGui.PushID(id);
        var spec = model.Spec;
        ImGui.Text(string.Create(CultureInfo.InvariantCulture, $"{spec.Label} ({model.Rows.Count})"));
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
            ImGui.TextDisabled($"No {items}.");
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
            var edited = ReorderableList.Draw("##rows", model.Rows.Select((r, i) => new EditRow(r, i)).ToList(),
                (index, item, width) => item with { Row = Edit(model, item.Row, width) },
                () => new EditRow(new ListFieldRow(spec.Keys[0], [], null) { Changed = true }, -1),
                spec.Codec == ListFieldCodec.MixedKeys ? "Add order" : "Add");
            if (edited != null)
            {
                result = Rebuild(model, edited);
            }
        }

        ImGui.PopID();
        return result;
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

    private static IReadOnlyList<(ListFieldRow Row, int Index)> Rebuild(IReadOnlyList<EditRow> rows) =>
        rows.Select(r => (r.Row, r.Index)).ToList();

    private static ListFieldModel Rebuild(ListFieldModel model, IReadOnlyList<EditRow> rows) =>
        Rebuild(model, Rebuild(rows));

    private static ListFieldRow Edit(ListFieldModel model, ListFieldRow row, float width)
    {
        var columns = model.Spec.ColumnsOf(row.Key);
        var cells = row.Cells.ToList();
        var cellWidth = MathF.Max(40, width / Math.Max(1, columns.Count) - ImGui.GetStyle().ItemSpacing.X);
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            for (var v = column.Index; v < column.Index + column.Width; v++)
            {
                while (cells.Count <= v)
                {
                    cells.Add("");
                }

                if (c > 0 || v > column.Index)
                {
                    ImGui.SameLine();
                }

                var text = cells[v];
                ImGui.SetNextItemWidth(column.Width > 1 ? cellWidth / column.Width : cellWidth);
                if (ImGui.InputTextWithHint($"##c{c}v{v}", column.Name, ref text, 256))
                {
                    cells[v] = text;
                }

                if (column.Help != null)
                {
                    ImGui.SetItemTooltip(column.Help);
                }
            }
        }

        return cells.SequenceEqual(row.Cells) ? row : row with { Cells = cells, Changed = true };
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
        var columns = model.Spec.ColumnsOf(row.Key);
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            ImGui.SameLine();
            var text = string.Join(", ", Enumerable.Range(column.Index, column.Width).Select(row.Cell)
                .Where(t => t.Length > 0));
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

            ItemTooltip.Show(problem != null ? $"{column.Name}: {text}\n{problem.Message}" : $"{column.Name}: {text}");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                ImGui.SetClipboardText(text);
            }
        }

        ImGui.PopID();
    }
}

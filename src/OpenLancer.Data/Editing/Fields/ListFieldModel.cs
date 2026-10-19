using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;

namespace OpenLancer.Data.Editing.Fields;

/// <summary>How a list field reads its rows from the lines of a section (decision D540, section 11.1 of the M6a
/// design).</summary>
public enum ListFieldCodec
{
    /// <summary>One row for each line of one key, for example the <c>ship</c> lines of a formation.</summary>
    RepeatedKey,

    /// <summary>The rows are value groups of a fixed width in one line, after a fixed prefix. For example, the ships of
    /// <c>Cnd_NPCSystemEnter = system, ship1, ship2</c>.</summary>
    ValueList,

    /// <summary>One row for each line whose key is in a set, in file order, with a column set for each key. For example,
    /// the orders of an order list.</summary>
    MixedKeys,
}

/// <summary>The kind of the cells of a column.</summary>
public enum ListColumnKind
{
    /// <summary>A name of a data list, for example a ship.</summary>
    Reference,
    Number,

    /// <summary>A number that the game reads as a whole number (TR15).</summary>
    WholeNumber,

    /// <summary>A word of a closed set.</summary>
    Token,
    Text,

    /// <summary>Three numbers: x, y and z.</summary>
    Position,
}

/// <summary>One column of a list field.</summary>
/// <param name="Name">The plain name, for example "Ship".</param>
/// <param name="Index">The index of the first value of the column in a row.</param>
/// <param name="List">The id of the data list of a reference column, or null.</param>
/// <param name="Tokens">The words of a token column, or null.</param>
/// <param name="CaseSensitive">True when the game compares the words with letter case (TR16).</param>
/// <param name="Key">The key of the column set of a mixed-keys field, or null.</param>
public sealed record ListColumn(string Name, int Index, ListColumnKind Kind, string? List = null,
    IReadOnlyList<string>? Tokens = null, bool CaseSensitive = false, string? Help = null, double? Min = null,
    double? Max = null, string? Key = null)
{
    /// <summary>The number of values of a cell: 3 for a position, else 1.</summary>
    public int Width => Kind == ListColumnKind.Position ? 3 : 1;
}

/// <summary>The spec of a list field (section 11.1 of the M6a design). The rule data gives it.</summary>
/// <param name="Keys">The key of a repeated-key or value-list field, or the keys of a mixed-keys field.</param>
/// <param name="Prefix">The number of values before the rows of a value-list field.</param>
/// <param name="Columns">The columns. A mixed-keys field has a column set for each key.</param>
public sealed record ListFieldSpec(string Id, string Label, string? Help, ListFieldCodec Codec, IReadOnlyList<string> Keys,
    IReadOnlyList<ListColumn> Columns, int Prefix = 0)
{
    /// <summary>The columns of the rows of a key: the columns of its set for a mixed-keys field, else all.</summary>
    public IReadOnlyList<ListColumn> ColumnsOf(string? key) => Codec == ListFieldCodec.MixedKeys
        ? Columns.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).ToList()
        : Columns;

    /// <summary>The number of values of one row of a value-list field.</summary>
    public int GroupWidth => Columns.Count == 0 ? 1 : Columns.Max(c => c.Index + c.Width);
}

/// <summary>One line of a section: its key and its values.</summary>
public sealed record ListFieldLine(string Key, IReadOnlyList<ValueBase> Values)
{
    /// <summary>The line of an entry of the parser.</summary>
    public static ListFieldLine Of(Entry entry) => new(entry.Name, entry.ToList());

    /// <summary>The value texts, as the retail game reads them.</summary>
    public IReadOnlyList<string> Texts => Values.Select(RetailText.Of).ToList();
}

/// <summary>One row of a list field. A row keeps its source values, so that an unchanged row writes them again with
/// the same values and value types.</summary>
/// <param name="Key">The key of the row: the key of its line, or the key of a mixed-keys row.</param>
/// <param name="Cells">The text of each value of the row, in value order.</param>
/// <param name="Source">The values of the row in its source line, or null for a new row.</param>
public sealed record ListFieldRow(string Key, IReadOnlyList<string> Cells, IReadOnlyList<ValueBase>? Source)
{
    /// <summary>True when a cell changed after the read, or the row is new.</summary>
    public bool Changed { get; init; }

    /// <summary>The text of a value of the row, or an empty text.</summary>
    public string Cell(int index) => index < Cells.Count ? Cells[index] : "";
}

/// <summary>A problem of one cell of a list field.</summary>
/// <param name="Row">The index of the row.</param>
/// <param name="Column">The index of the column in the columns of the row.</param>
public sealed record ListCellProblem(int Row, int Column, DiagnosticSeverity Severity, string Message);

/// <summary>
/// The model of a list field (AGENTS.md UI rule 3, section 11.1 of the M6a design): the rows of a list of a section,
/// with Add, Remove, Move and Set, and the write of the retail lines. Each operation gives a new model and does not
/// change the old one, so the edit history can keep both. The model makes no ImGui call, so the tests check it.
/// </summary>
public sealed class ListFieldModel
{
    private ListFieldModel(ListFieldSpec spec, IReadOnlyList<ListFieldRow> rows, ListFieldLine? line)
    {
        Spec = spec;
        Rows = rows;
        PrefixLine = line;
    }

    public ListFieldSpec Spec { get; }

    public IReadOnlyList<ListFieldRow> Rows { get; }

    /// <summary>The source line of a value-list field, with its prefix values, or null.</summary>
    public ListFieldLine? PrefixLine { get; }

    /// <summary>Reads the rows of a field from the lines of a section, in file order.</summary>
    public static ListFieldModel Read(ListFieldSpec spec, IReadOnlyList<ListFieldLine> lines)
    {
        var rows = new List<ListFieldRow>();
        ListFieldLine? prefixLine = null;
        switch (spec.Codec)
        {
            case ListFieldCodec.RepeatedKey:
            case ListFieldCodec.MixedKeys:
                foreach (var line in lines.Where(l => spec.Keys.Any(k => k.Equals(l.Key, StringComparison.OrdinalIgnoreCase))))
                    rows.Add(new ListFieldRow(line.Key, line.Texts, line.Values));
                break;
            case ListFieldCodec.ValueList:
                prefixLine = lines.FirstOrDefault(l => spec.Keys.Any(k => k.Equals(l.Key, StringComparison.OrdinalIgnoreCase)));
                if (prefixLine == null)
                    break;
                var width = spec.GroupWidth;
                for (var at = spec.Prefix; at < prefixLine.Values.Count; at += width)
                {
                    var values = prefixLine.Values.Skip(at).Take(width).ToList();
                    rows.Add(new ListFieldRow(prefixLine.Key, values.Select(RetailText.Of).ToList(), values));
                }
                break;
        }
        return new ListFieldModel(spec, rows, prefixLine);
    }

    /// <summary>A model with a new row at an index. An index outside the rows adds the row at the end.</summary>
    public ListFieldModel Add(int index, ListFieldRow row)
    {
        var rows = Rows.ToList();
        rows.Insert(Math.Clamp(index, 0, rows.Count), row with { Changed = true });
        return With(rows);
    }

    /// <summary>A model without the row at an index. An index outside the rows changes nothing.</summary>
    public ListFieldModel Remove(int index)
    {
        if (index < 0 || index >= Rows.Count)
            return this;
        var rows = Rows.ToList();
        rows.RemoveAt(index);
        return With(rows);
    }

    /// <summary>A model with the row at <paramref name="from"/> at the place <paramref name="to"/>. An index outside the
    /// rows changes nothing.</summary>
    public ListFieldModel Move(int from, int to)
    {
        if (from < 0 || from >= Rows.Count || to < 0 || to >= Rows.Count || from == to)
            return this;
        var rows = Rows.ToList();
        var row = rows[from];
        rows.RemoveAt(from);
        rows.Insert(to, row);
        return With(rows);
    }

    /// <summary>A model with a new text in one value of a row. A row index outside the rows changes nothing.</summary>
    /// <param name="value">The index of the value in the row.</param>
    public ListFieldModel Set(int row, int value, string text)
    {
        if (row < 0 || row >= Rows.Count || value < 0)
            return this;
        var cells = Rows[row].Cells.ToList();
        while (cells.Count <= value)
            cells.Add("");
        if (cells[value] == text)
            return this;
        cells[value] = text;
        var rows = Rows.ToList();
        rows[row] = rows[row] with { Cells = cells, Changed = true };
        return With(rows);
    }

    private ListFieldModel With(IReadOnlyList<ListFieldRow> rows) => new(Spec, rows, PrefixLine);

    /// <summary>
    /// The lines of the field in the retail line format, in row order. A row with no changed cell writes its source
    /// values, so an edit of one row never changes the text of the other rows. A value-list field gives one line: the
    /// prefix values of its source line, then the values of each row.
    /// </summary>
    public IReadOnlyList<ListFieldLine> Write()
    {
        if (Spec.Codec == ListFieldCodec.ValueList)
        {
            var key = PrefixLine?.Key ?? Spec.Keys[0];
            var values = new List<ValueBase>(PrefixLine?.Values.Take(Spec.Prefix) ?? []);
            foreach (var row in Rows)
                values.AddRange(ValuesOf(row));
            return [new ListFieldLine(key, values)];
        }
        return Rows.Select(r => new ListFieldLine(r.Key, ValuesOf(r))).ToList();
    }

    /// <summary>The values of a row: its source values when no cell changed, else a value for each cell. An empty last
    /// cell keeps its place, so the line keeps its comma.</summary>
    private static IReadOnlyList<ValueBase> ValuesOf(ListFieldRow row)
    {
        if (!row.Changed && row.Source != null)
            return row.Source;
        var cells = row.Cells.ToList();
        while (cells.Count > 0 && cells[^1].Length == 0)
            cells.RemoveAt(cells.Count - 1);
        return cells.Select(c => (ValueBase)new StringValue(c)).ToList();
    }

    /// <summary>
    /// Checks each cell (section 11.1 of the M6a design): names outside their lists, numbers outside their ranges,
    /// fractions in whole numbers (TR15), and words in the wrong letter case (TR16). One problem for each bad cell.
    /// </summary>
    /// <param name="resolves">Tells whether a data list has a name, or null to skip the reference checks.</param>
    /// <param name="listLabel">Gives the label of a data list for a message, or null for the list id.</param>
    public IReadOnlyList<ListCellProblem> Check(Func<string, string, bool>? resolves, Func<string, string>? listLabel = null)
    {
        var result = new List<ListCellProblem>();
        for (var r = 0; r < Rows.Count; r++)
        {
            var columns = Spec.ColumnsOf(Rows[r].Key);
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                for (var v = column.Index; v < column.Index + column.Width; v++)
                {
                    var text = Rows[r].Cell(v).Trim();
                    if (text.Length == 0)
                        continue;
                    if (CheckCell(column, text, resolves, listLabel) is { } problem)
                    {
                        result.Add(new ListCellProblem(r, c, problem.Severity, problem.Message));
                        break;
                    }
                }
            }
        }
        return result;
    }

    private static (DiagnosticSeverity Severity, string Message)? CheckCell(ListColumn column, string text,
        Func<string, string, bool>? resolves, Func<string, string>? listLabel)
    {
        switch (column.Kind)
        {
            case ListColumnKind.Reference when column.List != null && resolves != null && !resolves(column.List, text):
                var label = (listLabel?.Invoke(column.List) ?? column.List).ToLowerInvariant();
                return (DiagnosticSeverity.Error,
                    $"'{text}' is not in the list of the {label} entries. The game ignores this value. Pick a value from " +
                    "the list.");
            case ListColumnKind.Number or ListColumnKind.WholeNumber or ListColumnKind.Position:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return (DiagnosticSeverity.Error, $"'{text}' is not a number. Write a number.");
                if (column.Min is { } min && number < min || column.Max is { } max && number > max)
                    return (DiagnosticSeverity.Warning,
                        $"{text} is outside {Format(column.Min)} to {Format(column.Max)}. Use a value from " +
                        $"{Format(column.Min)} to {Format(column.Max)}.");
                if (column.Kind == ListColumnKind.WholeNumber && number != Math.Truncate(number))
                    return (DiagnosticSeverity.Warning,
                        $"The game reads only the whole part of the {column.Name.ToLowerInvariant()}. So {text} becomes " +
                        $"{Format(Math.Truncate(number))}. Use a whole number.");
                return null;
            case ListColumnKind.Token when column.Tokens != null:
                var word = column.Tokens.FirstOrDefault(t => t.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (word == null)
                    return (DiagnosticSeverity.Warning,
                        $"'{text}' is not one of {string.Join(", ", column.Tokens)}. The game uses its default value. " +
                        "Pick a value from the list.");
                if (column.CaseSensitive && word != text)
                    return (DiagnosticSeverity.Error,
                        $"'{text}' must be '{word}'. The game compares this word with letter case, so the line does " +
                        $"nothing. Write '{word}'.");
                return null;
            default:
                return null;
        }
    }

    private static string Format(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
}

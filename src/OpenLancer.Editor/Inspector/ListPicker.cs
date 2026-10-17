using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>The kind of a row of the list picker.</summary>
internal enum ListRowKind
{
    /// <summary>"None": no line. Only a key that is not required has it.</summary>
    None,

    /// <summary>The value of the field, which is outside the list.</summary>
    Missing,

    /// <summary>An entry of the list.</summary>
    Entry,
}

/// <summary>One row of the list picker.</summary>
/// <param name="Value">The value that the row gives the field: empty for "None".</param>
/// <param name="Name">The display name: the text of the <c>ids_name</c> of the entry, or the nickname.</param>
/// <param name="Entry">The entry of an entry row, or null.</param>
internal sealed record ListPickerRow(ListRowKind Kind, string Value, string Name, ListEntry? Entry)
{
    /// <summary>The tag of an entry of the active mod: "new" for an entry that is not saved, else "mod".</summary>
    public string Tag => Entry is { Origin: ValueOrigin.ActiveMod } entry ? entry.Unsaved ? "new" : "mod" : "";

    /// <summary>The text of the row: the name, or "None" and "Missing: value".</summary>
    public string Text => Kind switch
    {
        ListRowKind.None => "None",
        ListRowKind.Missing => $"Missing: {Value}",
        _ => Name,
    };
}

/// <summary>
/// The rows of the list picker, without ImGui (section 12.2 of the M21 design): "None" for a key that is not required,
/// the "Missing" value, and the entries sorted by name and then by nickname. Each row has an index, so repeated names
/// never clash as ImGui ids.
/// </summary>
internal sealed class ListPickerModel
{
    /// <param name="entries">The entries with their display names.</param>
    /// <param name="none">True when the key can have no line: the first row is "None".</param>
    /// <param name="current">The value of the field: empty when the key has no line.</param>
    public ListPickerModel(IEnumerable<(ListEntry Entry, string Name)> entries, bool none, string current)
    {
        var rows = new List<ListPickerRow>();
        if (none)
            rows.Add(new ListPickerRow(ListRowKind.None, "", "None", null));
        var sorted = entries.Select(e => new ListPickerRow(ListRowKind.Entry, e.Entry.Value,
                string.IsNullOrWhiteSpace(e.Name) ? e.Entry.Value : e.Name, e.Entry))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        current = current.Trim();
        if (current.Length > 0 && !sorted.Any(r => Is(r.Value, current)))
            rows.Add(new ListPickerRow(ListRowKind.Missing, current, current, null));
        rows.AddRange(sorted);
        Rows = rows;
        Current = current.Length == 0
            ? rows.FindIndex(r => r.Kind == ListRowKind.None)
            : rows.FindIndex(r => r.Kind != ListRowKind.None && Is(r.Value, current));
    }

    public IReadOnlyList<ListPickerRow> Rows { get; }

    /// <summary>The row of the value of the field, or -1. The list scrolls to it when it opens.</summary>
    public int Current { get; }

    /// <summary>
    /// The indices of the rows that the filter shows: each entry whose name or nickname contains the text, without
    /// letter case. "None" and "Missing" show only without a filter, so Enter after a filter takes an entry.
    /// </summary>
    public List<int> Visible(string filter)
    {
        filter = filter.Trim();
        var result = new List<int>();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (filter.Length == 0 || row.Kind == ListRowKind.Entry &&
                (row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                 row.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                result.Add(i);
        }
        return result;
    }

    /// <summary>The row after a move of the selection by <paramref name="delta"/> in the visible rows. A hidden
    /// selection moves to the first or the last visible row.</summary>
    public static int Step(IReadOnlyList<int> visible, int selected, int delta)
    {
        if (visible.Count == 0)
            return selected;
        var position = IndexOf(visible, selected);
        if (position < 0)
            return delta > 0 ? visible[0] : visible[^1];
        return visible[Math.Clamp(position + delta, 0, visible.Count - 1)];
    }

    /// <summary>The row that Enter assigns: the selection, or the first visible row when the filter hides the
    /// selection. -1 when no row is visible.</summary>
    public static int EnterRow(IReadOnlyList<int> visible, int selected) =>
        IndexOf(visible, selected) >= 0 ? selected : visible.Count > 0 ? visible[0] : -1;

    /// <summary>
    /// The footer lines of a row: the name and the nickname, the kind, the file, the layer, the number of lines that
    /// name it and the detail keys of the list.
    /// </summary>
    /// <param name="kind">The label of the kind, for example "Loadout".</param>
    /// <param name="usedBy">The number of lines that name an entry.</param>
    public static IReadOnlyList<string> Footer(ListPickerRow row, string kind, Func<ListEntry, int> usedBy)
    {
        switch (row.Kind)
        {
            case ListRowKind.None:
                return ["No line. The engine uses its default."];
            case ListRowKind.Missing:
                return [$"{row.Value}: no {kind.ToLowerInvariant()} has this name."];
        }
        var entry = row.Entry!;
        var lines = new List<string>
        {
            Is(row.Name, row.Value) ? row.Value : $"{row.Name} ({row.Value})",
            entry.Section == null ? kind : $"{kind} in {entry.Section.Path}",
            entry.Origin switch
            {
                ValueOrigin.ActiveMod => entry.Unsaved
                    ? $"The active mod '{entry.ModId}' adds it. It is not saved."
                    : $"The active mod '{entry.ModId}' adds it.",
                ValueOrigin.LowerMod => $"Mod '{entry.ModId}' gives it.",
                _ => "The base data gives it.",
            },
            $"Used by {usedBy(entry)} lines",
        };
        if (entry.Detail.Length > 0)
            lines.Add(entry.Detail);
        if (entry.Problem != null)
            lines.Add(entry.Problem);
        return lines;
    }

    private static int IndexOf(IReadOnlyList<int> visible, int row)
    {
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i] == row)
                return i;
        }
        return -1;
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The popup of the list picker, as the Unity object picker (decision D113): a search field with the focus, "None", the
/// "Missing" value and the entries, and a footer with the hovered or selected entry. A click on a row and the arrow keys
/// assign the value at once. Enter assigns the selected row and closes. A double-click and a click outside close and keep
/// the value. Esc closes and puts back the value of the opening. One opening is one field edit, so one entry of the edit
/// history (see <see cref="PopupSession"/>).
/// </summary>
internal sealed class ListPicker
{
    // Sizes in unscaled points.
    private const float PopupWidth = 360;
    private const int VisibleRows = 14;

    private readonly PopupSession session = new();
    private string filter = "";
    private int selected = -1;
    private int hovered = -1;
    private ListPickerModel? model;

    /// <summary>The popup of a field. Call it in each frame for each field with a list, after the field draws, in the
    /// ImGui id scope of the field.</summary>
    /// <param name="open">Opens the popup in this frame, for example after a click on the field.</param>
    /// <param name="makeModel">Makes the rows when the popup opens.</param>
    /// <param name="kind">The kind of the entries.</param>
    /// <param name="usedBy">The number of lines that name an entry, for the footer.</param>
    /// <param name="owns">True when the open field edit belongs to the popup with an ImGui id.</param>
    /// <param name="route">Gives the events of the popup and the value of the selected row to the field edits.</param>
    public void Draw(string id, bool open, Func<ListPickerModel> makeModel, ObjectKind kind, Func<ListEntry, int> usedBy,
        Func<uint, bool> owns, Action<uint, FieldEvents, string> route)
    {
        var popup = ImGui.GetID(id);
        if (open)
        {
            ImGui.OpenPopup(id);
            ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y));
        }

        ImGui.SetNextWindowSize(new Vector2(MathF.Round(PopupWidth * ImGuiHelper.Scale), 0));
        var mine = session.IsOpenFor(popup);
        var shown = ImGui.BeginPopup(id);
        if (!shown && !mine)
        {
            return;
        }

        var frame = ImGui.GetFrameCount();
        if (shown && !mine)
        {
            // The opening goes to the field edits first, so the edit starts from the value of the opening.
            model = makeModel();
            filter = "";
            selected = model.Current;
            route(popup, session.Frame(popup, frame, true, false, false, owns(popup)), Value());
            Content(kind, usedBy);
            ImGui.EndPopup();
            return;
        }

        var (changed, close) = shown ? Content(kind, usedBy) : (false, false);
        var escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        // Enter and a double-click assign the row in this frame and close the popup. The next frame ends the edit and
        // keeps the value.
        var events = session.Frame(popup, frame, shown, escape, changed, owns(popup));
        if (shown)
        {
            if (close || !session.IsOpenFor(popup))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        route(popup, events, Value());
    }

    /// <summary>The value of the selected row.</summary>
    private string Value() => model != null && selected >= 0 && selected < model.Rows.Count ? model.Rows[selected].Value : "";

    /// <summary>Draws the search field, the rows and the footer. Returns whether the selection changed, and whether the
    /// popup closes.</summary>
    private (bool Changed, bool Close) Content(ObjectKind kind, Func<ListEntry, int> usedBy)
    {
        var rows = model!;
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##filter", $"{EditorIcons.MagnifyingGlass}  Search", ref filter, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var visible = rows.Visible(filter);
        var changed = false;
        var close = false;
        var scroll = ImGui.IsWindowAppearing() ? selected : -1;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            var next = ListPickerModel.Step(visible, selected, ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : -1);
            changed = next != selected;
            selected = next;
            scroll = selected;
        }

        if (enter && ListPickerModel.EnterRow(visible, selected) is >= 0 and var row)
        {
            changed |= row != selected;
            selected = row;
            close = true;
        }

        var height = Math.Min(Math.Max(visible.Count, 1), VisibleRows) * ImGui.GetTextLineHeightWithSpacing() +
                     ImGui.GetStyle().WindowPadding.Y;
        ImGui.BeginChild("##rows", new Vector2(0, height));
        if (visible.Count == 0)
        {
            ImGui.TextDisabled("Nothing matches.");
        }

        hovered = -1;
        var clipper = new ImGuiListClipper();
        clipper.Begin(visible.Count);
        var scrollPosition = scroll < 0 ? -1 : visible.IndexOf(scroll);
        if (scrollPosition >= 0)
        {
            clipper.IncludeItemByIndex(scrollPosition);
        }

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var index = visible[i];
                var item = rows.Rows[index];
                var color = item.Kind == ListRowKind.Missing ? EditorTheme.Error
                    : item.Kind == ListRowKind.None ? EditorTheme.TextDim : EditorTheme.Text;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                var icon = item.Kind == ListRowKind.Entry ? $"{kind.Icon}  " : "    ";
                if (ImGui.Selectable($"{icon}{item.Text}##row{index}", index == selected,
                        ImGuiSelectableFlags.AllowDoubleClick))
                {
                    changed |= index != selected;
                    selected = index;
                    close |= ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                }

                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    hovered = index;
                }

                if (index == scroll)
                {
                    ImGui.SetScrollHereY();
                }

                var dim = item.Kind == ListRowKind.Entry &&
                          !string.Equals(item.Name, item.Value, StringComparison.OrdinalIgnoreCase)
                    ? item.Value
                    : "";
                var tag = item.Tag.Length > 0 ? $"[{item.Tag}]" : "";
                if (dim.Length > 0 || tag.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{dim} {tag}".Trim());
                }
            }
        }

        clipper.End();
        ImGui.EndChild();
        ImGui.Separator();
        var footer = hovered >= 0 ? hovered : selected;
        if (footer >= 0 && footer < rows.Rows.Count)
        {
            ImGui.PushFont(null, EditorTheme.CaptionSize);
            foreach (var line in ListPickerModel.Footer(rows.Rows[footer], kind.Label, usedBy))
            {
                ImGui.TextWrapped(line);
            }

            ImGui.PopFont();
        }

        return (changed, close);
    }
}

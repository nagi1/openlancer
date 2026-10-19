using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>The kind of an edit of a <see cref="ReorderableList"/>.</summary>
internal enum ListEditKind
{
    /// <summary>A new entry at the end.</summary>
    Add,

    Remove,

    /// <summary>The entry at <see cref="ListEdit.Index"/> goes to the place <see cref="ListEdit.To"/>.</summary>
    Move,
}

/// <summary>An edit of a list: Add, Remove or a move.</summary>
/// <param name="Index">The entry of a Remove or a move.</param>
/// <param name="To">The place of the entry after a move, in the list after the move.</param>
internal readonly record struct ListEdit(ListEditKind Kind, int Index = -1, int To = -1)
{
    public static ListEdit Add => new(ListEditKind.Add);

    public static ListEdit Remove(int index) => new(ListEditKind.Remove, index);

    public static ListEdit Move(int index, int to) => new(ListEditKind.Move, index, to);
}

/// <summary>
/// A list widget with Add, Remove and reorder (decision D579 of the M11c design, AGENTS.md UI rule 3), as the
/// ReorderableList of Unity. Each entry has one row with named sub-fields, a drag handle at the left, and Move up, Move
/// down and Remove at the right. Add is at the end. The lists of the publish page of the Mods window use it.
/// </summary>
internal static class ReorderableList
{
    /// <summary>Draws the fields of one entry in the width that it gets, and returns the entry with the changes of its
    /// fields.</summary>
    public delegate T Row<T>(int index, T item, float width);

    /// <summary>
    /// Draws the list. A change of a field of an entry, Add, Remove and each move give a new list. Without a change, the
    /// result is null.
    /// </summary>
    /// <param name="id">The id of the list. Two lists in one window need different ids.</param>
    /// <param name="newItem">The entry that Add appends, or null for a list without Add, for example a list whose
    /// caller adds entries from a file dialog.</param>
    /// <param name="addLabel">The text of the Add button, for example "Add author".</param>
    /// <param name="readOnly">The reason why the list cannot change, or null. A read-only list shows its rows
    /// disabled, with the reason in the tooltip.</param>
    public static unsafe IReadOnlyList<T>? Draw<T>(string id, IReadOnlyList<T> items, Row<T> row, Func<T>? newItem,
        string? addLabel, string? readOnly = null)
    {
        ImGui.PushID(id);
        // ImGui keeps a payload type of at most 32 bytes. The id of the list keeps a drag in its own list.
        var payloadType = "olrl" + ImGui.GetID("##rows").ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<T>? result = null;
        ListEdit? edit = null;
        ImGui.BeginDisabled(readOnly != null);
        var style = ImGui.GetStyle();
        var buttonWidth = ImGui.GetFrameHeight();
        for (var i = 0; i < items.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.BeginGroup();
            ImGui.Button($"{EditorIcons.GripLines}##grip", new Vector2(buttonWidth, 0));
            if (ImGui.BeginDragDropSource())
            {
                var index = i;
                ImGui.SetDragDropPayload(payloadType, (IntPtr)(&index), sizeof(int));
                ImGui.Text($"{EditorIcons.GripLines}  Entry {i + 1}");
                ImGui.EndDragDropSource();
            }

            ImGui.SetItemTooltip("Drag the entry to another place in the list.");
            ImGui.SameLine();
            var fieldsWidth = ImGui.GetContentRegionAvail().X - 3 * (buttonWidth + style.ItemInnerSpacing.X) -
                              style.ItemSpacing.X;
            ImGui.BeginGroup();
            var changed = row(i, items[i], MathF.Max(1, fieldsWidth));
            ImGui.EndGroup();
            if (!EqualityComparer<T>.Default.Equals(changed, items[i]))
            {
                var copy = new List<T>(result ?? items);
                copy[i] = changed;
                result = copy;
            }

            ImGui.SameLine();
            if (RowButton(EditorIcons.ArrowUp, "Move up", i > 0))
            {
                edit = ListEdit.Move(i, i - 1);
            }

            ImGui.SameLine(0, style.ItemInnerSpacing.X);
            if (RowButton(EditorIcons.ArrowDown, "Move down", i < items.Count - 1))
            {
                edit = ListEdit.Move(i, i + 1);
            }

            ImGui.SameLine(0, style.ItemInnerSpacing.X);
            if (RowButton(EditorIcons.TrashAlt, "Remove", true))
            {
                edit = ListEdit.Remove(i);
            }

            ImGui.EndGroup();
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload(payloadType);
                if (payload is { Delivery: true } && payload.DataSize == sizeof(int))
                {
                    edit = ListEdit.Move(*(int*)payload.Data, i);
                }

                ImGui.EndDragDropTarget();
            }

            ImGui.PopID();
        }

        if (newItem != null && ImGui.Button($"{EditorIcons.Plus}  {addLabel}"))
        {
            edit = ListEdit.Add;
        }

        ImGui.EndDisabled();
        if (readOnly != null && newItem != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(readOnly);
        }

        ImGui.PopID();
        return edit is { } done ? Apply(result ?? items, done, newItem!) : result;
    }

    /// <summary>The list after an edit. A move to the same place, or of an entry that is not in the list, changes
    /// nothing.</summary>
    /// <param name="newItem">The entry of Add.</param>
    internal static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> items, ListEdit edit, Func<T> newItem)
    {
        var list = new List<T>(items);
        switch (edit.Kind)
        {
            case ListEditKind.Add:
                list.Add(newItem());
                break;
            case ListEditKind.Remove when edit.Index >= 0 && edit.Index < list.Count:
                list.RemoveAt(edit.Index);
                break;
            case ListEditKind.Move when edit.Index >= 0 && edit.Index < list.Count && edit.To >= 0 &&
                                        edit.To < list.Count && edit.To != edit.Index:
                var item = list[edit.Index];
                list.RemoveAt(edit.Index);
                list.Insert(edit.To, item);
                break;
        }

        return list;
    }

    /// <summary>A square button with a glyph and a tooltip.</summary>
    private static bool RowButton(char glyph, string tooltip, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.Button(glyph.ToString(), new Vector2(ImGui.GetFrameHeight(), 0));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }
}

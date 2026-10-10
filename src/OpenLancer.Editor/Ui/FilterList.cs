using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>One row of a <see cref="FilterList{T}"/>.</summary>
/// <param name="Name">The display name, or the nickname when the item has no name.</param>
internal sealed record FilterEntry<T>(T Item, string Name, string Nickname, char Icon)
{
    /// <summary>The ImGui label of the row: the glyph and the name, with the nickname as the id.</summary>
    public string Label { get; } = $"{Icon}  {Name}##{Nickname}";
}

/// <summary>
/// The list of an open combo: a filter field at the top, and below it the rows whose name or nickname contains the
/// filter text. Case does not matter. Enter chooses the first row that matches.
/// </summary>
/// <remarks>A mod can add many rows, so the rows scroll in a child, and only the rows in view draw. The height does
/// not change while the user types, so the list does not jump.</remarks>
internal sealed class FilterList<T>
{
    private const int VisibleRows = 16;

    private IReadOnlyList<FilterEntry<T>> entries = [];
    private List<FilterEntry<T>> shown = [];
    private string filter = "";
    private string? shownFilter;

    /// <summary>The rows, in the order that the list shows them.</summary>
    public IReadOnlyList<FilterEntry<T>> Entries
    {
        get => entries;
        set
        {
            entries = value;
            shownFilter = null;
        }
    }

    /// <summary>The entries sorted by name and then by nickname, without case.</summary>
    public static FilterEntry<T>[] Sorted(IEnumerable<FilterEntry<T>> entries) =>
        entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Nickname, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>The entries whose name or nickname contains the filter text. Case does not matter.</summary>
    public static List<FilterEntry<T>> Filter(IReadOnlyList<FilterEntry<T>> entries, string filter) =>
        entries.Where(e => filter.Length == 0 ||
                           e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           e.Nickname.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Draws the filter field and the rows in an open combo or popup. When the list opens, it scrolls to the current
    /// row. Returns the row that the user chose, or null.
    /// </summary>
    /// <param name="isCurrent">True for the row of the current value, which shows as selected.</param>
    public FilterEntry<T>? Draw(Func<FilterEntry<T>, bool> isCurrent)
    {
        var appearing = ImGui.IsWindowAppearing();
        if (appearing)
        {
            filter = "";
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##filter", $"{EditorIcons.MagnifyingGlass}  Filter", ref filter, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        if (filter != shownFilter)
        {
            shown = Filter(entries, filter);
            shownFilter = filter;
        }

        if (enter && shown.Count > 0)
        {
            return shown[0];
        }

        var rows = Math.Min(entries.Count, VisibleRows);
        var height = rows * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y;
        ImGui.BeginChild("##rows", new Vector2(0, height));
        if (shown.Count == 0)
        {
            ImGui.TextDisabled("Nothing matches.");
        }

        // When the list opens, the clipper also draws the current row, so that the list can scroll to it.
        var scrollIndex = appearing ? shown.FindIndex(e => isCurrent(e)) : -1;
        FilterEntry<T>? chosen = null;
        var clipper = new ImGuiListClipper();
        clipper.Begin(shown.Count);
        if (scrollIndex >= 0)
        {
            clipper.IncludeItemByIndex(scrollIndex);
        }

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = shown[i];
                if (ImGui.Selectable(entry.Label, isCurrent(entry)))
                {
                    chosen = entry;
                }

                if (i == scrollIndex)
                {
                    ImGui.SetScrollHereY();
                }

                if (entry.Name != entry.Nickname)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(entry.Nickname);
                }
            }
        }

        clipper.End();
        ImGui.EndChild();
        return chosen;
    }
}

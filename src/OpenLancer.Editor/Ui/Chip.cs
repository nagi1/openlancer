using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>The colors of a chip.</summary>
/// <param name="Border">The color of the outline, or a transparent color for no outline.</param>
internal readonly record struct ChipLook(Vector4 Fill, Vector4 Border, Vector4 Text)
{
    /// <summary>The look of a fact without a state, for example a flag of the Inspector or a target game.</summary>
    public static ChipLook Grey => new(EditorTheme.SelectionUnfocused, EditorTheme.Transparent, EditorTheme.Text);

    /// <summary>A filled chip in a state color, for example blue for unsaved edits or red for an error.</summary>
    public static ChipLook Filled(Vector4 color) =>
        new(color with { W = 0.3f }, color with { W = 0.7f }, EditorTheme.TextBright);

    /// <summary>A chip with an outline only, for example the active mod.</summary>
    public static ChipLook Outline(Vector4 color) => new(EditorTheme.Transparent, color, color);
}

/// <summary>One chip of a row of chips.</summary>
/// <param name="Tooltip">The tooltip of the chip, or null.</param>
internal readonly record struct ChipItem(string Text, ChipLook Look, string? Tooltip = null);

/// <summary>
/// A chip: a short text in a small rounded box (decision D579 of the M11c design). The flags rows of the Inspector and
/// the state chips of the Mods window use it, so both look the same.
/// </summary>
internal static class Chip
{
    // Sizes in unscaled points.
    private const float Gap = 4;
    private const float Inset = 2;
    private const float PaddingX = 6;
    private const float Rounding = 3;

    /// <summary>
    /// Draws the chips from left to right in the caption size. They wrap at the right edge of the content region. The
    /// area of the chips is one item with the width of its widest row, so a table column that fits its content grows
    /// to the chips, and a tooltip of the caller can follow the item. A chip with a tooltip shows it on hover.
    /// </summary>
    public static void Flow(IReadOnlyList<ChipItem> chips)
    {
        var scale = ImGuiHelper.Scale;
        var font = ImGui.GetFont();
        var fontSize = MathF.Round(EditorTheme.CaptionSize * scale);
        var available = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight();
        var inset = MathF.Truncate(Inset * scale);
        var padding = MathF.Truncate(PaddingX * scale);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var sizes = new List<Vector2>(chips.Count);
        var widths = new List<float>(chips.Count);
        foreach (var chip in chips)
        {
            var size = font.CalcTextSizeA(fontSize, float.MaxValue, 0, chip.Text);
            sizes.Add(size);
            widths.Add(MathF.Ceiling(size.X) + 2 * padding);
        }

        var places = Layout(widths, available, MathF.Truncate(Gap * scale));
        var rows = 0;
        string? tooltip = null;
        var mouse = ImGui.GetMousePos();
        for (var i = 0; i < chips.Count; i++)
        {
            var (x, row) = places[i];
            rows = Math.Max(rows, row + 1);
            var min = start + new Vector2(x, row * height + inset);
            var max = min + new Vector2(widths[i], height - 2 * inset);
            var look = chips[i].Look;
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(look.Fill), Rounding * scale);
            if (look.Border.W > 0)
            {
                drawList.AddRect(min, max, ImGui.GetColorU32(look.Border), Rounding * scale);
            }

            drawList.AddText(font, fontSize,
                new Vector2(min.X + padding, min.Y + MathF.Truncate((max.Y - min.Y - sizes[i].Y) / 2)),
                ImGui.GetColorU32(look.Text), chips[i].Text);
            if (chips[i].Tooltip != null && mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y)
            {
                tooltip = chips[i].Tooltip;
            }
        }

        ImGui.Dummy(new Vector2(MathF.Max(1, Width(places, widths)), Math.Max(1, rows) * height));
        if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    /// <summary>The width of the widest row of the chips, in pixels.</summary>
    /// <param name="places">The places of <see cref="Layout"/>.</param>
    /// <param name="widths">The width of each chip, in pixels.</param>
    internal static float Width(IReadOnlyList<(float X, int Row)> places, IReadOnlyList<float> widths)
    {
        var width = 0f;
        for (var i = 0; i < places.Count; i++)
        {
            width = MathF.Max(width, places[i].X + widths[i]);
        }

        return width;
    }

    /// <summary>
    /// The place of each chip: its left edge from the start of the row, and its row from 0. A chip goes on the row of
    /// the chip before it when it fits. A chip that is wider than the space starts its own row.
    /// </summary>
    /// <param name="widths">The width of each chip, in pixels.</param>
    /// <param name="available">The width of the space, in pixels.</param>
    /// <param name="gap">The space between two chips of a row, in pixels.</param>
    internal static IReadOnlyList<(float X, int Row)> Layout(IReadOnlyList<float> widths, float available, float gap)
    {
        var places = new List<(float, int)>(widths.Count);
        var x = 0f;
        var row = 0;
        foreach (var width in widths)
        {
            if (x > 0 && x + width > available)
            {
                x = 0;
                row++;
            }

            places.Add((x, row));
            x += width + gap;
        }

        return places;
    }
}

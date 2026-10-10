using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// The look of the rows of a panel list, for example the Hierarchy tree and the Database categories: 20 point rows
/// that touch, a selection color that shows the focus, and a text with a kind glyph, a name, a dim detail and a count.
/// </summary>
internal static class ListRow
{
    // Sizes in unscaled points. With no item spacing, a row is 20 points high and selected rows touch.
    private const float PaddingY = 2.5f;
    private const float GlyphGap = 5;

    /// <summary>
    /// Pushes the row style: no item spacing, the row padding and the selection color. The selection is blue while
    /// the list has the focus and grey when it does not, as in Unity. Call <see cref="PopStyle"/> after the rows.
    /// </summary>
    public static void PushStyle(bool focused)
    {
        ImGui.PushStyleVarY(ImGuiStyleVar.ItemSpacing, 0);
        ImGui.PushStyleVarY(ImGuiStyleVar.FramePadding, PaddingY * ImGuiHelper.Scale);
        ImGui.PushStyleColor(ImGuiCol.Header, SelectionColor(focused));
    }

    public static void PopStyle()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    /// <summary>The fill of a selected row: blue while its list has the focus, and grey when it does not.</summary>
    public static Vector4 SelectionColor(bool focused) =>
        focused ? EditorTheme.AccentStrong : EditorTheme.SelectionUnfocused;

    /// <summary>The height of a row with the pushed style, in pixels.</summary>
    public static float Height => ImGui.GetFontSize() + 2 * ImGui.GetStyle().FramePadding.Y;

    /// <summary>
    /// Draws the text of a row: the glyph in its color, the name, the dim detail, and the count in the caption size
    /// at the right edge. The name and the detail stop before the count, with "..." when they are cut.
    /// </summary>
    /// <param name="rowMin">The top-left corner of the row.</param>
    /// <param name="textX">The left edge of the glyph, for example after the arrow of a tree node.</param>
    /// <param name="right">The right edge of the row.</param>
    public static void Text(Vector2 rowMin, float textX, float right, char icon, uint iconColor, string name,
        string detail = "", string count = "")
    {
        var style = ImGui.GetStyle();
        var scale = ImGuiHelper.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var rowHeight = Height;
        var gap = MathF.Truncate(GlyphGap * scale);

        var textEnd = right - style.FramePadding.X;
        if (count.Length > 0)
        {
            var countSize = EditorTheme.CaptionSize * scale;
            var size = font.CalcTextSizeA(countSize, float.MaxValue, 0, count);
            var countPos = new Vector2(textEnd - size.X, rowMin.Y + MathF.Truncate((rowHeight - size.Y) / 2));
            drawList.AddText(font, countSize, countPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), count);
            textEnd = countPos.X - gap;
        }

        var position = new Vector2(textX, rowMin.Y + style.FramePadding.Y);
        var clip = new Vector4(position.X, rowMin.Y, Math.Max(position.X, textEnd), rowMin.Y + rowHeight);
        font.RenderChar(drawList, fontSize, position, iconColor, icon, ref clip);
        // The icon glyphs all have the same advance, so the names line up.
        position.X += font.GetFontBaked(fontSize).FindGlyph(icon).AdvanceX + gap;
        position.X += TextFit.AddText(drawList, font, fontSize, position, ImGui.GetColorU32(ImGuiCol.Text), name,
            textEnd - position.X);
        if (detail.Length > 0)
        {
            position.X += style.ItemSpacing.X;
            TextFit.AddText(drawList, font, fontSize, position, ImGui.GetColorU32(ImGuiCol.TextDisabled), detail,
                textEnd - position.X);
        }
    }
}

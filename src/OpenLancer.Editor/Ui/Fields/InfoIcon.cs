using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Editor.Ui.Fields;

/// <summary>
/// The info icon of a high-level item (AGENTS.md UI rule 10, section 11.2 of the M6a design): a small dim glyph that is
/// always visible. A hover shows the first sentence of the help text. A click opens a popup with what the item is and
/// what it writes to the game files.
/// </summary>
internal static class InfoIcon
{
    /// <summary>The width of the icon, in pixels.</summary>
    public static float Width => ImGui.GetFrameHeight();

    /// <summary>Draws the icon at the cursor.</summary>
    /// <param name="id">The id of the icon. Two icons in one window need different ids.</param>
    public static void Draw(string id, HelpText help)
    {
        ImGui.PushID(id);
        var size = new Vector2(Width, ImGui.GetFrameHeight());
        var min = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##info", size))
        {
            ImGui.OpenPopup("##info-popup");
        }

        var hovered = ImGui.IsItemHovered();
        var glyph = EditorIcons.InfoCircle.ToString();
        var glyphSize = ImGui.CalcTextSize(glyph);
        ImGui.GetWindowDrawList().AddText(min + (size - glyphSize) / 2,
            ImGui.GetColorU32(hovered ? EditorTheme.AccentText : EditorTheme.TextDim), glyph);
        if (hovered && !ImGui.IsPopupOpen("##info-popup"))
        {
            ImGui.SetTooltip(help.FirstSentence);
        }

        Popup(help);
        ImGui.PopID();
    }

    /// <summary>Draws the icon at the right end of the line of the last item, for example a section header, which must
    /// allow overlap.</summary>
    public static void AtRightEnd(string id, HelpText help)
    {
        var max = ImGui.GetItemRectMax();
        var min = ImGui.GetItemRectMin();
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(max.X - Width - ImGui.GetStyle().FramePadding.X, min.Y +
            MathF.Truncate((max.Y - min.Y - ImGui.GetFrameHeight()) / 2)));
        Draw(id, help);
        ImGui.SetCursorScreenPos(cursor);
    }

    private static void Popup(HelpText help)
    {
        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0) * ImGuiHelper.Scale, new Vector2(420, 600) * ImGuiHelper.Scale);
        if (!ImGui.BeginPopup("##info-popup"))
        {
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 400 * ImGuiHelper.Scale);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        ImGui.Text(help.Title);
        ImGui.PopStyleColor();
        ImGui.Separator();
        Paragraph("What it is", help.WhatItIs);
        Paragraph("What it writes to the game files", help.WhatItWrites);
        if (help.Example is { } example)
        {
            Paragraph("Retail example", example);
        }

        ImGui.PopTextWrapPos();
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void Paragraph(string heading, string text)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.Text(heading);
        ImGui.PopStyleColor();
        ImGui.TextWrapped(text);
    }
}

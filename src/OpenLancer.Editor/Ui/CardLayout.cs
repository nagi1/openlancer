using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>One button of a button row of <see cref="CardLayout"/>.</summary>
/// <param name="Primary">True for the one button of a page in the accent color.</param>
internal readonly record struct CardButton(string Label, bool Primary = false, bool Enabled = true);

/// <summary>
/// The parts of the card pages and the card windows, in the style of the Unity dialogs: a title in bold with a
/// subtitle, lines with equal gaps, a status row with a large glyph, notes with small glyphs, facts in two columns, a
/// folder line with an "Open folder" link, and buttons with one primary button. The first-run screen and the Game link
/// window use these parts, so they look the same.
/// </summary>
internal static class CardLayout
{
    /// <summary>The height of the large buttons of a card page, in unscaled points.</summary>
    public const float ButtonHeight = 32;

    // Sizes in unscaled points.
    private const float ButtonMinWidth = 112;
    private const float ButtonGap = 8;
    private const float SmallGap = 4;
    private const float SectionGap = 10;
    private const float StatusGlyphSize = 30;
    private const float StatusGlyphGap = 12;
    private const float NoteGlyphWidth = 22;
    private const float FactLabelWidth = 116;
    private const float LinkGap = 16;
    private const float AreaPadding = 16;
    private const float AreaRounding = 4;

    private const string OpenFolder = "Open folder";

    /// <summary>The title in bold and the large font, and the subtitle below it in the dim color.</summary>
    public static void Title(string title, string subtitle)
    {
        ImGui.PushFont(null, EditorTheme.HeroSize);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        Bold(title);
        ImGui.PopStyleColor();
        ImGui.PopFont();
        Space(SmallGap);
        Dim(subtitle);
    }

    /// <summary>A horizontal line with the same gap above and below it.</summary>
    public static void Divider()
    {
        Space(SectionGap);
        ImGui.Separator();
        Space(SectionGap);
    }

    /// <summary>The gap between two parts of a card.</summary>
    public static void Gap() => Space(SectionGap);

    /// <summary>A small gap, for example between the status row and the folder line.</summary>
    public static void SmallSpace() => Space(SmallGap);

    /// <summary>A heading with a line after it, in the label color.</summary>
    public static void Heading(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.SeparatorText(text);
        ImGui.PopStyleColor();
    }

    /// <summary>Text in the dim color. It wraps at the wrap position of the caller, else at the right edge.</summary>
    public static void Dim(string text) => Wrapped(text, EditorTheme.TextDim);

    /// <summary>Small text in the dim color, for example a hint below a button.</summary>
    public static void Caption(string text)
    {
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        Dim(text);
        ImGui.PopFont();
    }

    /// <summary>
    /// One line of text in a bold look, in the font and the text color of the caller. The font of the editor has no
    /// bold face, so the text draws a second time, one pixel to the right.
    /// </summary>
    public static void Bold(string text)
    {
        ImGui.Text(text);
        var offset = MathF.Max(1, MathF.Round(ImGuiHelper.Scale));
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(),
            ImGui.GetItemRectMin() + new Vector2(offset, 0), ImGui.GetColorU32(ImGuiCol.Text), text);
    }

    /// <summary>
    /// The status row: a large glyph in its color, the title in bold, and a line below the title in the label color.
    /// The glyph is centered on the height of the text.
    /// </summary>
    public static void StatusRow(char glyph, Vector4 color, string title, string line)
    {
        var size = Scaled(StatusGlyphSize);
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(size));
        ImGui.SameLine(0, Scaled(StatusGlyphGap));
        ImGui.BeginGroup();
        ImGui.PushFont(null, EditorTheme.TitleSize);
        Bold(title);
        ImGui.PopFont();
        Wrapped(line, EditorTheme.Label);
        ImGui.EndGroup();
        ImGui.EndGroup();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        Glyphs.Draw(ImGui.GetWindowDrawList(), glyph, size, new Vector2(min.X + size / 2, (min.Y + max.Y) / 2),
            ImGui.GetColorU32(color));
    }

    /// <summary>A note: a small glyph in its color, the wrapped text, and an optional line below the text in the dim
    /// color. All lines start at the same column, right of the glyph.</summary>
    public static void Note(char glyph, Vector4 color, string text, string? detail = null)
    {
        var start = ImGui.GetCursorScreenPos();
        ImGui.TextColored(color, glyph.ToString());
        ImGui.SameLine(0, 0);
        ImGui.SetCursorScreenPos(new Vector2(start.X + Scaled(NoteGlyphWidth), ImGui.GetCursorScreenPos().Y));
        ImGui.BeginGroup();
        ImGui.TextWrapped(text);
        if (detail != null)
        {
            Dim(detail);
        }

        ImGui.EndGroup();
    }

    /// <summary>A folder: the folder glyph and the path in the dim color. A long path loses its start, and the tooltip
    /// shows all of it.</summary>
    public static void Folder(string path) => FolderPath(path, 0);

    /// <summary>
    /// A folder with an "Open folder" link after it. The link is off when <paramref name="canOpen"/> is false, for
    /// example for a folder that is not found. Returns true when the user clicks the link.
    /// </summary>
    public static bool FolderWithOpenLink(string path, bool canOpen)
    {
        var gap = Scaled(LinkGap);
        FolderPath(path, ImGui.CalcTextSize(OpenFolder).X + gap);
        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(!canOpen);
        var clicked = ImGui.TextLink(OpenFolder);
        ImGui.EndDisabled();
        ImGui.SetItemTooltip(canOpen ? "Opens the folder in the file manager." : "The folder is not found.");
        return clicked && canOpen;
    }

    /// <summary>
    /// Starts a table of facts: the labels in the dim color in the first column, and the values in the second. Add
    /// the rows with <see cref="Fact"/> or <see cref="FactLabel"/>. Call <c>ImGui.EndTable</c> when this returns true.
    /// </summary>
    public static bool BeginFacts(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingFixedFit))
        {
            return false;
        }

        ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, Scaled(FactLabelWidth));
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    /// <summary>A row of facts with a text value.</summary>
    public static void Fact(string label, string value)
    {
        FactLabel(label);
        ImGui.TextWrapped(value);
    }

    /// <summary>Starts a row of facts with its label. The caller then draws the value.</summary>
    public static void FactLabel(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
    }

    /// <summary>Text links that go from left to right and wrap at the right edge. Returns the index of the clicked
    /// link, or -1.</summary>
    public static int LinkFlow(IReadOnlyList<string> labels, IReadOnlyList<string> tooltips)
    {
        var clicked = -1;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
                if (ImGui.GetCursorScreenPos().X + ImGui.CalcTextSize(labels[i]).X > right)
                {
                    ImGui.NewLine();
                }
            }

            ImGui.PushID(i);
            if (ImGui.TextLink(labels[i]))
            {
                clicked = i;
            }

            ImGui.PopID();
            ImGui.SetItemTooltip(tooltips[i]);
        }

        return clicked;
    }

    /// <summary>The width of a large button with this label.</summary>
    public static float ButtonWidth(string label) =>
        MathF.Max(Scaled(ButtonMinWidth),
            MathF.Ceiling(ImGui.CalcTextSize(label).X + 2 * ImGui.GetStyle().FramePadding.X));

    /// <summary>A large button. A primary button has the accent color. Returns true when the user clicks it.</summary>
    public static bool Button(CardButton button)
    {
        ImGui.BeginDisabled(!button.Enabled);
        if (button.Primary)
        {
            EditorTheme.PushPrimaryButtonColors();
        }

        var clicked = ImGui.Button(button.Label, new Vector2(ButtonWidth(button.Label), Scaled(ButtonHeight)));
        if (button.Primary)
        {
            EditorTheme.PopPrimaryButtonColors();
        }

        ImGui.EndDisabled();
        return clicked && button.Enabled;
    }

    /// <summary>
    /// Large buttons at the right edge, in the order of the list. With <paramref name="sameLine"/>, the buttons go on
    /// the line of the items before them, and on the next line when there is no room. Returns the index of the clicked
    /// button, or -1.
    /// </summary>
    public static int Buttons(bool sameLine, params ReadOnlySpan<CardButton> buttons)
    {
        var gap = Scaled(ButtonGap);
        var total = 0f;
        foreach (var button in buttons)
        {
            total += ButtonWidth(button.Label) + (total > 0 ? gap : 0);
        }

        if (sameLine)
        {
            ImGui.SameLine();
            if (ImGui.GetContentRegionAvail().X < total)
            {
                ImGui.NewLine();
            }
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - total));
        var clicked = -1;
        for (var i = 0; i < buttons.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0, gap);
            }

            if (Button(buttons[i]))
            {
                clicked = i;
            }
        }

        return clicked;
    }

    /// <summary>Moves the cursor so that an item of this width is centered in the free width of the line.</summary>
    public static void Center(float width) =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));

    /// <summary>A large glyph in its color, centered in the free width of the line.</summary>
    /// <param name="points">The size of the glyph in unscaled points.</param>
    public static void CenteredGlyph(char glyph, Vector4 color, float points)
    {
        var size = Scaled(points);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.Dummy(new Vector2(width, size));
        Glyphs.Draw(ImGui.GetWindowDrawList(), glyph, size, start + new Vector2(width / 2, size / 2),
            ImGui.GetColorU32(color));
    }

    /// <summary>
    /// Starts an area with the field color and a thin border, for example the area that takes the game folder. Call
    /// <see cref="EndArea"/> after its content, also when this returns false.
    /// </summary>
    public static bool BeginArea(string id)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Field);
        ImGui.PushStyleColor(ImGuiCol.Border, EditorTheme.SectionHover);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Scaled(AreaRounding));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Scaled(AreaPadding)));
        var visible = ImGui.BeginChild(id, Vector2.Zero,
            ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        return visible;
    }

    public static void EndArea() => ImGui.EndChild();

    /// <summary>A small flat button with a glyph, for example Copy in a row of a table. Returns true when the user
    /// clicks it.</summary>
    public static bool IconButton(char glyph, string tooltip)
    {
        EditorTheme.PushFlatButtonColors();
        var clicked = ImGui.SmallButton(glyph.ToString());
        EditorTheme.PopFlatButtonColors();
        ImGui.SetItemTooltip(tooltip);
        return clicked;
    }

    /// <summary>The width of an <see cref="IconButton"/>.</summary>
    public static float IconButtonWidth(char glyph) =>
        ImGui.CalcTextSize(glyph.ToString()).X + 2 * ImGui.GetStyle().FramePadding.X;

    private static void FolderPath(string path, float reserve)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        ImGui.Text(EditorIcons.Folder.ToString());
        ImGui.SameLine();
        var width = MathF.Max(1, ImGui.GetContentRegionAvail().X - reserve);
        ImGui.Text(TextFit.CutLeft(path, width, t => ImGui.CalcTextSize(t).X));
        ImGui.PopStyleColor();
        ImGui.SetItemTooltip(path);
    }

    private static void Wrapped(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void Space(float points) => ImGui.Dummy(new Vector2(0, Scaled(points)));

    private static float Scaled(float points) => MathF.Truncate(points * ImGuiHelper.Scale);
}

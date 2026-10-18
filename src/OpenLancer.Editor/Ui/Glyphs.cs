using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>Icon glyphs on a draw list, centered on a point, for the markers of the Scene and the Cutscene view.</summary>
internal static class Glyphs
{
    private static readonly Vector2 ShadowOffset = new(1, 1);
    private static readonly uint ShadowColor = EditorTheme.ToAbgr(EditorTheme.Shadow);

    // The offset from the text position of a glyph to the center of its visible shape, for each glyph and pixel size.
    private static readonly Dictionary<(char Glyph, float Size), Vector2> Centers = [];

    /// <summary>
    /// The offset from the text position of a glyph to the center of its visible shape. The icon glyphs have empty
    /// space around them, so the center of the text box is not the center of the shape.
    /// </summary>
    public static Vector2 Center(char glyph, float pixelSize)
    {
        if (!Centers.TryGetValue((glyph, pixelSize), out var center))
        {
            var info = ImGuiHelper.Roboto.GetFontBaked(pixelSize).FindGlyph(glyph);
            center = new Vector2(info.X0 + info.X1, info.Y0 + info.Y1) / 2;
            Centers[(glyph, pixelSize)] = center;
        }

        return center;
    }

    /// <summary>Draws a glyph with its shape centered on a point.</summary>
    /// <param name="color">The ABGR color of the glyph.</param>
    public static void Draw(ImDrawListPtr drawList, char glyph, float pixelSize, Vector2 center, uint color) =>
        ImGuiHelper.Roboto.RenderChar(drawList, pixelSize, center - Center(glyph, pixelSize), color, glyph);

    /// <summary>Draws a glyph with its shape centered on a point, over a drop shadow.</summary>
    /// <param name="color">The ABGR color of the glyph.</param>
    /// <param name="alpha">The opacity of the glyph and of its shadow.</param>
    public static void DrawShadowed(ImDrawListPtr drawList, char glyph, float pixelSize, Vector2 center, uint color,
        float alpha)
    {
        Draw(drawList, glyph, pixelSize, center + ShadowOffset * ImGuiHelper.Scale,
            ImGui.GetColorU32(ShadowColor, alpha));
        Draw(drawList, glyph, pixelSize, center, ImGui.GetColorU32(color, alpha));
    }
}

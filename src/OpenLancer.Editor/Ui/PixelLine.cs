using System;
using System.Numerics;
using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// Lines that are one pixel wide and fill one full pixel column or row, so they stay sharp. Use them for separators
/// and dividers.
/// </summary>
/// <remarks>
/// ImDrawList.AddLine moves its points by half a pixel. A line at the center of a pixel then lies on a pixel edge, and
/// it shows as two pixels at half intensity. A filled rectangle on whole pixels has no such offset.
/// </remarks>
internal static class PixelLine
{
    /// <summary>A vertical line in the pixel column that holds <paramref name="x"/>.</summary>
    public static void Vertical(ImDrawListPtr drawList, float x, float top, float bottom, uint color)
    {
        var left = MathF.Floor(x);
        drawList.AddRectFilled(new Vector2(left, MathF.Floor(top)), new Vector2(left + 1, MathF.Floor(bottom)), color);
    }

    /// <summary>A horizontal line in the pixel row that holds <paramref name="y"/>.</summary>
    public static void Horizontal(ImDrawListPtr drawList, float y, float left, float right, uint color)
    {
        var top = MathF.Floor(y);
        drawList.AddRectFilled(new Vector2(MathF.Floor(left), top), new Vector2(MathF.Floor(right), top + 1), color);
    }
}

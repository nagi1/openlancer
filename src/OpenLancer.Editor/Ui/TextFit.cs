using System;
using System.Numerics;
using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// Fits a text into a width. A text that is too wide loses its end or its start, and "..." shows in place of the
/// missing part. So the user sees that the text continues. The tooltip of the item can show all of it.
/// </summary>
internal static class TextFit
{
    public const string Ellipsis = "...";

    /// <summary>
    /// The start of a text that fits in <paramref name="width"/> with "..." after it, measured with the current font,
    /// or all the text when it fits.
    /// </summary>
    public static string CutRight(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        var length = RightCutLength(text, width, n => ImGui.CalcTextSize(text, n).X, ImGui.CalcTextSize(Ellipsis).X);
        return string.Concat(text.AsSpan(0, length), Ellipsis);
    }

    /// <summary>
    /// Draws a text on one line. A text that is wider than <paramref name="maxWidth"/> ends with "..." and fits. When
    /// not even "..." fits, nothing draws. Returns the width of the drawn text, in pixels.
    /// </summary>
    /// <param name="fontSize">The size of the font, in pixels.</param>
    public static float AddText(ImDrawListPtr drawList, ImFontPtr font, float fontSize, Vector2 position, uint color,
        string text, float maxWidth)
    {
        var width = font.CalcTextSizeA(fontSize, float.MaxValue, 0, text).X;
        if (width <= maxWidth)
        {
            drawList.AddText(font, fontSize, position, color, text);
            return width;
        }

        var ellipsisWidth = font.CalcTextSizeA(fontSize, float.MaxValue, 0, Ellipsis).X;
        if (maxWidth < ellipsisWidth)
        {
            return 0;
        }

        // The draw list takes the length of the start, so the cut text needs no new string.
        var length = RightCutLength(text, maxWidth, n => font.CalcTextSizeA(fontSize, float.MaxValue, 0, text, n).X,
            ellipsisWidth);
        var startWidth = font.CalcTextSizeA(fontSize, float.MaxValue, 0, text, length).X;
        if (length > 0)
        {
            drawList.AddText(font, fontSize, position, color, text, length);
        }

        drawList.AddText(font, fontSize, position + new Vector2(startWidth, 0), color, Ellipsis);
        return startWidth + ellipsisWidth;
    }

    /// <summary>
    /// The number of characters at the start of a text that fit in <paramref name="width"/> with "..." after them, or
    /// the length of the text when all of it fits. The start does not end in a space or in half of a surrogate pair.
    /// </summary>
    /// <param name="measure">The width of the first n characters of the text.</param>
    /// <param name="ellipsisWidth">The width of "...".</param>
    internal static int RightCutLength(string text, float width, Func<int, float> measure, float ellipsisWidth)
    {
        if (measure(text.Length) <= width)
        {
            return text.Length;
        }

        // A longer start is wider, so a binary search finds the longest start that fits.
        var room = width - ellipsisWidth;
        var (low, high) = (0, text.Length - 1);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (measure(middle) <= room)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0 && char.IsHighSurrogate(text[low - 1]))
        {
            low--;
        }

        while (low > 0 && char.IsWhiteSpace(text[low - 1]))
        {
            low--;
        }

        return low;
    }

    /// <summary>
    /// The end of a text that fits in <paramref name="width"/>, with "..." before it, or all the text when it fits.
    /// A path keeps its end, which has the most specific folders.
    /// </summary>
    internal static string CutLeft(string text, float width, Func<string, float> measure)
    {
        if (measure(text) <= width)
        {
            return text;
        }

        // A later start gives a shorter text, so a binary search finds the first start that fits.
        var (low, high) = (1, text.Length);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (measure(Ellipsis + text[middle..]) <= width)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return Ellipsis + text[low..];
    }
}

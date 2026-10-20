using System;
using System.IO;
using System.Runtime.InteropServices;
using LibreLancer;
using LibreLancer.ImageLib;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>
/// The images of the view tools (section 7.7 of the MCP1 design): the PNG data of the pixels of a render target or of
/// the back buffer of the window. The GPU gives the rows of both from the bottom row up. The PNG holds the top row first,
/// so a viewer shows the image the right way up.
/// </summary>
internal static class AgentImages
{
    /// <summary>
    /// The PNG image of BGRA pixels with the bottom row first. Each pixel becomes opaque: a render writes the alpha of
    /// its blends into the target, and that alpha has no meaning in an image of the view. The method sets the alpha in
    /// the given array.
    /// </summary>
    /// <exception cref="ArgumentException">The pixels do not fill the size.</exception>
    public static ToolImage Png(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length != 4 * width * height)
            throw new ArgumentException($"The image of {width} by {height} pixels needs {4L * width * height} bytes.",
                nameof(bgra));
        var pixels = MemoryMarshal.Cast<byte, Bgra8>(bgra.AsSpan());
        for (var i = 0; i < pixels.Length; i++)
            pixels[i].A = 0xFF;
        using var stream = new MemoryStream();
        PNG.Save(stream, width, height, pixels, true);
        return new ToolImage(stream.ToArray(), width, height);
    }

    /// <summary>
    /// The pixels of a rectangle of the back buffer, with the bottom row first as the buffer has them. The rectangle has
    /// the coordinates of the window: the top-left corner and the size in pixels. The part outside the buffer is cut.
    /// Gives null when no part of the rectangle is in the buffer.
    /// </summary>
    /// <param name="buffer">The pixels of the back buffer, with the bottom row first.</param>
    public static (byte[] Bgra, int Width, int Height)? Crop(Bgra8[] buffer, int width, int height, Rectangle rect)
    {
        var left = Math.Max(0, rect.X);
        var top = Math.Max(0, rect.Y);
        var right = Math.Min(width, rect.X + rect.Width);
        var bottom = Math.Min(height, rect.Y + rect.Height);
        if (right <= left || bottom <= top || buffer.Length < width * height)
            return null;
        var w = right - left;
        var h = bottom - top;
        var result = new byte[4 * w * h];
        var target = MemoryMarshal.Cast<byte, Bgra8>(result.AsSpan());
        // Row r of the result, from the bottom, is the window row bottom - 1 - r, which is buffer row height - bottom + r.
        for (var r = 0; r < h; r++)
            buffer.AsSpan((height - bottom + r) * width + left, w).CopyTo(target.Slice(r * w, w));
        return (result, w, h);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.ImageLib;
using LibreLancer.ImUI;
using OpenLancer.Data.Publishing;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// The thumbnails of the screenshots of the Mods window (decision D592 of the M11c design). The decode runs on the UI
/// thread, one image for each frame, as the Assets panel does. StbImageSharp keeps its flip flag in a static field, so
/// a decode on a worker could flip another image (finding R9). The window keeps a copy of at most 256 pixels on the long
/// side until it closes.
/// </summary>
/// <remarks>
/// <para>The images decode with the row order of the Assets panel, the bottom row first. So the draw uses the same
/// texture coordinates as the Assets panel.</para>
/// <para>An image of the list of a mod passes the path rule of decision D586 before the window opens it, and its real
/// path must be in the mod folder. So a hand-written <c>publish.json</c> cannot make the editor open a file outside the
/// mod, for example a path of another computer.</para>
/// </remarks>
internal sealed class ScreenshotThumbnails(Game game) : IDisposable
{
    /// <summary>The longest side of a thumbnail, in pixels.</summary>
    public const int MaxSide = 256;

    private readonly Dictionary<string, (Texture2D Texture, ImTextureRef Ref, Vector2 Size)?> images =
        new(StringComparer.OrdinalIgnoreCase);

    // The size and the last write time of each file at its decode.
    private readonly Dictionary<string, (long Length, DateTime Written)> stamps = new(StringComparer.OrdinalIgnoreCase);

    // The paths that wait for their decode, each with the folder that its real path must be in, or null.
    private readonly List<(string Path, string? Root)> wanted = [];

    /// <summary>The texture coordinates of the top-left and the bottom-right corner of a thumbnail.</summary>
    public static (Vector2 Min, Vector2 Max) Uv => (new Vector2(0, 1), new Vector2(1, 0));

    /// <summary>The thumbnail of a screenshot of the list of a mod, or null. An entry that breaks the path rule or
    /// whose path leaves the mod folder gets no thumbnail and opens no file.</summary>
    /// <param name="file">The path of the entry in <c>publish.json</c>, for example
    /// <c>publish/screenshots/dock.png</c>.</param>
    public (ImTextureRef Ref, Vector2 Size)? Of(string modFolder, string file) =>
        ScreenshotRules.PathIn(modFolder, file) is { } path ? Get(path, modFolder) : null;

    /// <summary>The texture of the thumbnail of an image file and its size in pixels, or null. A thumbnail that is not
    /// ready yet goes into the queue of the next frames. An image that does not pass the rules or does not decode gives
    /// null.</summary>
    /// <param name="root">The folder that the real path of the file must be in, or null for a file that the user picked
    /// in the file dialog.</param>
    public (ImTextureRef Ref, Vector2 Size)? Get(string path, string? root)
    {
        var key = Path.GetFullPath(path);
        if (images.TryGetValue(key, out var image))
        {
            return image is { } ready ? (ready.Ref, ready.Size) : null;
        }

        if (!wanted.Any(w => string.Equals(w.Path, key, StringComparison.OrdinalIgnoreCase)))
        {
            wanted.Add((key, root));
        }

        return null;
    }

    /// <summary>Decodes the first image of the queue. Call it once each frame on the UI thread.</summary>
    public void Frame()
    {
        if (wanted.Count == 0)
        {
            return;
        }

        var (path, root) = wanted[0];
        wanted.RemoveAt(0);
        stamps[path] = Stamp(path);
        // The rules read the size from the header, so an image above 8,192 pixels on a side does not decode.
        if (ScreenshotRules.ThumbnailBytes(path, root) is not { } bytes)
        {
            images[path] = null;
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = Generic.ImageFromStream(stream, flip: true);
            var (data, width, height) = Shrink(image.Data, image.Width, image.Height, MaxSide);
            var texture = new Texture2D(game.RenderContext, width, height);
            texture.SetData(data);
            images[path] = (texture, ImGuiHelper.RegisterTexture(texture), new Vector2(width, height));
        }
        catch (Exception)
        {
            // The decoder throws its own exceptions for bad data. An image that does not decode or read shows its
            // glyph, as a thumbnail of the Assets panel does.
            images[path] = null;
        }
    }

    /// <summary>Forgets each thumbnail, for example when the window closes.</summary>
    public void Clear()
    {
        foreach (var path in images.Keys.ToList())
        {
            Forget(path);
        }

        wanted.Clear();
    }

    /// <summary>Forgets the thumbnails of the files that changed or went away after their decode, for example at a
    /// refresh of the window. The next draw decodes them again.</summary>
    public void ForgetChanged()
    {
        foreach (var path in images.Keys.Where(p => stamps.GetValueOrDefault(p) != Stamp(p)).ToList())
        {
            Forget(path);
        }
    }

    private void Forget(string path)
    {
        if (images.Remove(path, out var image) && image is { } ready)
        {
            ImGuiHelper.DeregisterTexture(ready.Texture);
            ready.Texture.Dispose();
        }

        stamps.Remove(path);
    }

    /// <summary>The size and the last write time of a file, or zero values when it does not exist.</summary>
    private static (long Length, DateTime Written) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (0, default);
    }

    public void Dispose() => Clear();

    /// <summary>
    /// A copy of a BGRA image whose long side has at most <paramref name="max"/> pixels. Each pixel of the copy is the
    /// mean of the pixels of the source that it covers. A smaller image stays as it is.
    /// </summary>
    internal static (byte[] Data, int Width, int Height) Shrink(byte[] bgra, int width, int height, int max)
    {
        var longSide = Math.Max(width, height);
        if (longSide <= max)
        {
            return (bgra, width, height);
        }

        var newWidth = Math.Max(1, (int)((long)width * max / longSide));
        var newHeight = Math.Max(1, (int)((long)height * max / longSide));
        var result = new byte[newWidth * newHeight * 4];
        for (var y = 0; y < newHeight; y++)
        {
            var y0 = (int)((long)y * height / newHeight);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * height / newHeight));
            for (var x = 0; x < newWidth; x++)
            {
                var x0 = (int)((long)x * width / newWidth);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * width / newWidth));
                long b = 0, g = 0, r = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = (sy * width + sx) * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                        a += bgra[i + 3];
                    }
                }

                var count = (x1 - x0) * (y1 - y0);
                var o = (y * newWidth + x) * 4;
                result[o] = (byte)(b / count);
                result[o + 1] = (byte)(g / count);
                result[o + 2] = (byte)(r / count);
                result[o + 3] = (byte)(a / count);
            }
        }

        return (result, newWidth, newHeight);
    }
}

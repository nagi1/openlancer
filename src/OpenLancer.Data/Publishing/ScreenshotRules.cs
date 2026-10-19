using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Publishing;

/// <summary>
/// The rule of a screenshot of a mod (decision D586 of the M11c design). The import, Apply and the export run the same
/// checks, so a hand-written <c>publish.json</c> cannot put a game file or a file outside the mod into a package.
/// </summary>
/// <remarks>
/// The size comes from the header of the image only. No image decode runs here (decision D592).
/// </remarks>
public static class ScreenshotRules
{
    /// <summary>The largest width and height of a screenshot (question Q6).</summary>
    public const int MaxSide = 8192;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The extensions of the file dialog of Add images.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg"];

    /// <summary>The problem of the path of a list entry, or null: the path must be relative, below
    /// <c>publish/screenshots/</c>, and without <c>..</c>.</summary>
    public static string? PathProblem(string file)
    {
        var normal = file.Replace('\\', '/');
        var prefix = PublishSettings.ScreenshotsFolder + "/";
        if (normal.Length <= prefix.Length || Path.IsPathRooted(file) ||
            !normal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            normal.Split('/').Any(part => part is "" or "." or ".."))
            return PublishMessages.ImageOutsideFolder(file);
        return null;
    }

    /// <summary>The reason why a screenshot of the list cannot go into an export, or null when it passes.</summary>
    /// <param name="gameRoots">The game folders to refuse: the base of the profile and the linked games. A folder
    /// with <c>EXE\freelancer.ini</c> above the image is a game folder too.</param>
    public static string? Check(string modFolder, string file, IReadOnlyCollection<string> gameRoots) =>
        Check(modFolder, file, gameRoots, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    internal static string? Check(string modFolder, string file, IReadOnlyCollection<string> gameRoots, KnownBuilds? builds)
    {
        if (PathProblem(file) is { } problem)
            return problem;
        var path = Path.GetFullPath(Path.Combine(modFolder, file.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathRules.IsInside(path, modFolder))
            return PublishMessages.ImageOutsideFolder(file);
        if (!File.Exists(path))
            return PublishMessages.ImageMissing(file);
        try
        {
            if (!PathRules.IsInside(PathRules.RealPath(path), PathRules.RealPath(modFolder)))
                return PublishMessages.ImageOutsideFolder(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{file}: {e.Message}";
        }
        return FileProblem(file, path, gameRoots, builds);
    }

    /// <summary>The reason why Add images refuses a file, or null when the import can copy it.</summary>
    public static string? CheckImport(string source, IReadOnlyCollection<string> gameRoots) =>
        CheckImport(source, gameRoots, null);

    internal static string? CheckImport(string source, IReadOnlyCollection<string> gameRoots, KnownBuilds? builds)
    {
        var path = Path.GetFullPath(source);
        var name = Path.GetFileName(path);
        if (!File.Exists(path))
            return $"{name} does not exist.";
        try
        {
            return FileProblem(name, path, gameRoots, builds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{name}: {e.Message}";
        }
    }

    /// <summary>The width and the height of a PNG or JPEG image from its header, or null when the bytes are not a
    /// PNG or JPEG image. An image with a width or a height of zero is not an image.</summary>
    public static (int Width, int Height)? ImageSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature))
        {
            // The first chunk is IHDR: its length, its name, then the width and the height. PNG allows 1 to 2^31 - 1.
            if (bytes.Length < 24 || !bytes[12..16].SequenceEqual("IHDR"u8))
                return null;
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            return width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ? null : ((int)width, (int)height);
        }
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return null;
        var i = 2;
        while (i + 4 <= bytes.Length)
        {
            if (bytes[i] != 0xFF)
                return null;
            var marker = bytes[i + 1];
            if (marker == 0xFF)
            {
                // A fill byte.
                i++;
                continue;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 2)..]);
            if (IsFrameHeader(marker))
            {
                if (i + 9 > bytes.Length)
                    return null;
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 7)..]);
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 5)..]);
                return width == 0 || height == 0 ? null : (width, height);
            }
            if (marker == 0xDA || length < 2)
                return null;
            i += 2 + length;
        }
        return null;
    }

    /// <summary>The checks of the bytes of an image: not in a game folder, a PNG or JPEG image, not too large, and not
    /// a game file.</summary>
    private static string? FileProblem(string file, string path, IReadOnlyCollection<string> gameRoots, KnownBuilds? builds)
    {
        if (ModWrite.GameFolderOf(path, gameRoots) is { } game)
            return PublishMessages.ImageInGameFolder(file, game);
        var bytes = File.ReadAllBytes(path);
        if (ImageSize(bytes) is not { } size)
            return PublishMessages.ImageRefused(file);
        if (size.Width > MaxSide || size.Height > MaxSide)
            return PublishMessages.ImageTooLarge(file, size.Width, size.Height);
        return PackageRules.GameFile(file, bytes, builds)?.Message;
    }

    /// <summary>True for a JPEG start-of-frame marker, which holds the size of the image.</summary>
    private static bool IsFrameHeader(byte marker) => marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
}

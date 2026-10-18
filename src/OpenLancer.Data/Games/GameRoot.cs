using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.Export;

namespace OpenLancer.Data.Games;

/// <summary>
/// Finds the game root from a folder that the user selected or dropped. The game root is the folder with
/// <c>EXE\freelancer.ini</c> (section 6.1 of the M11a design). A source build has <c>bin\freelancer.exe</c> in its root
/// (section 8.5 of the M11 design). The search reads folder names only.
/// </summary>
internal static class GameRoot
{
    /// <summary>The file that marks a game root, relative to the root.</summary>
    public const string MarkerFile = @"EXE\freelancer.ini";

    /// <summary>The program of a source build, relative to its root.</summary>
    public const string SourceBuildProgram = @"bin\freelancer.exe";

    /// <summary>The <c>freelancer.ini</c> of a source build, relative to its root.</summary>
    public const string SourceBuildIni = @"bin\freelancer.ini";

    /// <summary>The root and its layout, or the error text when the folder holds no game root.</summary>
    internal sealed record Result(string? Root, string? Error, bool Missing = false,
        GameLayout Layout = GameLayout.Standard);

    /// <summary>Finds the root. A file counts as its folder.</summary>
    public static Result Find(string folder)
    {
        string full;
        try
        {
            full = GameFolders.Normal(folder);
            if (File.Exists(full))
                full = Path.GetDirectoryName(full)!;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return new Result(null, GameTexts.FolderNotReadable(Sentence(e.Message)));
        }

        if (!Directory.Exists(full))
            return new Result(null, GameTexts.FolderMissing, Missing: true);
        try
        {
            // 0. The disc, or a copy of it (decision D211).
            if (HasFile(full, "SETUP.EXE") && HasFile(full, "CAB1.CAB") || HasFile(full, @"GAME\EXE\FREELA_1.EXE"))
                return new Result(null, GameTexts.Disc);
            // 1. The root itself. 2. The developer layout, GameData\base.
            if (IsRoot(full))
                return new Result(full, null);
            if (Resolve(full, "base") is { } developer && IsRoot(developer))
                return new Result(developer, null);
            // 3. A folder inside a game: EXE, DATA, DLLS or one level below them (decision D213).
            for (var parent = Path.GetDirectoryName(full); parent != null && Depth(full, parent) <= 2;
                 parent = Path.GetDirectoryName(parent))
            {
                if (IsRoot(parent))
                    return new Result(parent, null);
            }

            // 4. One game one level down, for example "Microsoft Games" with "Freelancer" in it.
            var children = Children(full).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (children.Count == 1)
                return new Result(children[0], null);
            if (children.Count > 1)
                return new Result(null, GameTexts.TwoGames(string.Join(", ", children.Select(Path.GetFileName))));
            // 5. A source build (milestone M11): the folder itself, or a folder in it, for example bin or DATA.
            for (var source = full; source != null && Depth(full, source) <= 2; source = Path.GetDirectoryName(source))
            {
                if (IsSourceBuildRoot(source))
                    return new Result(source, null, Layout: GameLayout.SourceBuild);
            }
            return new Result(null, HasFile(full, "librelancer.ini") ? GameTexts.LibrelancerData : GameTexts.NotAGameFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Result(null, GameTexts.FolderNotReadable(Sentence(e.Message)));
        }
    }

    /// <summary>True when the folder has <c>EXE\freelancer.ini</c>, in any letter case.</summary>
    public static bool IsRoot(string folder) => Directory.Exists(folder) && HasFile(folder, MarkerFile);

    /// <summary>True when the folder is the root of a source build: it has <c>bin\freelancer.exe</c> and
    /// <c>bin\freelancer.ini</c>, in any letter case.</summary>
    public static bool IsSourceBuildRoot(string folder) =>
        Directory.Exists(folder) && HasFile(folder, SourceBuildProgram) && HasFile(folder, SourceBuildIni);

    /// <summary>The layout of a game root, or null when the folder is no game root.</summary>
    public static GameLayout? LayoutOf(string folder) =>
        IsRoot(folder) ? GameLayout.Standard : IsSourceBuildRoot(folder) ? GameLayout.SourceBuild : null;

    /// <summary>
    /// The game root that holds a path: the path itself, or the nearest game root above it. A game root can also be the
    /// root of a source build. Null when no such folder exists. OpenLancer uses it to refuse each write into a game
    /// folder (milestone M11). The path does not have to exist.
    /// </summary>
    public static string? Containing(string path)
    {
        for (var folder = Path.GetFullPath(path); folder != null; folder = Path.GetDirectoryName(folder))
        {
            try
            {
                if (LayoutOf(folder) != null)
                    return folder;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // OpenLancer cannot read this folder, so it cannot find a game root here. The check goes on with the
                // folder above it.
            }
        }
        return null;
    }

    /// <summary>True when the folder has the file, in any letter case.</summary>
    public static bool HasFile(string folder, string relative) => Resolve(folder, relative) is { } path && File.Exists(path);

    /// <summary>The path of a file or folder below a folder, with the letter case on disk, or null when it does not
    /// exist.</summary>
    public static string? Resolve(string folder, string relative)
    {
        var direct = Path.Combine(folder, relative.Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(direct) || Directory.Exists(direct))
            return direct;
        return new SysFolderQuickInit(folder).GetBackingFileName(relative, out var path) ? path : null;
    }

    /// <summary>The message as a sentence that ends with a period.</summary>
    public static string Sentence(string message)
    {
        message = message.Trim();
        return message.EndsWith('.') ? message : message + ".";
    }

    private static int Depth(string folder, string ancestor) =>
        Path.GetRelativePath(ancestor, folder).Split(Path.DirectorySeparatorChar).Length;

    /// <summary>The child folders. A child that the search cannot read is skipped.</summary>
    private static IEnumerable<string> Children(string folder)
    {
        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            bool root;
            try
            {
                root = IsRoot(child);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (root)
                yield return child;
        }
    }
}

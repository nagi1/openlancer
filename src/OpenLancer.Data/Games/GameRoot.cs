using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;

namespace OpenLancer.Data.Games;

/// <summary>
/// Finds the game root from a folder that the user selected or dropped: the folder with <c>EXE\freelancer.ini</c>
/// (section 6.1 of the M11a design). The search reads folder names only.
/// </summary>
internal static class GameRoot
{
    /// <summary>The file that marks a game root, relative to the root.</summary>
    public const string MarkerFile = @"EXE\freelancer.ini";

    /// <summary>The root, or the error text when the folder holds no game root.</summary>
    internal sealed record Result(string? Root, string? Error, bool Missing = false);

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
            return new Result(null, HasFile(full, "librelancer.ini") ? GameTexts.LibrelancerData : GameTexts.NotAGameFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Result(null, GameTexts.FolderNotReadable(Sentence(e.Message)));
        }
    }

    /// <summary>True when the folder has <c>EXE\freelancer.ini</c>, in any letter case.</summary>
    public static bool IsRoot(string folder) => Directory.Exists(folder) && HasFile(folder, MarkerFile);

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

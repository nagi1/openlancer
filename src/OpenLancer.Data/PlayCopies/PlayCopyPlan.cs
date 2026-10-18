using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.PlayCopies;

/// <summary>One file of the game that a play copy holds.</summary>
/// <param name="Size">The size, from the folder listing.</param>
/// <param name="Time">The write time in UTC, from the folder listing.</param>
internal readonly record struct PlannedFile(PlayCopyKind Kind, long Size, DateTime Time);

/// <summary>
/// Which files of a game a play copy holds, and how (section 8.1 of the M11 design, decisions D308, D334 and D335).
/// </summary>
/// <remarks>
/// <para>A standard game gets hard links for <c>DLLS</c> and for the large files of <c>DATA</c>. The text files of
/// <c>DATA</c> (<c>.ini</c>, <c>.thn</c>, <c>.txt</c> and <c>.xml</c>) are real copies. So are <c>EXE</c>,
/// <c>SAVE</c> and the other files and folders of the root. The game, FLServer, FLHook and the HD Edition wrappers
/// write into these, and a text tool can save a text file in place. The uninstaller of the installer (<c>unins*</c> in
/// the root, any letter case) is never in a play copy. It removes the files at its recorded path.</para>
/// <para>A source build gets <c>bin</c> without the build files <c>.pdb</c>, <c>.lib</c>, <c>.exp</c>, <c>.ilk</c>,
/// <c>.iobj</c> and <c>.ipdb</c>. Its <c>DATA</c> is as in a standard game.
/// <c>DATA_EXTRA</c>, <c>EXE</c>, <c>EBUEula.dll</c> and <c>EULA.RTF</c> are real copies. The source code and the
/// other folders are not in the copy.</para>
/// <para>A full copy has real copies only. The walk never goes into a link to a folder.</para>
/// </remarks>
internal static class PlayCopyPlan
{
    private static readonly string[] TextExtensions = [".ini", ".thn", ".txt", ".xml"];
    private static readonly string[] BuildOnlyExtensions = [".pdb", ".lib", ".exp", ".ilk", ".iobj", ".ipdb"];
    private static readonly string[] SourceBuildFolders = ["bin", "DATA", "DATA_EXTRA", "EXE"];
    private static readonly string[] SourceBuildFiles = ["EBUEula.dll", "EULA.RTF"];

    /// <summary>The kind of a file of the game in a play copy, or null when the play copy leaves it out.</summary>
    /// <param name="relative">The path relative to the game root, with <c>\</c>.</param>
    public static PlayCopyKind? KindOf(string relative, GameLayout layout, PlayCopyMode mode)
    {
        var parts = relative.Split('\\');
        var extension = Path.GetExtension(relative);
        if (parts.Length == 1)
        {
            if (layout == GameLayout.SourceBuild)
                return Named(parts[0], SourceBuildFiles) ? PlayCopyKind.Copy : null;
            return parts[0].StartsWith("unins", StringComparison.OrdinalIgnoreCase) ? null : PlayCopyKind.Copy;
        }

        var top = parts[0];
        if (layout == GameLayout.SourceBuild)
        {
            if (!Named(top, SourceBuildFolders))
                return null;
            if (top.Equals("bin", StringComparison.OrdinalIgnoreCase) && Named(extension, BuildOnlyExtensions))
                return null;
        }

        var large = top.Equals("DLLS", StringComparison.OrdinalIgnoreCase) ||
                    top.Equals("DATA", StringComparison.OrdinalIgnoreCase) && !Named(extension, TextExtensions);
        return large && mode == PlayCopyMode.Links ? PlayCopyKind.Link : PlayCopyKind.Copy;
    }

    /// <summary>The files of the game that a play copy holds, by their path relative to the root, with the letter case
    /// on disk.</summary>
    public static SortedDictionary<string, PlannedFile> Of(GameBase game, PlayCopyMode mode)
    {
        var plan = new SortedDictionary<string, PlannedFile>(VfsPath.Comparer);
        foreach (var file in Files(game.Root))
        {
            if (KindOf(file.Relative, game.Layout, mode) is { } kind)
                plan[file.Relative] = new PlannedFile(kind, file.Info.Length, file.Info.LastWriteTimeUtc);
        }
        return plan;
    }

    /// <summary>
    /// Each file below a folder, with its path relative to the folder and its listing. The walk never goes into a link
    /// to a folder, and it lists hidden and system files too.
    /// </summary>
    public static IEnumerable<(string Relative, FileInfo Info)> Files(string root)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            MatchType = MatchType.Simple,
        };
        var folders = new Stack<string>([""]);
        while (folders.TryPop(out var relative))
        {
            var folder = new DirectoryInfo(relative.Length == 0 ? root : Path.Combine(root, relative));
            foreach (var info in folder.EnumerateFileSystemInfos("*", options))
            {
                var path = relative.Length == 0 ? info.Name : relative + @"\" + info.Name;
                if (info is FileInfo file)
                    yield return (path, file);
                else if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                    folders.Push(path);
            }
        }
    }

    private static bool Named(string name, string[] names) =>
        names.Contains(name, StringComparer.OrdinalIgnoreCase);
}

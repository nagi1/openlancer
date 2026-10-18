using System;
using System.Collections.Generic;
using System.IO;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Export;

/// <summary>How the files of a game folder map to install paths.</summary>
public enum GameLayout
{
    /// <summary>An installed game: <c>EXE</c>, <c>DATA</c> and <c>DLLS</c> in the game root.</summary>
    Standard,

    /// <summary>A game that a user built from source code: the program and its <c>freelancer.ini</c> are in
    /// <c>bin</c>, and <c>DATA</c> is next to it. The install paths under <c>EXE\</c> map to <c>bin\</c>.</summary>
    SourceBuild,
}

/// <summary>
/// The files of a game that a merge uses as its base: a game root and its layout (milestone M11). The merge only
/// reads these files.
/// </summary>
/// <param name="Root">The full path of the game root.</param>
public sealed record GameBase(string Root, GameLayout Layout = GameLayout.Standard)
{
    private const string ExeFolder = "EXE";
    private const string BinFolder = "bin";

    /// <summary>A standard game root, for example the base folder of a profile.</summary>
    public static GameBase Standard(string root) => new(Path.GetFullPath(root));

    /// <summary>A file system that shows the files of the game by install path.</summary>
    public FileSystem OpenFileSystem() => new(OpenProvider());

    /// <summary>The file provider of the game root. For a source build, it maps <c>EXE\</c> to <c>bin\</c>.</summary>
    internal IFileProvider OpenProvider() =>
        Layout == GameLayout.SourceBuild ? new SourceBuildProvider(Root) : new SysFolderQuickInit(Root);

    /// <summary>The path relative to the root of the file of an install path. For a source build, a path under
    /// <c>EXE\</c> goes under <c>bin\</c>.</summary>
    /// <param name="installPath">A path in the normal form of <see cref="VfsPath"/>.</param>
    public string ToDisk(string installPath) => Layout == GameLayout.SourceBuild ? MapExe(installPath) : installPath;

    /// <summary>The path with <c>bin</c> in place of a first part <c>EXE</c>, in any letter case.</summary>
    private static string MapExe(string path)
    {
        if (path.Equals(ExeFolder, StringComparison.OrdinalIgnoreCase))
            return BinFolder;
        return path.StartsWith(ExeFolder + @"\", StringComparison.OrdinalIgnoreCase)
            ? BinFolder + path[ExeFolder.Length..]
            : path;
    }

    /// <summary>The files of a source build root. A path under <c>EXE\</c> reads the file under <c>bin\</c>.</summary>
    private sealed class SourceBuildProvider(string root) : IFileProvider
    {
        private readonly SysFolderQuickInit folder = new(root);

        public Stream? Open(string filename) => folder.Open(Map(filename));

        public bool FileExists(string filename) => folder.FileExists(Map(filename));

        public bool GetBackingFileName(string path, out string? fileName) =>
            folder.GetBackingFileName(Map(path), out fileName);

        public IEnumerable<string> GetFiles(string path) => folder.GetFiles(Map(path));

        public IEnumerable<string> GetDirectories(string path) => folder.GetDirectories(Map(path));

        public void Refresh()
        {
        }

        private static string Map(string path) => VfsPath.Normalize(path) is { } normal ? MapExe(normal) : path;
    }
}

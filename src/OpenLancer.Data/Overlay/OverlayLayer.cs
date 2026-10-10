using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Overlay;

/// <summary>
/// One file in the overlay: bytes made by the merge, or a mod file on disk.
/// </summary>
public sealed class OverlayFile
{
    private readonly byte[]? content;

    private OverlayFile(string path, byte[]? content, string? diskPath)
    {
        Path = path;
        this.content = content;
        DiskPath = diskPath;
    }

    /// <summary>The normal install path (see <see cref="VfsPath"/>).</summary>
    public string Path { get; }

    /// <summary>The mod file on disk, or null for generated content.</summary>
    public string? DiskPath { get; }

    public static OverlayFile Generated(string path, byte[] content) => new(path, content, null);

    public static OverlayFile FromDisk(string path, string diskPath) => new(path, null, diskPath);

    public Stream OpenRead() => content != null ? new MemoryStream(content, false) : File.OpenRead(DiskPath!);

    public byte[] ReadAllBytes() => content ?? File.ReadAllBytes(DiskPath!);
}

/// <summary>
/// The top file provider of an overlay file system. It holds only the files that the mods add or change.
/// Librelancer's <see cref="FileSystem"/> searches providers from last to first, so these files hide the base files
/// and all other paths fall through to the base folder.
/// </summary>
/// <remarks>
/// Generated (merged) files report no backing file, so a tool cannot save them in place. A whole-file override
/// reports its file in the mod, and an unchanged file falls through to its file in the base folder, which only
/// the read-only attribute protects.
/// </remarks>
internal sealed class OverlayLayer : IFileProvider
{
    private readonly Dictionary<string, OverlayFile> files;
    private readonly Dictionary<string, HashSet<string>> folderFiles = new(VfsPath.Comparer);
    private readonly Dictionary<string, HashSet<string>> folderFolders = new(VfsPath.Comparer);

    public OverlayLayer(IEnumerable<OverlayFile> overlayFiles)
    {
        files = overlayFiles.ToDictionary(f => f.Path, VfsPath.Comparer);
        foreach (var path in files.Keys)
        {
            Child(folderFiles, VfsPath.Parent(path), path);
            for (var folder = VfsPath.Parent(path); folder.Length > 0; folder = VfsPath.Parent(folder))
                Child(folderFolders, VfsPath.Parent(folder), folder);
        }
    }

    public IReadOnlyCollection<OverlayFile> Files => files.Values;

    public Stream? Open(string filename) => Find(filename)?.OpenRead();

    public bool FileExists(string filename) => Find(filename) != null;

    public bool GetBackingFileName(string path, out string? fileName)
    {
        var file = Find(path);
        fileName = file?.DiskPath;
        return file != null;
    }

    public IEnumerable<string> GetFiles(string path) => Names(folderFiles, path);

    public IEnumerable<string> GetDirectories(string path) => Names(folderFolders, path);

    public void Refresh()
    {
        // The overlay is built once. Build a new overlay to see changed mod files.
    }

    private OverlayFile? Find(string path)
    {
        var normal = VfsPath.Normalize(path);
        return normal != null && files.TryGetValue(normal, out var file) ? file : null;
    }

    private static IEnumerable<string> Names(Dictionary<string, HashSet<string>> index, string path)
    {
        var normal = VfsPath.Normalize(path);
        return normal != null && index.TryGetValue(normal, out var children)
            ? children.Select(c => c[(c.LastIndexOf('\\') + 1)..]).ToArray()
            : [];
    }

    private static void Child(Dictionary<string, HashSet<string>> index, string parent, string child)
    {
        if (!index.TryGetValue(parent, out var set))
            index[parent] = set = new HashSet<string>(VfsPath.Comparer);
        set.Add(child);
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>
/// The two fingerprints of a mod (decision D583 and section 8.5 of the M11c design). The content fingerprint changes
/// with the game data of the mod. The page fingerprint changes with the texts of the publish page. The Mods window
/// compares them with the last export.
/// </summary>
/// <remarks>
/// Both read the saved files only. The instance keeps the hash of each file by its path, its size and its last write
/// time, so that a new scan reads only the changed files. One instance can serve several threads.
/// </remarks>
public sealed class ModFingerprint
{
    private readonly ConcurrentDictionary<string, (long Size, DateTime Time, string Hash)> cache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private int filesHashed;

    /// <summary>The number of files that this instance read to hash them.</summary>
    public int FilesHashed => filesHashed;

    /// <summary>
    /// The content fingerprint: the id, the format version, the tier, the targets, <c>dependsOn</c>,
    /// <c>loadAfter</c>, the bytes of <c>strings.json</c>, and the path and the hash of each file of <c>patches</c>
    /// and <c>files</c> that a package does not skip. Not the display name, the version and the publish files.
    /// </summary>
    public string Content(ModManifest manifest, string modFolder)
    {
        var text = new StringBuilder();
        text.Append($"id\t{manifest.Id}\n");
        text.Append($"formatVersion\t{manifest.FormatVersion}\n");
        text.Append($"tier\t{manifest.Tier}\n");
        text.Append($"targets\t{string.Join(',', manifest.Targets)}\n");
        text.Append($"dependsOn\t{string.Join(',', manifest.DependsOn)}\n");
        text.Append($"loadAfter\t{string.Join(',', manifest.LoadAfter)}\n");
        text.Append($"{Mod.StringsFile}\t{HashOrNone(Path.Combine(modFolder, Mod.StringsFile))}\n");
        var files = new List<(string Relative, string Path)>();
        foreach (var sub in new[] { Mod.PatchesFolder, Mod.FilesFolder })
        {
            var root = Path.Combine(modFolder, sub);
            if (!Directory.Exists(root))
                continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(modFolder, path);
                if (PackageRules.SkipReason(path, relative) == null)
                    files.Add((relative.Replace('\\', '/'), path));
            }
        }
        foreach (var (relative, path) in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
            text.Append($"file\t{relative}\t{Hash(path)}\n");
        return ContentHash.Of(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>The page fingerprint: the display name, the version, the bytes of <c>publish.json</c> and the hash of
    /// each listed screenshot.</summary>
    public string Page(ModManifest manifest, string modFolder, PublishSettings settings)
    {
        var text = new StringBuilder();
        text.Append($"name\t{manifest.Name}\n");
        text.Append($"version\t{manifest.Version}\n");
        text.Append($"{PublishSettings.FileName}\t{HashOrNone(Path.Combine(modFolder, PublishSettings.FileName))}\n");
        foreach (var screenshot in settings.Screenshots)
        {
            var path = ScreenshotRules.PathProblem(screenshot.File) == null
                ? Path.Combine(modFolder, screenshot.File.Replace('/', Path.DirectorySeparatorChar))
                : null;
            text.Append($"screenshot\t{screenshot.File}\t{(path == null ? "-" : HashOrNone(path))}\n");
        }
        return ContentHash.Of(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Both fingerprints of a mod folder, from its saved files.</summary>
    /// <exception cref="ModLoadException">mod.json does not load.</exception>
    public (string Content, string Page) Of(string modFolder)
    {
        var manifest = ModManifest.Load(modFolder);
        return (Content(manifest, modFolder), Page(manifest, modFolder, PublishSettings.Load(modFolder).Settings));
    }

    private string HashOrNone(string path) => File.Exists(path) ? Hash(path) : "-";

    private string Hash(string path)
    {
        var info = new FileInfo(path);
        var key = (info.Length, info.LastWriteTimeUtc);
        if (cache.TryGetValue(info.FullName, out var known) && known.Size == key.Length && known.Time == key.LastWriteTimeUtc)
            return known.Hash;
        Interlocked.Increment(ref filesHashed);
        var hash = DataDigest.HashFile(path);
        cache[info.FullName] = (key.Length, key.LastWriteTimeUtc, hash);
        return hash;
    }
}

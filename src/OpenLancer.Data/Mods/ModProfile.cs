using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Mods;

/// <summary>
/// A named set of mods, stored as <c>&lt;name&gt;.json</c>: <c>{ "base": "../base", "mods": [ "path/to/mod", ... ] }</c>.
/// Paths are relative to the profile file. <c>base</c> is the game folder or the extracted game data, by default
/// <c>../base</c>. It can be an absolute path, for example the linked game folder of a player.
/// </summary>
public sealed class ModProfile
{
    public const string DefaultBase = "../base";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private ModProfile(string name, string path, string baseFolder, IReadOnlyList<Mod> mods)
    {
        Name = name;
        FilePath = path;
        BaseFolder = baseFolder;
        Mods = mods;
    }

    public string Name { get; }

    public string FilePath { get; }

    /// <summary>The game folder or the extracted game data that the mods apply to. Nothing writes into it.</summary>
    public string BaseFolder { get; }

    /// <summary>The mods in load order. A later mod wins over an earlier one.</summary>
    public IReadOnlyList<Mod> Mods { get; }

    /// <summary>The place of a mod in <see cref="Mods"/>, or -1 when the profile has no mod with the id.</summary>
    public int IndexOf(string modId)
    {
        for (var i = 0; i < Mods.Count; i++)
        {
            if (Mods[i].Id == modId)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The mods of the edit view (decision D2): the mods up to the active mod and the active mod, in load order. When
    /// <paramref name="activeModId"/> is null, all mods. The editor, <c>oltool validate --active</c> and Play show
    /// this view.
    /// </summary>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    public IReadOnlyList<Mod> ModsThrough(string? activeModId)
    {
        if (activeModId == null)
            return Mods;
        var index = IndexOf(activeModId);
        if (index < 0)
            throw new ArgumentException($"The profile {Name} has no mod '{activeModId}'.", nameof(activeModId));
        return Mods.Take(index + 1).ToList();
    }

    /// <exception cref="ModLoadException">The profile, a mod, or the mod order is not valid. When the base folder does
    /// not exist, <see cref="ModLoadException.MissingBaseFolder"/> names it.</exception>
    public static ModProfile Load(string path)
    {
        path = Path.GetFullPath(path);
        var json = Read(path);
        var folder = Path.GetDirectoryName(path)!;
        var baseFolder = BaseOf(path, json);
        if (!Directory.Exists(baseFolder))
            throw new ModLoadException(path, $"The base folder {baseFolder} does not exist.") { MissingBaseFolder = baseFolder };
        if ((json?.Mods ?? []).Any(string.IsNullOrWhiteSpace))
            throw new ModLoadException(path, "The mods list must not contain empty entries.");
        var mods = (json?.Mods ?? []).Select(m => Mod.Load(Path.Combine(folder, m))).ToList();
        return new ModProfile(Path.GetFileNameWithoutExtension(path), path, baseFolder, ResolveOrder(mods, path));
    }

    /// <summary>
    /// The full path of the base folder of a profile file. It does not check that the folder exists, and it loads no mod.
    /// So it also reads a profile whose game folder moved.
    /// </summary>
    /// <exception cref="ModLoadException">The file is not found, or it is not a valid profile.</exception>
    public static string BaseFolderOf(string profilePath)
    {
        profilePath = Path.GetFullPath(profilePath);
        return BaseOf(profilePath, Read(profilePath));
    }

    /// <summary>
    /// The full paths of the mod folders that a profile file lists, in the order of the file. It reads the JSON as a
    /// load does, also with comments, and it loads no mod. So it also reads a profile whose mods do not load.
    /// </summary>
    /// <exception cref="ModLoadException">The file is not found, or it is not a valid profile.</exception>
    public static IReadOnlyList<string> ListedFolders(string profilePath)
    {
        profilePath = Path.GetFullPath(profilePath);
        var folder = Path.GetDirectoryName(profilePath)!;
        return [.. (Read(profilePath)?.Mods ?? []).Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => Path.GetFullPath(Path.Combine(folder, m)))];
    }

    /// <exception cref="ModLoadException">The file is not found, or it is not valid JSON of a profile.</exception>
    private static ProfileJson? Read(string path)
    {
        if (!File.Exists(path))
            throw new ModLoadException(path, "File not found.");
        try
        {
            return JsonSerializer.Deserialize<ProfileJson>(SharedFile.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new ModLoadException(path, e.Message);
        }
    }

    private static string BaseOf(string path, ProfileJson? json) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, json?.Base ?? DefaultBase));

    /// <summary>
    /// Sorts the mods so that each mod loads after the mods in its <c>dependsOn</c> and <c>loadAfter</c> lists.
    /// Otherwise the requested order stays.
    /// </summary>
    /// <exception cref="ModLoadException">A dependency is missing, an id is used twice, or the order has a cycle.</exception>
    public static IReadOnlyList<Mod> ResolveOrder(IReadOnlyList<Mod> requested, string source)
    {
        var byId = new Dictionary<string, Mod>(StringComparer.Ordinal);
        foreach (var mod in requested)
        {
            if (!byId.TryAdd(mod.Id, mod))
                throw new ModLoadException(source, $"Mod '{mod.Id}' is in the profile two times.");
        }

        var before = requested.ToDictionary(m => m, _ => new HashSet<Mod>());
        foreach (var mod in requested)
        {
            foreach (var dep in mod.Manifest.DependsOn)
            {
                if (!byId.TryGetValue(dep, out var depMod))
                    throw new ModLoadException(source, $"Mod '{mod.Id}' needs mod '{dep}', which is not in the profile.");
                before[mod].Add(depMod);
            }
            foreach (var after in mod.Manifest.LoadAfter)
            {
                if (byId.TryGetValue(after, out var afterMod))
                    before[mod].Add(afterMod);
            }
        }

        var ordered = new List<Mod>();
        var remaining = requested.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(m => before[m].All(ordered.Contains))
                       ?? throw new ModLoadException(source,
                           "The mod order has a cycle: " + string.Join(", ", remaining.Select(m => m.Id)));
            ordered.Add(next);
            remaining.Remove(next);
        }
        return ordered;
    }

    private sealed class ProfileJson
    {
        public string? Base { get; set; }
        public List<string>? Mods { get; set; }
    }
}

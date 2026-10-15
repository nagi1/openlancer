using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Mods;

/// <summary>
/// A named set of mods, stored as <c>&lt;name&gt;.json</c>: <c>{ "base": "../base", "mods": [ "path/to/mod", ... ] }</c>.
/// Paths are relative to the profile file. <c>base</c> is the extracted vanilla data, by default <c>../base</c>.
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

    /// <summary>The extracted vanilla data that the mods apply to.</summary>
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

    /// <exception cref="ModLoadException">The profile, a mod, or the mod order is not valid.</exception>
    public static ModProfile Load(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new ModLoadException(path, "File not found.");

        ProfileJson? json;
        try
        {
            json = JsonSerializer.Deserialize<ProfileJson>(SharedFile.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new ModLoadException(path, e.Message);
        }

        var folder = Path.GetDirectoryName(path)!;
        var baseFolder = Path.GetFullPath(Path.Combine(folder, json?.Base ?? DefaultBase));
        if (!Directory.Exists(baseFolder))
            throw new ModLoadException(path, $"The base folder {baseFolder} does not exist.");
        if ((json?.Mods ?? []).Any(string.IsNullOrWhiteSpace))
            throw new ModLoadException(path, "The mods list must not contain empty entries.");
        var mods = (json?.Mods ?? []).Select(m => Mod.Load(Path.Combine(folder, m))).ToList();
        return new ModProfile(Path.GetFileNameWithoutExtension(path), path, baseFolder, ResolveOrder(mods, path));
    }

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

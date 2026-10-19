using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Mods;

/// <summary>
/// Changes a profile file. The file keeps the fields that the editor does not know, for example a later
/// <c>engineFlags</c> block, and its byte order mark and line ends (roadmap rule R7).
/// </summary>
internal static class ProfileFile
{
    private const string ModsField = "mods";
    private const string BaseField = "base";

    /// <summary>
    /// Adds a mod folder to the end of the mods list, as a path relative to the profile file. The profile must load
    /// with the mod, so the file does not change when it would not.
    /// </summary>
    /// <exception cref="ModLoadException">The profile or the mod is not valid, the profile file has a comment, or the
    /// profile does not load with the mod: it has a mod with the same id, it does not have a mod that the mod depends
    /// on, or the load order has a cycle.</exception>
    public static void AddMod(string profilePath, string modFolder)
    {
        profilePath = Path.GetFullPath(profilePath);
        modFolder = Path.GetFullPath(modFolder);
        var file = Read(profilePath);
        // The same checks as a load of the profile with the mod.
        ModProfile.ResolveOrder([.. ModProfile.Load(profilePath).Mods, Mod.Load(modFolder)], profilePath);

        if (file.Root[ModsField] is not JsonArray mods)
        {
            if (file.Root[ModsField] != null)
                throw new ModLoadException(profilePath, $"'{ModsField}' must be a list.");
            file.Root[ModsField] = mods = new JsonArray();
        }
        mods.Add(Path.GetRelativePath(Path.GetDirectoryName(profilePath)!, modFolder).Replace(Path.DirectorySeparatorChar, '/'));
        AtomicFile.WriteAllBytes(profilePath, file.ToBytes());
    }

    /// <summary>
    /// Removes a mod folder from the mods list (decision D574 of the M11c design). The folder stays. A mod that
    /// another mod of the profile names in its <c>dependsOn</c> list cannot go.
    /// </summary>
    /// <exception cref="ModLoadException">The profile does not list the folder, another mod needs the mod, or the
    /// file has a comment.</exception>
    public static void RemoveMod(string profilePath, string modFolder)
    {
        profilePath = Path.GetFullPath(profilePath);
        modFolder = Path.GetFullPath(modFolder);
        var file = Read(profilePath);
        var mods = file.Root[ModsField] as JsonArray;
        var index = mods == null ? -1 : IndexOf(profilePath, mods, modFolder);
        if (mods == null || index < 0)
            throw new ModLoadException(profilePath, $"The profile has no mod in {modFolder}.");
        if (Manifest(modFolder)?.Id is { } id)
        {
            for (var i = 0; i < mods.Count; i++)
            {
                if (i != index && Entry(mods[i]) is { } entry && Manifest(Resolve(profilePath, entry)) is { } other &&
                    other.DependsOn.Contains(id, StringComparer.Ordinal))
                    throw new ModLoadException(profilePath, Publishing.PublishMessages.RemoveRefused(other.Id));
            }
        }
        mods.RemoveAt(index);
        AtomicFile.WriteAllBytes(profilePath, file.ToBytes());
    }

    /// <summary>
    /// Changes each entry of the mods list that names the old folder to the new folder, after a rename of the mod
    /// folder. A relative entry stays relative, and an absolute entry stays absolute. It loads no mod.
    /// </summary>
    /// <returns>True when the file changed.</returns>
    /// <exception cref="ModLoadException">The file is not a valid JSON object, or it has a comment.</exception>
    public static bool RenameMod(string profilePath, string oldFolder, string newFolder)
    {
        profilePath = Path.GetFullPath(profilePath);
        newFolder = Path.GetFullPath(newFolder);
        var file = Read(profilePath);
        if (file.Root[ModsField] is not JsonArray mods)
            return false;
        var changed = false;
        for (var i = 0; i < mods.Count; i++)
        {
            if (Entry(mods[i]) is not { } entry || !GameFolders.Same(Resolve(profilePath, entry), oldFolder))
                continue;
            mods[i] = Path.IsPathRooted(entry)
                ? newFolder
                : Path.GetRelativePath(Path.GetDirectoryName(profilePath)!, newFolder).Replace(Path.DirectorySeparatorChar, '/');
            changed = true;
        }
        if (changed)
            AtomicFile.WriteAllBytes(profilePath, file.ToBytes());
        return changed;
    }

    private static int IndexOf(string profilePath, JsonArray mods, string modFolder)
    {
        for (var i = 0; i < mods.Count; i++)
        {
            if (Entry(mods[i]) is { } entry && GameFolders.Same(Resolve(profilePath, entry), modFolder))
                return i;
        }
        return -1;
    }

    private static string? Entry(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text : null;

    private static string Resolve(string profilePath, string entry) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(profilePath)!, entry));

    /// <summary>The manifest of a mod folder, or null when it does not load.</summary>
    private static ModManifest? Manifest(string modFolder)
    {
        try
        {
            return ModManifest.Load(modFolder);
        }
        catch (ModLoadException)
        {
            return null;
        }
    }

    /// <summary>Reads a profile file for a write.</summary>
    /// <exception cref="ModLoadException">The file does not exist, is not a JSON object, or has a comment.</exception>
    private static JsonFile Read(string profilePath)
    {
        try
        {
            return JsonFile.Read(profilePath);
        }
        catch (InvalidDataException e)
        {
            throw new ModLoadException(profilePath, e.Message);
        }
        catch (FileNotFoundException)
        {
            throw new ModLoadException(profilePath, "File not found.");
        }
    }

    /// <summary>
    /// Sets the base folder of a profile to an absolute path, for example after the game folder moved. Only the new base
    /// folder must exist, because the old one is usually gone.
    /// </summary>
    /// <exception cref="ModLoadException">The file is not a valid profile, it has a comment, or the new base folder does
    /// not exist.</exception>
    public static void SetBase(string profilePath, string baseFolder)
    {
        profilePath = Path.GetFullPath(profilePath);
        baseFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseFolder));
        // The same checks of the JSON as a load, without the checks of the old base folder and the mods.
        ModProfile.BaseFolderOf(profilePath);
        var file = Read(profilePath);
        if (!Directory.Exists(baseFolder))
            throw new ModLoadException(profilePath, $"The base folder {baseFolder} does not exist.");
        file.Root[BaseField] = baseFolder;
        AtomicFile.WriteAllBytes(profilePath, file.ToBytes());
    }
}

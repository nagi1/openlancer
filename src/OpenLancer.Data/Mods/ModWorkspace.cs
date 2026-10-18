using System;
using System.IO;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Mods;

/// <summary>Makes new mods and adds mods to profiles.</summary>
public static class ModWorkspace
{
    private const string NewModVersion = "1.0.0";

    /// <summary>
    /// Makes the folder <c>&lt;modsFolder&gt;\&lt;id&gt;</c> with a <c>mod.json</c> of the current format version for a
    /// tier-1 mod, and returns the folder.
    /// </summary>
    /// <param name="name">The display name. An empty name gives the id.</param>
    /// <exception cref="ArgumentException">The id is not valid.</exception>
    /// <exception cref="IOException">The folder exists, or the file could not be written.</exception>
    public static string CreateMod(string modsFolder, string id, string name)
    {
        if (!ModManifest.IsValidId(id))
            throw new ArgumentException($"'{id}': {ModManifest.IdRule}", nameof(id));
        var folder = Path.Combine(Path.GetFullPath(modsFolder), id);
        if (Directory.Exists(folder) || File.Exists(folder))
            throw new IOException($"{folder} exists. Use another id.");

        var manifest = JsonFile.Create();
        manifest.Root["id"] = id;
        manifest.Root["name"] = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        manifest.Root["version"] = NewModVersion;
        manifest.Root["formatVersion"] = ModManifest.CurrentFormatVersion;
        // A new mod is a data mod. Without a targets field, it is for each target of its tier.
        manifest.Root["tier"] = ModManifest.DataTier;
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, ModManifest.FileName), manifest.ToBytes());
        }
        catch
        {
            // The folder is new, so nothing else is in it.
            Directory.Delete(folder, recursive: true);
            throw;
        }
        return folder;
    }

    /// <summary>
    /// Adds a mod folder to the end of the mods list of a profile file. The file keeps its other fields, its byte
    /// order mark and its line ends. The file does not change when the profile would not load with the mod.
    /// </summary>
    /// <exception cref="ModLoadException">The profile or the mod is not valid, the profile file has a JSON comment
    /// (the editor cannot keep it), or the profile does not load with the mod: it already has a mod with the same id,
    /// it does not have a mod that the mod depends on, or the load order has a cycle.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void AddToProfile(string profilePath, string modFolder) => ProfileFile.AddMod(profilePath, modFolder);
}

using System.IO;
using System.Text.Json.Nodes;
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
        JsonFile file;
        try
        {
            file = JsonFile.Read(profilePath);
        }
        catch (InvalidDataException e)
        {
            throw new ModLoadException(profilePath, e.Message);
        }
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
        JsonFile file;
        try
        {
            file = JsonFile.Read(profilePath);
        }
        catch (InvalidDataException e)
        {
            throw new ModLoadException(profilePath, e.Message);
        }

        if (!Directory.Exists(baseFolder))
            throw new ModLoadException(profilePath, $"The base folder {baseFolder} does not exist.");
        file.Root[BaseField] = baseFolder;
        AtomicFile.WriteAllBytes(profilePath, file.ToBytes());
    }
}

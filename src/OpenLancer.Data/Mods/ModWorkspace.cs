using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Mods;

/// <summary>Makes new profiles and mods, and changes profiles.</summary>
/// <remarks>
/// A workspace folder holds the profiles in <c>profiles</c> and the mods in <c>mods</c>. Nothing here writes into a
/// game folder (the write guard of milestone M11a).
/// </remarks>
public static class ModWorkspace
{
    private const string NewModVersion = "1.0.0";

    /// <summary>The folder of the profiles in a workspace folder.</summary>
    public const string ProfilesFolder = "profiles";

    /// <summary>The folder of the mods in a workspace folder.</summary>
    public const string ModsFolder = "mods";

    /// <summary>The id of the empty mod of a new profile, before a number that makes it unique.</summary>
    public const string StarterModId = "my-mod";

    private const string StarterModName = "My mod";

    /// <summary>The default workspace folder: <c>Documents\OpenLancer</c>. It follows a moved Documents folder.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenLancer");

    /// <summary>
    /// Makes the folder <c>&lt;modsFolder&gt;\&lt;id&gt;</c> with a <c>mod.json</c> of the current format version for a
    /// tier-1 mod, and returns the folder.
    /// </summary>
    /// <param name="name">The display name. An empty name gives the id.</param>
    /// <param name="baseFolder">The base folder of the profile, or null. A mods folder inside it is refused.</param>
    /// <exception cref="ArgumentException">The id is not valid.</exception>
    /// <exception cref="IOException">The folder exists, the mods folder is in the base folder, or the file could not be
    /// written.</exception>
    public static string CreateMod(string modsFolder, string id, string name, string? baseFolder = null)
    {
        if (!ModManifest.IsValidId(id))
            throw new ArgumentException($"'{id}': {ModManifest.IdRule}", nameof(id));
        if (baseFolder != null && GameFolders.Overlaps(modsFolder, baseFolder))
            throw new IOException(GameTexts.ModsFolderInGame);
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

    /// <summary>
    /// Makes <c>profiles\&lt;name&gt;.json</c> in the workspace, with the game root as its absolute base, and an empty
    /// mod <c>my-mod</c> (or the first free <c>my-mod-N</c>) in <c>mods</c> as its only mod. A failure leaves no new
    /// file.
    /// </summary>
    /// <param name="linkedRoots">The roots of the linked games. The workspace must be outside each of them.</param>
    /// <returns>The full path of the profile file and the id of the mod.</returns>
    /// <exception cref="ArgumentException">The name is not valid.</exception>
    /// <exception cref="IOException">The profile or its play folder exists, the workspace is in a game root, or a write
    /// failed.</exception>
    /// <exception cref="UnauthorizedAccessException">A write failed.</exception>
    public static (string ProfilePath, string ModId) CreateProfile(string workspaceFolder, string name, string gameRoot,
        IReadOnlyCollection<string> linkedRoots)
    {
        if (!ModManifest.IsValidId(name))
            throw new ArgumentException(GameTexts.ProfileNameRule, nameof(name));
        gameRoot = GameFolders.Normal(gameRoot);
        CheckWorkspace(workspaceFolder, [gameRoot, .. linkedRoots]);
        var profiles = Path.Combine(Path.GetFullPath(workspaceFolder), ProfilesFolder);
        var mods = Path.Combine(Path.GetFullPath(workspaceFolder), ModsFolder);
        if (ProfileNameTaken(profiles, name))
            throw new IOException(GameTexts.ProfileNameExists);

        var profilePath = Path.Combine(profiles, name + ".json");
        var profile = JsonFile.Create();
        profile.Root["base"] = gameRoot;
        profile.Root["mods"] = new JsonArray();
        Directory.CreateDirectory(profiles);
        AtomicFile.WriteAllBytes(profilePath, profile.ToBytes());
        string? modFolder = null;
        try
        {
            var modId = FreeName(StarterModId, id => Path.Exists(Path.Combine(mods, id)));
            modFolder = CreateMod(mods, modId, StarterModName, gameRoot);
            AddToProfile(profilePath, modFolder);
            return (profilePath, modId);
        }
        catch
        {
            // The profile file and the mod folder are new, so nothing else is in them.
            File.Delete(profilePath);
            if (modFolder != null && Directory.Exists(modFolder))
                Directory.Delete(modFolder, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Refuses a workspace whose <c>profiles</c> or <c>mods</c> folder is a game root or is inside one, also through a
    /// link.
    /// </summary>
    /// <exception cref="IOException">The workspace is in a game root.</exception>
    public static void CheckWorkspace(string workspaceFolder, IEnumerable<string> gameRoots)
    {
        var folders = new[] { ProfilesFolder, ModsFolder }
            .Select(f => Path.Combine(Path.GetFullPath(workspaceFolder), f))
            .ToList();
        if (gameRoots.Any(root => folders.Any(f => PathRules.IsInside(f, root) ||
                                                   PathRules.IsInside(PathRules.RealPath(f), PathRules.RealPath(root)))))
            throw new IOException(GameTexts.WorkspaceInGame);
    }

    /// <summary>The first free profile name of a workspace: the name, or the name with "-2", "-3" and so on.</summary>
    public static string FreeProfileName(string workspaceFolder, string name) =>
        FreeName(name, n => ProfileNameTaken(Path.Combine(Path.GetFullPath(workspaceFolder), ProfilesFolder), n));

    /// <summary>
    /// Sets the base folder of a profile file to an absolute path. The file keeps its other fields, its byte order mark
    /// and its line ends. Only the new base folder must exist, so this works after the old game folder moved.
    /// </summary>
    /// <exception cref="ModLoadException">The file is not a valid profile, it has a JSON comment, or the new base folder
    /// does not exist.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetBase(string profilePath, string baseFolder) => ProfileFile.SetBase(profilePath, baseFolder);

    /// <summary>True when the profile file or its play folder (see <c>PlayFolder</c>) exists.</summary>
    private static bool ProfileNameTaken(string profilesFolder, string name) =>
        Path.Exists(Path.Combine(profilesFolder, name + ".json")) ||
        Path.Exists(Path.Combine(profilesFolder, name + ".play"));

    private static string FreeName(string name, Func<string, bool> taken)
    {
        if (!taken(name))
            return name;
        for (var i = 2; ; i++)
        {
            if (!taken($"{name}-{i}"))
                return $"{name}-{i}";
        }
    }
}

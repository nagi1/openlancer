using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Mods;

/// <summary>
/// A mod folder: <c>mod.json</c>, entry-level INI patches, cutscene patches, whole files and text.
/// </summary>
/// <remarks>
/// Layout (docs/MOD_FORMAT.md):
/// <c>patches/&lt;install path&gt;.ini</c> patches the file at that path.
/// <c>patches/&lt;install path&gt;.thn</c> patches the cutscene script at that path.
/// <c>files/&lt;install path&gt;</c> adds or replaces the file at that path.
/// <c>strings.json</c> holds the text that patches refer to with <c>$str:key</c> and <c>$ifc:key</c>.
/// <c>props/&lt;nickname&gt;.ini</c> and <c>props/&lt;nickname&gt;.strings.json</c> hold a prop group of the editor
/// (milestone M23). <c>graphs/</c> and <c>prefabs/missions/</c> hold the editor files of missions (see
/// <see cref="Editing.ModFileDocument"/>). The merge and the game ignore these folders and each other folder of a
/// mod.
/// </remarks>
public sealed class Mod
{
    public const string PatchesFolder = "patches";
    public const string FilesFolder = "files";

    /// <summary>The folder of the prop groups of the editor (decision D283 of the M23 design). The merge ignores
    /// it.</summary>
    public const string PropsFolder = "props";
    public const string StringsFile = "strings.json";

    private Mod(ModManifest manifest, string folder,
        Dictionary<string, string> patches, Dictionary<string, string> files, ModStrings strings)
    {
        Manifest = manifest;
        Folder = folder;
        Patches = patches;
        Files = files;
        Strings = strings;
    }

    public ModManifest Manifest { get; }

    public string Id => Manifest.Id;

    public string Folder { get; }

    /// <summary>Normal install path of the patched file, to the patch file on disk.</summary>
    internal IReadOnlyDictionary<string, string> Patches { get; }

    /// <summary>Normal install path, to the file on disk that adds or replaces it.</summary>
    internal IReadOnlyDictionary<string, string> Files { get; }

    internal ModStrings Strings { get; }

    /// <summary>
    /// True when the journal of a save of the editor exists now: the save runs, or it stopped after its commit point.
    /// Some files of the mod can then be from before the save. Each read checks the disk.
    /// </summary>
    public bool HasPendingSave => SaveTransaction.HasJournal(Folder);

    /// <exception cref="ModLoadException">The mod folder is not valid.</exception>
    public static Mod Load(string folder)
    {
        folder = Path.GetFullPath(folder);
        var manifest = ModManifest.Load(folder);

        var patches = MapFolder(folder, PatchesFolder);
        foreach (var (target, file) in patches)
        {
            if (!target.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                !target.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
                throw new ModLoadException(file, "A patch must be an .ini file or a .thn file.");
        }

        return new Mod(manifest, folder, patches, MapFolder(folder, FilesFolder),
            ModStrings.Load(Path.Combine(folder, StringsFile)));
    }

    private static Dictionary<string, string> MapFolder(string modFolder, string subFolder)
    {
        var map = new Dictionary<string, string>(VfsPath.Comparer);
        var root = Path.Combine(modFolder, subFolder);
        if (!Directory.Exists(root))
            return map;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var target = VfsPath.Normalize(Path.GetRelativePath(root, file))!;
            if (!map.TryAdd(target, file))
                throw new ModLoadException(file, $"Two files map to {target}.");
        }
        return map;
    }
}

/// <summary>
/// The text of a mod. String entries go to the string table, infocard entries (RDL XML) to the infocard table.
/// </summary>
internal sealed class ModStrings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly ModStrings Empty = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    private ModStrings(IReadOnlyDictionary<string, string> strings, IReadOnlyDictionary<string, string> infocards)
    {
        Strings = strings;
        Infocards = infocards;
    }

    public IReadOnlyDictionary<string, string> Strings { get; }

    public IReadOnlyDictionary<string, string> Infocards { get; }

    public static ModStrings Load(string path) => File.Exists(path) ? Parse(path, SharedFile.ReadAllText(path)) : Empty;

    /// <param name="path">The file, for messages.</param>
    /// <param name="text">The text of the file.</param>
    /// <exception cref="ModLoadException">The text is not valid.</exception>
    public static ModStrings Parse(string path, string text)
    {
        StringsJson? json;
        try
        {
            json = JsonSerializer.Deserialize<StringsJson>(text, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new ModLoadException(path, e.Message);
        }

        var strings = json?.Strings ?? new Dictionary<string, string>();
        var infocards = json?.Infocards ?? new Dictionary<string, string>();
        foreach (var (key, value) in strings.Concat(infocards))
        {
            if (!IsValidKey(key))
                throw new ModLoadException(path, $"Key '{key}' must use letters, digits, '_', '-' or '.'.");
            if (string.IsNullOrEmpty(value))
                throw new ModLoadException(path, $"The text of '{key}' is empty.");
        }
        return new ModStrings(strings, infocards);
    }

    /// <summary>True when a text key has only ASCII letters, digits, '_', '-' and '.', and is not empty.</summary>
    public static bool IsValidKey(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private sealed class StringsJson
    {
        public Dictionary<string, string>? Strings { get; set; }
        public Dictionary<string, string>? Infocards { get; set; }
    }
}

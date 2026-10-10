using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenLancer.Data.Mods;

/// <summary>
/// The contents of a mod's <c>mod.json</c>.
/// </summary>
public sealed partial record ModManifest(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> LoadAfter)
{
    public const string FileName = "mod.json";
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <exception cref="ModLoadException">The file is missing or not valid.</exception>
    public static ModManifest Load(string modFolder)
    {
        var path = Path.Combine(modFolder, FileName);
        if (!File.Exists(path))
            throw new ModLoadException(path, "File not found.");

        ManifestJson? json;
        try
        {
            json = JsonSerializer.Deserialize<ManifestJson>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new ModLoadException(path, e.Message);
        }
        if (json == null)
            throw new ModLoadException(path, "The file is empty.");
        if (json.FormatVersion != CurrentFormatVersion)
            throw new ModLoadException(path, $"formatVersion must be {CurrentFormatVersion}.");
        if (json.Id == null || !IsValidId(json.Id))
            throw new ModLoadException(path, IdRule);

        if ((json.DependsOn ?? []).Concat(json.LoadAfter ?? []).Any(string.IsNullOrWhiteSpace))
            throw new ModLoadException(path, "dependsOn and loadAfter must not contain empty entries.");

        return new ModManifest(
            json.Id,
            json.Name ?? json.Id,
            json.Version ?? "0.0.0",
            json.DependsOn ?? [],
            json.LoadAfter ?? []);
    }

    /// <summary>The rule for a mod id, as a message.</summary>
    public const string IdRule = "id must be lowercase letters, digits, '-' or '_', and start with a letter.";

    /// <summary>True when <paramref name="id"/> has only lowercase letters, digits, '-' and '_', and starts with a
    /// letter.</summary>
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex(@"^[a-z][a-z0-9_-]*\z")]
    private static partial Regex IdPattern();

    private sealed class ManifestJson
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public int FormatVersion { get; set; }
        public List<string>? DependsOn { get; set; }
        public List<string>? LoadAfter { get; set; }
    }
}

/// <summary>
/// A mod or profile file could not be loaded.
/// </summary>
public sealed class ModLoadException(string path, string message) : Exception($"{path}: {message}")
{
    public string Path { get; } = path;
}

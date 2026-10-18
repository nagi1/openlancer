using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Mods;

/// <summary>
/// The contents of a mod's <c>mod.json</c>.
/// </summary>
/// <remarks>
/// Format version 2 (milestone M25) adds <see cref="Tier"/> and <see cref="Targets"/>. A mod of format version 1 is a
/// tier-1 mod for all targets. A reader of version 1 ignores the fields that it does not know. Thus, without the new
/// version, an older reader would load a tier-2 mod as a data mod. With it, the older reader refuses the mod.
/// </remarks>
public sealed partial record ModManifest(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> LoadAfter)
{
    public const string FileName = "mod.json";

    /// <summary>The newest format version. The reader also reads version 1.</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>The tier of a data mod: INI changes, text and cutscenes, which the retail game and OpenLancer run.</summary>
    public const int DataTier = 1;

    /// <summary>The tier of a runtime mod, which only OpenLancer runs, for example a mod with scripts.</summary>
    public const int RuntimeTier = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary><see cref="DataTier"/> or <see cref="RuntimeTier"/>.</summary>
    public int Tier { get; init; } = DataTier;

    /// <summary>The games that the mod is for, in the order of the file (see <see cref="ModTargets"/>).</summary>
    public IReadOnlyList<string> Targets { get; init; } = ModTargets.All;

    /// <exception cref="ModLoadException">The file is missing or not valid.</exception>
    public static ModManifest Load(string modFolder)
    {
        var path = Path.Combine(modFolder, FileName);
        if (!File.Exists(path))
            throw new ModLoadException(path, "File not found.");

        ManifestJson? json;
        try
        {
            json = JsonSerializer.Deserialize<ManifestJson>(SharedFile.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new ModLoadException(path, e.Message);
        }
        if (json == null)
            throw new ModLoadException(path, "The file is empty.");
        if (json.FormatVersion is not (1 or CurrentFormatVersion))
            throw new ModLoadException(path, $"OpenLancer cannot read formatVersion {json.FormatVersion}. It reads " +
                                             $"formatVersion 1 and {CurrentFormatVersion}.");
        if (json.Id == null || !IsValidId(json.Id))
            throw new ModLoadException(path, IdRule);

        if ((json.DependsOn ?? []).Concat(json.LoadAfter ?? []).Any(string.IsNullOrWhiteSpace))
            throw new ModLoadException(path, "dependsOn and loadAfter must not contain empty entries.");

        var (tier, targets) = TierAndTargets(path, json);
        return new ModManifest(
            json.Id,
            json.Name ?? json.Id,
            json.Version ?? "0.0.0",
            json.DependsOn ?? [],
            json.LoadAfter ?? [])
        {
            Tier = tier,
            Targets = targets,
        };
    }

    /// <summary>The tier and the targets of a manifest. Version 1 has neither, so its mod is a data mod for each
    /// target. In version 2, a mod without a tier is a data mod, and a mod without targets is for each target of its
    /// tier.</summary>
    /// <exception cref="ModLoadException">The tier or the targets are not valid.</exception>
    private static (int Tier, IReadOnlyList<string> Targets) TierAndTargets(string path, ManifestJson json)
    {
        if (json.FormatVersion == 1)
        {
            if (json.Tier != null || json.Targets != null)
                throw new ModLoadException(path, "tier and targets need formatVersion 2. Set formatVersion to 2.");
            return (DataTier, ModTargets.All);
        }

        var tier = DataTier;
        if (json.Tier is { } element)
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out tier) ||
                tier is not (DataTier or RuntimeTier))
                throw new ModLoadException(path, $"OpenLancer cannot read tier {element.GetRawText()}. Use tier " +
                                                 $"{DataTier} for a data mod, or tier {RuntimeTier} for a mod that " +
                                                 "only OpenLancer runs.");
        }

        if (json.Targets == null)
            return (tier, ModTargets.Default(tier));
        if (json.Targets.Count == 0)
            throw new ModLoadException(path, "targets is empty. Name one target or more, or remove targets.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in json.Targets)
        {
            if (!ModTargets.All.Contains(target, StringComparer.Ordinal))
                throw new ModLoadException(path, $"'{target}' is not a target. Use {ModTargets.Describe()}.");
            if (!seen.Add(target))
                throw new ModLoadException(path, $"targets names '{target}' two times. Remove one of them.");
            if (tier == RuntimeTier && target != ModTargets.OpenLancer)
                throw new ModLoadException(path, $"A tier-{RuntimeTier} mod runs only on OpenLancer. Remove the " +
                                                 $"target '{target}', or use tier {DataTier}.");
        }
        return (tier, json.Targets);
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

        /// <summary>The raw value, so that a value of the wrong kind gets the message of an unknown tier.</summary>
        public JsonElement? Tier { get; set; }

        public List<string>? Targets { get; set; }
    }
}

/// <summary>
/// The games that a mod can target, in the order of roadmap decision P1: HD Edition 0.7.1 first, then vanilla 1.1
/// (with or without JFLP), then OpenLancer. M11 Export reads the targets of a mod.
/// </summary>
public static class ModTargets
{
    /// <summary>HD Edition 0.7.1, English.</summary>
    public const string HdEdition = "hde-0.7.1";

    /// <summary>The retail game with patch 1.1, with or without JFLP.</summary>
    public const string Vanilla = "vanilla-1.1";

    public const string OpenLancer = "openlancer";

    /// <summary>All targets, in the order of decision P1.</summary>
    public static IReadOnlyList<string> All { get; } = [HdEdition, Vanilla, OpenLancer];

    /// <summary>The targets of a mod without a <c>targets</c> field: each target that can run its tier.</summary>
    internal static IReadOnlyList<string> Default(int tier) =>
        tier == ModManifest.RuntimeTier ? [OpenLancer] : All;

    /// <summary>The targets for a message, for example "'hde-0.7.1', 'vanilla-1.1' or 'openlancer'".</summary>
    internal static string Describe() =>
        string.Join(", ", All.SkipLast(1).Select(t => $"'{t}'")) + $" or '{All[^1]}'";
}

/// <summary>
/// A mod or profile file could not be loaded.
/// </summary>
public sealed class ModLoadException(string path, string message) : Exception($"{path}: {message}")
{
    public string Path { get; } = path;

    /// <summary>The full path of the base folder of a profile when the folder does not exist, else null. The editor
    /// then offers a relink of the game.</summary>
    public string? MissingBaseFolder { get; init; }
}

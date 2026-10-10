using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLancer.Editor;

/// <summary>A profile in the list of recent profiles.</summary>
/// <param name="Path">The full path of the profile file.</param>
/// <param name="LastOpenedUtc">The time when the editor opened the profile for the last time.</param>
internal sealed record RecentProfile(string Path, DateTime LastOpenedUtc);

/// <summary>
/// Editor preferences that persist between sessions: the UI scale, the panel sizes, the view options, the Database
/// category, the open Inspector sections, the ImGui table state and the recent profiles.
/// </summary>
internal sealed class EditorSettings
{
    /// <summary>The maximum number of recent profiles.</summary>
    public const int MaxRecentProfiles = 10;

    // A null value for a property that cannot be null, or a recent profile without a path or a time, throws a
    // JsonException. So a damaged file gives the defaults, and no setting is null.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenLancer", "editor.json");

    // The default panel sizes, in unscaled points.
    public const float DefaultHierarchyWidth = 280;
    public const float DefaultInspectorWidth = 360;
    public const float DefaultBottomHeight = 260;
    // The category list fits its longest name, "Countermeasure", with its count and the scroll bar.
    public const float DefaultDatabaseCategoryWidth = 190;
    public const float DefaultConsoleDetailHeight = 80;

    /// <summary>The user's UI scale. The display DPI scale multiplies it.</summary>
    public float UiScale { get; set; } = 1;

    // The panel sizes are unscaled, so they stay the same at each UI scale.
    public float HierarchyWidth { get; set; } = DefaultHierarchyWidth;
    public float InspectorWidth { get; set; } = DefaultInspectorWidth;
    public float BottomHeight { get; set; } = DefaultBottomHeight;
    public float DatabaseCategoryWidth { get; set; } = DefaultDatabaseCategoryWidth;

    /// <summary>The id of the Database category that shows, for example "ships" or "equipment/Gun".</summary>
    public string DatabaseCategory { get; set; } = "systems";

    /// <summary>The height of the details pane below the Console list.</summary>
    public float ConsoleDetailHeight { get; set; } = DefaultConsoleDetailHeight;

    /// <summary>The recent profiles, the newest first.</summary>
    public List<RecentProfile> RecentProfiles { get; set; } = [];

    /// <summary>
    /// The active mod of each profile, by the full path of the profile file. An empty id means no active mod: the
    /// profile opens read-only. A profile that is not in the list opens with its last mod as the active mod.
    /// </summary>
    public Dictionary<string, string> ActiveMods { get; set; } = new(PathComparer);

    public bool ShowMarkers { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowZones { get; set; } = true;
    public bool ShowLights { get; set; } = true;
    public bool ShowNebulae { get; set; } = true;
    public bool ShowStarsphere { get; set; } = true;
    public bool Wireframe { get; set; }

    /// <summary>
    /// The open state of the Inspector sections that the user changed, by section key ("Type.Section"). A section that
    /// is not in the list has its default state.
    /// </summary>
    public Dictionary<string, bool> InspectorFoldouts { get; set; } = [];

    /// <summary>
    /// The ImGui state in the ImGui .ini text format: the column widths, order, visibility and sort of the tables. Null
    /// until ImGui saves it for the first time.
    /// </summary>
    public string? ImGuiState { get; set; }

    /// <summary>
    /// The only profile that an editor before the recent list kept. <see cref="Parse"/> moves it into
    /// <see cref="RecentProfiles"/>, so the settings never write it again.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("LastProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private string? LegacyLastProfile { get; set; }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Reads the settings. A missing or damaged file gives the defaults.</summary>
    public static EditorSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? Parse(File.ReadAllText(FilePath), File.GetLastWriteTimeUtc(FilePath))
                : new EditorSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new EditorSettings();
        }
    }

    /// <summary>
    /// Reads the settings from their JSON text. The last profile of an older editor becomes the first recent profile,
    /// so the editor still opens it at the start.
    /// </summary>
    /// <param name="savedUtc">The time when the file was saved. It is the time of a moved last profile.</param>
    /// <exception cref="JsonException">The text is not valid settings, for example a setting is null.</exception>
    internal static EditorSettings Parse(string json, DateTime savedUtc)
    {
        var settings = JsonSerializer.Deserialize<EditorSettings>(json, JsonOptions) ?? new EditorSettings();
        // The serializer does not check the items of a list for null.
        if (settings.RecentProfiles.Any(r => r is null || string.IsNullOrWhiteSpace(r.Path)))
        {
            throw new JsonException("The list of recent profiles has an entry without a path.");
        }

        if (settings.LegacyLastProfile is { Length: > 0 } last && settings.RecentProfiles.Count == 0)
        {
            settings.RecentProfiles.Add(new RecentProfile(last, savedUtc));
        }

        settings.LegacyLastProfile = null;
        // The serializer makes the dictionary without the comparer of the paths.
        settings.ActiveMods = new Dictionary<string, string>(settings.ActiveMods, PathComparer);
        return settings;
    }

    /// <summary>
    /// The id of the active mod of a profile: the stored id when the profile has that mod, else the last mod of the
    /// profile. Null means no active mod.
    /// </summary>
    /// <param name="modIds">The ids of the mods of the profile, in load order.</param>
    public string? ActiveModOf(string profilePath, IReadOnlyList<string> modIds)
    {
        if (ActiveMods.TryGetValue(profilePath, out var id) && (id.Length == 0 || modIds.Contains(id)))
        {
            return id.Length == 0 ? null : id;
        }

        return modIds.Count > 0 ? modIds[^1] : null;
    }

    /// <summary>Stores the active mod of a profile. Null stores no active mod.</summary>
    public void SetActiveMod(string profilePath, string? modId) => ActiveMods[profilePath] = modId ?? "";

    /// <summary>
    /// Puts a profile at the top of the recent list. The list keeps each path once and has at most
    /// <see cref="MaxRecentProfiles"/> entries.
    /// </summary>
    public void AddRecentProfile(string path, DateTime utcNow)
    {
        RemoveRecentProfile(path);
        RecentProfiles.Insert(0, new RecentProfile(path, utcNow));
        if (RecentProfiles.Count > MaxRecentProfiles)
        {
            RecentProfiles.RemoveRange(MaxRecentProfiles, RecentProfiles.Count - MaxRecentProfiles);
        }
    }

    public void RemoveRecentProfile(string path) => RecentProfiles.RemoveAll(r => PathComparer.Equals(r.Path, path));

    /// <summary>The serialized settings, as <see cref="Save"/> writes them.</summary>
    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Writes the settings. A failure only loses the preferences, so it does not stop the editor.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, ToJson());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

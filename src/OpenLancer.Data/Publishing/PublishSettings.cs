using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Publishing;

/// <summary>A row of the credits: who helped and what they did.</summary>
public sealed record PublishCredit(string Name, string For);

/// <summary>A requirement that the export does not add itself, for example another mod.</summary>
public sealed record PublishRequirement(string Name, string Url, string Note);

/// <summary>A screenshot for the Nexus page.</summary>
/// <param name="File">The path relative to the mod folder, with <c>/</c>, below <c>publish/screenshots/</c>.</param>
public sealed record PublishScreenshot(string File, string Caption);

/// <summary>The changes of one version.</summary>
public sealed record ChangelogEntry(string Version, IReadOnlyList<string> Changes)
{
    /// <summary>True when both entries have the same version and the same changes in the same order.</summary>
    public bool SameAs(ChangelogEntry other) => Version == other.Version && Changes.SequenceEqual(other.Changes);
}

/// <summary>A field of the publish page. The draft marks a changed field, and a check names the field of a
/// problem.</summary>
public enum PublishField
{
    DisplayName,
    Version,
    Targets,
    FormatVersion,
    Summary,
    Description,
    Authors,
    Credits,
    Permissions,
    Category,
    Tags,
    Requirements,
    PageUrl,
    Screenshots,
    Changelog,
}

/// <summary>A problem of a field of the publish page.</summary>
/// <param name="Index">The row of a list field, or -1.</param>
public sealed record FieldProblem(PublishField Field, DiagnosticSeverity Severity, string Message, int Index = -1);

/// <summary>The result of a read of <c>publish.json</c>.</summary>
/// <param name="Problems">The problems of the file. A problem never stops the load of the mod.</param>
/// <param name="ReadOnly">The reason why OpenLancer cannot write the file, or null.</param>
/// <param name="Hash">The SHA-256 of the bytes of the file, or null when the file does not exist.</param>
public sealed record PublishLoad(PublishSettings Settings, IReadOnlyList<string> Problems, string? ReadOnly, string? Hash);

/// <summary>
/// The publish settings of a mod (decision D560 and section 8.1 of the M11c design): the optional file
/// <c>publish.json</c> next to <c>mod.json</c>. The display name and the version stay in <c>mod.json</c>
/// (decision D561).
/// </summary>
/// <remarks>
/// The merge never reads <c>publish.json</c>, so a bad publish field never stops the load of a profile. The writer keeps
/// the fields that it does not know, the byte order mark and the line ends (see <see cref="JsonFile"/>).
/// </remarks>
public sealed record PublishSettings
{
    public const string FileName = "publish.json";

    /// <summary>The folder of the screenshots, relative to the mod folder, with <c>/</c>.</summary>
    public const string ScreenshotsFolder = "publish/screenshots";

    public const int CurrentFormatVersion = 1;

    /// <summary>The Nexus category of the Freelancer mods on Nexus.</summary>
    public const string DefaultCategory = "Miscellaneous";

    public static PublishSettings Empty { get; } = new();

    public string Summary { get; init; } = "";

    /// <summary>The text in the format of <see cref="PageText"/>.</summary>
    public string Description { get; init; } = "";

    public IReadOnlyList<string> Authors { get; init; } = [];

    public IReadOnlyList<PublishCredit> Credits { get; init; } = [];

    public string Permissions { get; init; } = "";

    public string Category { get; init; } = DefaultCategory;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<PublishRequirement> Requirements { get; init; } = [];

    /// <summary>The link of the mod page, or empty. FLMM and the README use it.</summary>
    public string PageUrl { get; init; } = "";

    public IReadOnlyList<PublishScreenshot> Screenshots { get; init; } = [];

    /// <summary>One entry for each version, newest first.</summary>
    public IReadOnlyList<ChangelogEntry> Changelog { get; init; } = [];

    /// <summary>The changes of a version, or an empty list.</summary>
    public IReadOnlyList<string> ChangesOf(string version) =>
        Changelog.FirstOrDefault(e => ModVersion.TextComparer.Equals(e.Version, version))?.Changes ?? [];

    /// <summary>Reads <c>publish.json</c> of a mod folder. A missing file gives empty settings. The read never
    /// throws.</summary>
    public static PublishLoad Load(string modFolder)
    {
        var path = Path.Combine(modFolder, FileName);
        if (!File.Exists(path))
            return new PublishLoad(Empty, [], null, null);
        byte[] bytes;
        try
        {
            bytes = SharedFile.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var message = $"{FileName}: {e.Message}";
            return new PublishLoad(Empty, [message], message, null);
        }
        var hash = ContentHash.Of(bytes);
        JsonFile file;
        try
        {
            file = JsonFile.Parse(bytes);
        }
        catch (InvalidDataException e)
        {
            var message = $"{FileName}: {e.Message}";
            return new PublishLoad(Empty, [message], message, hash);
        }

        var problems = new List<string>();
        var root = file.Root;
        // A file without the field is version 1.
        var version = root["formatVersion"] switch
        {
            null => CurrentFormatVersion,
            JsonValue v when v.TryGetValue<int>(out var number) => number,
            _ => 0,
        };
        if (version != CurrentFormatVersion)
        {
            var message = PublishMessages.PublishJsonVersion(version);
            return new PublishLoad(Read(root, problems), [message], message, hash);
        }
        return new PublishLoad(Read(root, problems), problems, null, hash);
    }

    /// <summary>
    /// Writes the settings into <c>publish.json</c> of a mod folder. The file keeps its unknown fields, its byte order
    /// mark and its line ends.
    /// </summary>
    /// <exception cref="InvalidDataException">The file has a comment or is not a JSON object.</exception>
    /// <exception cref="IOException">The write failed.</exception>
    public static void Write(string modFolder, PublishSettings settings)
    {
        var path = Path.Combine(modFolder, FileName);
        var file = File.Exists(path) ? JsonFile.Read(path) : JsonFile.Create();
        var root = file.Root;
        root["formatVersion"] = CurrentFormatVersion;
        root["summary"] = settings.Summary;
        root["description"] = settings.Description;
        root["authors"] = Strings(settings.Authors);
        root["credits"] = new JsonArray([.. settings.Credits.Select(c => new JsonObject { ["name"] = c.Name, ["for"] = c.For })]);
        root["permissions"] = settings.Permissions;
        root["category"] = settings.Category;
        root["tags"] = Strings(settings.Tags);
        root["requirements"] = new JsonArray([.. settings.Requirements.Select(r =>
            new JsonObject { ["name"] = r.Name, ["url"] = r.Url, ["note"] = r.Note })]);
        root["pageUrl"] = settings.PageUrl;
        root["screenshots"] = new JsonArray([.. settings.Screenshots.Select(s =>
            new JsonObject { ["file"] = s.File, ["caption"] = s.Caption })]);
        root["changelog"] = new JsonArray([.. settings.Changelog.Select(e =>
            new JsonObject { ["version"] = e.Version, ["changes"] = Strings(e.Changes) })]);
        AtomicFile.WriteAllBytes(path, file.ToBytes());
    }

    /// <summary>The problems of the fields of the settings, by the rules of section 8.1 of the M11c design.</summary>
    public IReadOnlyList<FieldProblem> Check()
    {
        var problems = new List<FieldProblem>();
        if (Summary.Length > PublishLimits.SummaryLength)
            problems.Add(new FieldProblem(PublishField.Summary, DiagnosticSeverity.Warning,
                PublishMessages.SummaryLong(Summary.Length)));
        var bytes = Encoding.UTF8.GetByteCount(Description);
        if (bytes > PublishLimits.DescriptionBytes)
            problems.Add(new FieldProblem(PublishField.Description, DiagnosticSeverity.Error,
                $"The description has {bytes} bytes. Use {PublishLimits.DescriptionBytes} bytes or fewer."));
        for (var i = 0; i < Authors.Count; i++)
        {
            if (Authors[i].Trim().Length == 0)
                problems.Add(Error(PublishField.Authors, "An author name cannot be empty. Remove the empty row.", i));
        }
        for (var i = 0; i < Credits.Count; i++)
        {
            if (Credits[i].Name.Trim().Length == 0)
                problems.Add(Error(PublishField.Credits, "A credit needs a name. Type the name, or remove the row.", i));
        }
        for (var i = 0; i < Tags.Count; i++)
        {
            if (Tags[i].Trim().Length == 0)
                problems.Add(Error(PublishField.Tags, "A tag cannot be empty. Remove the empty row.", i));
            else if (Tags[i].Contains(','))
                problems.Add(Error(PublishField.Tags, PublishMessages.TagWithComma, i));
        }
        for (var i = 0; i < Requirements.Count; i++)
        {
            if (Requirements[i].Name.Trim().Length == 0)
                problems.Add(Error(PublishField.Requirements, "A requirement needs a name. Type the name, or remove the row.", i));
            if (Requirements[i].Url.Length > 0 && !IsHttps(Requirements[i].Url))
                problems.Add(Error(PublishField.Requirements, PublishMessages.PageLink, i));
        }
        if (PageUrl.Length > 0 && !IsHttps(PageUrl))
            problems.Add(Error(PublishField.PageUrl, PublishMessages.PageLink));
        for (var i = 0; i < Screenshots.Count; i++)
        {
            if (ScreenshotRules.PathProblem(Screenshots[i].File) is { } path)
                problems.Add(Error(PublishField.Screenshots, path, i));
        }
        var versions = new HashSet<string>(ModVersion.TextComparer);
        for (var i = 0; i < Changelog.Count; i++)
        {
            var version = Changelog[i].Version;
            if (ModVersion.Problem(version) is { } problem)
                problems.Add(new FieldProblem(PublishField.Changelog, problem.Severity, problem.Message, i));
            if (!versions.Add(version))
                problems.Add(Error(PublishField.Changelog,
                    $"Version {version} has two changelog entries. Remove one of them.", i));
        }
        return problems;
    }

    /// <summary>True when the link starts with <c>https://</c>.</summary>
    public static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && url.Length > 8;

    /// <summary>The settings with each list and text compared field by field.</summary>
    public bool SameField(PublishSettings other, PublishField field) => field switch
    {
        PublishField.Summary => Summary == other.Summary,
        PublishField.Description => Description == other.Description,
        PublishField.Authors => Authors.SequenceEqual(other.Authors),
        PublishField.Credits => Credits.SequenceEqual(other.Credits),
        PublishField.Permissions => Permissions == other.Permissions,
        PublishField.Category => Category == other.Category,
        PublishField.Tags => Tags.SequenceEqual(other.Tags),
        PublishField.Requirements => Requirements.SequenceEqual(other.Requirements),
        PublishField.PageUrl => PageUrl == other.PageUrl,
        PublishField.Screenshots => Screenshots.SequenceEqual(other.Screenshots),
        PublishField.Changelog => Changelog.Count == other.Changelog.Count &&
                                  Changelog.Zip(other.Changelog).All(p => p.First.SameAs(p.Second)),
        _ => true,
    };

    private static FieldProblem Error(PublishField field, string message, int index = -1) =>
        new(field, DiagnosticSeverity.Error, message, index);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)v)]);

    /// <summary>The fields of a parsed file. A field of the wrong kind is a problem, and the field stays empty.</summary>
    private static PublishSettings Read(JsonObject root, List<string> problems) => new()
    {
        Summary = Text(root, "summary", false, problems),
        Description = Text(root, "description", true, problems),
        Authors = TextList(root, "authors", problems),
        Credits = Objects(root, "credits", problems, o => new PublishCredit(Field(o, "name"), Field(o, "for"))),
        Permissions = Text(root, "permissions", true, problems),
        Category = root["category"] == null ? DefaultCategory : Text(root, "category", false, problems),
        Tags = TextList(root, "tags", problems),
        Requirements = Objects(root, "requirements", problems,
            o => new PublishRequirement(Field(o, "name"), Field(o, "url"), Field(o, "note"))),
        PageUrl = Text(root, "pageUrl", false, problems),
        Screenshots = Objects(root, "screenshots", problems,
            o => new PublishScreenshot(Field(o, "file"), Field(o, "caption"))),
        Changelog = Objects(root, "changelog", problems, o =>
        {
            var changes = o["changes"] is JsonArray array
                ? array.Select(n => Clean(n is JsonValue value && value.TryGetValue<string>(out var s) ? s : "", true))
                    .ToList()
                : [];
            return new ChangelogEntry(Field(o, "version"), changes);
        }),
    };

    private static string Text(JsonObject root, string name, bool multiLine, List<string> problems)
    {
        var node = root[name];
        if (node == null)
            return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return Clean(text, multiLine);
        problems.Add($"{FileName}: '{name}' must be a text. OpenLancer ignores the value.");
        return "";
    }

    private static IReadOnlyList<string> TextList(JsonObject root, string name, List<string> problems)
    {
        var node = root[name];
        if (node == null)
            return [];
        if (node is JsonArray array && array.All(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String))
            return [.. array.Select(n => Clean(n!.GetValue<string>(), false))];
        problems.Add($"{FileName}: '{name}' must be a list of texts. OpenLancer ignores the value.");
        return [];
    }

    private static IReadOnlyList<T> Objects<T>(JsonObject root, string name, List<string> problems,
        Func<JsonObject, T> read)
    {
        var node = root[name];
        if (node == null)
            return [];
        if (node is JsonArray array && array.All(n => n is JsonObject))
            return [.. array.Select(n => read((JsonObject)n!))];
        problems.Add($"{FileName}: '{name}' must be a list of objects. OpenLancer ignores the value.");
        return [];
    }

    /// <summary>A text field of an object of a list, or empty when the field is not a text.</summary>
    private static string Field(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? Clean(text, false) : "";

    /// <summary>The text without control characters. A multi-line field keeps its line ends as <c>\n</c>.</summary>
    private static string Clean(string text, bool multiLine) => PageText.RemoveControl(text, keepLineEnds: multiLine);
}

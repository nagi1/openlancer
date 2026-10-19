using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>The fields of a draft: the fields of <c>mod.json</c> that the publish page edits, and the publish
/// settings.</summary>
public sealed record DraftValues(string Name, string Version, IReadOnlyList<string> Targets, int FormatVersion,
    PublishSettings Settings)
{
    /// <summary>True when the field has the same value in both.</summary>
    public bool SameField(DraftValues other, PublishField field) => field switch
    {
        PublishField.DisplayName => Name == other.Name,
        PublishField.Version => Version == other.Version,
        PublishField.Targets => Targets.SequenceEqual(other.Targets),
        PublishField.FormatVersion => FormatVersion == other.FormatVersion,
        _ => Settings.SameField(other.Settings, field),
    };
}

/// <summary>What Apply did.</summary>
public enum ApplyState
{
    /// <summary>Apply wrote the files.</summary>
    Applied,

    /// <summary>Apply wrote nothing. <see cref="ApplyResult.Message"/> gives the reason.</summary>
    Refused,

    /// <summary>A file changed on disk after the draft read it. Apply wrote nothing. Ask "Load again, Replace or
    /// Cancel".</summary>
    ChangedOnDisk,
}

/// <summary>The result of Apply.</summary>
/// <param name="Message">The reason of a refusal, or the question of a file that changed on disk.</param>
/// <param name="ReopenProfile">True when the targets or the format version changed. The save gate reads the loaded
/// manifest, so the editor opens a profile with the mod again.</param>
/// <param name="UnusedImages">The files in <c>publish\screenshots</c> that no entry uses. Ask before a
/// delete.</param>
public sealed record ApplyResult(ApplyState State, string? Message, bool ReopenProfile, IReadOnlyList<string> UnusedImages)
{
    public static ApplyResult Refused(string message) => new(ApplyState.Refused, message, false, []);
}

/// <summary>
/// The draft of the publish page of a mod (decision D571 and section 10.7 of the M11c design). It works as the import
/// settings of Unity. The page edits a copy of the fields, and a changed field shows the blue mark. Apply writes the
/// files, and Revert drops the draft.
/// </summary>
/// <remarks>
/// <para>Apply runs the write guard and the repo rule first (decisions D587 and D584). Then it checks that
/// <c>mod.json</c> and <c>publish.json</c> did not change on disk after the draft read them. Then it copies the new
/// screenshots, and only after that it writes <c>publish.json</c> and <c>mod.json</c>. A failed copy changes no JSON
/// file.</para>
/// <para>A <c>mod.json</c> with a comment loads, but OpenLancer cannot write it (finding R6). Its fields are then
/// read-only. A <c>publish.json</c> with a comment or of another format version is read-only too.</para>
/// </remarks>
public sealed class PublishDraft
{
    private readonly Dictionary<string, string> imports = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModManifest manifest;
    private DraftValues applied;
    private string? manifestHash;
    private string? settingsHash;

    private PublishDraft(string folder, ModManifest manifest, DraftValues values, PublishLoad settings, string? manifestHash,
        string? manifestReadOnly)
    {
        ModFolder = folder;
        this.manifest = manifest;
        ModId = manifest.Id;
        Tier = manifest.Tier;
        applied = values;
        Values = values;
        this.manifestHash = manifestHash;
        settingsHash = settings.Hash;
        ManifestReadOnly = manifestReadOnly;
        SettingsReadOnly = settings.ReadOnly;
        LoadProblems = settings.Problems;
    }

    public string ModFolder { get; }

    public string ModId { get; }

    public int Tier { get; }

    /// <summary>The values of the draft. The page changes them with <c>with</c>.</summary>
    public DraftValues Values { get; set; }

    /// <summary>The values that the files hold.</summary>
    public DraftValues Applied => applied;

    /// <summary>The reason why the fields of <c>mod.json</c> are read-only, or null.</summary>
    public string? ManifestReadOnly { get; }

    /// <summary>The reason why the publish settings are read-only, or null.</summary>
    public string? SettingsReadOnly { get; }

    /// <summary>The problems of the read of <c>publish.json</c>.</summary>
    public IReadOnlyList<string> LoadProblems { get; }

    /// <summary>The screenshots that Apply copies: the path relative to the mod folder, to the source file. Only the
    /// imports of the rows of the list count, so a removed row copies nothing.</summary>
    public IReadOnlyDictionary<string, string> Imports => PendingImports().ToDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a field or a screenshot import differs from the files.</summary>
    public bool HasChanges => PendingImports().Count > 0 || Enum.GetValues<PublishField>().Any(IsChanged);

    /// <summary>True when the field differs from the files. The page shows the blue mark of an unsaved edit.</summary>
    public bool IsChanged(PublishField field) => !Values.SameField(applied, field);

    /// <summary>Reads <c>mod.json</c> and <c>publish.json</c> of a mod folder.</summary>
    /// <exception cref="ModLoadException">mod.json does not load.</exception>
    public static PublishDraft Open(string modFolder)
    {
        modFolder = Path.GetFullPath(modFolder);
        var manifest = ModManifest.Load(modFolder);
        var path = Path.Combine(modFolder, ModManifest.FileName);
        var bytes = SharedFile.ReadAllBytes(path);
        string? readOnly = null;
        try
        {
            JsonFile.Parse(bytes);
        }
        catch (InvalidDataException)
        {
            readOnly = PublishMessages.ModJsonComment;
        }
        var settings = PublishSettings.Load(modFolder);
        var values = new DraftValues(manifest.Name, manifest.Version, manifest.Targets, manifest.FormatVersion, settings.Settings);
        return new PublishDraft(modFolder, manifest, values, settings, ContentHash.Of(bytes), readOnly);
    }

    /// <summary>Drops the changes of the draft.</summary>
    public void Revert()
    {
        Values = applied;
        imports.Clear();
    }

    /// <summary>
    /// Increases the version of the draft (section 8.3 of the M11c design). The increase skips the versions of the
    /// history, and it adds a changelog entry for the new version when none exists.
    /// </summary>
    /// <returns>The new version, or null when the version does not follow the rule.</returns>
    public string? IncreaseVersion(VersionPart part, IEnumerable<string> historyVersions)
    {
        var next = ModVersion.Next(Values.Version, part, historyVersions);
        if (next != null)
            SetVersion(next);
        return next;
    }

    /// <summary>Sets the version of the draft, and adds a changelog entry for it when none exists.</summary>
    public void SetVersion(string version)
    {
        var settings = Values.Settings;
        if (!settings.Changelog.Any(e => ModVersion.TextComparer.Equals(e.Version, version)))
            settings = settings with { Changelog = [new ChangelogEntry(version, [""]), .. settings.Changelog] };
        Values = Values with { Version = version, Settings = settings };
    }

    /// <summary>Removes a target from the draft (the Fix "Remove the target" of the checklist).</summary>
    public void RemoveTarget(string target) =>
        Values = Values with { Targets = [.. Values.Targets.Where(t => t != target)] };

    /// <summary>The Fix "Use format version 2" of a mod of format version 1.</summary>
    public void UseFormatVersion2() => Values = Values with { FormatVersion = ModManifest.CurrentFormatVersion };

    /// <summary>Puts the link of "Mark as published" into the draft as an unapplied change (finding R14). The user
    /// then clicks Apply.</summary>
    /// <returns>True when the link differs from the link of the draft.</returns>
    public bool TakePageLink(string link)
    {
        if (link.Length == 0 || Values.Settings.PageUrl == link)
            return false;
        Values = Values with { Settings = Values.Settings with { PageUrl = link } };
        return true;
    }

    /// <summary>
    /// Adds images to the screenshot list of the draft. Each image passes the screenshot rule (decision D586). Apply
    /// copies each image to <c>publish\screenshots\&lt;safe name&gt;</c>. Apply does not copy an image that is already
    /// in that folder.
    /// </summary>
    /// <returns>The reason for each refused file. A repo mod refuses each file (decision D584).</returns>
    public IReadOnlyList<string> AddImages(IEnumerable<string> sources, IReadOnlyCollection<string> gameRoots) =>
        AddImages(sources, gameRoots, null);

    internal IReadOnlyList<string> AddImages(IEnumerable<string> sources, IReadOnlyCollection<string> gameRoots,
        Games.KnownBuilds? builds)
    {
        if (RepoCheck.RepoRoot(ModFolder) != null)
            return [PublishMessages.ImageInRepoMod];
        var refused = new List<string>();
        var screenshots = Values.Settings.Screenshots.ToList();
        var folder = ScreenshotsPath;
        foreach (var source in sources.Select(Path.GetFullPath))
        {
            if (ScreenshotRules.CheckImport(source, gameRoots, builds) is { } reason)
            {
                refused.Add(reason);
                continue;
            }
            // A file that the list already imports gets no second row.
            if (imports.Any(i => Games.GameFolders.Same(i.Value, source) &&
                                 screenshots.Any(s => s.File.Equals(i.Key, StringComparison.OrdinalIgnoreCase))))
                continue;
            string relative;
            if (PathRules.IsInside(source, folder) && Path.GetDirectoryName(source) is { } parent &&
                Games.GameFolders.Same(parent, folder))
                relative = $"{PublishSettings.ScreenshotsFolder}/{Path.GetFileName(source)}";
            else
            {
                relative = $"{PublishSettings.ScreenshotsFolder}/{SafeName(source, folder, screenshots)}";
                imports[relative] = source;
            }
            if (!screenshots.Any(s => s.File.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                screenshots.Add(new PublishScreenshot(relative, ""));
        }
        Values = Values with { Settings = Values.Settings with { Screenshots = screenshots } };
        return refused;
    }

    /// <summary>The error of a display name, or null: the name is empty, has more than one line, or is too long.</summary>
    public static string? DisplayNameProblem(string name)
    {
        if (name.Trim().Length == 0)
            return PublishMessages.DisplayNameEmpty;
        if (name.Contains('\n') || name.Contains('\r'))
            return PublishMessages.DisplayNameOneLine;
        if (name.Length > PublishLimits.NameLength)
            return $"The display name has {name.Length} characters. Use {PublishLimits.NameLength} characters or fewer.";
        return null;
    }

    /// <summary>The problems of the fields of the draft. An error stops Apply. A character that FLMM cannot show gives a
    /// warning on its field (decision D580).</summary>
    public IReadOnlyList<FieldProblem> Problems()
    {
        var problems = new List<FieldProblem>();
        if (DisplayNameProblem(Values.Name) is { } name)
            problems.Add(new FieldProblem(PublishField.DisplayName, DiagnosticSeverity.Error, name));
        if (ModVersion.Problem(Values.Version) is { } version)
            problems.Add(new FieldProblem(PublishField.Version, version.Severity, version.Message));
        if (Values.Targets.Count == 0)
            problems.Add(new FieldProblem(PublishField.Targets, DiagnosticSeverity.Error,
                "Pick one target game or more. The export makes the packages of these games."));
        else if (Values.FormatVersion == 1 && IsChanged(PublishField.Targets))
            problems.Add(new FieldProblem(PublishField.Targets, DiagnosticSeverity.Error, PublishMessages.FormatVersion2Fix));
        else if (Tier == ModManifest.RuntimeTier && Values.Targets.Any(t => t != ModTargets.OpenLancer))
            problems.Add(new FieldProblem(PublishField.Targets, DiagnosticSeverity.Error,
                $"A tier-{ModManifest.RuntimeTier} mod runs only on OpenLancer. Pick only the target OpenLancer."));
        problems.AddRange(Values.Settings.Check());
        if (Values.Targets.Any(t => Export.ExportTarget.Find(t) is { IsRetail: true }))
            problems.AddRange(new Export.PublishTexts(manifest with { Name = Values.Name }, Values.Settings).FlmmLost()
                .Select(l => new FieldProblem(l.Field, DiagnosticSeverity.Warning, l.Message)));
        return problems;
    }

    /// <summary>
    /// Writes the draft: first the new screenshots, then <c>publish.json</c>, then <c>mod.json</c>. Each write uses
    /// <see cref="AtomicFile"/>, and the JSON files keep their unknown fields.
    /// </summary>
    /// <param name="sessionHolds">True when the edit session holds the lock of the mod.</param>
    /// <param name="replaceChangedFiles">True after the answer Replace to the question of a file that changed on
    /// disk.</param>
    public ApplyResult Apply(WriteContext context, bool sessionHolds, bool replaceChangedFiles = false) =>
        Apply(context, sessionHolds, replaceChangedFiles, null, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    /// <param name="copy">Copies an image, or null for <see cref="File.Copy(string, string)"/>. The tests use it for a
    /// failed copy.</param>
    internal ApplyResult Apply(WriteContext context, bool sessionHolds, bool replaceChangedFiles,
        Games.KnownBuilds? builds, Action<string, string>? copy)
    {
        if (ModWrite.Guard(ModFolder, context) is { } guard)
            return ApplyResult.Refused(guard);
        var manifestChanged = new[] { PublishField.DisplayName, PublishField.Version, PublishField.Targets, PublishField.FormatVersion }
            .Any(IsChanged);
        var pending = PendingImports();
        var settingsChanged = pending.Count > 0 || Enum.GetValues<PublishField>().Where(f => f >= PublishField.Summary).Any(IsChanged);
        if (manifestChanged && ManifestReadOnly != null)
            return ApplyResult.Refused(ManifestReadOnly);
        if (settingsChanged && SettingsReadOnly != null)
            return ApplyResult.Refused(SettingsReadOnly);
        if (Problems().FirstOrDefault(p => p.Severity == DiagnosticSeverity.Error) is { } error)
            return ApplyResult.Refused(error.Message);
        foreach (var screenshot in Values.Settings.Screenshots)
        {
            var reason = imports.TryGetValue(screenshot.File, out var source)
                ? ScreenshotRules.CheckImport(source, context.GameRoots, builds)
                : ScreenshotRules.Check(ModFolder, screenshot.File, context.GameRoots, builds);
            if (reason != null)
                return ApplyResult.Refused(reason);
        }
        if (pending.Count > 0 && ScreenshotsFolderProblem() is { } outside)
            return ApplyResult.Refused(outside);
        if (!replaceChangedFiles && ChangedFile() is { } changed)
            return new ApplyResult(ApplyState.ChangedOnDisk, PublishMessages.FileChanged(changed), false, []);

        string? failure = null;
        var refusal = ModWrite.Run(ModFolder, ModId, context, sessionHolds,
            () => failure = Write(pending, manifestChanged, settingsChanged, copy));
        if (refusal != null || failure != null)
            return ApplyResult.Refused(refusal ?? failure!);
        var reopen = IsChanged(PublishField.Targets) || IsChanged(PublishField.FormatVersion);
        applied = Values;
        imports.Clear();
        manifestHash = ContentHash.OfFile(Path.Combine(ModFolder, ModManifest.FileName));
        settingsHash = ContentHash.OfFile(Path.Combine(ModFolder, PublishSettings.FileName));
        return new ApplyResult(ApplyState.Applied, null, reopen, UnusedImages());
    }

    /// <summary>
    /// The PNG and JPEG files directly in <c>publish\screenshots</c> that no entry of the applied list uses. Apply
    /// copies images only into that folder, so other files and subfolders are not in the list. A folder that links out
    /// of the mod gives no file.
    /// </summary>
    public IReadOnlyList<string> UnusedImages()
    {
        var folder = ScreenshotsPath;
        if (!Directory.Exists(folder) || ScreenshotsFolderProblem() != null)
            return [];
        var used = applied.Settings.Screenshots
            .Select(s => Path.GetFullPath(Path.Combine(ModFolder, s.File.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. Directory.EnumerateFiles(folder)
                .Where(f => ScreenshotRules.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !used.Contains(f))
                .Order(),
        ];
    }

    /// <summary>Deletes unused images after the answer Delete. It deletes only the files that
    /// <see cref="UnusedImages"/> gives at the time of the delete. A failed delete gives a warning and stops
    /// nothing.</summary>
    /// <returns>A warning for each file that OpenLancer did not delete.</returns>
    public IReadOnlyList<string> DeleteImages(IReadOnlyList<string> files, WriteContext context, bool sessionHolds)
    {
        var warnings = new List<string>();
        var refusal = ModWrite.Run(ModFolder, ModId, context, sessionHolds, () =>
        {
            var unused = UnusedImages().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Select(Path.GetFullPath).Where(unused.Contains))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"OpenLancer did not delete {file}: {e.Message}");
                }
            }
        });
        if (refusal != null)
            warnings.Add(refusal);
        return warnings;
    }

    /// <summary>The name of the file that changed on disk after the draft read it, or null.</summary>
    private string? ChangedFile()
    {
        if (ContentHash.OfFile(Path.Combine(ModFolder, ModManifest.FileName)) != manifestHash)
            return ModManifest.FileName;
        if (ContentHash.OfFile(Path.Combine(ModFolder, PublishSettings.FileName)) != settingsHash)
            return PublishSettings.FileName;
        return null;
    }

    /// <summary>The folder <c>publish\screenshots</c> of the mod.</summary>
    private string ScreenshotsPath =>
        Path.Combine(ModFolder, PublishSettings.ScreenshotsFolder.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The imports of the rows of the list. An import of a removed row does not count.</summary>
    private List<KeyValuePair<string, string>> PendingImports() =>
        [.. imports.Where(i => Values.Settings.Screenshots.Any(s => s.File.Equals(i.Key, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>The reason why the window cannot write into <c>publish\screenshots</c>, or null. A link in the path
    /// can point out of the mod folder, also into a game folder (decision D587).</summary>
    private string? ScreenshotsFolderProblem()
    {
        try
        {
            return PathRules.IsInside(PathRules.RealPath(ScreenshotsPath), PathRules.RealPath(ModFolder))
                ? null
                : PublishMessages.ScreenshotsOutsideMod;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>Copies the imports, then writes the JSON files.</summary>
    /// <returns>The reason of a failure, or null.</returns>
    private string? Write(IReadOnlyList<KeyValuePair<string, string>> pending, bool manifestChanged, bool settingsChanged,
        Action<string, string>? copy)
    {
        var copied = new List<string>();
        try
        {
            foreach (var (relative, source) in pending)
            {
                var target = Path.Combine(ModFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                (copy ?? File.Copy)(source, target);
                copied.Add(target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The copies are new files, so a delete restores the folder.
            foreach (var file in copied)
                File.Delete(file);
            return $"OpenLancer did not copy the images: {e.Message} No file of the mod changed.";
        }

        try
        {
            if (settingsChanged)
                PublishSettings.Write(ModFolder, Values.Settings);
            if (manifestChanged)
                WriteManifest();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"OpenLancer did not write the publish settings: {e.Message}";
        }
    }

    /// <summary>Writes the changed fields into <c>mod.json</c>. The file keeps its other fields. A first change of the
    /// targets writes an explicit <c>targets</c> list.</summary>
    private void WriteManifest()
    {
        var path = Path.Combine(ModFolder, ModManifest.FileName);
        var file = JsonFile.Read(path);
        if (IsChanged(PublishField.DisplayName))
            file.Root["name"] = Values.Name.Trim();
        if (IsChanged(PublishField.Version))
            file.Root["version"] = Values.Version;
        if (IsChanged(PublishField.FormatVersion))
            file.Root["formatVersion"] = Values.FormatVersion;
        if (IsChanged(PublishField.Targets))
            file.Root["targets"] = new JsonArray([.. Values.Targets.Select(t => (JsonNode?)t)]);
        AtomicFile.WriteAllBytes(path, file.ToBytes());
    }

    /// <summary>
    /// A file name for an imported image: lowercase ASCII letters, digits and <c>-</c>, the extension in lowercase, and
    /// a number when the name exists in the folder or in the list.
    /// </summary>
    private static string SafeName(string source, string folder, IReadOnlyList<PublishScreenshot> listed)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var stem = new StringBuilder();
        foreach (var c in Path.GetFileNameWithoutExtension(source).ToLowerInvariant())
        {
            var part = c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-';
            if (part != '-' || stem.Length > 0 && stem[^1] != '-')
                stem.Append(part);
        }
        var name = stem.ToString().Trim('-');
        if (name.Length == 0)
            name = "image";
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? name + extension : $"{name}-{i}{extension}";
            if (!File.Exists(Path.Combine(folder, candidate)) &&
                !listed.Any(s => Path.GetFileName(s.File).Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }
}

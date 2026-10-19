using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

/// <summary>What the text of a recovered file is.</summary>
internal enum RecoveredKind
{
    /// <summary>A patch of an INI file or a THN patch of a cutscene.</summary>
    Patch,

    /// <summary>The whole file of a cutscene, or a whole INI file of the kind "whole file", that the active mod
    /// has.</summary>
    WholeFile,

    /// <summary>An editor file of the active mod (see <see cref="ModFileDocument"/>). Its path is relative to the mod
    /// folder. Format 3 adds it.</summary>
    ModFile,
}

/// <summary>The file of the active mod that unsaved edits give for one install path or one editor file.</summary>
/// <param name="Path">The install path, in <see cref="VfsPath.Normalize"/> form. For an editor file, the path relative
/// to the mod folder, in the form of <see cref="ModFileDocument.Normalize"/>.</param>
/// <param name="KnownHash">The hash of the file of the mod that the edits change (see <see cref="ContentHash"/>), or
/// null when the mod had no such file.</param>
/// <param name="Text">The text of the file, or null for no patch. A whole file without text is the delete of a new
/// cutscene: an undo of New cutscene after its save (section 5.4 of the M8b design). An editor file without text is
/// the delete of the file.</param>
internal sealed record RecoveredFile(string Path, RecoveredKind Kind, string? KnownHash, string? Text);

/// <summary>The text of the mod with unsaved edits.</summary>
/// <param name="KnownHash">The hash of the strings.json file that the edits change, or null when there was none.</param>
internal sealed record RecoveredText(string? KnownHash, ModText Text);

/// <summary>
/// The unsaved edits of an editor session, in <c>.olsave\recovery.json</c> of the active mod. The editor keeps this
/// file while it has unsaved edits, and offers to restore them at the next open (decision D8). A close of the window
/// asks to save the edits first (roadmap decision D78), so the file is for a crash or a kill of the editor. Don't Save
/// at the close deletes the file.
/// </summary>
/// <remarks>
/// <para>The file holds the text that a save would write for each changed file, and the text of the mod. Each part
/// also holds the hash of the file on disk that the edits change. When a file of the mod changes after the editor
/// wrote the recovery file, the edits do not apply to it any more.</para>
/// <para>Format 2 adds the cutscenes: the THN patch or the whole file of each, with its kind. Format 1 holds only
/// INI patches. Format 3 adds the editor files of the mod with the kind <c>modfile</c>, and the whole INI files of the
/// mission files with the kind <c>file</c> (section 6.8 of the M6b design). The editor reads formats 1, 2 and 3. It
/// writes format 3 when the file holds one of these entries, else format 2, so that an earlier editor can restore
/// it.</para>
/// </remarks>
internal sealed record RecoveryFile(IReadOnlyList<RecoveredFile> Files, RecoveredText? Text)
{
    public const string FileName = "recovery.json";
    private const int FormatVersion = 3;
    private const int CutscenesVersion = 2;
    private const int PatchesOnlyVersion = 1;
    private const string PatchKind = "patch";
    private const string WholeFileKind = "file";
    private const string ModFileKind = "modfile";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string PathOf(string modFolder) => System.IO.Path.Combine(modFolder, SaveTransaction.FolderName, FileName);

    /// <exception cref="InvalidDataException">The file is not a valid recovery file.</exception>
    public static RecoveryFile Read(string path)
    {
        FileJson? json;
        try
        {
            json = JsonSerializer.Deserialize<FileJson>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
        var entries = json?.FormatVersion switch
        {
            PatchesOnlyVersion when json.Files == null && (json.Patches ?? []).All(p => p.Kind == null) => json.Patches ?? [],
            CutscenesVersion when json.Patches == null && (json.Files ?? []).All(f => f.Kind != ModFileKind) => json.Files ?? [],
            FormatVersion when json.Patches == null => json.Files ?? [],
            _ => null,
        };
        var files = entries?.Select(Recovered).ToList();
        if (json == null || files == null || files.Any(f => f == null))
            throw new InvalidDataException($"{path}: this is not a recovery file of this editor version.");
        var text = json.Text == null
            ? null
            : new RecoveredText(json.Text.KnownHash, ModText.From(json.Text.Strings ?? new(), json.Text.Infocards ?? new()));
        if (text != null && text.Text.Strings.Concat(text.Text.Infocards)
                .Any(e => !ModStrings.IsValidKey(e.Key) || string.IsNullOrEmpty(e.Value)))
            throw new InvalidDataException($"{path}: a text key or a text is not valid.");
        return new RecoveryFile(files!, text);
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(new FileJson
    {
        FormatVersion = Files.Any(NeedsFormat3) ? FormatVersion : CutscenesVersion,
        Files = Files.Select(f => new FileEntryJson
        {
            Path = f.Path,
            Kind = f.Kind switch
            {
                RecoveredKind.WholeFile => WholeFileKind,
                RecoveredKind.ModFile => ModFileKind,
                _ => PatchKind,
            },
            KnownHash = f.KnownHash,
            Text = f.Text,
        }).ToList(),
        Text = Text == null
            ? null
            : new TextJson
            {
                KnownHash = Text.KnownHash,
                Strings = new Dictionary<string, string>(Text.Text.Strings),
                Infocards = new Dictionary<string, string>(Text.Text.Infocards),
            },
    }, JsonOptions);

    /// <summary>True for an entry that format 2 cannot hold: an editor file, or a whole INI file.</summary>
    private static bool NeedsFormat3(RecoveredFile file) =>
        file.Kind == RecoveredKind.ModFile ||
        file.Kind == RecoveredKind.WholeFile && !file.Path.EndsWith(".thn", StringComparison.OrdinalIgnoreCase);

    /// <summary>An entry as a recovered file, or null when it is not valid. An entry without a kind is a patch, as in
    /// format 1. The path of an editor file must be in its folders, and the path of another file must be an install
    /// path.</summary>
    private static RecoveredFile? Recovered(FileEntryJson entry)
    {
        RecoveredKind? kind = entry.Kind switch
        {
            null or PatchKind => RecoveredKind.Patch,
            WholeFileKind => RecoveredKind.WholeFile,
            ModFileKind => RecoveredKind.ModFile,
            _ => null,
        };
        if (kind is not { } k || entry.Path == null)
            return null;
        var normal = k == RecoveredKind.ModFile ? ModFileDocument.Normalize(entry.Path) : Commands.InstallPaths.TryParse(entry.Path);
        return normal == entry.Path ? new RecoveredFile(entry.Path, k, entry.KnownHash, entry.Text) : null;
    }

    private sealed class FileJson
    {
        public int FormatVersion { get; set; }

        /// <summary>The entries of format 1.</summary>
        public List<FileEntryJson>? Patches { get; set; }

        /// <summary>The entries of formats 2 and 3.</summary>
        public List<FileEntryJson>? Files { get; set; }

        public TextJson? Text { get; set; }
    }

    private sealed class FileEntryJson
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        public string? KnownHash { get; set; }
        public string? Text { get; set; }
    }

    private sealed class TextJson
    {
        public string? KnownHash { get; set; }
        public Dictionary<string, string>? Strings { get; set; }
        public Dictionary<string, string>? Infocards { get; set; }
    }
}

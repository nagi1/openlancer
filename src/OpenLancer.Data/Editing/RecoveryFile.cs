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

    /// <summary>The whole file of a cutscene that the active mod has.</summary>
    WholeFile,
}

/// <summary>The file of the active mod that unsaved edits give for one install path.</summary>
/// <param name="Path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
/// <param name="KnownHash">The hash of the file of the mod that the edits change (see <see cref="ContentHash"/>), or
/// null when the mod had no such file.</param>
/// <param name="Text">The text of the file, or null for no patch.</param>
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
/// INI patches. The editor reads both and writes format 2.</para>
/// </remarks>
internal sealed record RecoveryFile(IReadOnlyList<RecoveredFile> Files, RecoveredText? Text)
{
    public const string FileName = "recovery.json";
    private const int FormatVersion = 2;
    private const int PatchesOnlyVersion = 1;
    private const string PatchKind = "patch";
    private const string WholeFileKind = "file";

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
        FormatVersion = FormatVersion,
        Files = Files.Select(f => new FileEntryJson
        {
            Path = f.Path,
            Kind = f.Kind == RecoveredKind.WholeFile ? WholeFileKind : PatchKind,
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

    /// <summary>An entry as a recovered file, or null when it is not valid. An entry without a kind is a patch, as in
    /// format 1.</summary>
    private static RecoveredFile? Recovered(FileEntryJson entry)
    {
        if (entry.Path == null || VfsPath.Normalize(entry.Path) != entry.Path)
            return null;
        RecoveredKind? kind = entry.Kind switch
        {
            null or PatchKind => RecoveredKind.Patch,
            WholeFileKind when entry.Text != null => RecoveredKind.WholeFile,
            _ => null,
        };
        return kind is { } k ? new RecoveredFile(entry.Path, k, entry.KnownHash, entry.Text) : null;
    }

    private sealed class FileJson
    {
        public int FormatVersion { get; set; }

        /// <summary>The entries of format 1.</summary>
        public List<FileEntryJson>? Patches { get; set; }

        /// <summary>The entries of format 2.</summary>
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

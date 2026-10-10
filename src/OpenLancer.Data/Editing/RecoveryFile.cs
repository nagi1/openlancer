using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

/// <summary>The patch that unsaved edits give for one file.</summary>
/// <param name="Path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
/// <param name="KnownHash">The hash of the patch file that the edits change (see <see cref="ContentHash"/>), or
/// null when the mod had no patch for the file.</param>
/// <param name="Text">The text of the patch, or null for no patch.</param>
internal sealed record RecoveredPatch(string Path, string? KnownHash, string? Text);

/// <summary>The text of the mod with unsaved edits.</summary>
/// <param name="KnownHash">The hash of the strings.json file that the edits change, or null when there was none.</param>
internal sealed record RecoveredText(string? KnownHash, ModText Text);

/// <summary>
/// The unsaved edits of an editor session, in <c>.olsave\recovery.json</c> of the active mod. Without local patch
/// 0017 the editor cannot stop the window from closing, so it keeps this file while it has unsaved edits, and offers
/// to restore them at the next open (decision D8).
/// </summary>
/// <remarks>
/// The file holds the patch text that a save would write for each changed file, and the text of the mod. Each part
/// also holds the hash of the file on disk that the edits change. When a file of the mod changes after the editor
/// wrote the recovery file, the edits do not apply to it any more.
/// </remarks>
internal sealed record RecoveryFile(IReadOnlyList<RecoveredPatch> Patches, RecoveredText? Text)
{
    public const string FileName = "recovery.json";
    private const int FormatVersion = 1;

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
        if (json == null || json.FormatVersion != FormatVersion ||
            (json.Patches ?? []).Any(p => p.Path == null || VfsPath.Normalize(p.Path) != p.Path))
            throw new InvalidDataException($"{path}: this is not a recovery file of this editor version.");
        var patches = (json.Patches ?? []).Select(p => new RecoveredPatch(p.Path!, p.KnownHash, p.Text)).ToList();
        var text = json.Text == null
            ? null
            : new RecoveredText(json.Text.KnownHash, ModText.From(json.Text.Strings ?? new(), json.Text.Infocards ?? new()));
        if (text != null && text.Text.Strings.Concat(text.Text.Infocards)
                .Any(e => !ModStrings.IsValidKey(e.Key) || string.IsNullOrEmpty(e.Value)))
            throw new InvalidDataException($"{path}: a text key or a text is not valid.");
        return new RecoveryFile(patches, text);
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(new FileJson
    {
        FormatVersion = FormatVersion,
        Patches = Patches.Select(p => new PatchJson { Path = p.Path, KnownHash = p.KnownHash, Text = p.Text }).ToList(),
        Text = Text == null
            ? null
            : new TextJson
            {
                KnownHash = Text.KnownHash,
                Strings = new Dictionary<string, string>(Text.Text.Strings),
                Infocards = new Dictionary<string, string>(Text.Text.Infocards),
            },
    }, JsonOptions);

    private sealed class FileJson
    {
        public int FormatVersion { get; set; }
        public List<PatchJson>? Patches { get; set; }
        public TextJson? Text { get; set; }
    }

    private sealed class PatchJson
    {
        public string? Path { get; set; }
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

using System;
using System.IO;
using System.Text;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>The state of an editor file of the mod: its text, or null when the file does not exist.</summary>
internal sealed record ModFileState(string? Text) : IFileState;

/// <summary>
/// An editor file of the active mod (section 6.8 of the M6b design): a graph file of a mission script in
/// <c>graphs\</c>, or a mission prefab in <c>prefabs\missions\</c>. The game, the merge, <c>oltool deploy</c>, the
/// validation and the packages ignore these folders. The file is a file of the edit session, so the edit history, the
/// save transaction and the recovery file cover it, as they cover the patches.
/// </summary>
/// <remarks>
/// <para>The path of the file is relative to the mod folder. In the session, the key of the file is
/// <see cref="KeyPrefix"/> and that path. An install path cannot hold a <c>:</c>, so the key never names an INI file
/// or a cutscene of the session.</para>
/// <para>A state without text is a file that does not exist. A save deletes such a file, and its empty folders.</para>
/// </remarks>
internal sealed class ModFileDocument : IEditFile
{
    /// <summary>The start of the key of an editor file in the session.</summary>
    public const string KeyPrefix = "mod:";

    /// <summary>The folders of the editor files, relative to the mod folder.</summary>
    private static readonly string[] Folders = [@"graphs\", @"prefabs\missions\"];

    /// <summary>The encoding of the files: UTF-8 without a byte order mark.</summary>
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);

    private ModFileState state;

    private ModFileDocument(string relativePath, ModFileState state, string? sourceHash)
    {
        RelativePath = relativePath;
        this.state = state;
        Initial = state;
        SourceHash = sourceHash;
    }

    /// <summary>The path relative to the mod folder, with <c>\</c> between its parts.</summary>
    public string RelativePath { get; }

    /// <summary>The key of the file in the session: <see cref="KeyPrefix"/> and <see cref="RelativePath"/>.</summary>
    public string Path => KeyPrefix + RelativePath;

    public ModFileState State => state;

    IFileState IEditFile.State => state;

    /// <summary>The state when the session opened the file, or after the last save.</summary>
    public ModFileState Initial { get; }

    /// <summary>The text of the file, or null when it does not exist.</summary>
    public string? Text => state.Text;

    /// <summary>The hash of the file on disk when the session read it (see <see cref="ContentHash"/>), or null when it
    /// did not exist.</summary>
    public string? SourceHash { get; }

    /// <summary>True when a save writes or deletes the file.</summary>
    public bool HasChanges => !string.Equals(state.Text, Initial.Text, StringComparison.Ordinal);

    /// <summary>The bytes that a save writes, or null for a file that does not exist.</summary>
    public byte[]? Bytes => state.Text == null ? null : Encoding.GetBytes(state.Text);

    /// <summary>Sets the text of the file, or makes it a file that does not exist with null.</summary>
    public void SetText(string? text) => state = new ModFileState(text);

    public void Restore(ModFileState restored) => state = restored;

    void IEditFile.Restore(IFileState restored) => Restore((ModFileState)restored);

    /// <summary>True for the key of an editor file in the session.</summary>
    public static bool IsKey(string key) => key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The normal form of the path of an editor file: relative to the mod folder, with <c>\</c> between its parts, in a
    /// folder of the editor files, with the extension <c>.ini</c>. Null for another path.
    /// </summary>
    public static string? Normalize(string relativePath)
    {
        var normal = InstallPaths.TryParse(relativePath);
        if (normal == null || !normal.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var folder in Folders)
        {
            if (normal.Length > folder.Length && normal.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                return normal;
        }
        return null;
    }

    /// <summary>Why a path cannot be the path of an editor file, as a message.</summary>
    public static string PathProblem(string relativePath) =>
        $"'{relativePath}' is not the path of an editor file of the mod. Use a path such as " +
        @"graphs\DATA\MISSIONS\M02\m02.ini or prefabs\missions\ol_prefab.ini.";

    /// <summary>Reads the file of a path in the mod folder.</summary>
    /// <param name="relativePath">The path in the form of <see cref="Normalize"/>.</param>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be read.</exception>
    public static ModFileDocument Open(string modFolder, string relativePath)
    {
        var file = System.IO.Path.Combine(modFolder, relativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        return From(relativePath, File.Exists(file) ? File.ReadAllBytes(file) : null);
    }

    /// <summary>The document that the bytes of a save give, so that its state is what a reload gives.</summary>
    public ModFileDocument Reopen(byte[]? content) => From(RelativePath, content);

    private static ModFileDocument From(string relativePath, byte[]? bytes)
    {
        string? text = null;
        if (bytes != null)
        {
            using var reader = new StreamReader(new MemoryStream(bytes, false), Encoding, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        return new ModFileDocument(relativePath, new ModFileState(text), bytes == null ? null : ContentHash.Of(bytes));
    }
}

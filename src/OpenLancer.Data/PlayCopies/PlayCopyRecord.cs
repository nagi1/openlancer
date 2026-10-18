using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.PlayCopies;

/// <summary>The state of a play copy. A make that stopped leaves <see cref="Incomplete"/>, and the next use completes
/// the copy.</summary>
public enum PlayCopyState
{
    Incomplete,
    Complete,
}

/// <summary>How a play copy holds the large files of the game.</summary>
public enum PlayCopyMode
{
    /// <summary>Hard links to the large binary files of <c>DATA</c> and <c>DLLS</c>, and real copies of the other
    /// files.</summary>
    Links,

    /// <summary>Real copies of all files, for example on another drive than the game. A test of FLMM or of a drop-in
    /// zip needs it, because these write into game files.</summary>
    Full,
}

/// <summary>What a file of a play copy is when OpenLancer makes it.</summary>
public enum PlayCopyKind
{
    /// <summary>A hard link to the file of the game.</summary>
    Link,

    /// <summary>A real copy of the file of the game.</summary>
    Copy,
}

/// <summary>One file of a play copy as OpenLancer made it.</summary>
/// <param name="Size">For a link, the size of the file. For a copy, the size of the source when OpenLancer copied
/// it.</param>
/// <param name="Time">For a link, the write time of the file. For a copy, the write time of the source when OpenLancer
/// copied it. Both in UTC.</param>
public sealed record PlayCopyEntry(PlayCopyKind Kind, long Size, DateTime Time)
{
    /// <summary>The volume serial number of the file record of a link, or null for a copy.</summary>
    public uint? Volume { get; init; }

    /// <summary>The file index of the file record of a link, or null for a copy.</summary>
    public ulong? FileIndex { get; init; }

    /// <summary>True when the stamp comes from the file record of this link.</summary>
    public bool IsFileOf(FileStamp stamp) => Volume == stamp.Volume && FileIndex == stamp.Index;
}

/// <summary>A file that an install wrote into a play copy.</summary>
/// <param name="Sha256">The lowercase SHA-256 of the file after the install.</param>
/// <param name="Profile">The profile file of the install, or null.</param>
public sealed record PlayCopyInstall(string Sha256, string? Profile, DateTime Time);

/// <summary>
/// The record of a play copy, <c>.openlancer-playcopy.json</c> in its root (section 8.1 of the M11 design). It names
/// the game that it copies. It keeps the kind of each file, the files of the last install and the own edits.
/// </summary>
/// <remarks>The keys of the files are paths relative to the root, with the letter case on disk and <c>\</c>. They
/// compare without letter case. The file names the game folder, so it stays on the machine of the user.</remarks>
public sealed class PlayCopyRecord
{
    /// <summary>The file name of the record in the root of a play copy.</summary>
    public const string FileName = ".openlancer-playcopy.json";

    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        RespectNullableAnnotations = true,
    };

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The full path of the game folder that the play copy copies.</summary>
    public string Source { get; set; } = "";

    public GameLayout Layout { get; set; }

    /// <summary>The build id of the game when OpenLancer made the copy, or null.</summary>
    public string? Build { get; set; }

    public DateTime Created { get; set; }

    public PlayCopyState State { get; set; }

    public PlayCopyMode Mode { get; set; }

    /// <summary>The kind and the stamps of each file that OpenLancer made from the game.</summary>
    public SortedDictionary<string, PlayCopyEntry> Entries { get; set; } = new(VfsPath.Comparer);

    /// <summary>The files of the last install.</summary>
    public SortedDictionary<string, PlayCopyInstall> Installed { get; set; } = new(VfsPath.Comparer);

    /// <summary>The own edits of the play copy: the file and the line that the play copy sets in it. For example
    /// <c>save_in_directory = true</c> in <c>EXE\flplusplus.ini</c>.</summary>
    public SortedDictionary<string, string> Own { get; set; } = new(VfsPath.Comparer);

    /// <summary>The number of hard links of the play copy. An installed file is not a link.</summary>
    [JsonIgnore]
    public int LinkCount => Entries.Count(e => e.Value.Kind == PlayCopyKind.Link && !Installed.ContainsKey(e.Key));

    /// <summary>The number of real files of the play copy that come from the game or from an install.</summary>
    [JsonIgnore]
    public int CopyCount => Entries.Keys.Concat(Installed.Keys).Distinct(VfsPath.Comparer).Count() - LinkCount;

    /// <summary>The bytes of the real files of a play copy. The links take no space.</summary>
    /// <param name="root">The root of the play copy, for the sizes of the installed files.</param>
    public long SizeOnDisk(string root) =>
        Entries.Where(e => e.Value.Kind == PlayCopyKind.Copy && !Installed.ContainsKey(e.Key)).Sum(e => e.Value.Size) +
        Installed.Keys.Select(p => Path.Combine(root, p)).Where(File.Exists).Sum(f => new FileInfo(f).Length);

    /// <summary>The full path of the record of a play copy root.</summary>
    public static string PathIn(string root) => Path.Combine(root, FileName);

    /// <summary>True when the folder has a record, valid or not.</summary>
    public static bool Exists(string root) => File.Exists(PathIn(root));

    /// <summary>Reads the record of a play copy root, or null when the root has no record.</summary>
    /// <exception cref="InvalidDataException">The record is not a valid record.</exception>
    public static PlayCopyRecord? Read(string root)
    {
        var path = PathIn(root);
        if (!File.Exists(path))
            return null;
        PlayCopyRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<PlayCopyRecord>(SharedFile.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is not a valid record of a play copy: {e.Message}", e);
        }
        if (record == null || record.FormatVersion != CurrentFormatVersion || record.Source.Length == 0)
            throw new InvalidDataException($"{path} is not a record of a play copy of this OpenLancer version.");
        // The serializer makes the dictionaries without the comparer of the paths.
        record.Entries = new SortedDictionary<string, PlayCopyEntry>(record.Entries, VfsPath.Comparer);
        record.Installed = new SortedDictionary<string, PlayCopyInstall>(record.Installed, VfsPath.Comparer);
        record.Own = new SortedDictionary<string, string>(record.Own, VfsPath.Comparer);
        return record;
    }

    /// <summary>The source of a play copy root for a message, or null when the folder has no valid record.</summary>
    public static string? SourceOf(string root)
    {
        try
        {
            return Read(root)?.Source;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The JSON text of the record, in UTF-8.</summary>
    internal byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
}

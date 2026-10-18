using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using OpenLancer.Data.Export;

namespace OpenLancer.Data.Games;

/// <summary>What a folder holds.</summary>
public enum GameKind
{
    /// <summary>No game: the folder is missing, it is not a game folder, or the game data is not complete.</summary>
    NotAGame,

    /// <summary>The retail game, 1.0 or 1.1, with or without JFLP.</summary>
    Vanilla,

    /// <summary>Freelancer: HD Edition.</summary>
    HdEdition,

    /// <summary>A game that OpenLancer does not support, for example Discovery.</summary>
    NotSupported,

    /// <summary>Code files that OpenLancer does not know.</summary>
    Unknown,

    /// <summary>A game that the user built from source code, with <c>bin\freelancer.exe</c> and its <c>DATA</c> folder
    /// (milestone M11). It is a play target only, never the base of a profile or of an export.</summary>
    SourceBuild,
}

/// <summary>How the detector found the version.</summary>
public enum VersionProof
{
    None,

    /// <summary>The SHA-256 of the five code files is the hash of a known build.</summary>
    Hash,

    /// <summary>The binary file version of <c>Common.dll</c>.</summary>
    FileVersion,

    /// <summary>The version text of the HD Edition marker DLL.</summary>
    VersionText,
}

/// <summary>The INI files of the game, compared with the file list of its build.</summary>
public enum DataState
{
    NotChanged,
    Changed,

    /// <summary>OpenLancer has no file list of the build.</summary>
    NotCompared,
}

/// <summary>The severity of a finding. A report has the highest severity of its findings, but info lines count as
/// <see cref="Ok"/>.</summary>
public enum GameSeverity
{
    Ok,
    Info,
    Warning,
    Error,
}

/// <summary>One line of a report for the user.</summary>
public sealed record GameFinding(GameSeverity Severity, string Text);

/// <summary>
/// The result of a game folder check (<see cref="GameDetector"/>): which game the folder holds, its version and how the
/// detector knows it, the state of its INI files, the evidence and the findings.
/// </summary>
/// <param name="Folder">The folder that the user gave.</param>
/// <param name="Root">The game root: the folder with <c>EXE\freelancer.ini</c>. Null for
/// <see cref="GameKind.NotAGame"/>.</param>
/// <param name="Version">For example "1.1" or "0.7.1", or null when the version is not known.</param>
/// <param name="JflpVersion">The binary file version of <c>jflp.dll</c>, or null.</param>
/// <param name="Product">The name of a game that OpenLancer does not support, for example "Discovery".</param>
/// <param name="ExePath">The full path of <c>EXE\Freelancer.exe</c> (<c>bin\freelancer.exe</c> for a source build), or
/// null when the folder has no game program.</param>
/// <param name="Build">The build id: <c>vanilla-1.0</c>, <c>vanilla-1.1</c>, <c>hde-&lt;version&gt;</c>, <c>hde</c>,
/// <c>discovery</c>, <c>unknown</c>, or <c>source-&lt;data build&gt;</c> for a source build. Null for
/// <see cref="GameKind.NotAGame"/>.</param>
/// <param name="Target">The export target of mods for this game (see <c>ModTargets</c>), or null.</param>
/// <param name="Reference">The build whose file list the check used, or null.</param>
/// <param name="ComparedFiles">The number of INI files of the reference that the check compared.</param>
/// <param name="ChangedFiles">The number of changed, missing and extra INI files.</param>
/// <param name="DataFiles">The first 50 changed, missing and extra INI files, each with the change in brackets.</param>
/// <param name="Evidence">One line for each rule that ran: what it read and what it found.</param>
/// <param name="Findings">The info, warning and error lines for the user.</param>
public sealed record GameReport(
    string Folder, string? Root, GameKind Kind, string? Version, VersionProof Proof,
    bool Jflp, string? JflpVersion, string? Product, string? ExePath,
    string? Build, string? Target, DataState Data, string? Reference, int ComparedFiles, int ChangedFiles,
    IReadOnlyList<string> DataFiles, IReadOnlyList<string> Evidence, IReadOnlyList<GameFinding> Findings)
{
    /// <summary>True when the folder does not exist, for example because the game moved.</summary>
    public bool FolderMissing { get; init; }

    /// <summary>How the files of the game map to install paths (milestone M11).</summary>
    public GameLayout Layout { get; init; }

    /// <summary>True when the folder holds a game that the editor can link.</summary>
    [JsonIgnore]
    public bool IsGame => Kind != GameKind.NotAGame;

    /// <summary>True when the editor can use the game as the base of a profile. A source build is a play target
    /// only.</summary>
    [JsonIgnore]
    public bool CanBeBase => IsGame && Kind != GameKind.SourceBuild;

    /// <summary>The files of the game for a merge.</summary>
    [JsonIgnore]
    public GameBase? Base => Root == null ? null : new GameBase(Root, Layout);

    /// <summary>The name of the game for the user, for example "Freelancer 1.1 with JFLP".</summary>
    public string Name => Kind switch
    {
        GameKind.NotAGame => "No game",
        GameKind.Vanilla => (Version == null ? "Freelancer (version unknown)" : $"Freelancer {Version}") + JflpSuffix,
        GameKind.HdEdition => Version == null
            ? "Freelancer: HD Edition (version unknown)"
            : $"Freelancer: HD Edition {Version}",
        GameKind.NotSupported => $"{Product} (not supported)",
        GameKind.SourceBuild => $"Source build ({Path.GetFileName(Root ?? Folder)})",
        _ => "Unknown Freelancer version",
    };

    /// <summary>The short name for the status bar, for example "HD Edition 0.7.1".</summary>
    public string ShortName => Kind switch
    {
        GameKind.NotAGame => "No game",
        GameKind.Vanilla => (Version == null ? "Freelancer" : $"Freelancer {Version}") + JflpSuffix,
        GameKind.HdEdition => Version == null ? "HD Edition" : $"HD Edition {Version}",
        GameKind.NotSupported => Product ?? "Not supported",
        GameKind.SourceBuild => "Source build",
        _ => "Unknown version",
    };

    /// <summary>The highest severity of the findings: <see cref="GameSeverity.Ok"/>, <see cref="GameSeverity.Warning"/>
    /// or <see cref="GameSeverity.Error"/>. Info lines count as <see cref="GameSeverity.Ok"/>. A folder without a game
    /// is an error.</summary>
    public GameSeverity Severity =>
        Kind == GameKind.NotAGame ? GameSeverity.Error
        : Findings.Select(f => f.Severity).Where(s => s > GameSeverity.Info).DefaultIfEmpty(GameSeverity.Ok).Max();

    /// <summary>The first error of a folder without a game, or null.</summary>
    [JsonIgnore]
    public string? Error => Kind == GameKind.NotAGame
        ? Findings.FirstOrDefault(f => f.Severity == GameSeverity.Error)?.Text
        : null;

    /// <summary>The state of the INI files for the user, for example "INI files: 3 changed".</summary>
    [JsonIgnore]
    public string DataText => Data switch
    {
        DataState.NotChanged => "INI files: not changed",
        DataState.Changed => $"INI files: {ChangedFiles} changed",
        _ => "INI files: not compared",
    };

    /// <summary>The name of a new profile for this game, before a number that makes it unique.</summary>
    [JsonIgnore]
    public string ProfileName => Kind switch
    {
        GameKind.HdEdition => "hd-edition",
        GameKind.Vanilla when Version == "1.1" => "freelancer-1-1",
        GameKind.Vanilla when Version == "1.0" => "freelancer-1-0",
        GameKind.NotSupported when Build == GameDetector.DiscoveryBuild => "discovery",
        _ => "freelancer",
    };

    private string JflpSuffix => Jflp ? " with JFLP" : "";
}

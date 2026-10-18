using System.Numerics;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>How a linked game shows: its glyph, its color and its state line. The Game link window, the status bar and
/// the Welcome page use the same look.</summary>
internal static class GameLook
{
    /// <summary>The glyph and the color of a severity: a green check, a yellow triangle or a red cross.</summary>
    public static (char Glyph, Vector4 Color) Of(GameSeverity severity) => severity switch
    {
        GameSeverity.Error => (EditorIcons.TimesCircle, EditorTheme.Error),
        GameSeverity.Warning => (EditorIcons.ExclamationTriangle, EditorTheme.Warning),
        GameSeverity.Info => (EditorIcons.InfoCircle, EditorTheme.Label),
        _ => (EditorIcons.CheckCircle, EditorTheme.Success),
    };

    /// <summary>The look of a game folder: the severity of its report, an error when it is not found, or a neutral
    /// glyph while the first check runs.</summary>
    public static (char Glyph, Vector4 Color) Of(GameReport? report) =>
        report == null ? (EditorIcons.Link, EditorTheme.Label) : Of(report.Severity);

    /// <summary>The state line of a game folder: "Checking...", "Not found", "Not valid: reason" or the state of its INI
    /// files.</summary>
    public static string State(GameReport? report, bool checking) =>
        checking && report == null ? "Checking..."
        : report == null ? "Not checked"
        : report.FolderMissing ? "Not found"
        : !report.IsGame ? $"Not valid: {report.Error}"
        : report.DataText;

    /// <summary>The name of a game folder: the name of its report, else the name of the last check.</summary>
    public static string Name(GameReport? report, LinkedGame? entry) =>
        report is { IsGame: true } ? report.Name : entry?.LastName ?? report?.Name ?? "No game";
}

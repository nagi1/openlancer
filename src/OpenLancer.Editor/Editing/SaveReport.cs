using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Editing;

/// <summary>One line of the Console report of a save.</summary>
/// <param name="Target">The section that the line is about, or null.</param>
/// <param name="Cutscene">The place in a cutscene that the line is about, or null.</param>
internal sealed record ReportLine(ConsoleSeverity Severity, string Source, string Message, SectionRef? Target = null,
    ThnLocation? Cutscene = null);

/// <summary>
/// The Console report of a save: the files that the save wrote and deleted, its problems, and then one summary line.
/// The status bar shows the summary (see <see cref="ConsoleLog.AddStatus"/>). The summaries of a save that does not
/// start are here too.
/// </summary>
internal static class SaveReport
{
    public const string Source = "Save";

    /// <summary>The summary of a save without an open profile.</summary>
    public const string NoProfile = "No profile is open, so there is nothing to save.";

    /// <summary>The summary of a save without an active mod (Mod > None).</summary>
    public const string NoActiveMod =
        "The editor did not save. Mod > None is read-only. Select a mod in the Mod menu, then save again.";

    /// <summary>The summary of a save of a session that takes no edits.</summary>
    /// <param name="reason">Why the session takes no edits, for example because the mod is in the game folder.</param>
    public static string ReadOnly(string modId, string reason) =>
        $"The editor did not save mod '{modId}' because it is read-only. {reason}";

    /// <summary>The summary of a save that could not start, for example because a field edit is open.</summary>
    public static string NotStarted(string modId, string reason) => $"The editor did not save mod '{modId}'. {reason}";

    /// <summary>The detail lines of a save: the files that it wrote and deleted, then its problems.</summary>
    /// <param name="modFolder">The folder of the active mod. The file names in the report are relative to it.</param>
    public static List<ReportLine> Lines(SaveResult result, string modFolder)
    {
        var lines = new List<ReportLine>();
        if (result.Saved)
        {
            lines.AddRange(result.Written.Select(f => new ReportLine(ConsoleSeverity.Info, Source,
                "Wrote " + Path.GetRelativePath(modFolder, f))));
            lines.AddRange(result.Deleted.Select(f => new ReportLine(ConsoleSeverity.Info, Source,
                "Deleted " + Path.GetRelativePath(modFolder, f))));
        }

        lines.AddRange(result.Problems.Select(p => new ReportLine(
            p.Severity == DiagnosticSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning, p.Source,
            p.Message, p.Target, p.Cutscene)));
        return lines;
    }

    /// <summary>The summary line of a save. It comes after the detail lines, so the status bar shows it.</summary>
    /// <param name="modId">The id of the active mod.</param>
    /// <param name="number">The number of the save in this editor session, or 0 when it changed no file.</param>
    /// <param name="hadEdits">True when the session had unsaved edits before the save.</param>
    public static ReportLine Summary(SaveResult result, string modId, int number, bool hadEdits)
    {
        if (!result.Saved)
        {
            var errors = result.Problems.Count(p => p.Severity == DiagnosticSeverity.Error);
            return new ReportLine(ConsoleSeverity.Error, Source, result.ChangedOnDisk.Count > 0
                ? $"The editor did not save mod '{modId}'. Something outside the editor changed " +
                  $"{Count(result.ChangedOnDisk.Count, "file")} of the mod. Select Overwrite or Cancel in the dialog."
                : $"The editor did not save mod '{modId}' because of {Count(errors, "error")}. The Console lists them.");
        }

        if (number > 0)
        {
            return new ReportLine(ConsoleSeverity.Info, Source,
                $"Saved mod '{modId}' (save {number}): {Count(result.Written.Count, "file")} written, " +
                $"{Count(result.Deleted.Count, "file")} deleted.");
        }

        return new ReportLine(ConsoleSeverity.Info, Source, hadEdits
            ? $"Saved mod '{modId}'. No file changed."
            : $"Mod '{modId}' has no unsaved edits. No file changed.");
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

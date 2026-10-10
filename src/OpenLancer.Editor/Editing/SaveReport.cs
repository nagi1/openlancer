using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Editing;

/// <summary>One line of the Console report of a save.</summary>
/// <param name="Target">The section that the line is about, or null.</param>
internal sealed record ReportLine(ConsoleSeverity Severity, string Source, string Message, SectionRef? Target = null);

/// <summary>The Console report of a save: what the save wrote and deleted, and its problems.</summary>
internal static class SaveReport
{
    public const string Source = "Save";

    /// <param name="modId">The id of the active mod.</param>
    /// <param name="modFolder">The folder of the active mod. The file names in the report are relative to it.</param>
    /// <param name="number">The number of the save in this editor session, or 0 when it changed no file.</param>
    public static List<ReportLine> Lines(SaveResult result, string modId, string modFolder, int number)
    {
        var lines = new List<ReportLine>();
        if (!result.Saved)
        {
            var errors = result.Problems.Count(p => p.Severity == DiagnosticSeverity.Error);
            lines.Add(new ReportLine(ConsoleSeverity.Error, Source,
                $"The save of mod '{modId}' wrote nothing: {Count(errors, "error")}. The edits are not saved."));
        }
        else if (number == 0)
            lines.Add(new ReportLine(ConsoleSeverity.Info, Source, $"Saved mod '{modId}'. No file changed."));
        else
        {
            lines.Add(new ReportLine(ConsoleSeverity.Info, Source,
                $"Saved mod '{modId}' (save {number}): {Count(result.Written.Count, "file")} written, " +
                $"{Count(result.Deleted.Count, "file")} deleted."));
            lines.AddRange(result.Written.Select(f => new ReportLine(ConsoleSeverity.Info, Source,
                "Wrote " + Path.GetRelativePath(modFolder, f))));
            lines.AddRange(result.Deleted.Select(f => new ReportLine(ConsoleSeverity.Info, Source,
                "Deleted " + Path.GetRelativePath(modFolder, f))));
        }
        lines.AddRange(result.Problems.Select(p => new ReportLine(
            p.Severity == DiagnosticSeverity.Error ? ConsoleSeverity.Error : ConsoleSeverity.Warning, p.Source,
            p.Message, p.Target)));
        return lines;
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

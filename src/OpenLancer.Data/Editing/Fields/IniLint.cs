using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Editing.Fields;

/// <summary>One mark of the raw INI editor: a line with the problems of the checks on it, or a note.</summary>
/// <param name="Line">The line, from 0.</param>
/// <param name="Severity">The highest severity of the problems of the line.</param>
/// <param name="Message">The messages of the problems, one on each line, and then the note of the line.</param>
/// <param name="BaseGame">True when each problem of the line is a base-game problem. The editor then shows the mark
/// muted.</param>
/// <param name="Note">True for a mark without a problem: the reason why a section is read-only. The editor shows it
/// muted.</param>
public sealed record RawMark(int Line, DiagnosticSeverity Severity, string Message, bool BaseGame, bool Note = false);

/// <summary>
/// Maps the problems of the checks to the lines of a raw text (section 11.5 of the M6a design). A problem of a line maps
/// to its line, and a problem of a whole section maps to the header line. So a mark in the raw view and a mark on a field
/// say the same thing. The header line of a section that the editor cannot write as text gets the reason (section 13.1
/// of the M6a design).
/// </summary>
public static class IniLint
{
    /// <summary>The marks of the problems and the read-only reasons on a raw text, one mark for each line, in line
    /// order.</summary>
    /// <param name="diagnostics">The problems of the file of the text.</param>
    /// <param name="showBaseGame">True while the base-game messages show. Else base-game problems give no mark
    /// (decision D527).</param>
    public static IReadOnlyList<RawMark> Marks(RawText text, IEnumerable<Diagnostic> diagnostics, bool showBaseGame)
    {
        var byLine = new SortedDictionary<int, List<Diagnostic>>();
        foreach (var diagnostic in diagnostics)
        {
            if (!BaseGameMessages.Shows(diagnostic, showBaseGame))
                continue;
            var line = diagnostic.Target is { } target ? text.LineOf(target.Id, diagnostic.EntryIndex) : 0;
            if (line is not { } at || at >= text.Lines.Count)
                continue;
            if (!byLine.TryGetValue(at, out var list))
                byLine[at] = list = [];
            list.Add(diagnostic);
        }
        var marks = byLine.ToDictionary(p => p.Key, p => new RawMark(p.Key,
            p.Value.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            string.Join("\n", p.Value.Select(d => d.Message).Distinct()),
            p.Value.All(d => d.Origin?.IsBaseGame == true)));
        foreach (var (section, reason) in text.ReadOnlySections)
        {
            var header = text.HeaderLine(section);
            marks[header] = marks.TryGetValue(header, out var mark)
                ? mark with { Message = mark.Message + "\n" + reason }
                : new RawMark(header, DiagnosticSeverity.Warning, reason, BaseGame: false, Note: true);
        }
        return marks.Values.OrderBy(m => m.Line).ToList();
    }
}

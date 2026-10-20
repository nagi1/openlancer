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
        var lines = new List<(int, Diagnostic)>();
        foreach (var diagnostic in diagnostics)
        {
            var line = diagnostic.Target is { } target ? text.LineOf(target.Id, diagnostic.EntryIndex) : 0;
            if (line is { } at && at < text.Lines.Count)
                lines.Add((at, diagnostic));
        }
        var marks = ByLine(lines, showBaseGame);
        foreach (var (section, reason) in text.ReadOnlySections)
        {
            var header = text.HeaderLine(section);
            marks[header] = marks.TryGetValue(header, out var mark)
                ? mark with { Message = mark.Message + "\n" + reason }
                : new RawMark(header, DiagnosticSeverity.Warning, reason, BaseGame: false, Note: true);
        }
        return marks.Values.OrderBy(m => m.Line).ToList();
    }

    /// <summary>The marks of problems with their lines from 0, one mark for each line, in line order. The raw editor of
    /// a mission file gives the problems of a draft text in this way while the user types (section 8.8 of the M6b
    /// design).</summary>
    /// <param name="showBaseGame">True while the base-game messages show. Else base-game problems give no mark
    /// (decision D527).</param>
    public static IReadOnlyList<RawMark> Marks(IEnumerable<(int Line, Diagnostic Problem)> problems, bool showBaseGame) =>
        ByLine(problems, showBaseGame).Values.OrderBy(m => m.Line).ToList();

    /// <summary>One mark for each line: an error when a problem of the line is an error, with the messages of the
    /// problems of the line.</summary>
    private static Dictionary<int, RawMark> ByLine(IEnumerable<(int Line, Diagnostic Problem)> problems, bool showBaseGame) =>
        problems.Where(p => BaseGameMessages.Shows(p.Problem, showBaseGame)).GroupBy(p => p.Line).ToDictionary(g => g.Key,
            g => new RawMark(g.Key,
                g.Any(p => p.Problem.Severity == DiagnosticSeverity.Error) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                string.Join("\n", g.Select(p => p.Problem.Message).Distinct()),
                g.All(p => p.Problem.Origin?.IsBaseGame == true)));
}

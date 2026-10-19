using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Editing.Fields;

/// <summary>One mark of the raw INI editor: a line with the problems of the checks on it.</summary>
/// <param name="Line">The line, from 0.</param>
/// <param name="Severity">The highest severity of the problems of the line.</param>
/// <param name="Message">The messages of the problems, one on each line.</param>
/// <param name="BaseGame">True when each problem of the line is a base-game problem. The editor then shows the mark
/// muted.</param>
public sealed record RawMark(int Line, DiagnosticSeverity Severity, string Message, bool BaseGame);

/// <summary>
/// Maps the problems of the checks to the lines of a raw text (section 11.5 of the M6a design). A problem of a line maps
/// to its line, and a problem of a whole section maps to the header line. So a mark in the raw view and a mark on a field
/// say the same thing.
/// </summary>
public static class IniLint
{
    /// <summary>The marks of the problems on a raw text, one mark for each line, in line order.</summary>
    /// <param name="diagnostics">The problems of the file of the text.</param>
    /// <param name="showBaseGame">True while the base-game messages show. Else base-game problems give no mark
    /// (decision D527).</param>
    public static IReadOnlyList<RawMark> Marks(RawText text, IEnumerable<Diagnostic> diagnostics, bool showBaseGame)
    {
        var byLine = new SortedDictionary<int, List<Diagnostic>>();
        foreach (var diagnostic in diagnostics)
        {
            var baseGame = diagnostic.Origin?.IsBaseGame == true;
            if (baseGame && !showBaseGame)
                continue;
            var line = diagnostic.Target is { } target ? text.LineOf(target.Id, diagnostic.EntryIndex) : 0;
            if (line is not { } at || at >= text.Lines.Count)
                continue;
            if (!byLine.TryGetValue(at, out var list))
                byLine[at] = list = [];
            list.Add(diagnostic);
        }
        return byLine.Select(p => new RawMark(p.Key,
            p.Value.Any(d => d.Severity == DiagnosticSeverity.Error) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            string.Join("\n", p.Value.Select(d => d.Message).Distinct()),
            p.Value.All(d => d.Origin?.IsBaseGame == true))).ToList();
    }
}

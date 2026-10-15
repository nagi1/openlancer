using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A problem found while the mod stack is loaded, merged or validated.
/// </summary>
/// <param name="Source">The file or mod that caused the problem.</param>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Source, string Message)
{
    /// <summary>The section that the problem is about, or null.</summary>
    public SectionRef? Target { get; init; }

    /// <summary>The key of the lines of <see cref="Target"/> that the problem is about, or null when the problem is not
    /// about one key. Compare it without letter case.</summary>
    public string? Key { get; init; }

    /// <summary>The place in a cutscene that the problem is about, or null.</summary>
    public ThnLocation? Cutscene { get; init; }

    public static Diagnostic Error(string source, string message) => new(DiagnosticSeverity.Error, source, message);

    public static Diagnostic Warning(string source, string message) => new(DiagnosticSeverity.Warning, source, message);

    public override string ToString() => $"{Severity}: {Source}: {Message}";

    /// <summary>
    /// Returns the diagnostics in <paramref name="current"/> that <paramref name="baseline"/> does not have.
    /// The comparison counts messages, so a message that occurs more often in <paramref name="current"/> is new.
    /// </summary>
    public static IReadOnlyList<Diagnostic> NewIn(IEnumerable<Diagnostic> baseline, IEnumerable<Diagnostic> current)
    {
        var known = baseline.CountBy(d => d.Message).ToDictionary(c => c.Key, c => c.Value);
        var result = new List<Diagnostic>();
        foreach (var d in current)
        {
            if (known.TryGetValue(d.Message, out var count) && count > 0)
                known[d.Message] = count - 1;
            else
                result.Add(d);
        }
        return result;
    }
}

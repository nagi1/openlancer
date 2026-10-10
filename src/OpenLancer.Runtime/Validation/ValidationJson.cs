using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data;

namespace OpenLancer.Runtime.Validation;

/// <summary>
/// The JSON form of a list of problems, as <c>oltool validate --json</c> writes it and the editor reads it:
/// <c>[{"severity": "error", "source": "...", "message": "..."}]</c>. The severity is <c>error</c> or
/// <c>warning</c>.
/// </summary>
public static class ValidationJson
{
    private const string ErrorText = "error";
    private const string WarningText = "warning";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Write(IEnumerable<Diagnostic> problems) =>
        JsonSerializer.Serialize(problems.Select(p => new ProblemJson
        {
            Severity = p.Severity == DiagnosticSeverity.Error ? ErrorText : WarningText,
            Source = p.Source,
            Message = p.Message,
        }).ToList(), JsonOptions);

    /// <exception cref="InvalidDataException">The text is not such a list.</exception>
    public static IReadOnlyList<Diagnostic> Read(string json)
    {
        List<ProblemJson>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<ProblemJson>>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The validation output is not valid JSON: " + e.Message, e);
        }
        if (list == null || list.Any(p => p is null || p.Source == null || p.Message == null ||
                                          p.Severity is not (ErrorText or WarningText)))
            throw new InvalidDataException("The validation output is not a list of problems.");
        return list.Select(p => new Diagnostic(
            p.Severity == ErrorText ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, p.Source!, p.Message!)).ToList();
    }

    private sealed class ProblemJson
    {
        public string? Severity { get; set; }
        public string? Source { get; set; }
        public string? Message { get; set; }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLancer.Data;
using OpenLancer.Data.Thn;

namespace OpenLancer.Runtime.Validation;

/// <summary>
/// The JSON form of a list of problems, as <c>oltool validate --json</c> writes it and the editor reads it:
/// <c>[{"severity": "error", "source": "...", "message": "..."}]</c>. The severity is <c>error</c> or
/// <c>warning</c>. A problem of a cutscene also has its place, so that a click in the Console selects it:
/// <c>"cutscene": {"path": "...", "kind": "entity", "index": 3, "value": "cameraprops.fovh", "key": "\"Cam_A\""}</c>.
/// The kind is <c>entity</c> or <c>event</c>, or it is missing for a problem of the whole script. The value and the key
/// can be missing. Only an element has a key (see <see cref="ThnLocation.Key"/>).
/// </summary>
public static class ValidationJson
{
    private const string ErrorText = "error";
    private const string WarningText = "warning";
    private const string EntityText = "entity";
    private const string EventText = "event";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Write(IEnumerable<Diagnostic> problems) =>
        JsonSerializer.Serialize(problems.Select(p => new ProblemJson
        {
            Severity = p.Severity == DiagnosticSeverity.Error ? ErrorText : WarningText,
            Source = p.Source,
            Message = p.Message,
            Cutscene = p.Cutscene is { } place
                ? new PlaceJson
                {
                    Path = place.Path,
                    Kind = place.Kind switch
                    {
                        ThnElementKind.Entity => EntityText,
                        ThnElementKind.Event => EventText,
                        _ => null,
                    },
                    Index = place.Kind == null ? null : place.Index,
                    Value = place.Value,
                    Key = place.Key,
                }
                : null,
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
                                          p.Severity is not (ErrorText or WarningText) ||
                                          p.Cutscene is { } place && !IsPlace(place)))
            throw new InvalidDataException("The validation output is not a list of problems.");
        return list.Select(p => new Diagnostic(
            p.Severity == ErrorText ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, p.Source!, p.Message!)
        {
            Cutscene = p.Cutscene is { } place ? Place(place) : null,
        }).ToList();
    }

    /// <summary>A place has a path, and a kind with an index from 0, or neither. Only a place with a kind has a
    /// key.</summary>
    private static bool IsPlace(PlaceJson place) =>
        place.Path != null && (place.Kind, place.Index, place.Key) is (null, null, null) or (EntityText or EventText, >= 0, _);

    private static ThnLocation Place(PlaceJson place) => new(place.Path!, place.Kind switch
    {
        EntityText => ThnElementKind.Entity,
        EventText => ThnElementKind.Event,
        _ => null,
    }, place.Index ?? 0, place.Value, place.Key);

    private sealed class ProblemJson
    {
        public string? Severity { get; set; }
        public string? Source { get; set; }
        public string? Message { get; set; }
        public PlaceJson? Cutscene { get; set; }
    }

    private sealed class PlaceJson
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        public int? Index { get; set; }
        public string? Value { get; set; }
        public string? Key { get; set; }
    }
}

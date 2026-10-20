using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The JSON form of the mission commands for MCP1 (section 4.13 of the M6b design): snake case property names and
/// <c>kind</c> names, the reference strings of section 4.0.7, and named values of lines.
/// </summary>
public static class MissionJson
{
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> KindTable = new(() =>
        typeof(MissionCommand).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsPublic: true } && typeof(MissionCommand).IsAssignableFrom(t))
            .ToDictionary(KindOf, t => t, StringComparer.Ordinal));

    /// <summary>The JSON settings of the command layer (see <see cref="EditJson.Options"/>).</summary>
    public static JsonSerializerOptions Options => EditJson.Options;

    /// <summary>The <c>kind</c> name of each command type, for example <c>add_trigger</c>.</summary>
    public static IReadOnlyDictionary<string, Type> Kinds => KindTable.Value;

    /// <summary>The <c>kind</c> name of a command type: its name in snake case.</summary>
    public static string KindOf(Type type) => JsonNamingPolicy.SnakeCaseLower.ConvertName(type.Name);

    /// <summary>Reads a batch.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the JSON is not a batch, a command
    /// has an unknown kind (<c>UnknownCommand</c>), or a reference is not valid.</exception>
    public static MissionBatch ReadBatch(string json) => Read<MissionBatch>(json);

    /// <summary>Reads one command.</summary>
    /// <exception cref="EditException">See <see cref="ReadBatch"/>.</exception>
    public static MissionCommand ReadCommand(string json) => Read<MissionCommand>(json);

    /// <summary>Writes a value of the command layer: a batch, a command or a result.</summary>
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static T Read<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ??
                   throw new EditException(new EditError(EditErrorKind.InvalidArgument, "BadJson",
                       "The request is empty. Send a JSON object."));
        }
        catch (MissionJsonException e)
        {
            throw new EditException(e.Error);
        }
        catch (JsonException e) when (e.InnerException is MissionJsonException inner)
        {
            throw new EditException(inner.Error);
        }
        catch (JsonException e)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, "BadJson",
                $"The request is not valid JSON of a mission batch: {e.Message}"));
        }
    }
}

/// <summary>Reads and writes a <see cref="MissionCommand"/> with its <c>kind</c> (section 4.13 of the M6b design). An
/// unknown kind gives <c>UnknownCommand</c>.</summary>
public sealed class MissionCommandConverter : JsonConverter<MissionCommand>
{
    public override MissionCommand? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
            throw new JsonException("A mission command is an object with a 'kind'.");
        var kind = kindElement.GetString()!;
        if (!MissionJson.Kinds.TryGetValue(kind, out var type))
            throw new MissionJsonException(MissionErrors.UnknownCommandError(kind));
        return (MissionCommand?)root.Deserialize(type, options);
    }

    public override void Write(Utf8JsonWriter writer, MissionCommand value, JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(value, value.GetType(), options);
        writer.WriteStartObject();
        writer.WriteString("kind", MissionJson.KindOf(value.GetType()));
        foreach (var property in element.EnumerateObject())
            property.WriteTo(writer);
        writer.WriteEndObject();
    }
}

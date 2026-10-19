using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>
/// The JSON settings of the command layer (section 4.0.6 of the M6b design): property names and enum values in snake
/// case, for example <c>undo_label</c> and <c>no_change</c>. An enum value whose snake case would split a name in a
/// wrong way has its own name, for example <c>openlancer</c>.
/// </summary>
public static class EditJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A whole number as JSON text, for example the revision <c>"142"</c>. Text keeps each digit in each
    /// client, also above the integer range of a JSON number of 53 bits.</summary>
    public sealed class LongAsText : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            long.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new JsonException("The value must be a whole number as text, for example \"42\".");

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The type of a <see cref="MissionValue"/>.</summary>
public enum MissionValueType
{
    Number,
    Text,
    Bool,

    /// <summary>A list of values, for example the three numbers of a position or the ships of a rest value.</summary>
    List,
}

/// <summary>
/// One value of a line of a command (section 4.3 of the M6b design): a number, a text, a boolean, or a list of values
/// for a position, a rotation or a value with the schema flag "rest". JSON writes it as a JSON number, string, boolean
/// or array.
/// </summary>
[JsonConverter(typeof(MissionValueConverter))]
public sealed record MissionValue
{
    private MissionValue(MissionValueType type) => Type = type;

    public MissionValueType Type { get; }

    /// <summary>The number of a <see cref="MissionValueType.Number"/>.</summary>
    public double Number { get; private init; }

    /// <summary>The text of a <see cref="MissionValueType.Text"/>.</summary>
    public string Text { get; private init; } = "";

    /// <summary>The value of a <see cref="MissionValueType.Bool"/>.</summary>
    public bool Bool { get; private init; }

    /// <summary>The values of a <see cref="MissionValueType.List"/>.</summary>
    public IReadOnlyList<MissionValue> Items { get; private init; } = [];

    public static MissionValue Of(double number) => new(MissionValueType.Number) { Number = number };

    public static MissionValue Of(string text) => new(MissionValueType.Text) { Text = text };

    public static MissionValue Of(bool value) => new(MissionValueType.Bool) { Bool = value };

    /// <summary>A list of numbers, for example a position.</summary>
    public static MissionValue Of(params double[] numbers) =>
        new(MissionValueType.List) { Items = numbers.Select(Of).ToList() };

    /// <summary>A list of values.</summary>
    public static MissionValue List(IEnumerable<MissionValue> items) => new(MissionValueType.List) { Items = items.ToList() };

    public static implicit operator MissionValue(double number) => Of(number);

    public static implicit operator MissionValue(int number) => Of(number);

    public static implicit operator MissionValue(string text) => Of(text);

    public static implicit operator MissionValue(bool value) => Of(value);

    /// <summary>The text of a single value as a line writes it: a number in invariant form, a text as it is, a boolean
    /// as <c>true</c> or <c>false</c>. Null for a list.</summary>
    public string? Scalar => Type switch
    {
        MissionValueType.Number => Format(Number),
        MissionValueType.Text => Text,
        MissionValueType.Bool => Bool ? "true" : "false",
        _ => null,
    };

    public bool Equals(MissionValue? other) =>
        other is not null && Type == other.Type && Number.Equals(other.Number) && Text == other.Text &&
        Bool == other.Bool && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Type, Number, Text, Bool, Items.Count);

    public override string ToString() => Scalar ?? "[" + string.Join(", ", Items) + "]";

    /// <summary>A number in invariant form: a whole number without a point, else the shortest exact text.</summary>
    public static string Format(double number) =>
        number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// A line of a command (section 4.3 of the M6b design): a key and its values. The values come by the names of the
/// schema in <see cref="Values"/>, for example <c>{ "key": "Cnd_Destroyed", "values": { "target": "ol_wave1" } }</c>. The
/// schema gives the index and the width of each name, and checks each value.
/// </summary>
/// <remarks><see cref="Texts"/> gives the value texts by position instead. The inverses and the raw INI editor use it
/// to write a line exactly as it was. Such texts get only the checks of a patch line, because a retail line can hold a
/// value that the checks of the schema refuse.</remarks>
public sealed record LineSpec(string Key)
{
    /// <summary>The values by the names of the schema.</summary>
    public IReadOnlyDictionary<string, MissionValue> Values { get; init; } = new Dictionary<string, MissionValue>();

    /// <summary>The value texts by position, or null. With texts, the line ignores <see cref="Values"/>.</summary>
    public IReadOnlyList<string>? Texts { get; init; }

    /// <summary>A line with values by name.</summary>
    public static LineSpec Of(string key, params (string Name, MissionValue Value)[] values) =>
        new(key) { Values = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase) };

    /// <summary>A line with value texts by position.</summary>
    public static LineSpec Raw(string key, params string[] texts) => new(key) { Texts = texts };

    public bool Equals(LineSpec? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) &&
        Values.Count == other.Values.Count &&
        Values.All(p => other.Values.TryGetValue(p.Key, out var v) && v.Equals(p.Value)) &&
        (Texts == null ? other.Texts == null : other.Texts != null && Texts.SequenceEqual(other.Texts));

    public override int GetHashCode() => HashCode.Combine(Key.ToLowerInvariant(), Values.Count, Texts?.Count);

    public override string ToString() => Texts != null
        ? Texts.Count == 0 ? $"{Key} =" : $"{Key} = {string.Join(", ", Texts)}"
        : $"{Key} = {{{string.Join(", ", Values.Select(v => $"{v.Key}: {v.Value}"))}}}";
}

/// <summary>Reads and writes a <see cref="MissionValue"/> as a JSON number, string, boolean or array.</summary>
public sealed class MissionValueConverter : JsonConverter<MissionValue>
{
    public override MissionValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return From(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, MissionValue value, JsonSerializerOptions options)
    {
        switch (value.Type)
        {
            case MissionValueType.Number:
                writer.WriteNumberValue(value.Number);
                break;
            case MissionValueType.Text:
                writer.WriteStringValue(value.Text);
                break;
            case MissionValueType.Bool:
                writer.WriteBooleanValue(value.Bool);
                break;
            default:
                writer.WriteStartArray();
                foreach (var item in value.Items)
                    Write(writer, item, options);
                writer.WriteEndArray();
                break;
        }
    }

    private static MissionValue From(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => MissionValue.Of(e.GetDouble()),
        JsonValueKind.String => MissionValue.Of(e.GetString()!),
        JsonValueKind.True => MissionValue.Of(true),
        JsonValueKind.False => MissionValue.Of(false),
        JsonValueKind.Array => MissionValue.List(e.EnumerateArray().Select(From)),
        _ => throw new MissionJsonException(MissionErrors.BadJsonError("A value is a number, a text, true, false or a list of values.")),
    };
}

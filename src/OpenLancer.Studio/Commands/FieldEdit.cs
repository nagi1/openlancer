using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;

namespace OpenLancer.Studio.Commands;

/// <summary>The line texts that the text of a field gives, or why the text is not valid.</summary>
/// <param name="Lines">The value text of each line, with the values separated by ", ". Null when the text is not
/// valid.</param>
/// <param name="Error">Why the text is not valid, or null.</param>
internal sealed record FieldParse(IReadOnlyList<string>? Lines, string? Error);

/// <summary>
/// The text of the lines of one INI key in an Inspector field, and the lines that the text of a field gives back.
/// </summary>
/// <remarks>
/// <para>A field shows each INI line on its own text line, with the values separated by ", ". A key that the engine
/// reads one time gets one line. An empty field removes the key.</para>
/// <para>The text of a value is the text that a patch line can hold: the invariant culture, the shortest text of a
/// float, and <c>true</c> or <c>false</c>. The edit command gives each value its type as the merge does (see
/// <see cref="EditCommand.SetLinesFromText"/>), so a float value stays exact.</para>
/// <para>The schema of the key checks the text before an edit: the number of lines, the number of values in a line
/// and the form of each value. A key without a schema type, for example <c>addon</c>, takes any values.</para>
/// </remarks>
internal static class FieldEdit
{
    public static string FormatValue(ValueBase value) => value switch
    {
        BooleanValue truth => (bool)truth ? "true" : "false",
        // A float of a large integer keeps the exact integer, as a patch line writes it.
        SingleValue single when single.TryToInt64(out var number) && number is > int.MaxValue or < int.MinValue =>
            number.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public static string FormatLine(IReadOnlyList<ValueBase> values) => string.Join(", ", values.Select(FormatValue));

    /// <summary>The text of a field: one text line for each INI line.</summary>
    public static string Format(IReadOnlyList<EntryView> lines) => string.Join("\n", lines.Select(l => FormatLine(l.Values)));

    /// <summary>The lines as INI text, for example <c>hit_pts = 1200</c>, one line for each INI line.</summary>
    public static string IniText(string name, IReadOnlyList<EntryView> lines) =>
        string.Join("\n", lines.Select(l => l.Values.Count == 0 ? $"{name} =" : $"{name} = {FormatLine(l.Values)}"));

    /// <summary>The values of one line text, without white space at their ends.</summary>
    public static string[] Values(string line) =>
        line.Trim().Length == 0 ? [] : line.Split(',').Select(v => v.Trim()).ToArray();

    /// <summary>A line text with value <paramref name="index"/> replaced. Missing values before it become empty.</summary>
    public static string WithValue(string line, int index, string value)
    {
        var values = Values(line).ToList();
        while (values.Count <= index)
            values.Add("");
        values[index] = value.Trim();
        return string.Join(", ", values);
    }

    /// <summary>
    /// The line texts of the text of a field, checked against the schema of the key. Each line gets its values
    /// separated by ", ". Empty text lines do not count.
    /// </summary>
    /// <param name="key">The schema of the key, or null for a key that the schema does not have.</param>
    public static FieldParse Parse(KeySchema? key, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Select(l => string.Join(", ", Values(l)))
            .ToList();
        if (key is { Multiline: false } && lines.Count > 1)
            return new FieldParse(null, $"'{key.Name}' takes one line. The game reads only the last line.");
        foreach (var line in lines)
        {
            if (key != null && LineError(key, Values(line)) is { } error)
                return new FieldParse(null, error);
        }
        return new FieldParse(lines, null);
    }

    /// <summary>Why the engine cannot read a line of a key, or null.</summary>
    private static string? LineError(KeySchema key, string[] values)
    {
        var type = key.ClrType;
        // The engine reads the one value 0 as the zero vector.
        if (type == typeof(Vector3) && values is ["0"])
            return null;
        if (values.Length < key.MinComponents)
            return $"A '{key.Name}' line needs {key.MinComponents} values, not {values.Length}.";
        if (type == null)
            return null;
        var (reads, most, kind) = Rule(type);
        if (values.Length > most)
            return most == 1
                ? $"A '{key.Name}' line has one value. Do not use ',' in a number."
                : $"A '{key.Name}' line has at most {most} values, not {values.Length}.";
        foreach (var value in values.Take(most))
        {
            if (!reads(value))
                return $"'{value}' is not {kind}. The '{key.Name}' line needs {kind}.";
        }
        return null;
    }

    /// <summary>How the engine reads the values of a line of a type: the test for one value, the most values that the
    /// line can have, and a description for the messages.</summary>
    private static (Func<string, bool> Reads, int Most, string Kind) Rule(Type type)
    {
        if (type.IsArray)
            return Rule(type.GetElementType()!) with { Most = int.MaxValue };
        if (type.IsEnum)
            return (v => Enum.TryParse(type, v, true, out _), 1, $"one of {string.Join(", ", Enum.GetNames(type))}");
        if (type == typeof(bool))
            return (v => bool.TryParse(v, out _), 1, "true or false");
        if (type == typeof(int))
            return (IsInt32, 1, "a whole number");
        if (type == typeof(long))
            return (v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _), 1, "a whole number");
        if (type == typeof(ValueRange<int>))
            return (IsInt32, 2, "a whole number");
        if (type == typeof(string))
            return (_ => true, 1, "text");
        var floats = type == typeof(float) ? 1
            : type == typeof(Vector2) || type == typeof(ValueRange<float>) ? 2
            : type == typeof(Vector3) || type == typeof(Color3f) ? 3
            : type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color4) ? 4
            : 0;
        return floats > 0 ? (IsFloat, floats, "a number") : (_ => true, int.MaxValue, "");
    }

    /// <summary>True for an Int32, or for a hash in its unsigned form, which a patch line also holds.</summary>
    private static bool IsInt32(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static bool IsFloat(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number);
}

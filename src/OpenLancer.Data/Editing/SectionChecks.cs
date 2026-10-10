using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The fast checks of one section against its schema: the problems that the engine finds when it reads the section.
/// They are fast enough to run on the UI thread after each edit.
/// </summary>
/// <remarks>
/// <para>The checks follow the generated parsers of upstream Librelancer. A number must have the strict form that the
/// engine reads from a binary file. The engine reads some other forms from a text file, for example <c>5x</c> as the
/// integer 5, but the checks report them.</para>
/// <para>W keeps the text placeholders of the active mod, for example <c>ids_name = $str:li_gun01</c>. The merge puts
/// the id of the text in place of a placeholder, so the engine reads a placeholder in a key that holds a text id. A
/// placeholder of the other kind of text is a warning, because then the engine can show no text.</para>
/// </remarks>
internal static class SectionChecks
{
    public static List<Diagnostic> Check(SectionSchema schema, Section section, SectionRef target)
    {
        var problems = new List<Diagnostic>();
        void Report(DiagnosticSeverity severity, string message) =>
            problems.Add(new Diagnostic(severity, target.Path, $"{target.Id}: {message}") { Target = target });

        foreach (var lines in section.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = schema.Key(lines.Key);
            if (key == null)
            {
                if (!schema.ReadsAllLines)
                    Report(DiagnosticSeverity.Warning, $"the engine does not know the key '{lines.Key}'. It ignores the line.");
                continue;
            }
            var count = lines.Count();
            if (!key.Multiline && count > 1)
                Report(DiagnosticSeverity.Warning, $"'{key.Name}' has {count} lines. The engine reads only the last line.");
            foreach (var line in lines)
            {
                if (Placeholder(key, line) is { } placeholder)
                {
                    if (placeholder.Kind != key.TextKind)
                        Report(DiagnosticSeverity.Warning, $"'{key.Name}' holds the id of {Describe(key.TextKind!.Value)}, " +
                                                           $"but '{placeholder}' is {Describe(placeholder.Kind)}. The engine " +
                                                           "can show no text for it.");
                }
                else if (Message(key, line) is { } message)
                    Report(DiagnosticSeverity.Error, message);
            }
        }
        foreach (var key in schema.Keys.Where(k => k.Required))
        {
            if (!section.Any(e => e.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase)))
                Report(DiagnosticSeverity.Error,
                    $"the key '{key.Name}' is missing. The engine does not load the section without it.");
        }
        return problems;
    }

    /// <summary>The text placeholder in a line of a key that holds a text id, or null.</summary>
    private static StringPlaceholder? Placeholder(KeySchema key, Entry line) =>
        key.TextKind != null && line.Count > 0 && line[0] is StringValue &&
        StringPlaceholder.TryParse(line[0].ToString(), out var placeholder)
            ? placeholder
            : null;

    private static string Describe(StringKind kind) => kind == StringKind.String ? "a string" : "an infocard";

    /// <summary>Why the engine cannot read a line of a key, or null.</summary>
    private static string? Message(KeySchema key, Entry line)
    {
        // The engine reads the one value 0 as the zero vector.
        if (key.ClrType == typeof(Vector3) && line.Count == 1 && IsSingle(line[0]) && line[0].ToSingle() == 0)
            return null;
        if (line.Count < key.MinComponents)
            return $"a '{key.Name}' line needs {key.MinComponents} values or more, not {line.Count}. " +
                   "The engine ignores the line.";
        if (key.ClrType == null)
            return null;
        var (reads, components, kind) = Rule(key.ClrType);
        for (var i = 0; i < Math.Min(line.Count, components); i++)
        {
            if (!reads(line[i]))
                return $"the engine cannot read '{line[i]}' in a '{key.Name}' line as {kind}.";
        }
        return null;
    }

    /// <summary>How the engine reads the values of a line of a type: the test for one value, the number of values
    /// that it reads, and a description for messages.</summary>
    private static (Func<ValueBase, bool> Reads, int Components, string Kind) Rule(Type type)
    {
        if (type.IsArray)
            return Rule(type.GetElementType()!) with { Components = int.MaxValue };
        if (type.IsEnum)
            return (v => Enum.TryParse(type, v.ToString(), true, out _), 1,
                $"one of {string.Join(", ", Enum.GetNames(type))}");
        if (type == typeof(int))
            return (IsInt32, 1, "an integer");
        if (type == typeof(long))
            return (IsInt64, 1, "an integer");
        if (type == typeof(ValueRange<int>))
            return (IsInt32, 2, "an integer");
        if (type == typeof(Guid))
            return (v => Guid.TryParse(v.ToString(), out _), 1, "a GUID");
        var floats = type == typeof(float) ? 1
            : type == typeof(Vector2) || type == typeof(ValueRange<float>) ? 2
            : type == typeof(Vector3) || type == typeof(Color3f) ? 3
            : type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color4) ? 4
            : 0;
        // A string, a bool and a hash read every value.
        return floats > 0 ? (IsSingle, floats, "a number") : (_ => true, 0, "");
    }

    /// <summary>
    /// True when the engine reads the value as an Int32 from a binary file. Only a string can fail. The test does not
    /// call <see cref="ValueBase.TryToInt32"/>, because a string of a text file also reads <c>5x</c> as 5.
    /// </summary>
    private static bool IsInt32(ValueBase value) =>
        value is not StringValue ||
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
        uint.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    /// <summary>True when the engine reads the value as an Int64 from a binary file. Only a string can fail.</summary>
    private static bool IsInt64(ValueBase value) =>
        value is not StringValue || long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// True when the engine reads the value as a float. Only a string can fail. The test does not call
    /// <see cref="ValueBase.TryToSingle"/>, because it logs the failure of a string through FLLog.
    /// </summary>
    private static bool IsSingle(ValueBase value) =>
        value is not StringValue ||
        !string.IsNullOrWhiteSpace(value.ToString()) &&
        float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}

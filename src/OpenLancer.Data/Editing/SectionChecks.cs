using System;
using System.Collections.Concurrent;
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
/// They are fast enough to run on the UI thread after each edit. The class also makes the copy of a section that the
/// generated parser reads without a problem (see <see cref="ParserCopy"/>).
/// </summary>
/// <remarks>
/// <para>The checks follow the generated parsers of upstream Librelancer. A float must have the strict form that the
/// engine reads from a binary file. The engine reads some other forms from a text file, for example <c>5x</c> as 5,
/// but the checks report them as errors.</para>
/// <para>The engine reads each string as an integer and logs nothing. From a binary file it reads -1, and from a text
/// file it reads the number at the start of the text, or 0. So the engine loads the line, and the checks report a
/// string in an integer key as a warning that names the integer that the engine reads. Retail has such strings, for
/// example <c>lane_id = br01_2</c> in each trade lane zone.</para>
/// <para>W keeps the text placeholders of the active mod, for example <c>ids_name = $str:li_gun01</c>. The merge puts
/// the id of the text in place of a placeholder, so the engine reads a placeholder in a key that holds a text id. A
/// placeholder of the other kind of text is a warning, because then the engine can show no text.</para>
/// </remarks>
internal static class SectionChecks
{
    /// <param name="binary">True when the file of the section is binary (BINI).</param>
    public static List<Diagnostic> Check(SectionSchema schema, Section section, SectionRef target, bool binary)
    {
        var problems = new List<Diagnostic>();
        void Report(DiagnosticSeverity severity, string key, string message) =>
            problems.Add(new Diagnostic(severity, target.Path, $"{target.Id}: {message}") { Target = target, Key = key });

        foreach (var lines in section.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = schema.Key(lines.Key);
            if (key == null)
            {
                if (!schema.ReadsAllLines)
                    Report(DiagnosticSeverity.Warning, lines.Key,
                        $"the engine does not know the key '{lines.Key}'. It ignores the line.");
                continue;
            }
            var count = lines.Count();
            if (!key.Multiline && count > 1)
                Report(DiagnosticSeverity.Warning, key.Name,
                    $"'{key.Name}' has {count} lines. The engine reads only the last line.");
            foreach (var line in lines)
            {
                if (Placeholder(key, line) is { } placeholder)
                {
                    if (placeholder.Kind != key.TextKind)
                        Report(DiagnosticSeverity.Warning, key.Name,
                            $"'{key.Name}' holds the id of {Describe(key.TextKind!.Value)}, but '{placeholder}' is " +
                            $"{Describe(placeholder.Kind)}. The engine can show no text for it.");
                }
                else if (Problem(key, line, binary) is { } problem)
                    Report(problem.Severity, key.Name, problem.Message);
            }
        }
        foreach (var key in schema.Keys.Where(k => k.Required))
        {
            if (!section.Any(e => e.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase)))
                Report(DiagnosticSeverity.Error, key.Name,
                    $"the key '{key.Name}' is missing. The engine does not load the section without it.");
        }
        return problems;
    }

    /// <summary>
    /// A copy of a section that the generated parser reads with no log line, and with the values that the engine
    /// reads from the section:
    /// <list type="bullet">
    /// <item>A text placeholder is 0. The merge puts the id of the text there, but an unsaved text has no id.</item>
    /// <item>A line of a key that the schema does not know is not in the copy, unless the class reads all lines.</item>
    /// <item>A key that the engine reads once keeps only its last line, because the engine reads only that line.</item>
    /// <item>A line of a key with a type keeps only the values that the type reads.</item>
    /// <item>A string has the form that the engine reads from the file (see <see cref="EngineValue"/>).</item>
    /// </list>
    /// </summary>
    /// <remarks>Use the copy only when <see cref="Check"/> gives no error for the section. Each other problem that makes
    /// the parser log is an error of the checks.</remarks>
    /// <param name="binary">True when the file of the section is binary (BINI).</param>
    public static Section ParserCopy(SectionSchema schema, Section section, bool binary)
    {
        var last = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in section)
        {
            if (schema.Key(entry.Name) is { Multiline: false })
                last[entry.Name] = entry;
        }
        var copy = new Section(section.Name, section.Count) { File = section.File, Line = section.Line };
        foreach (var entry in section)
        {
            var key = schema.Key(entry.Name);
            if (key == null ? !schema.ReadsAllLines : !key.Multiline && !ReferenceEquals(last[entry.Name], entry))
                continue;
            var count = key?.ClrType is { } type ? Math.Min(entry.Count, Rule(type).Components) : entry.Count;
            var line = new Entry(copy, entry.Name, count) { Line = entry.Line };
            for (var i = 0; i < count; i++)
            {
                var value = entry[i];
                line.Add(StringPlaceholder.TryParse(value, out _)
                    ? new Int32Value(0) { Entry = line, Line = value.Line }
                    : EngineValue(value, binary, line));
            }
            copy.Add(line);
        }
        return copy;
    }

    /// <summary>
    /// A value as the engine reads it from a file of the form. A binary file stores a string as text, so the engine
    /// reads a plain string. A text file gives the string of the text parser (upstream <c>LancerStringValue</c>), which
    /// reads a number in more forms, for example <c>5x</c> as 5. W can hold the other kind: the line of a patch of a
    /// binary file holds strings of the text parser, and the editor makes plain strings.
    /// </summary>
    /// <param name="entry">The entry that gets a new value.</param>
    private static ValueBase EngineValue(ValueBase value, bool binary, Entry entry) => value switch
    {
        LancerStringValue when binary => new StringValue(value.ToString()) { Entry = entry, Line = value.Line },
        StringValue and not LancerStringValue when !binary =>
            new LancerStringValue(value.ToString()) { Entry = entry, Line = value.Line },
        _ => value,
    };

    /// <summary>The text placeholder in a line of a key that holds a text id, or null.</summary>
    private static StringPlaceholder? Placeholder(KeySchema key, Entry line) =>
        key.TextKind != null && line.Count > 0 && line[0] is StringValue &&
        StringPlaceholder.TryParse(line[0].ToString(), out var placeholder)
            ? placeholder
            : null;

    private static string Describe(StringKind kind) => kind == StringKind.String ? "a string" : "an infocard";

    /// <summary>
    /// The problem of a line of a key, or null: an error when the engine cannot read the line, or a warning when the
    /// engine reads a string of the line as an integer that the text does not give.
    /// </summary>
    /// <param name="binary">True when the file of the line is binary (BINI).</param>
    private static (DiagnosticSeverity Severity, string Message)? Problem(KeySchema key, Entry line, bool binary)
    {
        // The engine reads the one value 0 as the zero vector.
        if (key.ClrType == typeof(Vector3) && line.Count == 1 && IsSingle(line[0]) && line[0].ToSingle() == 0)
            return null;
        if (line.Count < key.MinComponents)
            return (DiagnosticSeverity.Error, $"a '{key.Name}' line needs {key.MinComponents} values or more, " +
                                              $"not {line.Count}. The engine ignores the line.");
        if (key.ClrType == null)
            return null;
        var (reads, components, kind, integer) = Rule(key.ClrType);
        for (var i = 0; i < Math.Min(line.Count, components); i++)
        {
            if (reads(line[i]))
                continue;
            return integer != null
                ? (DiagnosticSeverity.Warning, $"the engine reads '{line[i]}' in a '{key.Name}' line as the integer " +
                                               $"{integer(EngineValue(line[i], binary, line))}.")
                : (DiagnosticSeverity.Error, $"the engine cannot read '{line[i]}' in a '{key.Name}' line as {kind}.");
        }
        return null;
    }

    /// <summary>
    /// How the engine reads the values of a line of a type: the test for one value, the number of values that it
    /// reads, and a description for messages. For an integer type, the rule also gives the integer that the engine
    /// reads from a value that the test refuses, as <see cref="EngineValue"/> gives it. The engine logs nothing for
    /// such a value.
    /// </summary>
    private static (Func<ValueBase, bool> Reads, int Components, string Kind, Func<ValueBase, long>? Integer) Rule(Type type) =>
        Rules.GetOrAdd(type, MakeRule);

    /// <summary>The rule of each type, made at its first use. The checks run after each edit and for each section of a
    /// system at its catch-up, so the rule of a line is not made again.</summary>
    private static readonly ConcurrentDictionary<Type, (Func<ValueBase, bool> Reads, int Components, string Kind,
        Func<ValueBase, long>? Integer)> Rules = new();

    private static (Func<ValueBase, bool> Reads, int Components, string Kind, Func<ValueBase, long>? Integer) MakeRule(Type type)
    {
        if (type.IsArray)
            return Rule(type.GetElementType()!) with { Components = int.MaxValue };
        if (type.IsEnum)
            return (v => Enum.TryParse(type, v.ToString(), true, out _), 1,
                $"one of {string.Join(", ", Enum.GetNames(type))}", null);
        if (type == typeof(int))
            return (IsInt32, 1, "an integer", v => v.ToInt32());
        if (type == typeof(long))
            return (IsInt64, 1, "an integer", v => v.ToInt64());
        if (type == typeof(ValueRange<int>))
            return (IsInt32, 2, "an integer", v => v.ToInt32());
        if (type == typeof(Guid))
            return (v => Guid.TryParse(v.ToString(), out _), 1, "a GUID", null);
        var floats = type == typeof(float) ? 1
            : type == typeof(Vector2) || type == typeof(ValueRange<float>) ? 2
            : type == typeof(Vector3) || type == typeof(Color3f) ? 3
            : type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color4) ? 4
            : 0;
        // A string, a bool and a hash read one value of any form.
        return floats > 0 ? (IsSingle, floats, "a number", null) : (_ => true, 1, "", null);
    }

    /// <summary>
    /// True when the value is an Int32 in the strict form of a binary file. Only a string can fail. The test does not
    /// call <see cref="ValueBase.TryToInt32"/>, because a string of a text file also reads <c>5x</c> as 5.
    /// </summary>
    private static bool IsInt32(ValueBase value) =>
        value is not StringValue ||
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
        uint.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    /// <summary>True when the value is an Int64 in the strict form of a binary file. Only a string can fail.</summary>
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

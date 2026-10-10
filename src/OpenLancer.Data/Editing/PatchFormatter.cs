using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>
/// Writes an entry as a patch line, <c>name = v1, v2</c>. Each value gets a text that the merge reads back as the
/// same value.
/// </summary>
/// <remarks>
/// <para>The merge reads a patch with the engine's text parser. So these are the value text rules:</para>
/// <list type="bullet">
/// <item>An Int32 is an invariant decimal. For a binary file it is always signed, because the file stores only the
/// 32 bits. A hash such as <c>2314954114</c> is then <c>-1980013182</c>. The merge reads the signed form as the
/// same bits, also in a new line where no replaced value gives the type. For a text file, an unsigned Int32
/// writes its exact number.</item>
/// <item>A float is the shortest text that gives the same float. The text gets <c>.0</c> when it has no <c>.</c>
/// and no exponent, because the parser reads <c>100</c> as an Int32. So negative zero is <c>-0.0</c>. For a text
/// file, a float that holds a large integer writes the exact integer.</item>
/// <item>A boolean is <c>true</c> or <c>false</c>.</item>
/// <item>A string stays as it is. It must be ASCII, with no <c>,</c>, <c>;</c> or control character, and with no
/// white space at an end. An empty string cannot be the only value of a line. Retail files have no control
/// characters, and a binary file ends a string at its first NUL.</item>
/// <item>A line without values is <c>name =</c>.</item>
/// <item>A line of a binary file holds at most <see cref="BinaryValueTypes.MaxValuesPerLine"/> values.</item>
/// </list>
/// <para>Then the formatter parses the line again. For a binary file, it applies
/// <see cref="BinaryValueTypes.Store"/> with the replaced line, as the merge does. Each value must read back
/// engine-equal to the entry value (see <see cref="IniValueComparer"/>). A value that the merge cannot store is a
/// refusal. So in a new line of a binary file, the bool string <c>TRUE</c> is a refusal, because the merge stores
/// the string <c>true</c> there.</para>
/// </remarks>
internal static class PatchFormatter
{
    /// <param name="entry">The entry to write.</param>
    /// <param name="replaced">The target line that the patch line replaces, or null for a new line. The merge finds
    /// it by occurrence: the n-th patch line of a name replaces the n-th target line of that name.</param>
    /// <param name="binary">True when the target file is a binary (BINI) file.</param>
    /// <param name="line">The patch line.</param>
    /// <param name="error">Why a patch line cannot hold the entry.</param>
    public static bool TryFormatLine(Entry entry, Entry? replaced, bool binary,
        [NotNullWhen(true)] out string? line, [NotNullWhen(false)] out string? error)
    {
        line = null;
        error = NameError(entry.Name);
        if (error != null)
            return false;
        if (binary && entry.Count > BinaryValueTypes.MaxValuesPerLine)
        {
            error = $"'{entry.Name}': a line of a binary file holds at most {BinaryValueTypes.MaxValuesPerLine} values, " +
                    $"not {entry.Count}.";
            return false;
        }

        var texts = new string[entry.Count];
        for (var i = 0; i < entry.Count; i++)
        {
            error = ValueError(entry[i], binary, out texts[i]);
            if (error == null && entry.Count == 1 && texts[i].Length == 0)
                error = "an empty text cannot be the only value of a line.";
            if (error != null)
            {
                error = $"'{entry.Name}': {error}";
                return false;
            }
        }

        var text = texts.Length == 0 ? $"{entry.Name} =" : $"{entry.Name} = {string.Join(", ", texts)}";
        error = ReadBackError(IniDocument.ParseTextLine(text), entry, replaced, text, binary);
        if (error != null)
            return false;
        line = text;
        return true;
    }

    /// <summary>
    /// The values that a patch line with the value text <paramref name="values"/> gives through the merge. The
    /// engine's text parser reads the text, as it reads a patch. For a binary file, each value is then stored as the
    /// merge stores it (see <see cref="BinaryValueTypes.Store"/>). <see cref="TryFormatLine"/> must also accept the
    /// line, so a save can write it.
    /// </summary>
    /// <param name="values">The values as text, for example <c>1, 2.5, li_gun01</c>. Empty text gives a line without
    /// values.</param>
    /// <param name="replaced">The target line that the patch line replaces, or null for a new line.</param>
    /// <param name="binary">True when the target file is a binary (BINI) file.</param>
    public static bool TryReadLine(string name, string values, Entry? replaced, bool binary,
        [NotNullWhen(true)] out IReadOnlyList<ValueBase>? line, [NotNullWhen(false)] out string? error)
    {
        line = null;
        error = NameError(name);
        if (error != null)
            return false;
        // The parser ends a line at ';' and at a line break. So such a value would lose its end without a message.
        if (!Ascii.IsValid(values) || values.IndexOf(';') >= 0 || values.Any(char.IsControl))
        {
            error = $"'{name} = {values}': a value must be ASCII text with no ';' and no control character such as a " +
                    "line break.";
            return false;
        }
        var parsed = IniDocument.ParseTextLine($"{name} = {values}");
        if (parsed == null)
        {
            error = $"'{name} = {values}' is not one line.";
            return false;
        }
        var entry = new Entry(new Section("Line"), name, parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var stored = binary ? BinaryValueTypes.Store(parsed, i, replaced, entry) : parsed[i];
            if (stored == null)
            {
                error = $"'{name}': a binary file cannot store value {i + 1}, {parsed[i]}, exactly. Write a hash in " +
                        "its signed form.";
                return false;
            }
            entry.Add(stored);
        }
        if (!TryFormatLine(entry, replaced, binary, out _, out error))
            return false;
        line = entry.ToList();
        return true;
    }

    /// <summary>
    /// True when a line of a loaded patch gives the entry through the merge, with the same rules as a line that
    /// <see cref="TryFormatLine"/> writes. So the writer can write the line again with its own text.
    /// </summary>
    /// <param name="parsed">The line as the engine's text parser reads it.</param>
    /// <param name="entry">The entry that the line must give.</param>
    /// <param name="replaced">The target line that the line replaces, or null for a new line.</param>
    /// <param name="binary">True when the target file is a binary (BINI) file.</param>
    public static bool ReadsBack(Entry parsed, Entry entry, Entry? replaced, bool binary) =>
        parsed.Name == entry.Name && !parsed.Name.StartsWith(IniPatcher.DirectivePrefix, StringComparison.OrdinalIgnoreCase) &&
        ReadBackError(parsed, entry, replaced, parsed.Name, binary) == null;

    /// <summary>Writes a section header, <c>[name]</c>.</summary>
    public static bool TryFormatHeader(string name,
        [NotNullWhen(true)] out string? line, [NotNullWhen(false)] out string? error)
    {
        line = null;
        error = null;
        if (name.Length == 0 || !Ascii.IsValid(name) || name.IndexOfAny(['[', ']']) >= 0 || name.Any(char.IsControl) ||
            char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
        {
            error = $"'{name}': a section name must be ASCII text with no '[', ']' or control character, and with no " +
                    "white space at an end.";
            return false;
        }
        line = $"[{name}]";
        return true;
    }

    /// <summary>
    /// Writes a directive line, for example <c>ol.remove = name1, name2</c>. The merge reads each text as a value and
    /// compares the text of that value without letter case.
    /// </summary>
    /// <param name="name">The directive, for example <c>ol.remove</c>.</param>
    /// <param name="texts">The texts, for example entry names or a key.</param>
    public static bool TryFormatDirective(string name, IReadOnlyList<string> texts,
        [NotNullWhen(true)] out string? line, [NotNullWhen(false)] out string? error)
    {
        line = null;
        foreach (var text in texts)
        {
            error = text.Length == 0 ? "a directive value cannot be empty." : ValueError(new StringValue(text), false, out _);
            if (error != null)
            {
                error = $"'{name}': {error}";
                return false;
            }
        }
        var result = $"{name} = {string.Join(", ", texts)}";
        var parsed = IniDocument.ParseTextLine(result);
        if (parsed == null || parsed.Count != texts.Count ||
            parsed.Where((value, i) => !value.ToString().Equals(texts[i], StringComparison.OrdinalIgnoreCase)).Any())
        {
            error = $"'{result}': the merge does not read back the same text.";
            return false;
        }
        line = result;
        error = null;
        return true;
    }

    /// <summary>Why a key cannot be the value of a key line, for example <c>nickname = li_gun01</c>, or null.</summary>
    /// <param name="binary">True when the target file is a binary (BINI) file.</param>
    public static string? KeyError(string key, bool binary)
    {
        var entry = new Entry(new Section("Item"), SectionKeys.DefaultKey) { new StringValue(key) };
        return TryFormatLine(entry, null, binary, out _, out var error) ? null : error;
    }

    /// <returns>Null, or why an entry name cannot be in a patch.</returns>
    public static string? NameError(string name)
    {
        if (name.Length == 0)
            return "An entry name cannot be empty.";
        if (!Ascii.IsValid(name))
            return $"'{name}': an entry name must be ASCII.";
        if (name.StartsWith(IniPatcher.DirectivePrefix, StringComparison.OrdinalIgnoreCase) || name[0] is ';' or '@' or '[')
            return $"'{name}': an entry name cannot start with '{IniPatcher.DirectivePrefix}', ';', '@' or '['.";
        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
            return $"'{name}': an entry name cannot start or end with white space.";
        if (name.IndexOfAny(['=', ';']) >= 0 || name.Any(char.IsControl))
            return $"'{name}': an entry name cannot contain '=', ';' or a control character such as a line break.";
        return null;
    }

    /// <summary>Gives the text of a value.</summary>
    /// <returns>Null, or why a patch line cannot hold the value.</returns>
    private static string? ValueError(ValueBase value, bool binary, out string text)
    {
        switch (value)
        {
            case BooleanValue boolean:
                text = (bool)boolean ? "true" : "false";
                return null;
            case Int32Value integer:
                text = binary ? ((int)integer).ToString(CultureInfo.InvariantCulture) : integer.ToString();
                return null;
            case SingleValue single when !binary && BinaryValueTypes.IsLargeInteger(single, out var large):
                text = large.ToString(CultureInfo.InvariantCulture);
                return null;
            case SingleValue single:
                var number = (float)single;
                text = number.ToString("R", CultureInfo.InvariantCulture);
                if (!float.IsFinite(number))
                    return $"a patch line cannot hold the number {text}.";
                if (text.IndexOfAny(['.', 'E', 'e']) < 0)
                    text += ".0";
                return null;
            default:
                text = value.ToString();
                if (!Ascii.IsValid(text))
                    return $"the text '{text}' must be ASCII.";
                if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
                    return $"the text '{text}' cannot start or end with white space.";
                if (text.IndexOfAny([',', ';']) >= 0 || text.Any(char.IsControl))
                    return $"the text '{text}' cannot contain ',', ';' or a control character such as a line break.";
                return null;
        }
    }

    /// <summary>
    /// Applies the merge's type rules to a line that the engine's text parser read, and compares each value with
    /// the entry.
    /// </summary>
    /// <param name="parsed">The parsed line, or null when the text does not give exactly one entry.</param>
    /// <param name="line">The text of the line, for the message.</param>
    /// <returns>Null, or why the merge does not read back the entry from the line.</returns>
    private static string? ReadBackError(Entry? parsed, Entry entry, Entry? replaced, string line, bool binary)
    {
        if (parsed == null || parsed.Count != entry.Count)
            return $"'{line}': the merge does not read back {entry.Count} values.";
        for (var i = 0; i < entry.Count; i++)
        {
            var stored = binary ? BinaryValueTypes.Store(parsed, i, replaced, null) : parsed[i];
            if (stored == null)
                return $"'{line}': the merge cannot store value {i + 1} exactly in a binary file.";
            if (!IniValueComparer.EngineEqual(stored, entry[i], binary))
                return $"'{line}': the merge reads value {i + 1} as {Describe(stored)}, not as {Describe(entry[i])}.";
        }
        return null;
    }

    private static string Describe(ValueBase value) => value switch
    {
        BooleanValue => $"the boolean {value}",
        Int32Value => $"the Int32 {value}",
        SingleValue => $"the float {value}",
        _ => $"the string '{value}'",
    };
}

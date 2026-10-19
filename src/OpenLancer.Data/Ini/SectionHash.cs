using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Ini;

/// <summary>
/// The hash of the body of a section in its engine form (section 6.3 of the M6b design, decision D666). A patch with
/// <c>ol.replace</c> holds the hash of the section that it replaces (<c>ol.base</c>), so the merge finds a base that
/// changed after the edit.
/// </summary>
/// <remarks>
/// <para>The engine form has one line for each entry, in order: the key in lowercase, then each value. A number gives
/// its value in invariant form, so an integer and a float with the same value give the same text. A boolean word gives
/// its value in lowercase, because the text parser reads <c>true</c> as a boolean and a binary file stores it as text.
/// Other text stays as written, with its letter case. So a binary section and the same section as text give the same
/// hash.</para>
/// <para>The hash is the first 8 bytes of the SHA-256 of the engine form, as 16 lowercase hex digits.</para>
/// </remarks>
internal static class SectionHash
{
    /// <summary>The number of hex digits of a hash.</summary>
    public const int Length = 16;

    public static string Of(Section section)
    {
        var text = new StringBuilder();
        foreach (var entry in section)
        {
            text.Append(entry.Name.ToLowerInvariant());
            foreach (var value in entry)
                text.Append('\u001f').Append(EngineText(value));
            text.Append('\n');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, Length / 2).ToLowerInvariant();
    }

    /// <summary>
    /// True when a section has the hash of an <c>ol.base</c> line (see <see cref="TextOf"/>). The text parser reads a
    /// hash with only decimal digits, or with the form of a float, as a number. So the hash of the section goes through
    /// the parser too, and the two texts compare without letter case.
    /// </summary>
    public static bool Matches(Section section, string patchText)
    {
        var hash = Of(section);
        var text = IniDocument.ParseTextLine($"{IniPatcher.DirectiveBase} = {hash}") is { Count: 1 } line
            ? TextOf(line[0])
            : hash;
        return string.Equals(text, patchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The text of the value of an <c>ol.base</c> line: the exact number of a large integer, else the
    /// text of the value.</summary>
    public static string TextOf(ValueBase value) =>
        BinaryValueTypes.IsLargeInteger(value, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : value.ToString() ?? "";

    /// <summary>The engine form of one value.</summary>
    private static string EngineText(ValueBase value)
    {
        switch (value)
        {
            case Int32Value:
                return value.ToInt32().ToString(CultureInfo.InvariantCulture);
            case SingleValue:
                // A binary file stores a hash above the integer range as the integer with the same 32 bits.
                if (BinaryValueTypes.IsLargeInteger(value, out var large))
                    return (BinaryValueTypes.IsUnsignedHash(large) ? unchecked((int)large) : large)
                        .ToString(CultureInfo.InvariantCulture);
                var number = (double)value.ToSingle();
                return number == Math.Floor(number) && Math.Abs(number) < 1e15
                    ? ((long)number).ToString(CultureInfo.InvariantCulture)
                    : number.ToString("R", CultureInfo.InvariantCulture);
            case BooleanValue:
                return value.ToBoolean() ? "true" : "false";
            default:
                var text = value.ToString() ?? "";
                return text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       text.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? text.ToLowerInvariant()
                    : text;
        }
    }
}

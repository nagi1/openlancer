using System.Globalization;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Export;

/// <summary>
/// The text of an INI value for a text file of the retail game (milestone M11). The retail getters read each form
/// that this text has as the value that a binary file holds.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The retail integer getter reads text with the C function <c>atoi</c>. It reads <c>1E+08</c> as 1. So a float
/// below 2^31 writes its decimal text without an exponent, for example <c>100000000</c>. A larger float can use the
/// exponent form, because no integer getter can read it in either form.</item>
/// <item>A large integer writes its exact number (see <see cref="BinaryValueTypes.IsLargeInteger"/>), for example a
/// hash.</item>
/// <item>A boolean writes <c>true</c> or <c>false</c>.</item>
/// <item>Each other value writes its own text.</item>
/// </list>
/// </remarks>
public static class RetailText
{
    /// <summary>2^31. Below this magnitude, a float writes no exponent.</summary>
    private const float ExponentLimit = 2_147_483_648f;

    public static string Of(ValueBase value) => value switch
    {
        _ when BinaryValueTypes.IsLargeInteger(value, out var number) => number.ToString(CultureInfo.InvariantCulture),
        SingleValue single => Float(single),
        BooleanValue truth => (bool)truth ? "true" : "false",
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// The text of a float. Below 2^31, a whole number writes its exact number, so that the integer getter reads the
    /// number that a binary file gives, also above 2^24. For example, the float of 1501900000 is 1501900032. Another
    /// float writes the shortest text that gives the same float, without an exponent below 2^31.
    /// </summary>
    public static string Float(float value)
    {
        if (float.Abs(value) >= ExponentLimit)
            return value.ToString("R", CultureInfo.InvariantCulture);
        return value == float.Truncate(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : Positional(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>The text of a number without its exponent, for example <c>0.000015</c> for <c>1.5E-05</c>.</summary>
    private static string Positional(string text)
    {
        var e = text.IndexOfAny(['E', 'e']);
        if (e < 0)
            return text;
        var exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = text[..e];
        var negative = mantissa.StartsWith('-');
        if (negative)
            mantissa = mantissa[1..];
        var dot = mantissa.IndexOf('.');
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var point = (dot < 0 ? mantissa.Length : dot) + exponent;
        var result = point <= 0 ? "0." + new string('0', -point) + digits
            : point >= digits.Length ? digits + new string('0', point - digits.Length)
            : digits[..point] + "." + digits[point..];
        return negative ? "-" + result : result;
    }
}

using System;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>
/// Compares two INI values as a file of one format stores them and as the engine reads them back.
/// </summary>
/// <remarks>
/// <para>A binary (BINI) file stores a boolean, the 32 bits of an Int32, a float or a string. So an unsigned Int32
/// reads back as the signed Int32 with the same bits. A float forgets the exact long of a large integer. Every
/// string reads back as a <see cref="StringValue"/>. The reader decodes it as ASCII and ends it at its first NUL.</para>
/// <para>A text file stores the text that <see cref="IniDocument.TextOf"/> gives. The engine's text parser reads
/// that text again. For example, the float 100 and the Int32 100 both store <c>100</c>.</para>
/// <para>Strict: the file stores the same bytes for both values. Engine-equal: the values that the engine reads
/// back give the same result for every accessor. The accessors are <c>ToString</c>, <c>TryToInt32</c>,
/// <c>TryToInt64</c>, <c>TryToBoolean</c> and <c>TryToSingle</c>, each with its success flag. So the values are
/// the same for every schema type. In a binary file, the string <c>true</c> and the boolean true are not
/// engine-equal, because <c>ToString</c> gives <c>True</c> for the boolean.</para>
/// </remarks>
internal static class IniValueComparer
{
    public static bool EngineEqual(ValueBase a, ValueBase b, bool binary)
    {
        if (binary)
            return SameAccessors(BinaryRead(a), BinaryRead(b));
        var textA = IniDocument.TextOf(a);
        var textB = IniDocument.TextOf(b);
        return textA == textB || (TextRead(textA) is { } readA && TextRead(textB) is { } readB && SameAccessors(readA, readB));
    }

    public static bool StrictEqual(ValueBase a, ValueBase b, bool binary)
    {
        if (!binary)
            return IniDocument.TextOf(a) == IniDocument.TextOf(b);
        return (a, b) switch
        {
            (BooleanValue x, BooleanValue y) => (bool)x == (bool)y,
            (Int32Value x, Int32Value y) => (int)x == (int)y,
            (SingleValue x, SingleValue y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
            (BooleanValue or Int32Value or SingleValue, _) or (_, BooleanValue or Int32Value or SingleValue) => false,
            _ => a.ToString() == b.ToString(),
        };
    }

    /// <summary>The value that the engine reads back from a binary file.</summary>
    private static ValueBase BinaryRead(ValueBase value) => value switch
    {
        BooleanValue => value,
        Int32Value integer => new Int32Value((int)integer),
        SingleValue single => new SingleValue((float)single, null),
        _ => new StringValue(BinaryText(value.ToString())),
    };

    /// <summary>
    /// The text that the engine reads back from the string block of a binary file. The writer stores UTF-8 and ends
    /// each string with a NUL. The reader decodes ASCII and ends the string at its first NUL.
    /// </summary>
    private static string BinaryText(string text)
    {
        var read = Encoding.ASCII.GetString(Encoding.UTF8.GetBytes(text));
        var end = read.IndexOf('\0');
        return end < 0 ? read : read[..end];
    }

    /// <summary>The value that the engine's text parser reads from one value text, or null when it is not one value.</summary>
    private static ValueBase? TextRead(string text) =>
        IniDocument.ParseTextLine("v = " + text) is { Count: 1 } entry ? entry[0] : null;

    /// <remarks>
    /// Two strings are equal when their text is equal, because their accessors read only the text. The other
    /// accessors run before <c>TryToSingle</c>. So <c>TryToSingle</c> runs on a string only when the string has the
    /// text of a number, and it does not log a parse error.
    /// </remarks>
    private static bool SameAccessors(ValueBase a, ValueBase b)
    {
        if (a is StringValue && b is StringValue)
            return a.ToString() == b.ToString();
        return a.ToString() == b.ToString() &&
               a.TryToInt32(out var intA) == b.TryToInt32(out var intB) && intA == intB &&
               a.TryToInt64(out var longA) == b.TryToInt64(out var longB) && longA == longB &&
               a.TryToBoolean(out var boolA) == b.TryToBoolean(out var boolB) && boolA == boolB &&
               a.TryToSingle(out var floatA) == b.TryToSingle(out var floatB) && floatA.Equals(floatB);
    }
}

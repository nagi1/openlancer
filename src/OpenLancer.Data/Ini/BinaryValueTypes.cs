using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Ini;

/// <summary>
/// The type rules for patch values that the merge copies into a binary (BINI) INI file.
/// </summary>
/// <remarks>
/// A binary value is a boolean, an Int32, a float or a string. A patch is text, so the text parser gives each patch
/// value a type. Where that type can differ from the value in the file, the type of the replaced value decides.
/// Without a replaced value, the retail form decides.
/// </remarks>
internal static class BinaryValueTypes
{
    /// <summary>2^24. A float holds every integer from -2^24 to 2^24 exactly.</summary>
    public const int MaxExactFloatInteger = 16_777_216;

    /// <summary>A binary file stores the value count of a line in one byte.</summary>
    public const int MaxValuesPerLine = byte.MaxValue;

    /// <summary>2^63. The smallest float that a long cannot hold.</summary>
    private const float MinFloatAboveLong = 9_223_372_036_854_775_808f;

    /// <summary>
    /// True for a text value such as <c>4000000000</c>: an integer outside the Int32 range. The text parser keeps
    /// it as a float plus the exact number, but a binary INI value is either an Int32 or a float.
    /// </summary>
    public static bool IsLargeInteger(ValueBase value, out long number) =>
        value.TryToInt64(out number) && value is SingleValue && number is > int.MaxValue or < int.MinValue;

    /// <summary>True when <paramref name="number"/> is in the hash range 2147483648 to 4294967295.</summary>
    public static bool IsUnsignedHash(long number) => number is > int.MaxValue and <= uint.MaxValue;

    /// <summary>True when the float holds exactly the integer <paramref name="number"/>.</summary>
    public static bool HoldsExactly(float single, long number) => single < MinFloatAboveLong && (long)single == number;

    /// <summary>
    /// Returns the value that a binary file stores for value <paramref name="index"/> of a patch line.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A large integer (see <see cref="IsLargeInteger"/>) that replaces an Int32 is a hash. In the range of
    /// <see cref="IsUnsignedHash"/>, it becomes an unsigned Int32 with the same 32 bits. A large integer that
    /// replaces a float stays a float.</item>
    /// <item>A boolean that replaces a string stays a string. Retail files store all booleans as strings. When the
    /// old text is <c>true</c> or <c>false</c> in any letter case and the truth value does not change, the old text
    /// stays, for example <c>TRUE</c>. Other old text, for example <c>yes</c>, becomes <c>true</c> or
    /// <c>false</c>.</item>
    /// <item>A boolean that replaces no value becomes the string <c>true</c> or <c>false</c>.</item>
    /// <item>A boolean cannot replace an Int32 or a float (milestone M11). The retail game reads the text <c>true</c>
    /// as 0 in a number key, and a binary file has no boolean type.</item>
    /// <item>An Int32 that replaces a float becomes a float when the float holds it exactly
    /// (<see cref="MaxExactFloatInteger"/>). A larger Int32, for example a hash in a float column, stays an Int32.</item>
    /// </list>
    /// All other values stay as they are.
    /// </remarks>
    /// <param name="line">The patch line, as the text parser reads it.</param>
    /// <param name="replaced">The target line that the patch line replaces, or null for a new line. Value
    /// <paramref name="index"/> replaces the value at the same position.</param>
    /// <param name="copy">The entry that gets the value, or null.</param>
    /// <returns>The value to store, or null when the file cannot store the value exactly. That is a large integer
    /// outside the hash range that replaces an Int32, a large integer that replaces no Int32 and no float, or a boolean
    /// that replaces a number.</returns>
    public static ValueBase? Store(Entry line, int index, Entry? replaced, Entry? copy)
    {
        var value = line[index];
        var old = replaced != null && index < replaced.Count ? replaced[index] : null;
        if (IsLargeInteger(value, out var number))
        {
            return old switch
            {
                Int32Value when IsUnsignedHash(number) =>
                    new Int32Value(unchecked((int)number), unsigned: true) { Entry = copy, Line = value.Line },
                SingleValue => value,
                _ => null,
            };
        }
        switch (value, old)
        {
            case (BooleanValue truth, StringValue text):
                var keep = bool.TryParse(text.ToString(), out var oldTruth) && oldTruth == (bool)truth;
                return keep ? new StringValue(text.ToString()) { Entry = copy, Line = value.Line } : Text(truth, copy);
            case (BooleanValue truth, null):
                return Text(truth, copy);
            case (BooleanValue, Int32Value or SingleValue):
                return null;
            case (Int32Value integer, SingleValue) when (int)integer is >= -MaxExactFloatInteger and <= MaxExactFloatInteger:
                return new SingleValue((int)integer, null) { Entry = copy, Line = value.Line };
            default:
                return value;
        }
    }

    /// <summary>The error of a boolean in place of a number (decision D333). The text writes the key of the line.</summary>
    public static string BooleanInNumber(string key) => $"{key}: write 1 or 0. The game reads true as 0 in a number key.";

    private static StringValue Text(BooleanValue truth, Entry? copy) =>
        new((bool)truth ? "true" : "false") { Entry = copy, Line = truth.Line };
}

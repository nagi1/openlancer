using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Editor.Editing;

/// <summary>How a label drag of the Inspector changes a value: its step and its range (section 6.4 of the M20
/// design).</summary>
internal enum DragKind
{
    /// <summary>A position: steps of 10, no range.</summary>
    Position,

    /// <summary>An angle in degrees: steps of 0.5, wrapped and rounded as a gizmo turn writes it.</summary>
    Rotation,

    /// <summary>A size of a zone: steps of 10, and a changed value is 1 or more.</summary>
    Size,

    /// <summary>Another vector, for example a direction or a ratio: the step comes from the largest value of the
    /// line.</summary>
    Scaled,

    /// <summary>The coefficients of an attenuation: the step comes from the dragged value, because the values of one
    /// line have very different sizes.</summary>
    Coefficient,
}

/// <summary>
/// The pure rules of a label drag: the kind of a key, the step of one unit, the value after a change, and the text of
/// the numbers. The values are exact: a change adds to the decimal value of the shortest text of the start, so a drag
/// from 0.3 by 0.1 writes 0.4.
/// </summary>
internal static class FieldDrag
{
    // The step of one unit is 10^(e - 2) for a value from 10^e to 10^(e+1), inside these exponents.
    private const int ScaledMinimum = -6;
    private const int CoefficientMinimum = -9;
    private const int StepMaximum = 4;
    private const int StepOffset = 2;

    /// <summary>The step of a line of zeros.</summary>
    private const decimal ZeroStep = 0.01m;

    /// <summary>The kind of the label drag of a bound key, or null when the key gets no label drag. Only a one-line
    /// <see cref="Vector3"/> key gets one, so the <see cref="Vector2"/> <c>pos</c> of a <c>[system]</c> does
    /// not.</summary>
    /// <param name="sectionType">The type of the section, for example <c>Zone</c>.</param>
    public static DragKind? KindOf(KeySchema? key, string sectionType)
    {
        if (key is not { Multiline: false } || key.ClrType != typeof(Vector3))
            return null;
        if (Is(key.Name, "pos"))
            return DragKind.Position;
        if (Is(key.Name, "rotate"))
            return DragKind.Rotation;
        if (Is(key.Name, "size") && Is(sectionType, "Zone"))
            return DragKind.Size;
        if (Is(key.Name, "attenuation") || Is(key.Name, "emit_attenuation"))
            return DragKind.Coefficient;
        return DragKind.Scaled;
    }

    /// <summary>The kind of the label drag of a cutscene row, or null when the row gets no label drag. A quaternion
    /// gets none, because one of its values has no meaning alone.</summary>
    /// <param name="path">The path of the value, for example <c>lightprops.atten</c>.</param>
    public static DragKind? KindOf(ThnKeyKind kind, string path) => kind switch
    {
        ThnKeyKind.Orient => DragKind.Rotation,
        ThnKeyKind.Vector when path.EndsWith("lightprops.atten", StringComparison.Ordinal) => DragKind.Coefficient,
        ThnKeyKind.Vector => DragKind.Scaled,
        _ => null,
    };

    /// <summary>The change of value <paramref name="index"/> for one unit of the drag, from the numbers at the start
    /// of the drag. The step stays the same for the whole drag.</summary>
    public static decimal BaseStep(DragKind kind, IReadOnlyList<float> start, int index) => kind switch
    {
        DragKind.Position or DragKind.Size => 10m,
        DragKind.Rotation => 0.5m,
        DragKind.Coefficient when start[index] != 0 => Step(MathF.Abs(start[index]), CoefficientMinimum),
        _ => ScaledStep(start),
    };

    /// <summary>
    /// The value after a change of <paramref name="delta"/> from <paramref name="start"/>. A change of 0 gives the
    /// start bit for bit. Other changes add to the exact text of the start, then apply the range of the kind, and the
    /// result is the float of the exact sum. The result is always a finite float.
    /// </summary>
    public static float Value(DragKind kind, float start, decimal delta)
    {
        if (delta == 0)
            return start;
        float result;
        if (Exact(start) is { } exact && TryAdd(exact, delta, out var sum))
        {
            result = kind switch
            {
                DragKind.Size => Parse(Math.Max(sum, 1m)),
                DragKind.Rotation => TransformValues.Angle((double)sum),
                _ => Parse(sum),
            };
        }
        else
        {
            // A start outside the range of a decimal, or so small that a decimal reads it as 0.
            var near = (double)start + (double)delta;
            result = kind switch
            {
                DragKind.Size => (float)Math.Max(near, 1),
                DragKind.Rotation => TransformValues.Angle(near),
                _ => (float)near,
            };
        }

        return Math.Clamp(result, -float.MaxValue, float.MaxValue);
    }

    /// <summary>The exact value of the text of a float: the shortest text that gives the float back. Null when that
    /// text is not a decimal, or when a decimal reads it as 0 although the float is not 0.</summary>
    public static decimal? Exact(float value) =>
        decimal.TryParse(Number(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var exact) &&
        (exact != 0 || value == 0)
            ? exact
            : null;

    /// <summary>
    /// The first <paramref name="count"/> values of a line as finite numbers (see <see cref="TransformValues.TryNumber"/>).
    /// Returns null, or the reason that a label drag or a color picker cannot write them.
    /// </summary>
    public static string? NumbersOf(IReadOnlyList<ValueBase> values, int count, out float[] numbers)
    {
        numbers = new float[count];
        if (values.Count < count)
            return $"The line has fewer than {count} values.";
        for (var i = 0; i < count; i++)
        {
            if (!TransformValues.TryNumber(values[i], out numbers[i]))
                return $"Value {i + 1} is not a number. Correct it in the field first.";
        }

        return null;
    }

    /// <summary>The text of numbers as a line of a field: the shortest text of each float, with <c>, </c> between
    /// them, for example <c>255, 128, 0</c>.</summary>
    public static string Text(IReadOnlyList<float> numbers) => string.Join(", ", numbers.Select(Number));

    /// <summary>Reads <paramref name="count"/> finite numbers separated by commas.</summary>
    public static bool TryParse(string text, int count, out float[] numbers)
    {
        var parts = text.Split(',');
        numbers = new float[count];
        if (parts.Length != count)
            return false;
        for (var i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) ||
                !float.IsFinite(numbers[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The text of a line after a drag of value <paramref name="index"/> by <paramref name="units"/>, from the text at
    /// the start of the drag. The other values keep their start numbers. A start text that is not
    /// <paramref name="count"/> finite numbers stays as it is.
    /// </summary>
    public static string DragText(DragKind kind, string startText, int count, int index, decimal units)
    {
        if (!TryParse(startText, count, out var numbers))
            return startText;
        numbers[index] = Value(kind, numbers[index], units * BaseStep(kind, numbers, index));
        return Text(numbers);
    }

    /// <summary>The shortest invariant text of a float. -0 gives <c>0</c>.</summary>
    private static string Number(float value) => (value + 0f).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The step of a line of the Scaled kind: from the largest absolute value, or 0.01 for a line of
    /// zeros.</summary>
    private static decimal ScaledStep(IReadOnlyList<float> start)
    {
        var largest = start.Count == 0 ? 0 : start.Max(MathF.Abs);
        return largest == 0 ? ZeroStep : Step(largest, ScaledMinimum);
    }

    /// <summary>10^(e - 2) for a value from 10^e to 10^(e+1), with the step exponent from
    /// <paramref name="minimum"/> to 4.</summary>
    private static decimal Step(float value, int minimum) =>
        Power(Math.Clamp(Exponent(value) - StepOffset, minimum, StepMaximum));

    /// <summary>The whole number e with 10^e &lt;= value &lt; 10^(e+1), for the exact text of the value. A value
    /// outside the range of a decimal gives an exponent that the steps clamp.</summary>
    private static int Exponent(float value)
    {
        if (value >= 1e20f)
            return 20;
        if (Exact(value) is not { } exact)
            return -20;
        var exponent = 0;
        while (exact >= 10)
        {
            exact /= 10;
            exponent++;
        }

        while (exact < 1)
        {
            exact *= 10;
            exponent--;
        }

        return exponent;
    }

    /// <summary>10 to a whole power, as an exact decimal.</summary>
    private static decimal Power(int exponent) => exponent >= 0
        ? Enumerable.Repeat(10m, exponent).Aggregate(1m, (product, ten) => product * ten)
        : new decimal(1, 0, 0, false, (byte)-exponent);

    private static bool TryAdd(decimal a, decimal b, out decimal sum)
    {
        try
        {
            sum = a + b;
            return true;
        }
        catch (OverflowException)
        {
            sum = 0;
            return false;
        }
    }

    /// <summary>The float of a decimal, through its invariant text, so that it rounds one time.</summary>
    private static float Parse(decimal value) =>
        float.Parse(value.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool Is(string name, string key) => name.Equals(key, StringComparison.OrdinalIgnoreCase);
}

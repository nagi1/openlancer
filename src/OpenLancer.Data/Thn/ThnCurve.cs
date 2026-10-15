using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace OpenLancer.Data.Thn;

/// <summary>A point of a parameter curve, as <c>ParameterCurve</c> reads it (ParameterCurve.cs:81-87).</summary>
/// <param name="Time">The time as a part of the period, from 0 to 1.</param>
/// <param name="Value">The value at the time.</param>
/// <param name="In">The slope into the point.</param>
/// <param name="Out">The slope out of the point.</param>
public readonly record struct ThnCurvePoint(float Time, float Value, float In, float Out);

/// <summary>
/// The <c>param_curve</c> record of an event: a <c>CLSID</c> that names the type of the curve, and the list of its
/// points <c>{ time, value, in, out }</c> (ParameterCurve.cs:42-88). The curve editor reads and changes the points.
/// </summary>
public static class ThnCurve
{
    public const string PointsKey = "points";
    public const string TypeKey = "CLSID";

    /// <summary>The path of the curve in an event.</summary>
    public static readonly ThnPath Path = new("props.param_curve");

    /// <summary>The path of the period of the curve in an event.</summary>
    public static readonly ThnPath PeriodPath = new("props.pcurve_period");

    /// <summary>The points of a curve, or null when the curve has no list of points with four numbers each.</summary>
    public static IReadOnlyList<ThnCurvePoint>? Points(ThnTable curve)
    {
        if (curve[PointsKey] is not ThnTable { Record.IsEmpty: true } points)
            return null;
        var result = new List<ThnCurvePoint>(points.List.Length);
        foreach (var item in points.List)
        {
            if (item is not ThnTable { Record.IsEmpty: true, List: [ThnNumber t, ThnNumber v, ThnNumber i, ThnNumber o] })
                return null;
            result.Add(new ThnCurvePoint(t.Value, v.Value, i.Value, o.Value));
        }
        return result;
    }

    /// <summary>
    /// The curve with one point changed. The other points keep their tables, and the other keys of the curve keep
    /// their values and their order.
    /// </summary>
    /// <exception cref="ArgumentException">The curve has no list of points, or the index is not a point of it.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A number of the point is not finite.</exception>
    public static ThnTable WithPoint(ThnTable curve, int index, ThnCurvePoint point)
    {
        if (curve[PointsKey] is not ThnTable { Record.IsEmpty: true } points || index < 0 || index >= points.List.Length)
            throw new ArgumentException($"The curve has no point {index + 1}.", nameof(index));
        var table = ThnTable.FromList(new ThnNumber(point.Time), new ThnNumber(point.Value), new ThnNumber(point.In),
            new ThnNumber(point.Out));
        var changed = new ThnTable(points.List.SetItem(index, table), []);
        return new ThnTable(curve.List, curve.Record.Select(f => f.Key == PointsKey ? f with { Value = changed } : f)
            .ToImmutableArray());
    }
}

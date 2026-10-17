using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.Ini;

namespace OpenLancer.Runtime.Scene;

/// <summary>
/// The INI values that a gizmo drag writes: the values of a line from gizmo floats, the rotation angles nearest to the
/// start angles, and the snap steps.
/// </summary>
public static class TransformValues
{
    /// <summary>The largest integer from which a float can hold each smaller integer: 2^24.</summary>
    private const float MaxExactInteger = 16_777_216;

    /// <summary>A changed angle within this distance of its start angle keeps the start angle. Other changed angles
    /// round to this step. Both are in degrees.</summary>
    private const double AngleStep = 0.001;

    /// <summary>Below this value of |cos y|, the rotation is in gimbal lock: only x + z or x - z is known.</summary>
    private const double GimbalLock = 1e-3;

    /// <summary>
    /// A rotation matrix within this distance of the matrix of the start angles, in each element, is the start
    /// rotation. The float noise of a quaternion round trip is below 8e-7 (measured over 4 million starts), and the
    /// gizmo math adds more steps. A turn of 0.001 degree (<see cref="AngleStep"/>) changes an element by up to 1.7e-5.
    /// </summary>
    private const float UnchangedRotation = 1e-5f;

    /// <summary>
    /// The values of a <c>pos</c> or <c>rotate</c> line after a move or a rotate. When the engine reads the start line
    /// as the new vector, the result is the start line itself, or an empty list when the section has no line. So a
    /// drag that changes nothing makes no new line, also when the start line has fewer than three values or no line.
    /// Otherwise the result has the three new values, by the number rules of
    /// <see cref="Line(IReadOnlyList{ValueBase}, ReadOnlySpan{float})"/>.
    /// </summary>
    /// <param name="start">The values of the line at the start of the drag, or null when the section has no line.</param>
    /// <exception cref="ArgumentOutOfRangeException">A new number is not finite.</exception>
    public static IReadOnlyList<ValueBase> Line(IReadOnlyList<ValueBase>? start, Vector3 value) =>
        SceneBuild.Vector(start) == value ? start ?? [] : Line(start, [value.X, value.Y, value.Z]);

    /// <summary>
    /// The values of a line with new numbers, by the number rules of a drag:
    /// <list type="bullet">
    /// <item>A value that the engine reads as its start value keeps the start value object, so its text does not
    /// change.</item>
    /// <item>A changed value that is an integer from -2^24 to 2^24, and that replaces an Int32 or no value, is an
    /// Int32. A float holds each such integer exactly.</item>
    /// <item>Any other changed value is a Single. An integer that replaces a Single stays a Single, because after a
    /// save and a reload of a binary file W holds a Single there.</item>
    /// <item>-0 becomes 0, because the text <c>-0.0</c> is not engine-equal to the Int32 0.</item>
    /// </list>
    /// A start value after the last new number stays. The rules compare each value alone, so a move and a rotate use
    /// <see cref="Line(IReadOnlyList{ValueBase}, Vector3)"/>, which also keeps a start line that has the vector in
    /// fewer values.
    /// </summary>
    /// <param name="start">The values of the line at the start of the drag, or null when the section has no line.</param>
    /// <exception cref="ArgumentOutOfRangeException">A new number is not finite.</exception>
    public static IReadOnlyList<ValueBase> Line(IReadOnlyList<ValueBase>? start, ReadOnlySpan<float> values)
    {
        var line = new ValueBase[Math.Max(values.Length, start?.Count ?? 0)];
        for (var i = 0; i < line.Length; i++)
        {
            var old = start != null && i < start.Count ? start[i] : null;
            if (i >= values.Length)
            {
                line[i] = old!;
                continue;
            }
            var value = values[i];
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(values), value, "A line value must be a finite number.");
            if (old != null && SceneBuild.TryFloat(old, out var oldValue) && oldValue == value)
            {
                line[i] = old;
                continue;
            }
            // Adding 0 makes -0 the value 0.
            value += 0f;
            var integral = MathF.Round(value) == value && MathF.Abs(value) <= MaxExactInteger;
            line[i] = integral && old is null or Int32Value ? new Int32Value((int)value) : new SingleValue(value, null);
        }
        return line;
    }

    /// <summary>
    /// The angles in degrees of a rotation, in the engine form <c>Rx(x) * Ry(y) * Rz(z)</c>
    /// (<c>MathHelper.MatrixFromEulerDegrees</c>), that are nearest to the start angles.
    /// </summary>
    /// <remarks>
    /// <para>Each rotation has two sets of angles, and the engine's conversion (<c>GetEulerDegrees</c>) always takes one
    /// of them. Then a drag of 15 degrees can turn (0, 105, 0) into (180, 60, 180). This method takes the set that is
    /// nearest to the start angles, with each angle from -180 to 180.</para>
    /// <para>A rotation that did not change gives the start angles bit for bit. The method compares the matrices first,
    /// because near gimbal lock the float noise of the matrix makes x and z move by more than 0.001 degree.</para>
    /// <para>In gimbal lock (|cos y| below 0.001), the method keeps the start z and solves x.</para>
    /// <para>An angle within 0.001 degree of its start angle is the start angle. Each other angle rounds to 0.001
    /// degree.</para>
    /// </remarks>
    /// <param name="rotation">A rotation matrix without scale. Only the rotation part is read.</param>
    public static Vector3 EulerNear(Matrix4x4 rotation, Vector3 startDegrees)
    {
        if (RotationDistance(rotation, MathHelper.MatrixFromEulerDegrees(startDegrees)) <= UnchangedRotation)
            return startDegrees;
        const double degrees = 180 / Math.PI;
        var start = new double[] { startDegrees.X, startDegrees.Y, startDegrees.Z };
        var cosY = Math.Sqrt((double)rotation.M11 * rotation.M11 + (double)rotation.M12 * rotation.M12);
        double[] angles;
        if (cosY < GimbalLock)
        {
            var y = Math.Atan2(-rotation.M13, cosY) * degrees;
            var withoutZ = rotation * Matrix4x4.CreateRotationZ((float)(-start[2] / degrees));
            angles = [Math.Atan2(-withoutZ.M32, withoutZ.M22) * degrees, y, start[2]];
        }
        else
        {
            double[] first =
            [
                Math.Atan2(rotation.M23, rotation.M33) * degrees,
                Math.Atan2(-rotation.M13, cosY) * degrees,
                Math.Atan2(rotation.M12, rotation.M11) * degrees,
            ];
            double[] second = [first[0] + 180, 180 - first[1], first[2] + 180];
            angles = Distance(first, start) <= Distance(second, start) ? first : second;
        }
        var result = new float[3];
        for (var i = 0; i < 3; i++)
        {
            result[i] = Math.Abs(Wrap(Wrap(angles[i]) - start[i])) <= AngleStep
                ? (float)start[i]
                : Angle(angles[i]);
        }
        return new Vector3(result[0], result[1], result[2]);
    }

    /// <summary>
    /// A changed angle as a drag writes it: wrapped into the range from -180 (not included) to 180, then rounded to
    /// 0.001 degree (<see cref="Math.Round(double, int)"/>). The rounding can give -180, for example for -179.9996. -0
    /// becomes 0. <see cref="EulerNear"/> uses this step for each angle that changed, and so does a label drag of the
    /// Inspector.
    /// </summary>
    public static float Angle(double degrees) => (float)Math.Round(Wrap(degrees), 3) + 0f;

    /// <summary>
    /// Reads a value as the engine reads a float from a binary file, and accepts only a finite result. An Int32, a
    /// Single and a text that is an invariant float number pass. A text that is not a number, for example <c>5x</c>,
    /// and a value that is not finite, for example <c>NaN</c> or <c>1e40</c>, do not.
    /// </summary>
    public static bool TryNumber(ValueBase value, out float number) =>
        SceneBuild.TryFloat(value, out number) && float.IsFinite(number);

    /// <summary>
    /// A position that moved a whole number of steps from the start: along the world axes, or along the local axes of
    /// a rotation.
    /// </summary>
    /// <param name="localAxes">The rotation of the item in the Local space, or null in the World space.</param>
    public static Vector3 SnapTranslation(Vector3 start, Vector3 current, float step, Quaternion? localAxes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        var delta = current - start;
        if (localAxes is not { } axes)
            return start + Steps(delta, step);
        var local = Vector3.Transform(delta, Quaternion.Inverse(axes));
        return start + Vector3.Transform(Steps(local, step), axes);
    }

    /// <summary>
    /// A rotation that turned a whole number of steps from the start. The turn from the start is an axis and an angle,
    /// and the angle rounds to the step. The axis stays.
    /// </summary>
    /// <remarks>
    /// One formula snaps a turn about a world axis (<c>current = turn * start</c>) and a turn about a local axis of the
    /// item (<c>current = start * localTurn</c>). The world form of a local turn is
    /// <c>start * localTurn * start^-1</c>. It has the same angle, and its axis is the local axis turned by the start,
    /// so both forms give the same snapped rotation.
    /// </remarks>
    public static Quaternion SnapRotation(Quaternion start, Quaternion current, float stepDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepDegrees);
        var turn = current * Quaternion.Inverse(start);
        // The turn with the smaller angle, from 0 to 180 degrees.
        if (turn.W < 0)
            turn = Quaternion.Negate(turn);
        var w = Math.Clamp(turn.W, -1f, 1f);
        var sinHalf = MathF.Sqrt(1 - w * w);
        var step = stepDegrees * MathF.PI / 180;
        var angle = Round(2 * MathF.Acos(w) / step) * step;
        if (angle == 0 || sinHalf == 0)
            return start;
        var snapped = Quaternion.CreateFromAxisAngle(new Vector3(turn.X, turn.Y, turn.Z) / sinHalf, angle);
        return Quaternion.Normalize(snapped * start);
    }

    /// <summary>
    /// A size that changed a whole number of steps from the start. <see cref="ZoneSizeAxes.Line"/> keeps a changed
    /// size at 1 or more.
    /// </summary>
    public static float SnapSize(float start, float current, float step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        return start + Round((current - start) / step) * step;
    }

    private static Vector3 Steps(Vector3 delta, float step) =>
        new Vector3(Round(delta.X / step), Round(delta.Y / step), Round(delta.Z / step)) * step;

    /// <summary>Rounds to the nearest whole number, and a half away from 0.</summary>
    private static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>An angle in degrees from -180 (not included) to 180.</summary>
    private static double Wrap(double angle)
    {
        angle %= 360;
        return angle <= -180 ? angle + 360 : angle > 180 ? angle - 360 : angle;
    }

    /// <summary>True when two rotations are the same, within the float noise of a quaternion round trip and of the
    /// gizmo math (see <see cref="UnchangedRotation"/>).</summary>
    internal static bool SameRotation(Quaternion a, Quaternion b) =>
        RotationDistance(Matrix4x4.CreateFromQuaternion(a), Matrix4x4.CreateFromQuaternion(b)) <= UnchangedRotation;

    /// <summary>The largest difference between two elements of the rotation parts of two matrices.</summary>
    private static float RotationDistance(Matrix4x4 a, Matrix4x4 b)
    {
        var largest = 0f;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            largest = MathF.Max(largest, MathF.Abs(a[row, column] - b[row, column]));
        return largest;
    }

    /// <summary>The largest distance in degrees between the angles of two sets.</summary>
    private static double Distance(double[] angles, double[] start)
    {
        var largest = 0.0;
        for (var i = 0; i < 3; i++)
            largest = Math.Max(largest, Math.Abs(Wrap(angles[i] - start[i])));
        return largest;
    }
}

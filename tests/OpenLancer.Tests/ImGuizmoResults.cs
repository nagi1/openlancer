using System;
using System.Numerics;

namespace OpenLancer.Tests;

/// <summary>
/// The matrices that ImGuizmo gives during a drag, made with its formulas, so that a test can script a drag without
/// ImGui. The retail proofs link this file too.
/// </summary>
/// <remarks>
/// Each method takes the matrix that the gizmo gets in a frame. The editor gives the gizmo the raw matrix of the frame
/// before, or the start matrix in the first frame. ImGuizmo makes the axes of that matrix length 1
/// (<c>OrthoNormalize</c>, ImGuizmo.cpp:399-404, :1044-1045) and measures their lengths in floats (:1056).
/// </remarks>
internal static class ImGuizmoResults
{
    /// <summary>A move: the matrix times a translation (ImGuizmo.cpp:2160).</summary>
    public static Matrix4x4 Move(Matrix4x4 matrix, Vector3 delta) => matrix * Matrix4x4.CreateTranslation(delta);

    /// <summary>A turn about a world axis: the matrix without its position, times the turn, with the position of the
    /// matrix (ImGuizmo.cpp:2419-2423).</summary>
    public static Matrix4x4 WorldTurn(Matrix4x4 matrix, Vector3 axis, float degrees)
    {
        var turned = (matrix with { Translation = Vector3.Zero }) * Matrix4x4.CreateFromAxisAngle(axis, Radians(degrees));
        return turned with { Translation = matrix.Translation };
    }

    /// <summary>A turn about a local axis: the axis lengths of the matrix, times the turn, times the matrix with axes of
    /// length 1 (ImGuizmo.cpp:2410-2415).</summary>
    public static Matrix4x4 LocalTurn(Matrix4x4 matrix, Vector3 localAxis, float degrees) =>
        Matrix4x4.CreateScale(AxisLengths(matrix)) * Matrix4x4.CreateFromAxisAngle(localAxis, Radians(degrees)) *
        Normalized(matrix);

    /// <summary>A scale: the factor times the axis lengths of the start matrix, times the matrix with axes of length 1
    /// (ImGuizmo.cpp:2246, :2297-2303).</summary>
    /// <param name="start">The matrix of the frame that started the drag.</param>
    /// <param name="factor">The scale from the start of the drag.</param>
    public static Matrix4x4 Scale(Matrix4x4 start, Matrix4x4 matrix, Vector3 factor) =>
        Matrix4x4.CreateScale(factor * AxisLengths(start)) * Normalized(matrix);

    private static Vector3 AxisLengths(Matrix4x4 m) => new(
        new Vector3(m.M11, m.M12, m.M13).Length(),
        new Vector3(m.M21, m.M22, m.M23).Length(),
        new Vector3(m.M31, m.M32, m.M33).Length());

    /// <summary>The matrix with each axis times 1 / its length, as <c>vec_t::Normalize</c> does
    /// (ImGuizmo.cpp:222).</summary>
    private static Matrix4x4 Normalized(Matrix4x4 m)
    {
        var lengths = AxisLengths(m);
        return Matrix4x4.CreateScale(1 / lengths.X, 1 / lengths.Y, 1 / lengths.Z) * m;
    }

    private static float Radians(float degrees) => degrees * MathF.PI / 180;
}

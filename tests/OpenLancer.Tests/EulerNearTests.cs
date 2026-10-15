using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rotation angles of a drag: the set of angles nearest to the start angles, each from -180 to 180, rounded to
/// 0.001 degree. The engine builds a rotation as Rx(x) * Ry(y) * Rz(z) from degrees.
/// </summary>
public sealed class EulerNearTests
{
    private static readonly Matrix4x4 WorldYaw15 = Matrix4x4.CreateRotationY(15 * MathF.PI / 180);

    [Fact]
    public void TheIdentityGivesZeroAngles() =>
        Assert.Equal(Vector3.Zero, TransformValues.EulerNear(Matrix4x4.Identity, Vector3.Zero));

    /// <summary>The world object holds a quaternion, so the drag starts from the matrix of that quaternion.</summary>
    [Fact]
    public void AnUnchangedRotationGivesTheStartAnglesBitForBit()
    {
        // A hand-written patch can give angles with more digits than a drag writes. They come back too.
        foreach (var start in new[]
                 {
                     new Vector3(0, 105, 0), new Vector3(0, 180, 0), new Vector3(90, -21, 180), new Vector3(-10, 3, 7),
                     new Vector3(12.3456789f, -7.77777f, 10.12345f),
                 })
        {
            var angles = TransformValues.EulerNear(ObjectMatrix(start), start);

            Assert.Equal(Bits(start), Bits(angles));
        }
    }

    /// <summary>
    /// Near gimbal lock, the float noise of the quaternion goes up by 1/|cos y| in x and z. A rule for each angle alone
    /// then gives (0, 89.9, -70.006) for the start (0, 89.9, -70). The method compares the matrices first, so each
    /// start comes back bit for bit: the examples of the review, and 10,000 random starts within 3 degrees of 90 and -90.
    /// </summary>
    [Fact]
    public void AnUnchangedRotationNearGimbalLockGivesTheStartAnglesBitForBit()
    {
        var random = new Random(2026);
        var starts = new List<Vector3>
        {
            new(0, 89.9f, -70), new(33, 89.8f, 5), new(120, 89.93f, 135), new(119, -89, -110),
        };
        for (var i = 0; i < 10_000; i++)
        {
            var y = (random.Next(2) == 0 ? 1 : -1) * (90 - random.Next(1, 3000) / 1000f);
            starts.Add(new Vector3(ThreeDecimals(random), y, ThreeDecimals(random)));
        }

        Assert.DoesNotContain(starts, s => Bits(TransformValues.EulerNear(ObjectMatrix(s), s)) != Bits(s));
    }

    /// <summary>A snapped turn of less than half a step gives the start quaternion. The drag then writes the start
    /// angles, so its line does not change.</summary>
    [Fact]
    public void ASnappedTurnBelowHalfAStepGivesTheStartAngles()
    {
        var start = new Vector3(119, -89, -110);
        var startRotation = MathHelper.QuatFromEulerDegrees(start);
        var current = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.05f * MathF.PI / 180) * startRotation;

        var snapped = TransformValues.SnapRotation(startRotation, current, 15);

        Assert.Equal(startRotation, snapped);
        Assert.Equal(Bits(start), Bits(TransformValues.EulerNear(Matrix4x4.CreateFromQuaternion(snapped), start)));
    }

    /// <summary>The compare of the matrices takes only a turn of about 0.001 degree or less. A turn of 0.002 degree
    /// changes the angle.</summary>
    [Fact]
    public void ATurnOfMoreThanTheAngleStepChangesTheAngle() =>
        Assert.Equal(new Vector3(0.002f, 0, 0),
            TransformValues.EulerNear(MathHelper.MatrixFromEulerDegrees(0.002f, 0, 0), Vector3.Zero));

    /// <summary>The plain conversion gives (180, 60, 180) here: the same rotation with the other set of angles.</summary>
    [Fact]
    public void AWorldYawKeepsTheBranchOfTheStartAngles()
    {
        var start = new Vector3(0, 105, 0);
        var turned = ObjectMatrix(start) * WorldYaw15;

        Assert.Equal(new Vector3(0, 120, 0), TransformValues.EulerNear(turned, start));
        Assert.NotEqual(new Vector3(0, 120, 0), turned.GetEulerDegrees());
    }

    [Fact]
    public void AnAngleOver180WrapsToMinus165()
    {
        var start = new Vector3(0, 180, 0);

        Assert.Equal(new Vector3(0, -165, 0), TransformValues.EulerNear(ObjectMatrix(start) * WorldYaw15, start));
    }

    /// <summary>In gimbal lock, only a sum or a difference of x and z is known. The method keeps the start z.</summary>
    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void InGimbalLockTheStartZStays(float y)
    {
        var start = new Vector3(10, y, 40);
        var rotation = MathHelper.MatrixFromEulerDegrees(25, y, 70);

        var angles = TransformValues.EulerNear(rotation, start);

        Assert.Equal(40, angles.Z);
        Assert.Equal(y, angles.Y);
        Assert.True(MaxDifference(MathHelper.MatrixFromEulerDegrees(angles), rotation) < 2e-5f, angles.ToString());
        Assert.Equal(Bits(start), Bits(TransformValues.EulerNear(MathHelper.MatrixFromEulerDegrees(start), start)));
    }

    [Fact]
    public void AChangedAngleRoundsTo0001AndANearAngleKeepsTheStart()
    {
        var rotation = MathHelper.MatrixFromEulerDegrees(12.34567f, 0, 10.1238f);

        var angles = TransformValues.EulerNear(rotation, new Vector3(0, 0, 10.12345f));

        Assert.Equal(12.346f, angles.X);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(angles.Y));
        Assert.Equal(10.12345f, angles.Z);
    }

    private static Matrix4x4 ObjectMatrix(Vector3 degrees) =>
        Matrix4x4.CreateFromQuaternion(MathHelper.MatrixFromEulerDegrees(degrees).ExtractRotation());

    /// <summary>An angle from -180 to 180 with three decimals, as a drag writes it.</summary>
    private static float ThreeDecimals(Random random) => (float)Math.Round(random.NextDouble() * 360 - 180, 3);

    private static (int, int, int) Bits(Vector3 v) =>
        (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));

    private static float MaxDifference(Matrix4x4 a, Matrix4x4 b)
    {
        var largest = 0f;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            largest = MathF.Max(largest, MathF.Abs(a[row, column] - b[row, column]));
        return largest;
    }
}

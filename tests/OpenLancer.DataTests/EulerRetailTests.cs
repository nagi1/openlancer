using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the rotation angles of a drag: each distinct retail <c>rotate</c> line comes back bit for bit from
/// the quaternion of its world object, and a world yaw of 15 degrees keeps the branch of the start angles.
/// </summary>
public sealed class EulerRetailTests(ITestOutputHelper output)
{
    static EulerRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EachRetailRotationComesBackAndAWorldYawKeepsItsBranch()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var rotations = new HashSet<Vector3>();
        foreach (var path in RetailChecks.SystemFiles(session))
        {
            foreach (var section in session.Sections(path))
            {
                foreach (var line in session.Get(section)!.Lines("rotate"))
                {
                    Assert.Equal(3, line.Values.Count);
                    rotations.Add(new Vector3(line.Values[0].ToSingle(), line.Values[1].ToSingle(), line.Values[2].ToSingle()));
                }
            }
        }
        var yaw = Matrix4x4.CreateRotationY(15 * MathF.PI / 180);

        int exact = 0, plainExact = 0, yawTotal = 0, yawExact = 0;
        var largestError = 0f;
        foreach (var start in rotations)
        {
            // The world object holds a quaternion, so a drag starts from the matrix of that quaternion.
            var matrix = Matrix4x4.CreateFromQuaternion(MathHelper.MatrixFromEulerDegrees(start).ExtractRotation());
            exact += Bits(TransformValues.EulerNear(matrix, start)) == Bits(start) ? 1 : 0;
            plainExact += Bits(matrix.GetEulerDegrees()) == Bits(start) ? 1 : 0;

            var turned = matrix * yaw;
            var angles = TransformValues.EulerNear(turned, start);
            largestError = MathF.Max(largestError, MaxDifference(MathHelper.MatrixFromEulerDegrees(angles), turned));
            if (start.X == 0 && start.Z == 0)
            {
                yawTotal++;
                var y = start.Y + 15 > 180 ? start.Y + 15 - 360 : start.Y + 15;
                yawExact += Bits(angles) == Bits(new Vector3(0, y, 0)) ? 1 : 0;
            }
        }

        output.WriteLine($"distinct rotations {rotations.Count}, bit-exact at zero delta {exact} " +
                         $"(plain conversion {plainExact}), world yaw +15 on (0, y, 0): {yawExact} of {yawTotal}, " +
                         $"largest matrix error {largestError:R}");
        Assert.Equal(913, rotations.Count);
        Assert.Equal(913, exact);
        Assert.Equal(180, yawTotal);
        Assert.Equal(180, yawExact);
        Assert.True(largestError < 2e-5f, $"largest matrix error {largestError:R}");
    }

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

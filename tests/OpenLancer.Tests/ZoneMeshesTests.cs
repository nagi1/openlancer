using System;
using System.Numerics;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

public sealed class ZoneMeshesTests
{
    private const float Tolerance = 1e-4f;

    private static ZoneMeshData Build(string name) => name switch
    {
        "cube" => ZoneMeshes.BuildCube(),
        "sphere" => ZoneMeshes.BuildSphere(),
        "cylinder" => ZoneMeshes.BuildCylinder(),
        "ring" => ZoneMeshes.BuildRing(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("cube")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("ring")]
    public void WindsEachTriangleCounterclockwiseSeenFromTheSideOfItsNormals(string name)
    {
        // Back-face culling keeps the counterclockwise triangles. So a triangle that winds against its normals would
        // show the shell inside out.
        var mesh = Build(name);
        Assert.Equal(0, mesh.Indices.Length % 3);
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]];
            var b = mesh.Vertices[mesh.Indices[i + 1]];
            var c = mesh.Vertices[mesh.Indices[i + 2]];
            var face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (face.LengthSquared() < 1e-12f)
            {
                // The triangles at the sphere poles have no area.
                continue;
            }

            Assert.True(Vector3.Dot(face, a.Normal + b.Normal + c.Normal) > 0,
                $"Triangle {i / 3} of the {name} winds against its normals.");
        }
    }

    [Theory]
    [InlineData("cube")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    public void PointsTheNormalsOfAClosedShapeAwayFromItsCenter(string name)
    {
        Assert.All(Build(name).Vertices, v => Assert.True(Vector3.Dot(v.Normal, v.Position) > 0));
    }

    [Fact]
    public void BuildsAUnitCubeWithFourCornersOnEachFace()
    {
        var cube = ZoneMeshes.BuildCube();

        Assert.Equal(24, cube.Vertices.Length);
        Assert.Equal(36, cube.Indices.Length);
        Assert.All(cube.Vertices, v =>
        {
            Assert.Equal(1, v.Normal.Length(), Tolerance);
            Assert.Equal(0.5f, Vector3.Dot(v.Position, v.Normal), Tolerance);
            Assert.Equal(0.5f, MathF.Max(MathF.Abs(v.Position.X), MathF.Max(MathF.Abs(v.Position.Y),
                MathF.Abs(v.Position.Z))), Tolerance);
        });
    }

    [Fact]
    public void BuildsASphereWithRadiusOne()
    {
        Assert.All(ZoneMeshes.BuildSphere().Vertices, v => Assert.Equal(1, v.Position.Length(), Tolerance));
    }

    [Fact]
    public void BuildsACylinderWithRadiusOneAndHeightOne()
    {
        Assert.All(ZoneMeshes.BuildCylinder().Vertices, v =>
        {
            Assert.InRange(new Vector2(v.Position.X, v.Position.Z).Length(), 0, 1 + Tolerance);
            Assert.Equal(0.5f, MathF.Abs(v.Position.Y), Tolerance);
        });
    }

    [Fact]
    public void BuildsARingWithItsInnerWallTowardTheAxis()
    {
        // The ZoneVolume shader tells the walls apart by the radius: more than 1.1 is the outer wall.
        Assert.All(ZoneMeshes.BuildRing().Vertices, v =>
        {
            var radial = new Vector3(v.Position.X, 0, v.Position.Z);
            var radius = radial.Length();
            Assert.True(MathF.Abs(radius - 1) < Tolerance || MathF.Abs(radius - 2) < Tolerance);
            Assert.Equal(0.5f, MathF.Abs(v.Position.Y), Tolerance);
            if (v.Normal.Y == 0)
            {
                // A wall: the outer wall faces away from the axis, and the inner wall faces toward it.
                Assert.Equal(radius > 1.1f ? 1 : -1, Vector3.Dot(v.Normal, radial / radius), Tolerance);
            }
            else
            {
                // A cap faces up at the top and down at the bottom.
                Assert.Equal(MathF.Sign(v.Position.Y), v.Normal.Y);
            }
        });
    }
}

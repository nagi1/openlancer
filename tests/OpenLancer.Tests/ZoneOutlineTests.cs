using System;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The accent outline of a selected nebula, field or zone (section 7 of the M22 design).</summary>
public sealed class ZoneOutlineTests
{
    private static Zone Zone(ShapeKind shape, Vector3 size, Vector3 position = default, Matrix4x4? rotation = null) => new()
    {
        Nickname = "zone",
        Shape = shape,
        Size = size,
        Position = position,
        RotationMatrix = rotation ?? Matrix4x4.Identity,
    };

    [Fact]
    public void AnEllipsoidGivesThreeEllipsesThroughItsRadii()
    {
        var lines = ZoneOutline.Lines(Zone(ShapeKind.Ellipsoid, new Vector3(1_000, 2_000, 3_000)), 16);

        Assert.Equal(3 * 16, lines.Count);
        // Each point is on the ellipsoid surface, and the ellipses reach each radius.
        var points = lines.SelectMany(l => new[] { l.A, l.B }).ToList();
        Assert.All(points, p => Assert.Equal(1, (p / new Vector3(1_000, 2_000, 3_000)).LengthSquared(), 3));
        Assert.Equal(1_000, points.Max(p => p.X), 1);
        Assert.Equal(2_000, points.Max(p => p.Y), 1);
        Assert.Equal(3_000, points.Max(p => p.Z), 1);

        // A sphere has its size as the radius on each axis.
        var sphere = ZoneOutline.Lines(Zone(ShapeKind.Sphere, new Vector3(500, 0, 0)), 16);
        Assert.All(sphere.SelectMany(l => new[] { l.A, l.B }), p => Assert.Equal(500, p.Length(), 1));
    }

    /// <summary>A sphere at a point gives three circles around that point, as a sphere zone does.</summary>
    [Fact]
    public void ASphereAtAPointGivesThreeCirclesAroundIt()
    {
        var center = new Vector3(100, -200, 300);
        var lines = ZoneOutline.Sphere(center, 500, 16);

        Assert.Equal(3 * 16, lines.Count);
        Assert.All(lines.SelectMany(l => new[] { l.A, l.B }), p => Assert.Equal(500, Vector3.Distance(p, center), 1));
        var zone = ZoneOutline.Lines(Zone(ShapeKind.Sphere, new Vector3(500, 0, 0), center), 16);
        Assert.All(zone.Zip(lines), pair => Assert.True(Vector3.Distance(pair.First.A, pair.Second.A) < 0.01f));
    }

    [Fact]
    public void ABoxGivesTwelveEdges()
    {
        var lines = ZoneOutline.Lines(Zone(ShapeKind.Box, new Vector3(200, 400, 600)));

        Assert.Equal(12, lines.Count);
        // The size of a box is its full extent, so each corner is at half the size.
        Assert.All(lines.SelectMany(l => new[] { l.A, l.B }),
            p => Assert.Equal(new Vector3(100, 200, 300), Vector3.Abs(p)));
        // Each edge runs along one axis.
        Assert.All(lines, l => Assert.Equal(1, new[] { l.A.X != l.B.X, l.A.Y != l.B.Y, l.A.Z != l.B.Z }.Count(c => c)));
    }

    [Fact]
    public void ARingGivesItsInnerAndOuterCircles()
    {
        // A ring stores (outer radius, height, inner radius).
        var lines = ZoneOutline.Lines(Zone(ShapeKind.Ring, new Vector3(1_000, 200, 600)), 32);

        Assert.Equal(4 * 32 + 4, lines.Count);
        var radii = lines.SelectMany(l => new[] { l.A, l.B })
            .Select(p => MathF.Round(new Vector2(p.X, p.Z).Length())).Distinct().Order().ToList();
        Assert.Equal([600f, 1_000f], radii);
        Assert.All(lines.SelectMany(l => new[] { l.A, l.B }), p => Assert.Equal(100, Math.Abs(p.Y), 1));

        // A cylinder has its two end circles and four side lines.
        Assert.Equal(2 * 32 + 4, ZoneOutline.Lines(Zone(ShapeKind.Cylinder, new Vector3(500, 100, 0)), 32).Count);
    }

    [Fact]
    public void TheLinesTurnWithTheZone()
    {
        // A box 2,000 long on X, turned 90 degrees about Y, lies along Z at its position.
        var turned = Matrix4x4.CreateRotationY(MathF.PI / 2);
        var lines = ZoneOutline.Lines(Zone(ShapeKind.Box, new Vector3(2_000, 100, 100), new Vector3(10, 20, 30), turned));

        var points = lines.SelectMany(l => new[] { l.A, l.B }).ToList();
        Assert.Equal(-970, points.Min(p => p.Z), 1);
        Assert.Equal(1_030, points.Max(p => p.Z), 1);
        Assert.Equal(-40, points.Min(p => p.X), 1);
        Assert.Equal(60, points.Max(p => p.X), 1);
    }
}

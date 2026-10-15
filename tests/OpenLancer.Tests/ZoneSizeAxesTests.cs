using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The scale handles of each zone shape for each count of size values (section 10.5 of the design), and the size line
/// that a scale writes.
/// </summary>
public sealed class ZoneSizeAxesTests
{
    private const string TwoValuesBox =
        "The size has two values, so the engine gives Z the size 0. Give it three values first.";

    private const string TwoValuesRing =
        "The size has two values, so the engine gives the ring the height 0. Give it three values first.";
    private const string ZeroSize = "The size has a value of 0 or less on an axis that the shape uses.";

    /// <summary>The handles of each shape and value count: the gizmo scale, the free axes (X, Y, Z) and the reason
    /// when the scale tool is not available.</summary>
    [Theory]
    [InlineData(ShapeKind.Sphere, "4100", 4100, 1, 1, "X", null)]
    [InlineData(ShapeKind.Sphere, "4100, 50", 4100, 1, 1, "X", null)]
    [InlineData(ShapeKind.Sphere, "4100, 50, 60", 4100, 1, 1, "X", null)]
    [InlineData(ShapeKind.Sphere, null, 1, 1, 1, "X", null)]
    [InlineData(ShapeKind.Ellipsoid, "100, 200, 300", 100, 200, 300, "XYZ", null)]
    [InlineData(ShapeKind.Box, "100, 200, 300", 100, 200, 300, "XYZ", null)]
    [InlineData(ShapeKind.Box, "100", 100, 100, 100, "XYZ", null)]
    [InlineData(ShapeKind.Box, "100, 200", 100, 200, 0, "", TwoValuesBox)]
    [InlineData(ShapeKind.Ellipsoid, "100, 200", 100, 200, 0, "", TwoValuesBox)]
    [InlineData(ShapeKind.Box, null, 1, 1, 1, "XYZ", null)]
    [InlineData(ShapeKind.Cylinder, "750, 13088", 750, 13088, 1, "XY", null)]
    [InlineData(ShapeKind.Cylinder, "750, 13088, 5", 750, 13088, 1, "XY", null)]
    [InlineData(ShapeKind.Cylinder, "750, 13088, 0", 750, 13088, 1, "XY", null)]
    [InlineData(ShapeKind.Cylinder, "750", 750, 750, 1, "XY", null)]
    [InlineData(ShapeKind.Cylinder, null, 1, 1, 1, "XY", null)]
    [InlineData(ShapeKind.Ring, "3000, 700, 200", 3000, 200, 700, "XYZ", null)]
    [InlineData(ShapeKind.Ring, "3000", 3000, 3000, 3000, "XYZ", null)]
    [InlineData(ShapeKind.Ring, "3000, 700", 3000, 0, 700, "", TwoValuesRing)]
    [InlineData(ShapeKind.Ring, null, 1, 1, 1, "XYZ", null)]
    [InlineData(ShapeKind.Sphere, "0", 0, 1, 1, "", ZeroSize)]
    [InlineData(ShapeKind.Box, "100, 0, 300", 100, 0, 300, "", ZeroSize)]
    [InlineData(ShapeKind.Box, "100, -5, 300", 100, -5, 300, "", ZeroSize)]
    [InlineData(ShapeKind.Cylinder, "0, 5", 0, 5, 1, "", ZeroSize)]
    [InlineData(ShapeKind.Ring, "3000, 0, 200", 3000, 200, 0, "", ZeroSize)]
    public void EachShapeAndValueCountHasItsHandles(ShapeKind shape, string? size, float x, float y, float z, string free,
        string? disabled)
    {
        var axes = ZoneSizeAxes.Of(shape, Values(size));

        Assert.Equal(new Vector3(x, y, z), axes.GizmoScale);
        Assert.Equal(free, (axes.FreeX ? "X" : "") + (axes.FreeY ? "Y" : "") + (axes.FreeZ ? "Z" : ""));
        Assert.Equal(disabled, axes.Disabled);
    }

    /// <summary>The line after a scale: the values that each axis writes, in INI order, as text. A value in brackets
    /// is the start value object.</summary>
    [Theory]
    [InlineData(ShapeKind.Sphere, "4100", 6150, 1, 1, "6150")]
    [InlineData(ShapeKind.Sphere, "4100, 50, 60", 5000, 1, 1, "5000, (50), (60)")]
    [InlineData(ShapeKind.Sphere, null, 500, 1, 1, "500")]
    [InlineData(ShapeKind.Box, "100, 200, 300", 100, 250, 300, "(100), 250, (300)")]
    [InlineData(ShapeKind.Box, "100", 100, 150, 100, "(100), 150, 100")]
    [InlineData(ShapeKind.Box, null, 2, 1, 1, "2, 1, 1")]
    [InlineData(ShapeKind.Cylinder, "750, 13088", 1500, 13088, 1, "1500, (13088)")]
    [InlineData(ShapeKind.Cylinder, "750, 13088, 5", 750, 20000, 7, "(750), 20000, (5)")]
    [InlineData(ShapeKind.Cylinder, "750", 750, 1000, 1, "(750), 1000")]
    [InlineData(ShapeKind.Cylinder, null, 500, 600, 1, "500, 600")]
    [InlineData(ShapeKind.Ring, "3000, 700, 200", 6000, 200, 700, "6000, (700), (200)")]
    [InlineData(ShapeKind.Ring, "3000, 700, 200", 3000, 250, 800, "(3000), 800, 250")]
    [InlineData(ShapeKind.Ring, "3000", 3000, 3000, 1000, "(3000), 1000, 3000")]
    [InlineData(ShapeKind.Ring, null, 5, 3, 2, "5, 2, 3")]
    [InlineData(ShapeKind.Box, "150, 200, 300", 0.25f, 200, 300, "1, (200), (300)")]
    [InlineData(ShapeKind.Box, "150, 200, 300", 150.5f, 200, 300, "150.5, (200), (300)")]
    public void AScaleWritesTheValuesOfItsShape(ShapeKind shape, string? size, float x, float y, float z, string expected)
    {
        var start = Values(size);

        var line = ZoneSizeAxes.Of(shape, start).Line(start, new Vector3(x, y, z));

        Assert.Equal(expected, string.Join(", ", line.Select((v, i) =>
            start != null && i < start.Count && ReferenceEquals(start[i], v) ? $"({v})" : v.ToString())));
    }

    [Fact]
    public void AScaleWithoutAChangeOfAFreeAxisGivesTheStartLine()
    {
        var sphere = Values("4100")!;
        var box = Values("100")!;

        Assert.Same(sphere, ZoneSizeAxes.Of(ShapeKind.Sphere, sphere).Line(sphere, new Vector3(4100, 5, 7)));
        Assert.Same(box, ZoneSizeAxes.Of(ShapeKind.Box, box).Line(box, new Vector3(100, 100, 100)));
        Assert.Empty(ZoneSizeAxes.Of(ShapeKind.Ring, null).Line(null, Vector3.One));
    }

    [Fact]
    public void AScaleOfADisabledZoneThrows()
    {
        var start = Values("100, 200");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ZoneSizeAxes.Of(ShapeKind.Box, start).Line(start, new Vector3(200, 200, 0)));

        Assert.Equal(TwoValuesBox, error.Message);
        Assert.NotNull(ZoneSizeAxes.Of((ShapeKind)7, null).Disabled);
    }

    private static List<ValueBase>? Values(string? text) => text == null
        ? null
        : IniFile.ParseFile("x.ini", new MemoryStream(Encoding.ASCII.GetBytes($"[Zone]\nsize = {text}\n"))).Single()["size"]!
            .ToList();
}

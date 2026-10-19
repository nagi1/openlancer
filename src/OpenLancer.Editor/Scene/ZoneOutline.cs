using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer.Data.GameData.World;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The outline of a zone shape as line segments. The Scene draws it in the accent color for a selected nebula, field or
/// zone (decision D163 of the M22 design). The shapes are the shapes that the Scene draws (see
/// <see cref="ScenePicker.ZoneSpan"/>). This class has no GPU call.
/// </summary>
internal static class ZoneOutline
{
    /// <summary>
    /// The segments of the outline of a zone, in world coordinates. The outline turns and moves with the zone.
    /// <list type="bullet">
    /// <item>A sphere or an ellipsoid: three ellipses through its radii, one in each plane of the zone frame.</item>
    /// <item>A box: its 12 edges.</item>
    /// <item>A cylinder: the two end circles and four lines along its side.</item>
    /// <item>A ring: the outer and the inner circle at each end, and four lines along its outer side.</item>
    /// </list>
    /// </summary>
    /// <param name="segments">The number of segments of each circle or ellipse.</param>
    public static IReadOnlyList<(Vector3 A, Vector3 B)> Lines(Zone zone, int segments = 64)
    {
        var lines = new List<(Vector3 A, Vector3 B)>();
        var size = zone.Size;
        switch (zone.Shape)
        {
            case ShapeKind.Sphere:
                Ellipses(lines, Vector3.Zero, new Vector3(size.X), segments);
                break;
            case ShapeKind.Ellipsoid:
                Ellipses(lines, Vector3.Zero, size, segments);
                break;
            case ShapeKind.Box:
                BoxEdges(lines, size / 2);
                break;
            case ShapeKind.Cylinder:
                Tube(lines, size.X, 0, size.Y, segments);
                break;
            case ShapeKind.Ring:
                // A ring zone stores its size as (outer radius, height, inner radius).
                Tube(lines, size.X, size.Z, size.Y, segments);
                break;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = (World(zone, lines[i].A), World(zone, lines[i].B));
        }

        return lines;
    }

    /// <summary>The segments of a sphere outline in world coordinates: three circles through its radius, one in each
    /// axis plane. The mission layer of the Scene draws its volumes with it (section 7.2 of the M6a design).</summary>
    public static IReadOnlyList<(Vector3 A, Vector3 B)> Sphere(Vector3 center, float radius, int segments = 64)
    {
        var lines = new List<(Vector3 A, Vector3 B)>();
        Ellipses(lines, center, new Vector3(radius), segments);
        return lines;
    }

    /// <summary>The segments of an ellipse through center + u and center + v, in world coordinates.</summary>
    public static IReadOnlyList<(Vector3 A, Vector3 B)> Ellipse(Vector3 center, Vector3 u, Vector3 v, int segments = 64)
    {
        var lines = new List<(Vector3 A, Vector3 B)>();
        Ellipse(lines, center, u, v, segments);
        return lines;
    }

    private static Vector3 World(Zone zone, Vector3 local) =>
        zone.Position + Vector3.TransformNormal(local, zone.RotationMatrix);

    private static void Ellipses(List<(Vector3, Vector3)> lines, Vector3 center, Vector3 radii, int segments)
    {
        Ellipse(lines, center, Vector3.UnitX * radii.X, Vector3.UnitY * radii.Y, segments);
        Ellipse(lines, center, Vector3.UnitX * radii.X, Vector3.UnitZ * radii.Z, segments);
        Ellipse(lines, center, Vector3.UnitY * radii.Y, Vector3.UnitZ * radii.Z, segments);
    }

    private static void BoxEdges(List<(Vector3, Vector3)> lines, Vector3 half)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            corners[i] = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
        }

        // Two corners share an edge when their indices differ in one bit.
        for (var i = 0; i < 8; i++)
        {
            for (var bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    lines.Add((corners[i], corners[i | bit]));
                }
            }
        }
    }

    /// <summary>A tube that stands on the Y axis, from -height/2 to height/2. A cylinder has the inner radius 0.</summary>
    private static void Tube(List<(Vector3, Vector3)> lines, float outer, float inner, float height, int segments)
    {
        foreach (var y in new[] { -height / 2, height / 2 })
        {
            var center = new Vector3(0, y, 0);
            Ellipse(lines, center, Vector3.UnitX * outer, Vector3.UnitZ * outer, segments);
            if (inner > 0)
            {
                Ellipse(lines, center, Vector3.UnitX * inner, Vector3.UnitZ * inner, segments);
            }
        }

        foreach (var side in new[] { Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitZ })
        {
            lines.Add((side * outer - Vector3.UnitY * height / 2, side * outer + Vector3.UnitY * height / 2));
        }
    }

    /// <summary>An ellipse through center + u and center + v.</summary>
    private static void Ellipse(List<(Vector3, Vector3)> lines, Vector3 center, Vector3 u, Vector3 v, int segments)
    {
        var previous = center + u;
        for (var i = 1; i <= segments; i++)
        {
            var angle = 2 * MathF.PI * i / segments;
            var point = center + MathF.Cos(angle) * u + MathF.Sin(angle) * v;
            lines.Add((previous, point));
            previous = point;
        }
    }
}

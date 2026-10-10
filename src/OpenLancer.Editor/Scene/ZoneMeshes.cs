using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer.Graphics;
using LibreLancer.Graphics.Vertices;

namespace OpenLancer.Editor.Scene;

/// <summary>The vertices and the triangle indices of one zone mesh.</summary>
internal readonly record struct ZoneMeshData(VertexPositionNormal[] Vertices, ushort[] Indices);

/// <summary>One zone mesh on the GPU: its vertices and its triangle indices.</summary>
internal sealed class ZoneMesh : IDisposable
{
    private readonly VertexBuffer vertices;
    private readonly ElementBuffer elements;
    private readonly int triangles;

    public ZoneMesh(RenderContext context, ZoneMeshData data)
    {
        elements = new ElementBuffer(context, data.Indices.Length);
        elements.SetData(data.Indices);
        vertices = new VertexBuffer(context, typeof(VertexPositionNormal), data.Vertices.Length);
        vertices.SetData<VertexPositionNormal>(data.Vertices);
        vertices.SetElementBuffer(elements);
        triangles = data.Indices.Length / 3;
    }

    /// <summary>Draws all the triangles with the current shader and state.</summary>
    public void Draw() => vertices.Draw(PrimitiveTypes.TriangleList, 0, 0, triangles);

    public void Dispose()
    {
        vertices.Dispose();
        elements.Dispose();
    }
}

/// <summary>
/// The unit meshes of the zone shapes, built in code. Each triangle is counterclockwise when you look at it from the
/// side that its normal points to. So the outside of each shape is its front side, and back-face culling shows the
/// near half of a shell.
/// </summary>
internal sealed class ZoneMeshes : IDisposable
{
    // The number of segments around the Y axis, and the number of sphere rings from pole to pole.
    private const int Segments = 48;
    private const int Stacks = 24;

    public ZoneMeshes(RenderContext context)
    {
        Cube = new ZoneMesh(context, BuildCube());
        Sphere = new ZoneMesh(context, BuildSphere());
        Cylinder = new ZoneMesh(context, BuildCylinder());
        Ring = new ZoneMesh(context, BuildRing());
    }

    /// <summary>A cube from -0.5 to 0.5. The size of a box zone is its full extent, so it scales this cube.</summary>
    public ZoneMesh Cube { get; }

    /// <summary>A sphere with radius 1.</summary>
    public ZoneMesh Sphere { get; }

    /// <summary>A cylinder with radius 1 along the Y axis, with caps at Y -0.5 and 0.5.</summary>
    public ZoneMesh Cylinder { get; }

    /// <summary>
    /// A ring along the Y axis: an outer wall at radius 2, an inner wall at radius 1 and flat caps at Y -0.5 and 0.5.
    /// The ZoneVolume shader moves the outer wall to radius 1 and the inner wall to the ratio of the inner radius to
    /// the outer radius.
    /// </summary>
    public ZoneMesh Ring { get; }

    internal static ZoneMeshData BuildCube()
    {
        var mesh = new Builder();
        ReadOnlySpan<Vector3> normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ,
            -Vector3.UnitZ];
        foreach (var normal in normals)
        {
            // u and v lie in the face, and u x v = normal. So the corners go counterclockwise seen from outside.
            var u = new Vector3(normal.Y, normal.Z, normal.X);
            var v = Vector3.Cross(normal, u);
            var center = normal * 0.5f;
            mesh.Quad(mesh.Add(center + (-u - v) * 0.5f, normal), mesh.Add(center + (u - v) * 0.5f, normal),
                mesh.Add(center + (u + v) * 0.5f, normal), mesh.Add(center + (-u + v) * 0.5f, normal));
        }

        return mesh.Build();
    }

    internal static ZoneMeshData BuildSphere()
    {
        var mesh = new Builder();
        for (var stack = 0; stack <= Stacks; stack++)
        {
            var phi = MathF.PI * stack / Stacks;
            for (var segment = 0; segment <= Segments; segment++)
            {
                var direction = Around(segment);
                var point = new Vector3(MathF.Sin(phi) * direction.X, MathF.Cos(phi), MathF.Sin(phi) * direction.Z);
                mesh.Add(point, point);
            }
        }

        // The vertices go from the top pole down, and around the axis toward +Z. The triangles at the poles have no
        // area, which is harmless.
        for (var stack = 0; stack < Stacks; stack++)
        {
            for (var segment = 0; segment < Segments; segment++)
            {
                var a = (ushort)(stack * (Segments + 1) + segment);
                var b = (ushort)(a + Segments + 1);
                mesh.Quad(a, (ushort)(a + 1), (ushort)(b + 1), b);
            }
        }

        return mesh.Build();
    }

    internal static ZoneMeshData BuildCylinder()
    {
        var mesh = new Builder();
        Wall(mesh, 1, true);
        Disc(mesh, 0.5f);
        Disc(mesh, -0.5f);
        return mesh.Build();
    }

    internal static ZoneMeshData BuildRing()
    {
        var mesh = new Builder();
        Wall(mesh, 2, true);
        Wall(mesh, 1, false);
        Annulus(mesh, 0.5f);
        Annulus(mesh, -0.5f);
        return mesh.Build();
    }

    /// <summary>The unit direction in the XZ plane of a segment edge. The angle goes from +X toward +Z.</summary>
    private static Vector3 Around(int segment)
    {
        var theta = 2 * MathF.PI * (segment % Segments) / Segments;
        return new Vector3(MathF.Cos(theta), 0, MathF.Sin(theta));
    }

    /// <summary>A wall from Y -0.5 to 0.5, with its normals away from the axis or toward it.</summary>
    private static void Wall(Builder mesh, float radius, bool outward)
    {
        var half = new Vector3(0, 0.5f, 0);
        for (var segment = 0; segment < Segments; segment++)
        {
            var d0 = Around(segment);
            var d1 = Around(segment + 1);
            var n0 = outward ? d0 : -d0;
            var n1 = outward ? d1 : -d1;
            var bottom0 = mesh.Add(d0 * radius - half, n0);
            var top0 = mesh.Add(d0 * radius + half, n0);
            var top1 = mesh.Add(d1 * radius + half, n1);
            var bottom1 = mesh.Add(d1 * radius - half, n1);
            if (outward)
            {
                mesh.Quad(bottom0, top0, top1, bottom1);
            }
            else
            {
                mesh.Quad(bottom0, bottom1, top1, top0);
            }
        }
    }

    /// <summary>
    /// A flat cap with radius 1 at height <paramref name="y"/>. Its normal points away from the center.
    /// </summary>
    private static void Disc(Builder mesh, float y)
    {
        var normal = new Vector3(0, MathF.Sign(y), 0);
        var center = mesh.Add(new Vector3(0, y, 0), normal);
        for (var segment = 0; segment < Segments; segment++)
        {
            var edge0 = mesh.Add(Around(segment) + new Vector3(0, y, 0), normal);
            var edge1 = mesh.Add(Around(segment + 1) + new Vector3(0, y, 0), normal);
            if (y > 0)
            {
                mesh.Triangle(center, edge1, edge0);
            }
            else
            {
                mesh.Triangle(center, edge0, edge1);
            }
        }
    }

    /// <summary>A flat cap between radius 1 and radius 2 at height <paramref name="y"/>.</summary>
    private static void Annulus(Builder mesh, float y)
    {
        var normal = new Vector3(0, MathF.Sign(y), 0);
        var up = new Vector3(0, y, 0);
        for (var segment = 0; segment < Segments; segment++)
        {
            var d0 = Around(segment);
            var d1 = Around(segment + 1);
            var inner0 = mesh.Add(d0 + up, normal);
            var inner1 = mesh.Add(d1 + up, normal);
            var outer1 = mesh.Add(d1 * 2 + up, normal);
            var outer0 = mesh.Add(d0 * 2 + up, normal);
            if (y > 0)
            {
                mesh.Quad(inner0, inner1, outer1, outer0);
            }
            else
            {
                mesh.Quad(inner0, outer0, outer1, inner1);
            }
        }
    }

    public void Dispose()
    {
        Cube.Dispose();
        Sphere.Dispose();
        Cylinder.Dispose();
        Ring.Dispose();
    }

    /// <summary>Collects the vertices and the triangles of a mesh.</summary>
    private sealed class Builder
    {
        private readonly List<VertexPositionNormal> vertices = [];
        private readonly List<ushort> indices = [];

        public ushort Add(Vector3 position, Vector3 normal)
        {
            vertices.Add(new VertexPositionNormal(position, normal));
            return (ushort)(vertices.Count - 1);
        }

        public void Triangle(ushort a, ushort b, ushort c) => indices.AddRange([a, b, c]);

        /// <summary>Two triangles for four corners in counterclockwise order, seen from the front.</summary>
        public void Quad(ushort a, ushort b, ushort c, ushort d) => indices.AddRange([a, b, c, a, c, d]);

        public ZoneMeshData Build() => new(vertices.ToArray(), indices.ToArray());
    }
}

// MIT License - Copyright (c) Callum McGing, Librelancer Contributors 2013-2026
// Vendored from Librelancer src/Editor/LancerEdit/GameContent/ZoneRenderer.cs at commit dcbcf8080dec4a10ebe1ef28b89a0a6d122fe415.
// docs/VENDORED.md has the Librelancer license text and the OpenLancer changes to this file.

using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.Graphics.Vertices;
using LibreLancer.Render;
using LibreLancer.Render.Materials;
using LibreLancer.Resources;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// Draws zone volumes as translucent shells. Call <see cref="Begin"/>, add the zones of the frame with the Draw
/// methods, then call <see cref="Finish"/> inside a renderer hook after the opaque objects.
/// </summary>
internal sealed class ZoneRenderer : IDisposable
{
    private static readonly IVertexType VertexType = new VertexPositionNormal();

    private readonly RenderContext rstate;
    private readonly ZoneMeshes meshes;
    private readonly ZoneVolumeMaterial material;
    private readonly WorldMatrixBuffer buf = new();
    private readonly List<ZoneToDraw> draws = [];
    private Vector3 viewPosition;

    private struct ZoneToDraw
    {
        public WorldMatrixHandle W;
        public ZoneMesh Mesh;
        public Color4 Color;
        public float Ring;
        public bool FlipNormals;
        public float Distance;
    }

    public ZoneRenderer(RenderContext context, ResourceManager resources)
    {
        rstate = context;
        meshes = new ZoneMeshes(context);
        // One material for all draws. Each new material takes a new global material key.
        material = new ZoneVolumeMaterial(resources);
    }

    /// <summary>
    /// Starts a frame. <see cref="Finish"/> sorts the draws by their distance from <paramref name="cameraPosition"/>.
    /// </summary>
    public void Begin(Vector3 cameraPosition)
    {
        viewPosition = cameraPosition;
    }

    public void DrawCube(
        Vector3 position,
        Vector3 size,
        Matrix4x4 rotation,
        Color4 color,
        bool inZone)
    {
        var w = Matrix4x4.CreateScale(size) *
                rotation *
                Matrix4x4.CreateTranslation(position);
        Mesh(meshes.Cube, ref w, position, color, inZone, 0);
    }

    public void DrawCylinder(
        Vector3 position,
        float radius,
        float height,
        Matrix4x4 rotation,
        Color4 color,
        bool inZone)
    {
        var w = Matrix4x4.CreateScale(new Vector3(radius, height, radius)) *
                rotation *
                Matrix4x4.CreateTranslation(position);
        Mesh(meshes.Cylinder, ref w, position, color, inZone, 0);
    }

    private void Mesh(ZoneMesh mesh, ref Matrix4x4 w, Vector3 position, Color4 color, bool flipNormals, float ring)
    {
        draws.Add(new ZoneToDraw()
        {
            W = buf.SubmitMatrix(ref w), Mesh = mesh, Color = color, FlipNormals = flipNormals, Ring = ring,
            Distance = Vector3.DistanceSquared(viewPosition, position),
        });
    }

    public void DrawRing(
        Vector3 position,
        float innerRadius,
        float outerRadius,
        float height,
        Matrix4x4 rotation,
        Color4 color,
        bool inZone)
    {
        if (innerRadius / outerRadius < float.Epsilon) {
            DrawCylinder(position, outerRadius, height, rotation, color, inZone);
            return;
        }
        var w = Matrix4x4.CreateScale(new Vector3(outerRadius, height, outerRadius)) *
                rotation *
                Matrix4x4.CreateTranslation(position);
        Mesh(meshes.Ring, ref w, position, color, inZone, innerRadius / outerRadius);
    }

    public void DrawSphere(
        Vector3 position,
        float radius,
        Matrix4x4 rotation,
        Color4 color,
        bool inZone)
    {
        var w = Matrix4x4.CreateScale(radius) *
                rotation *
                Matrix4x4.CreateTranslation(position);
        Mesh(meshes.Sphere, ref w, position, color, inZone, 0);
    }

    public void DrawEllipsoid(
        Vector3 position,
        Vector3 size,
        Matrix4x4 rotation,
        Color4 color,
        bool inZone)
    {
        var w = Matrix4x4.CreateScale(size) *
                rotation *
                Matrix4x4.CreateTranslation(position);
        Mesh(meshes.Sphere, ref w, position, color, inZone, 0);
    }

    /// <summary>
    /// Draws the zones of the frame from far to near, because they blend without depth write. Then it restores the
    /// render state.
    /// </summary>
    public void Finish()
    {
        draws.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        var wireframe = rstate.Wireframe;
        var blendMode = rstate.BlendMode;
        rstate.Wireframe = false;
        rstate.Cull = true;
        rstate.DepthWrite = false;
        foreach (var draw in draws)
        {
            material.Dc = draw.Color;
            material.World = draw.W;
            material.RadiusRatio = draw.Ring;
            material.Use(rstate, VertexType, ref Lighting.Empty, 0);
            rstate.CullFace = draw.FlipNormals ? CullFaces.Front : CullFaces.Back;
            draw.Mesh.Draw();
        }
        rstate.CullFace = CullFaces.Back;
        buf.Reset();
        draws.Clear();
        rstate.Cull = true;
        rstate.DepthWrite = true;
        rstate.BlendMode = blendMode;
        rstate.Wireframe = wireframe;
    }

    public void Dispose()
    {
        meshes.Dispose();
        buf.Dispose();
    }
}

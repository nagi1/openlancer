// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package
// Vendored from Librelancer src/Editor/LancerEdit/Model/GridRender.cs at commit dcbcf8080dec4a10ebe1ef28b89a0a6d122fe415.
// docs/VENDORED.md has the Librelancer license text and the OpenLancer changes to this file.

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.Graphics.Vertices;

namespace OpenLancer.Editor.Scene;

// Adapted from http://asliceofrendering.com/scene%20helper/2020/01/05/InfiniteGrid/
/// <summary>
/// An infinite grid on the ecliptic plane (Y = 0). The fragment shader finds the point on the plane for each pixel, so
/// the grid needs no line geometry. Two levels of lines show at a time, and the levels follow the camera height.
/// </summary>
internal sealed class GridRenderer : IDisposable
{
    private readonly VertexBuffer vertices;
    private readonly ElementBuffer elements;
    private readonly Shader shader;

    public GridRenderer(RenderContext context)
    {
        shader = ShaderBundle.FromResource<GridRenderer>(context, "Grid.bin").Get(0);
        vertices = new VertexBuffer(context, typeof(VertexPosition), 25);
        vertices.SetData<VertexPosition>(new[]
        {
            new VertexPosition(new Vector3(-1.0f, -1.0f, 0f)),
            new VertexPosition(new Vector3(-0.5f, -1.0f, 0f)),
            new VertexPosition(new Vector3( 0.0f, -1.0f, 0f)),
            new VertexPosition(new Vector3( 0.5f, -1.0f, 0f)),
            new VertexPosition(new Vector3( 1.0f, -1.0f, 0f)),

            new VertexPosition(new Vector3(-1.0f, -0.5f, 0f)),
            new VertexPosition(new Vector3(-0.5f, -0.5f, 0f)),
            new VertexPosition(new Vector3( 0.0f, -0.5f, 0f)),
            new VertexPosition(new Vector3( 0.5f, -0.5f, 0f)),
            new VertexPosition(new Vector3( 1.0f, -0.5f, 0f)),

            new VertexPosition(new Vector3(-1.0f,  0.0f, 0f)),
            new VertexPosition(new Vector3(-0.5f,  0.0f, 0f)),
            new VertexPosition(new Vector3( 0.0f,  0.0f, 0f)),
            new VertexPosition(new Vector3( 0.5f,  0.0f, 0f)),
            new VertexPosition(new Vector3( 1.0f,  0.0f, 0f)),

            new VertexPosition(new Vector3(-1.0f,  0.5f, 0f)),
            new VertexPosition(new Vector3(-0.5f,  0.5f, 0f)),
            new VertexPosition(new Vector3( 0.0f,  0.5f, 0f)),
            new VertexPosition(new Vector3( 0.5f,  0.5f, 0f)),
            new VertexPosition(new Vector3( 1.0f,  0.5f, 0f)),

            new VertexPosition(new Vector3(-1.0f,  1.0f, 0f)),
            new VertexPosition(new Vector3(-0.5f,  1.0f, 0f)),
            new VertexPosition(new Vector3( 0.0f,  1.0f, 0f)),
            new VertexPosition(new Vector3( 0.5f,  1.0f, 0f)),
            new VertexPosition(new Vector3( 1.0f,  1.0f, 0f))
        });
        elements = new ElementBuffer(context, 96);
        elements.SetData(new ushort[]
        {
            0, 5, 1,   1, 5, 6,
            1, 6, 2,   2, 6, 7,
            2, 7, 3,   3, 7, 8,
            3, 8, 4,   4, 8, 9,
            5, 10, 6,  6, 10, 11,
            6, 11, 7,  7, 11, 12,
            7, 12, 8,  8, 12, 13,
            8, 13, 9,  9, 13, 14,
            10, 15, 11,  11, 15, 16,
            11, 16, 12,  12, 16, 17,
            12, 17, 13,  13, 17, 18,
            13, 18, 14,  14, 18, 19,
            15, 20, 16,  16, 20, 21,
            16, 21, 17,  17, 21, 22,
            17, 22, 18,  18, 22, 23,
            18, 23, 19,  19, 23, 24
        });
        vertices.SetElementBuffer(elements);
    }

    /// <summary>The grid levels for one camera height.</summary>
    /// <param name="Scale">The number of coarse lines per world unit. The fine lines are ten times as dense.</param>
    /// <param name="FineAlpha">The opacity of the fine lines, from 1 at the bottom of a level to 0 at its top.</param>
    /// <param name="FadeDistance">The distance along the plane where the grid is gone.</param>
    internal readonly record struct Levels(float Scale, float FineAlpha, float FadeDistance);

    /// <summary>
    /// The levels for a camera at <paramref name="height"/> above or below the plane. The coarse spacing is the power
    /// of ten below the height. The fine lines fade out continuously while the camera goes up, so the grid does not
    /// jump when the levels change.
    /// </summary>
    internal static Levels LevelsFor(float height)
    {
        var h = Math.Max(Math.Abs(height), 1);
        var level = MathF.Log10(h);
        var step = MathF.Floor(level);
        return new Levels(MathF.Pow(10, -step), 1 - (level - step), 20 * h);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FragmentVariables
    {
        public Color4 Color;
        public Color4 AxisXColor;
        public Color4 AxisZColor;
        public Matrix4x4 ViewProjection;
        public Vector3 CameraPosition;
        public float FadeDistance;
        public float Scale;
        public float FineAlpha;
    }

    /// <summary>
    /// Draws the grid with depth test and without depth write. Call it after the opaque objects, so that they hide the
    /// grid, and before the transparent objects, so that they blend over it.
    /// </summary>
    public void Draw(RenderContext rstate, ICamera camera, Color4 color, Color4 axisX, Color4 axisZ)
    {
        var levels = LevelsFor(camera.Position.Y);
        Matrix4x4.Invert(camera.ViewProjection, out var inverseViewProjection);
        var fv = new FragmentVariables
        {
            Color = color,
            AxisXColor = axisX,
            AxisZColor = axisZ,
            ViewProjection = camera.ViewProjection,
            CameraPosition = camera.Position,
            FadeDistance = levels.FadeDistance,
            Scale = levels.Scale,
            FineAlpha = levels.FineAlpha,
        };
        shader.SetUniformBlock(0, ref inverseViewProjection);
        shader.SetUniformBlock(3, ref fv);
        var wf = rstate.Wireframe;
        rstate.Wireframe = false;
        rstate.Cull = false;
        rstate.BlendMode = BlendMode.Normal;
        //Draw
        rstate.Shader = shader;
        rstate.DepthWrite = false;
        vertices.Draw(PrimitiveTypes.TriangleList, 32);
        rstate.DepthWrite = true;
        //Restore State
        rstate.BlendMode = BlendMode.Opaque;
        rstate.Cull = true;
        rstate.Wireframe = wf;
    }

    public void Dispose()
    {
        vertices.Dispose();
        elements.Dispose();
    }
}

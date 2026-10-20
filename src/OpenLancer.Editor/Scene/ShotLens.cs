using System.Numerics;
using LibreLancer;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The engine camera of an image of the agent: the matrices of a <see cref="ShotCamera"/> for an image of a size, with
/// two planes. The result of <c>render_scene</c> projects its items with the same camera, so the pixel places fit the
/// image.
/// </summary>
internal sealed class ShotLens : ICamera
{
    private readonly BoundingFrustum frustum;

    /// <param name="planes">The near and the far plane.</param>
    public ShotLens(ShotCamera shot, int width, int height, Vector2 planes)
    {
        View = shot.View;
        Projection = shot.Projection(width / (float)height, planes.X, planes.Y);
        ViewProjection = View * Projection;
        Position = shot.Position;
        frustum = new BoundingFrustum(ViewProjection);
    }

    /// <summary>The camera of the image with the planes of the camera.</summary>
    public static ShotLens Of(ShotCamera shot, int width, int height) =>
        new(shot, width, height, new Vector2(shot.Near, shot.Far));

    public Matrix4x4 ViewProjection { get; }

    public Matrix4x4 Projection { get; }

    public Matrix4x4 View { get; }

    public Vector3 Position { get; }

    public bool FrustumCheck(BoundingSphere sphere) => frustum.Intersects(sphere);

    public bool FrustumCheck(BoundingBox box) => frustum.Intersects(box);
}

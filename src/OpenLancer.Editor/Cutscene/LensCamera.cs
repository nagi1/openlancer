using System.Numerics;
using LibreLancer;
using LibreLancer.Render;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The free camera of the Cutscene view: a pose and a field of view as a cutscene camera has them. The field of view
/// follows the rule of <c>ThnCamera</c> (ThnCamera.cs:68-75): <c>fovh</c> is half the horizontal angle for a 4:3
/// screen, and a wider screen widens it. The vertical angle follows from the ratio of the view. So Align view to
/// camera shows what the camera shows, across the width of the view.
/// </summary>
/// <remarks>With the default field of view of 35 degrees and the screen ratio of the view, the projection is the one
/// of the Scene camera (<c>LookAtCamera</c> with <c>GameFOV</c>).</remarks>
internal sealed class LensCamera : ICamera
{
    /// <summary>The field of view of the editor camera, as <c>LookAtCamera</c> has it with <c>GameFOV</c>.</summary>
    public const float DefaultFovH = 35;

    private BoundingFrustum frustum = new(Matrix4x4.Identity);

    public Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;

    public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;

    public Matrix4x4 View { get; private set; } = Matrix4x4.Identity;

    public Vector3 Position { get; private set; }

    /// <summary>Sets the pose and the lens for a view of a size.</summary>
    /// <param name="fovH">Half the horizontal angle for a 4:3 screen, in degrees.</param>
    /// <param name="screenAspect">The ratio of the screen whose width the angle fills.</param>
    /// <param name="zRange">The near and the far plane.</param>
    public void Update(float width, float height, Vector3 position, Quaternion orientation, float fovH,
        float screenAspect, Vector2 zRange)
    {
        var ratio = width / height;
        var fovV = FOVUtil.FovVRad(FOVUtil.CalcFovx(fovH, screenAspect) * 2, ratio);
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(fovV, ratio, zRange.X, zRange.Y);
        View = new Transform3D(position, orientation).Inverse().Matrix();
        ViewProjection = View * Projection;
        frustum = new BoundingFrustum(ViewProjection);
        Position = position;
    }

    public bool FrustumCheck(BoundingSphere sphere) => frustum.Intersects(sphere);

    public bool FrustumCheck(BoundingBox box) => frustum.Intersects(box);
}

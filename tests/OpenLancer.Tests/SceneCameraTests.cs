using System.Numerics;
using LibreLancer.Render.Cameras;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

public sealed class SceneCameraTests
{
    private static readonly Vector3 Center = new(100, 0, 200);

    private static (SceneCamera Camera, float Distance) Framed()
    {
        var camera = new SceneCamera();
        camera.Frame(Center, 1_000);
        return (camera, Vector3.Distance(camera.Position, Center));
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, but got {actual}.");

    [Fact]
    public void GivesAnExactTopViewWithMinusZUp()
    {
        var (camera, distance) = Framed();

        camera.LookAlong(-Vector3.UnitY);

        Near(Center + Vector3.UnitY * distance, camera.Position, 0.01f);
        var view = new LookAtCamera();
        var sky = new LookAtCamera();
        camera.Apply(view, sky, 800, 600);
        // In view space, X is right, Y is up and the camera looks along -Z.
        Near(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, view.View), 1e-4f);
        Near(Vector3.UnitY, Vector3.TransformNormal(-Vector3.UnitZ, view.View), 1e-4f);
        Near(-Vector3.UnitZ, Vector3.TransformNormal(-Vector3.UnitY, view.View), 1e-4f);
        // Straight down, the world up (+Y) is on the view axis. The sky camera must turn its up with the scene camera.
        Assert.Equal(view.View, sky.View);
    }

    /// <summary>The pivot is the orbit point, where Create puts a new item (decision D234 of the M3 design).</summary>
    [Fact]
    public void ThePivotIsTheOrbitPoint()
    {
        var (camera, _) = Framed();

        Assert.Equal(Center, camera.Pivot);
        camera.LookAlong(-Vector3.UnitX);
        Assert.Equal(Center, camera.Pivot);
    }

    [Fact]
    public void LooksAlongAnAxisFromTheSameDistanceAtTheSameOrbitPoint()
    {
        var (camera, distance) = Framed();

        camera.LookAlong(-Vector3.UnitX);
        Near(Center + Vector3.UnitX * distance, camera.Position, 0.01f);

        camera.LookAlong(Vector3.UnitZ);
        Near(Center - Vector3.UnitZ * distance, camera.Position, 0.01f);
    }

    [Fact]
    public void GivesTheSkyCameraTheSameViewWithTheNearPlaneOfTheStarsphere()
    {
        // The whole-system view of Li01: its objects are up to 97,205 units from their mean position, so the camera
        // is 155,528 units away.
        var camera = new SceneCamera();
        camera.Frame(Center, 97_205);
        var scene = new LookAtCamera { GameFOV = true };
        var sky = new LookAtCamera();

        camera.Apply(scene, sky, 1280, 720);

        Assert.Equal(155.5f, NearPlane(scene), 0.1f);
        Assert.Equal(2.5f, NearPlane(sky), 0.001f);
        // The largest retail starsphere part has a radius of 19,456 units. A far plane below that clips it.
        Assert.Equal(1_000_000f, FarPlane(sky), 10_000f);
        Assert.Equal(scene.View, sky.View);
        // The same field of view and aspect.
        Assert.Equal(scene.Projection.M11, sky.Projection.M11);
        Assert.Equal(scene.Projection.M22, sky.Projection.M22);
    }

    /// <summary>
    /// Play from camera gives the ship the orientation of the camera (section 9.5 of the M4 design). A ship flies along
    /// its -Z, so the orientation must turn -Z into the view direction and +Y into the up of the view.
    /// </summary>
    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(1, -0.5f, -2)]
    [InlineData(-3, 1, 0.5f)]
    [InlineData(0, -1, 0)]
    public void TheOrientationTurnsMinusZIntoTheViewDirectionAndPlusYIntoTheUpOfTheView(float x, float y, float z)
    {
        var (camera, _) = Framed();
        var direction = Vector3.Normalize(new Vector3(x, y, z));
        camera.LookAlong(direction);
        var view = new LookAtCamera();
        camera.Apply(view, new LookAtCamera(), 800, 600);

        var orientation = camera.Orientation;

        Assert.Equal(1, orientation.Length(), 1e-5f);
        Near(direction, Vector3.Transform(-Vector3.UnitZ, orientation), 1e-4f);
        // In view space, the view direction is -Z and the up of the view is +Y.
        Near(-Vector3.UnitZ, Vector3.TransformNormal(Vector3.Transform(-Vector3.UnitZ, orientation), view.View), 1e-4f);
        Near(Vector3.UnitY, Vector3.TransformNormal(Vector3.Transform(Vector3.UnitY, orientation), view.View), 1e-4f);
        // The camera has no roll: the right side of the ship stays level.
        Assert.Equal(0, Vector3.Transform(Vector3.UnitX, orientation).Y, 1e-5f);
    }

    // The near plane of a System.Numerics perspective projection: M43 / M33 = (near * far / (near - far)) /
    // (far / (near - far)).
    private static float NearPlane(LookAtCamera camera) => camera.Projection.M43 / camera.Projection.M33;

    // The far plane: M43 / (M33 + 1) = (near * far / (near - far)) / (near / (near - far)). M33 is near -1, so the sum
    // loses float precision. For the sky camera, the result is within 0.2 percent of the far plane.
    private static float FarPlane(LookAtCamera camera) => camera.Projection.M43 / (camera.Projection.M33 + 1);
}

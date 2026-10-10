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
        camera.Apply(view, 800, 600);
        // In view space, X is right, Y is up and the camera looks along -Z.
        Near(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, view.View), 1e-4f);
        Near(Vector3.UnitY, Vector3.TransformNormal(-Vector3.UnitZ, view.View), 1e-4f);
        Near(-Vector3.UnitZ, Vector3.TransformNormal(-Vector3.UnitY, view.View), 1e-4f);
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
}

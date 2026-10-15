using System;
using LibreLancer.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rules of the editor's cutscene player that need no GPU. The bench of the editor builds the full scene.
/// </summary>
public sealed class CutscenePlayerTests
{
    /// <summary>
    /// The room camera must be a CAMERA entity of the scene. <c>SetCamera</c> takes any name, and the first draw then
    /// reads the lens of the object. So a missing object and an object without camera props stop the build with a
    /// clear message.
    /// </summary>
    [Fact]
    public void TheRoomCameraMustBeACameraEntity()
    {
        var camera = new ThnSceneObject { Name = "Camera_0", Camera = new ThnCameraProps() };
        CutscenePlayer.RequireCamera(camera, "Camera_0");

        var missing = Assert.Throws<InvalidOperationException>(() => CutscenePlayer.RequireCamera(null, "Camera_9"));
        var marker = Assert.Throws<InvalidOperationException>(() =>
            CutscenePlayer.RequireCamera(new ThnSceneObject { Name = "X/Shipcentre/01" }, "X/Shipcentre/01"));

        Assert.Contains("Camera_9", missing.Message);
        Assert.Contains("X/Shipcentre/01", marker.Message);
        Assert.Contains("[Camera] name", marker.Message);
    }
}

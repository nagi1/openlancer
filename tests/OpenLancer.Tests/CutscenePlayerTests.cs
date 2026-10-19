using System;
using System.Numerics;
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

    /// <summary>
    /// A chain of attachments that is too deep to resolve throws in a step, and the clock catches it (decision D655).
    /// The view then reads the poses of the stopped player in each frame. So after a step that failed, a pose is the
    /// pose of the last update, and the chain does not resolve again.
    /// </summary>
    [Fact]
    public void AStoppedPlayerGivesThePoseOfTheLastUpdate()
    {
        const int depth = 200_000;
        var chain = new ThnSceneObject[depth];
        for (var i = depth - 1; i >= 0; i--)
        {
            chain[i] = new ThnSceneObject { Name = $"m{i}", Translate = new Vector3(i, 0, 0), Rotate = Quaternion.Identity };
            if (i < depth - 1)
            {
                chain[i].Attachments.Add(new ThnAttachment(new ThnObjectParent(chain[i + 1], null, null)) { Position = true });
            }
        }

        Assert.Throws<InvalidOperationException>(() => CutscenePlayer.PoseOf(chain[0], false));

        var pose = CutscenePlayer.PoseOf(chain[0], true);
        Assert.Equal(Vector3.Zero, pose.Position);
        Assert.Equal(Quaternion.Identity, pose.Orientation);
        Assert.Equal(new Vector3(5, 0, 0), CutscenePlayer.PoseOf(chain[5], true).Position);
    }
}

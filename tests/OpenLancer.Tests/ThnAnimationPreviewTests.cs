using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Utf.Anm;
using OpenLancer.Editor.Inspector;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The camera controls and the loop length of the clip preview, without a render (section 12.1 of the M8b
/// design). The controls follow the Unity animation preview.</summary>
public sealed class ThnAnimationPreviewTests
{
    [Fact]
    public void TheButtonsFollowTheUnityAnimationPreview()
    {
        Assert.Equal(PreviewDrag.Pan, ThnAnimationPreview.DragOf(ImGuiMouseButton.Left, alt: false));
        Assert.Equal(PreviewDrag.Pan, ThnAnimationPreview.DragOf(ImGuiMouseButton.Middle, alt: false));
        Assert.Equal(PreviewDrag.Orbit, ThnAnimationPreview.DragOf(ImGuiMouseButton.Right, alt: false));
        Assert.Equal(PreviewDrag.Orbit, ThnAnimationPreview.DragOf(ImGuiMouseButton.Left, alt: true));
        Assert.Equal(PreviewDrag.Zoom, ThnAnimationPreview.DragOf(ImGuiMouseButton.Right, alt: true));
    }

    /// <summary>A pan moves the center in the plane of the view, by an amount that grows with the distance. With a yaw
    /// of 90 degrees, a horizontal drag moves the center along Z, not X.</summary>
    [Fact]
    public void APanMovesInThePlaneOfTheView()
    {
        var horizontal = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0), Quaternion.Identity, 2, 400);
        var vertical = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(0, 20), Quaternion.Identity, 2, 400);
        var farther = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0), Quaternion.Identity, 4, 400);
        var turned = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0),
            Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0), 2, 400);

        Assert.True(horizontal.X > 0);
        Assert.Equal(0, horizontal.Z, 5);
        Assert.True(vertical.Y > 0);
        Assert.Equal(horizontal.X * 2, farther.X, 5);
        Assert.Equal(0, turned.X, 5);
        Assert.Equal(-horizontal.X, turned.Z, 5);
    }

    /// <summary>The pitch of an orbit stops at 90 degrees up and down, as in the Unity animation preview. The yaw has no
    /// limit.</summary>
    [Fact]
    public void AnOrbitStopsAtNinetyDegrees()
    {
        var (yaw, pitch) = ThnAnimationPreview.Orbit(0, 0, new Vector2(10, -20));
        Assert.Equal(-0.1f, yaw, 5);
        Assert.Equal(0.2f, pitch, 5);

        Assert.Equal(MathF.PI / 2, ThnAnimationPreview.Orbit(0, 0, new Vector2(0, -1000)).Pitch, 5);
        Assert.Equal(-MathF.PI / 2, ThnAnimationPreview.Orbit(0, 0, new Vector2(0, 1000)).Pitch, 5);
        Assert.Equal(-10f, ThnAnimationPreview.Orbit(0, 0, new Vector2(1000, 0)).Yaw, 5);
    }

    /// <summary>A wheel step up and a drag to the right or up move the camera closer. The zoom has limits.</summary>
    [Fact]
    public void TheZoomMovesCloserAndHasLimits()
    {
        Assert.True(ThnAnimationPreview.Zoom(1, 1) < 1);
        Assert.True(ThnAnimationPreview.Zoom(1, -1) > 1);
        Assert.Equal(0.35f, ThnAnimationPreview.Zoom(1, 100));
        Assert.Equal(4f, ThnAnimationPreview.Zoom(1, -100));
        Assert.True(ThnAnimationPreview.DragZoom(new Vector2(10, 0)) > 0);
        Assert.True(ThnAnimationPreview.DragZoom(new Vector2(0, -10)) > 0);
        Assert.True(ThnAnimationPreview.DragZoom(new Vector2(-10, 10)) < 0);
    }

    /// <summary>A loop lasts as long as the longest channel, also when an object map, for example the root motion, is
    /// longer than the joint maps.</summary>
    [Fact]
    public void ALoopCoversTheJointMapsAndTheObjectMaps()
    {
        var script = new Script("motion");
        script.JointMaps.Add(new JointMap { Channel = new Channel { FrameCount = 5, Interval = 0.5f } });
        script.JointMaps.Add(new JointMap { Channel = new Channel { FrameCount = 3, Interval = 0.25f } });
        Assert.Equal(2f, ThnAnimationPreview.ClipDuration(script));

        script.ObjectMaps.Add(new ObjectMap { Channel = new Channel { FrameCount = 7, Interval = 0.5f } });
        Assert.Equal(3f, ThnAnimationPreview.ClipDuration(script));
        Assert.Equal(0.1f, ThnAnimationPreview.ClipDuration(new Script("empty")));
    }
}

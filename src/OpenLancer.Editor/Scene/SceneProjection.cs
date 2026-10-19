using System.Numerics;
using LibreLancer;
using LibreLancer.Render.Cameras;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The camera of one Scene frame, the scene rectangle on the screen and the UI scale. The markers, the brackets, the
/// axis widget and the picker use it, so a click finds what the frame shows.
/// </summary>
internal readonly struct SceneProjection
{
    private readonly Matrix4x4 viewProjection;
    private readonly Matrix4x4 inverseView;
    private readonly Vector3 eye;

    // The X and Y scale of the projection matrix.
    private readonly Vector2 focal;

    /// <param name="camera">The camera after its update for this frame.</param>
    /// <param name="origin">The top-left corner of the scene on the screen, in pixels.</param>
    /// <param name="size">The size of the scene, in pixels.</param>
    /// <param name="uiScale">The pixels of one point of the UI.</param>
    public SceneProjection(LookAtCamera camera, Vector2 origin, Vector2 size, float uiScale)
        : this(camera, camera.ZRange.X, origin, size, uiScale)
    {
    }

    /// <param name="camera">The camera after its update for this frame.</param>
    /// <param name="near">The distance of the near plane of the camera.</param>
    /// <param name="origin">The top-left corner of the scene on the screen, in pixels.</param>
    /// <param name="size">The size of the scene, in pixels.</param>
    /// <param name="uiScale">The pixels of one point of the UI.</param>
    public SceneProjection(ICamera camera, float near, Vector2 origin, Vector2 size, float uiScale)
    {
        View = camera.View;
        viewProjection = camera.ViewProjection;
        Matrix4x4.Invert(View, out inverseView);
        focal = new Vector2(camera.Projection.M11, camera.Projection.M22);
        eye = camera.Position;
        Near = near;
        Origin = origin;
        Size = size;
        UiScale = uiScale;
    }

    public Matrix4x4 View { get; }

    /// <summary>The position of the camera.</summary>
    public Vector3 Eye => eye;

    /// <summary>The distance of the near plane from the camera, along the view direction.</summary>
    public float Near { get; }

    /// <summary>The top-left corner of the scene on the screen, in pixels.</summary>
    public Vector2 Origin { get; }

    /// <summary>The size of the scene, in pixels.</summary>
    public Vector2 Size { get; }

    /// <summary>The pixels of one point of the UI. The sizes of the markers and the widgets are in points.</summary>
    public float UiScale { get; }

    /// <summary>
    /// Projects a world position to the screen. <paramref name="depth"/> is the distance from the camera along the
    /// view direction. Returns false for a position behind the camera.
    /// </summary>
    public bool Project(Vector3 position, out Vector2 screen, out float depth)
    {
        var clip = Clip(position);
        depth = clip.W;
        if (clip.W <= 0)
        {
            screen = default;
            return false;
        }

        screen = Screen(clip);
        return true;
    }

    /// <summary>
    /// The screen point of a world position, also for a position behind the camera (section 7.3 of the M6a design). A
    /// position in front of the camera gives its projected point. A position behind the camera gives a point far
    /// outside the scene, in the direction that the camera must turn to see it. So a line to the point ends at the edge
    /// of the scene with an arrow.
    /// </summary>
    public Vector2 Toward(Vector3 position)
    {
        var clip = Clip(position);
        if (clip.W > 0)
        {
            return Screen(clip);
        }

        // Behind the camera, X and Y of the clip position keep the side of the position, and the screen Y goes down.
        var direction = new Vector2(clip.X, -clip.Y);
        direction = direction.LengthSquared() > 1e-12f ? Vector2.Normalize(direction) : Vector2.UnitY;
        return Origin + Size / 2 + direction * 4 * (Size.X + Size.Y);
    }

    /// <summary>The clip position of a world position. W is the distance from the camera along the view direction.
    /// A clip position changes linearly with the world position.</summary>
    public Vector4 Clip(Vector3 position) => Vector4.Transform(new Vector4(position, 1), viewProjection);

    /// <summary>The screen point of a clip position in front of the camera.</summary>
    public Vector2 Screen(Vector4 clip)
    {
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return Origin + new Vector2((ndc.X + 1) * 0.5f * Size.X, (1 - ndc.Y) * 0.5f * Size.Y);
    }

    /// <summary>True when a screen point is in the scene rectangle, or out of it by less than the margin.</summary>
    public bool InView(Vector2 screen, float margin) =>
        screen.X >= Origin.X - margin && screen.Y >= Origin.Y - margin &&
        screen.X <= Origin.X + Size.X + margin && screen.Y <= Origin.Y + Size.Y + margin;

    /// <summary>
    /// The ray of the camera through a point on the screen. It starts where it crosses the near plane, because nothing
    /// nearer to the camera shows. Its direction has the length 1.
    /// </summary>
    public Ray Ray(Vector2 screen)
    {
        var ndc = new Vector2((screen.X - Origin.X) / Size.X * 2 - 1, 1 - (screen.Y - Origin.Y) / Size.Y * 2);
        // In view space the camera looks along -Z, and the projection scales X and Y by its focal values. So this
        // direction gets to the near plane at Near times its length. This stays exact far from the world origin, where
        // the inverse of the whole view projection loses precision.
        var direction = Vector3.TransformNormal(new Vector3(ndc.X / focal.X, ndc.Y / focal.Y, -1), inverseView);
        return new Ray(eye + direction * Near, Vector3.Normalize(direction));
    }
}

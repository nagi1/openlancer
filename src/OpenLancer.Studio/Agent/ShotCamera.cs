using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Studio.Agent;

/// <summary>The place of an item in an image of the agent, in pixels.</summary>
/// <param name="X">The distance of the center from the left edge of the image.</param>
/// <param name="Y">The distance of the center from the top edge of the image.</param>
/// <param name="Radius">The radius of the bounding sphere of the item in the image.</param>
/// <param name="Distance">The distance of the center from the camera along the view direction, in meters.</param>
public readonly record struct ShotPoint(float X, float Y, float Radius, float Distance);

/// <summary>
/// The camera of an offscreen image of the agent (tool <c>render_scene</c>, section 7.7 of the MCP1 design): a position,
/// a point that the camera looks at and the horizontal angle of the image. It gives the matrices of the render and the
/// pixel places of the items. So the places in the result fit the image. The top of the image is toward +Y. A view
/// straight down or straight up has -Z at the top of the image, as the axis views of the Scene have it.
/// </summary>
/// <param name="Fov">The horizontal angle of the whole image, in degrees.</param>
/// <remarks>The camera has no type of the engine and makes no GPU call. The editor makes the camera of the render from
/// its matrices.</remarks>
public sealed record ShotCamera(Vector3 Position, Vector3 LookAt, float Fov)
{
    /// <summary>The smallest horizontal angle that a call can give, in degrees.</summary>
    public const float MinFov = 5;

    /// <summary>The largest horizontal angle that a call can give, in degrees.</summary>
    public const float MaxFov = 150;

    /// <summary>The far plane, as the Scene camera has it for a star system.</summary>
    public const float Far = 10_000_000;

    /// <summary>The direction of a framed view, as the Scene frames a sphere: from the front and 0.6 radians above
    /// (<c>SceneCamera.Frame</c>).</summary>
    public static readonly Vector3 FrameDirection = Vector3.Normalize(new Vector3(0, -MathF.Sin(0.6f), -MathF.Cos(0.6f)));

    /// <summary>A view direction nearer than this to straight up or straight down has -Z at the top of the
    /// image.</summary>
    private const float VerticalLimit = 0.9999f;

    /// <summary>The smallest radius of a framed sphere, in meters, as the Scene has it for a small object.</summary>
    private const float MinFrameRadius = 10;

    /// <summary>The framed sphere fills this part of the narrower angle of the image.</summary>
    private const float FrameFill = 0.9f;

    /// <summary>
    /// The horizontal angle of the Scene camera in an image of a ratio, in degrees. The Scene camera has the angle of
    /// the game camera: 35 degrees on each side of a 4:3 screen, wider on a wider screen.
    /// </summary>
    public static float SceneFov(float aspect)
    {
        var half = MathF.Atan(aspect / 2 / (4f / 3 / 2 / MathF.Tan(35 * MathF.PI / 180)));
        return 2 * half * 180 / MathF.PI;
    }

    /// <summary>
    /// The camera that shows all of a set of spheres from the frame direction of the Scene, with the angle of the
    /// Scene camera for the ratio of the image. A set without a sphere frames a sphere of 10 km at the origin.
    /// </summary>
    /// <param name="aspect">The width of the image divided by its height.</param>
    /// <param name="fov">The horizontal angle, or null for the angle of the Scene camera.</param>
    public static ShotCamera Frame(IEnumerable<(Vector3 Center, float Radius)> spheres, float aspect, float? fov = null)
    {
        var list = spheres.ToList();
        var angle = fov ?? SceneFov(aspect);
        var (center, radius) = list.Count == 0 ? (Vector3.Zero, 10_000f) : Bounds(list);
        radius = Math.Max(radius, MinFrameRadius);
        var vertical = VerticalAngle(angle, aspect);
        var narrow = Math.Min(angle * MathF.PI / 180, vertical);
        var distance = radius / MathF.Sin(narrow / 2) / FrameFill;
        return new ShotCamera(center - FrameDirection * distance, center, angle);
    }

    /// <summary>A sphere that holds each sphere of a set: the center of their box, and the farthest edge from
    /// it.</summary>
    internal static (Vector3 Center, float Radius) Bounds(IReadOnlyList<(Vector3 Center, float Radius)> spheres)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (c, r) in spheres)
        {
            min = Vector3.Min(min, c - new Vector3(r));
            max = Vector3.Max(max, c + new Vector3(r));
        }
        var center = (min + max) / 2;
        return (center, spheres.Max(s => Vector3.Distance(center, s.Center) + s.Radius));
    }

    /// <summary>The distance from the camera to the point that it looks at.</summary>
    public float Distance => Vector3.Distance(Position, LookAt);

    /// <summary>The near plane: a part of the distance to the point that the camera looks at, between 1 m and 1 km, as
    /// the Scene camera has it. So the depth stays precise at each distance.</summary>
    public float Near => Math.Clamp(Distance * 0.001f, 1, 1000);

    /// <summary>The direction of the view, with the length 1.</summary>
    public Vector3 Forward => Vector3.Normalize(LookAt - Position);

    /// <summary>The direction of the top of the image.</summary>
    public Vector3 Up
    {
        get
        {
            var forward = Forward;
            return MathF.Abs(forward.Y) > VerticalLimit ? -Vector3.UnitZ : Vector3.UnitY;
        }
    }

    /// <summary>The view matrix.</summary>
    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, LookAt, Up);

    /// <summary>The projection of an image of a ratio, with two planes.</summary>
    /// <param name="aspect">The width of the image divided by its height.</param>
    public Matrix4x4 Projection(float aspect, float near, float far) =>
        Matrix4x4.CreatePerspectiveFieldOfView(VerticalAngle(Fov, aspect), aspect, near, far);

    /// <summary>
    /// The place of a sphere in an image of a size, or null when the image does not show it: its center is behind the
    /// near plane, or its circle is outside the image.
    /// </summary>
    public ShotPoint? Project(Vector3 center, float radius, int width, int height)
    {
        var aspect = Aspect(width, height);
        var projection = Projection(aspect, Near, Far);
        var clip = Vector4.Transform(new Vector4(center, 1), View * projection);
        if (clip.W <= Near)
            return null;
        var x = (clip.X / clip.W + 1) * 0.5f * width;
        var y = (1 - clip.Y / clip.W) * 0.5f * height;
        var pixels = radius * projection.M22 * height / 2 / clip.W;
        if (x + pixels < 0 || y + pixels < 0 || x - pixels > width || y - pixels > height)
            return null;
        return new ShotPoint(x, y, pixels, clip.W);
    }

    /// <summary>
    /// The items that an image shows, the largest first, and the number of all items that it shows. An item of the
    /// same size comes after a nearer item.
    /// </summary>
    /// <param name="max">The most items of the list.</param>
    public (IReadOnlyList<(T Item, ShotPoint Point)> Items, int Total) Visible<T>(
        IEnumerable<(T Item, Vector3 Center, float Radius)> items, int width, int height, int max)
    {
        var shown = items.Select(i => (i.Item, Point: Project(i.Center, i.Radius, width, height)))
            .Where(i => i.Point != null)
            .Select(i => (i.Item, Point: i.Point!.Value))
            .ToList();
        var top = shown.OrderByDescending(i => i.Point.Radius).ThenBy(i => i.Point.Distance).Take(max).ToList();
        return (top, shown.Count);
    }

    /// <summary>The vertical angle of an image of a ratio with a horizontal angle in degrees, in radians.</summary>
    internal static float VerticalAngle(float fov, float aspect) =>
        2 * MathF.Atan(MathF.Tan(fov * MathF.PI / 180 / 2) / aspect);

    private static float Aspect(int width, int height) => width / (float)height;
}

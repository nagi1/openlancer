using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Render;
using LibreLancer.World;

namespace OpenLancer.Editor.Scene;

/// <summary>What a click in the Scene hits.</summary>
/// <param name="Axis">The direction of the end of the axis widget under the mouse, or null.</param>
/// <param name="Item">The object, zone or light under the mouse, or null for empty space.</param>
internal readonly record struct ScenePick(Vector3? Axis, object? Item);

/// <summary>The part of a ray in a volume: from the distance Enter to the distance Exit, in units of the length of the
/// ray direction. It is empty when Enter is after Exit.</summary>
internal readonly record struct RaySpan(float Enter, float Exit)
{
    public static readonly RaySpan All = new(float.NegativeInfinity, float.PositiveInfinity);
    public static readonly RaySpan None = new(float.PositiveInfinity, float.NegativeInfinity);

    /// <summary>True when the volume holds the origin of the ray.</summary>
    public bool HoldsOrigin => Enter <= 0 && Exit > 0;

    public RaySpan And(RaySpan other) => new(Math.Max(Enter, other.Enter), Math.Min(Exit, other.Exit));
}

/// <summary>
/// Finds what a click in the Scene selects, in this order:
/// <list type="number">
/// <item>The axis widget, then the markers as they show. A marker that faded out with the distance cannot be
/// picked.</item>
/// <item>A ray against the pick shape of each object of the system, with no distance limit. The nearest hit
/// wins.</item>
/// <item>Only when the ray hits no object: the zone volumes and the patrol path lines that the Zones toggle shows. The
/// nearest hit wins.</item>
/// </list>
/// The ray starts at the near plane, because nothing nearer to the camera shows. A shape that holds the start of the ray
/// is not hit. Without this rule, the large box of a station around the camera would take every click. A patrol path
/// line stops at the near plane for the same reason, and its distance is measured from the start of the ray too. This
/// class has no ImGui call and no GPU call.
/// </summary>
/// <param name="axes">The axis widget of the frame.</param>
/// <param name="markers">The markers of the frame.</param>
/// <param name="objects">The world object of each object of the system.</param>
internal sealed class ScenePicker(
    AxisGizmo axes,
    SceneMarkers markers,
    IReadOnlyDictionary<SystemObject, GameObject> objects)
{
    // A click this near to a patrol path line on the screen hits the path, in unscaled points.
    private const float PathMargin = 4;

    // The radius of an object without a model, and the smallest radius of an object with a model.
    private const float NoModelRadius = 500;
    private const float MinModelRadius = 50;

    /// <summary>What a click at a point of the screen hits.</summary>
    /// <param name="showZones">True when the Zones toggle shows the zone volumes and the patrol paths.</param>
    public ScenePick Pick(SceneProjection projection, Vector2 mouse, SystemIndex index, bool showZones)
    {
        if (axes.HitTest(mouse) is { } axis)
        {
            return new ScenePick(axis, null);
        }

        if (markers.HitTest(mouse) is { } marker)
        {
            return new ScenePick(null, marker);
        }

        var ray = projection.Ray(mouse);
        var item = (object?)NearestObject(ray) ?? (showZones ? NearestZone(projection, ray, mouse, index) : null);
        return new ScenePick(null, item);
    }

    /// <summary>
    /// The radius of an object for the camera focus and its pick sphere: the radius of its model, at least 50, or 500
    /// for an object without a model.
    /// </summary>
    public static float Radius(RigidModel? model) =>
        model == null ? NoModelRadius : Math.Max(model.GetRadius(), MinModelRadius);

    /// <summary>
    /// The distance along a ray to where it enters the volume of a zone, or null when it misses the volume or starts in
    /// it.
    /// </summary>
    internal static float? ZoneEntry(Zone zone, Ray ray) => Entry(ZoneSpan(zone, ray));

    /// <summary>
    /// The part of a ray in the volume of a zone. The volumes are the volumes that the Scene draws. A sphere and an
    /// ellipsoid have the size as their radii, and a box has the size as its full extent. A cylinder and a ring stand on
    /// their Y axis, and their size is (outer radius, height, inner radius). The pick uses the entry, and the spawn check
    /// of Play uses the exit (section 9.5 of the M4 design).
    /// </summary>
    internal static RaySpan ZoneSpan(Zone zone, Ray ray)
    {
        // In the frame of the zone. The rotation keeps lengths, so a distance in this frame is a world distance.
        var inverse = Matrix4x4.Transpose(zone.RotationMatrix);
        var origin = Vector3.TransformNormal(ray.Position - zone.Position, inverse);
        var direction = Vector3.TransformNormal(ray.Direction, inverse);
        var size = zone.Size;
        return zone.Shape switch
        {
            ShapeKind.Sphere => Ellipsoid(origin, direction, new Vector3(size.X)),
            ShapeKind.Ellipsoid => Ellipsoid(origin, direction, size),
            ShapeKind.Box => Box(origin, direction, size),
            ShapeKind.Cylinder => Tube(origin, direction, size.X, 0, size.Y),
            ShapeKind.Ring => Tube(origin, direction, size.X, size.Z, size.Y),
            _ => RaySpan.None,
        };
    }

    private SystemObject? NearestObject(Ray ray)
    {
        var nearest = new Nearest<SystemObject>();
        foreach (var (obj, gameObject) in objects)
        {
            nearest.Offer(obj, Entry(ObjectSpan(obj, gameObject, ray)));
        }

        return nearest.Item;
    }

    private static Zone? NearestZone(SceneProjection projection, Ray ray, Vector2 mouse, SystemIndex index)
    {
        var nearest = new Nearest<Zone>();
        foreach (var zone in index.VolumeZones)
        {
            nearest.Offer(zone, ZoneEntry(zone, ray));
        }

        // A patrol path shows as a line, so the click must be near the line. Its cylinder is not a volume of the
        // Scene, and a camera in that cylinder does not stop the hit.
        var margin = PathMargin * projection.UiScale;
        foreach (var zone in index.PatrolPaths)
        {
            var (start, end) = SystemIndex.PathLine(zone);
            var point = LinePoint(projection, start, end, mouse, margin);
            nearest.Offer(zone, point is { } hit ? Vector3.Distance(ray.Position, hit) : null);
        }

        return nearest.Item;
    }

    /// <summary>
    /// The part of a ray in the pick shape of an object. A planet is a sphere with <see cref="Radius"/>, and a sun is a
    /// sphere with the radius of its star. Any other object with a model is the box of its model, turned with the
    /// object. An object without a model is a sphere with <see cref="Radius"/>. The pick uses the entry, and the spawn
    /// check of Play uses the exit (section 9.5 of the M4 design).
    /// </summary>
    internal static RaySpan ObjectSpan(SystemObject obj, GameObject gameObject, Ray ray)
    {
        var transform = gameObject.Transform;
        var model = gameObject.RenderComponent is ModelRenderer { Model: { } drawn } ? drawn : null;
        if (obj.Archetype?.Type == ArchetypeType.planet)
        {
            return SphereSpan(ray, transform.Position, Radius(model));
        }

        if (obj.Star is { } star)
        {
            return SphereSpan(ray, transform.Position, star.Radius);
        }

        // A model without a mesh has an empty box, with its minimum above its maximum. A ray never enters such a box, so
        // the object gets the sphere below.
        if (model?.GetBoundingBox() is { } box && box.Min.X <= box.Max.X && box.Min.Y <= box.Max.Y &&
            box.Min.Z <= box.Max.Z)
        {
            // In the frame of the model. The turn keeps lengths, so a distance in this frame is a world distance.
            var origin = transform.InverseTransform(ray.Position) - (box.Min + box.Max) / 2;
            var direction = Vector3.Transform(ray.Direction, Quaternion.Conjugate(transform.Orientation));
            return Box(origin, direction, box.Max - box.Min);
        }

        return SphereSpan(ray, transform.Position, Radius(model));
    }

    private static RaySpan SphereSpan(Ray ray, Vector3 center, float radius) =>
        Ellipsoid(ray.Position - center, ray.Direction, new Vector3(radius));

    /// <summary>
    /// The world point of a line under the mouse, or null when the line on the screen does not pass within the margin
    /// of the mouse.
    /// </summary>
    private static Vector3? LinePoint(SceneProjection projection, Vector3 start, Vector3 end, Vector2 mouse,
        float margin)
    {
        var a = projection.Clip(start);
        var b = projection.Clip(end);
        var near = projection.Near;
        if (a.W < near && b.W < near)
        {
            return null;
        }

        // The part of the line that is nearer than the near plane does not show, so the line stops at the plane.
        if (a.W < near)
        {
            var cut = (near - a.W) / (b.W - a.W);
            start = Vector3.Lerp(start, end, cut);
            a = Vector4.Lerp(a, b, cut);
        }
        else if (b.W < near)
        {
            var cut = (near - b.W) / (a.W - b.W);
            end = Vector3.Lerp(end, start, cut);
            b = Vector4.Lerp(b, a, cut);
        }

        var screenStart = projection.Screen(a);
        var line = projection.Screen(b) - screenStart;
        var lengthSquared = line.LengthSquared();
        var s = lengthSquared > 0 ? Math.Clamp(Vector2.Dot(mouse - screenStart, line) / lengthSquared, 0, 1) : 0;
        if (Vector2.Distance(screenStart + s * line, mouse) > margin)
        {
            return null;
        }

        // A part of the line on the screen is not the same part of the line in the world, because 1 / W changes
        // linearly along the screen line.
        var u = s / b.W / ((1 - s) / a.W + s / b.W);
        return Vector3.Lerp(start, end, u);
    }

    /// <summary>
    /// The distance along a ray to where it enters a volume. Null when the ray misses the volume, when the volume is
    /// behind the ray, or when the volume holds the start of the ray.
    /// </summary>
    private static float? Entry(RaySpan span) => span.Enter > 0 && span.Enter <= span.Exit ? span.Enter : null;

    /// <summary>The part of a ray where one coordinate is from -half to half.</summary>
    private static RaySpan Slab(float origin, float direction, float half)
    {
        if (direction == 0)
        {
            return Math.Abs(origin) <= half ? RaySpan.All : RaySpan.None;
        }

        var a = (-half - origin) / direction;
        var b = (half - origin) / direction;
        return a < b ? new RaySpan(a, b) : new RaySpan(b, a);
    }

    /// <summary>The part of a ray in a box around the origin with the size as its full extent.</summary>
    private static RaySpan Box(Vector3 origin, Vector3 direction, Vector3 size) =>
        Slab(origin.X, direction.X, size.X / 2).And(Slab(origin.Y, direction.Y, size.Y / 2))
            .And(Slab(origin.Z, direction.Z, size.Z / 2));

    /// <summary>
    /// The part of a ray in an ellipsoid around the origin with these radii. The math starts at the point of the ray
    /// nearest to the center, so that a ray from far away loses no precision.
    /// </summary>
    private static RaySpan Ellipsoid(Vector3 origin, Vector3 direction, Vector3 radii)
    {
        if (radii.X <= 0 || radii.Y <= 0 || radii.Z <= 0)
        {
            return RaySpan.None;
        }

        // In these units the ellipsoid is a sphere with the radius 1. A point of the ray keeps its distance t.
        var o = origin / radii;
        var d = direction / radii;
        var a = d.LengthSquared();
        var t = -Vector3.Dot(o, d) / a;
        var rest = 1 - (o + t * d).LengthSquared();
        if (rest < 0)
        {
            return RaySpan.None;
        }

        var half = MathF.Sqrt(rest / a);
        return new RaySpan(t - half, t + half);
    }

    /// <summary>The part of a ray in an endless cylinder of a radius around the Y axis.</summary>
    private static RaySpan Radial(Vector3 origin, Vector3 direction, float radius)
    {
        var a = direction.X * direction.X + direction.Z * direction.Z;
        if (a == 0)
        {
            return origin.X * origin.X + origin.Z * origin.Z <= radius * radius ? RaySpan.All : RaySpan.None;
        }

        // From the point of the ray nearest to the axis, as in Ellipsoid.
        var t = -(origin.X * direction.X + origin.Z * direction.Z) / a;
        var x = origin.X + t * direction.X;
        var z = origin.Z + t * direction.Z;
        var rest = radius * radius - (x * x + z * z);
        if (rest < 0)
        {
            return RaySpan.None;
        }

        var half = MathF.Sqrt(rest / a);
        return new RaySpan(t - half, t + half);
    }

    /// <summary>
    /// The part of a ray in a tube that stands on the Y axis: from -height/2 to height/2, between the inner and the
    /// outer radius. A cylinder is a tube with the inner radius 0. A ray can pass through the hole of a tube, so the
    /// tube can hold two parts of the ray. This gives the first part that ends after the origin of the ray.
    /// </summary>
    private static RaySpan Tube(Vector3 origin, Vector3 direction, float outer, float inner, float height)
    {
        var span = Slab(origin.Y, direction.Y, height / 2).And(Radial(origin, direction, outer));
        if (inner <= 0)
        {
            return span;
        }

        var hole = Radial(origin, direction, inner);
        var start = Math.Max(span.Enter, 0);
        // A ray that is in the hole where it enters the tube, or at its origin, enters the wall where it leaves the
        // hole.
        if (hole.Enter < start && start < hole.Exit)
        {
            return span with { Enter = hole.Exit };
        }

        // A ray that reaches the hole after that point leaves the wall where it enters the hole.
        return hole.Enter >= start ? span with { Exit = Math.Min(span.Exit, hole.Enter) } : span;
    }

    /// <summary>The item with the smallest hit distance of the items that it got.</summary>
    private struct Nearest<T>() where T : class
    {
        private float distance = float.PositiveInfinity;

        public T? Item { get; private set; }

        public void Offer(T item, float? hit)
        {
            if (hit is { } value && value < distance)
            {
                Item = item;
                distance = value;
            }
        }
    }
}

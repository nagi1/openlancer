using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Render;
using LibreLancer.World;

namespace OpenLancer.Editor.Scene;

/// <summary>What the Scene shows, for the pick: the pick follows what the user can see.</summary>
/// <param name="Camera">The position of the camera.</param>
/// <param name="Zones">True when the Zones toggle shows the zone volumes, the patrol paths and the zone markers.</param>
/// <param name="Nebulae">True when the Nebulae toggle shows the nebulae.</param>
/// <param name="Fog">True when the Fog toggle shows the fog of the nebula that holds the camera.</param>
internal readonly record struct PickView(Vector3 Camera, bool Zones, bool Nebulae, bool Fog);

/// <summary>What a click in the Scene hits.</summary>
/// <param name="Axis">The direction of the end of the axis widget under the mouse, or null.</param>
/// <param name="Items">The items under the mouse, each one once, in the order of the pick stack (see
/// <see cref="ScenePicker"/>). Empty for empty space and for an axis end.</param>
internal readonly record struct ScenePick(Vector3? Axis, IReadOnlyList<object> Items)
{
    /// <summary>The first item of the stack, which a click selects, or null for empty space.</summary>
    public object? Item => Items is { Count: > 0 } items ? items[0] : null;
}

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
/// Finds what a click in the Scene hits: the axis widget, or a stack of all the items under the mouse (milestone M22).
/// The stack has four layers, in this order:
/// <list type="number">
/// <item>The glyphs under the mouse: the object and light markers, then the nebula and field markers. The overlay draws
/// each glyph on top of the 3D scene, so its item comes first. A marker that faded out cannot be picked.</item>
/// <item>The objects that the ray hits, the nearest entry first, with no distance limit.</item>
/// <item>The nebulae and asteroid fields that the user can see (see <see cref="CanPick"/>). First the volumes that the
/// ray enters ahead, the nearest entry first. Then the volumes that hold the start of the ray: first the nebulae in file
/// order, then the fields, the smallest first. The renderer shows the fog of the first nebula.</item>
/// <item>The zone diamonds under the mouse. Then, with the Zones toggle on, the patrol path lines and the zone volumes
/// that the ray enters. Last, the zone volumes that hold the start of the ray, the smallest first. A diamond shows only
/// with the Zones toggle, or for the selected zone, so each diamond that shows can be picked.</item>
/// </list>
/// Each item is in the stack once. The ray starts at the near plane, because nothing nearer to the camera shows. An
/// object shape that holds the start of the ray is not hit. Else the large box of a station around the camera would take
/// every click. A volume that holds it is a hit with a lower rank: inside a nebula, the fog covers each pixel. A
/// patrol path line stops at the near plane, and its distance is measured from the start of the ray too. This class has
/// no ImGui call and no GPU call.
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
    public ScenePick Pick(SceneProjection projection, Vector2 mouse, SystemIndex index, PickView view)
    {
        if (axes.HitTest(mouse) is { } axis)
        {
            return new ScenePick(axis, []);
        }

        var stack = new Stack();
        var glyphs = markers.HitAll(mouse);
        foreach (var marker in glyphs)
        {
            if (marker.Item is not Zone && index.VolumeOf(marker.Item) == null)
            {
                stack.Add(marker.Item);
            }
        }

        foreach (var marker in glyphs)
        {
            if (index.VolumeOf(marker.Item) != null)
            {
                stack.Add(marker.Item);
            }
        }

        var ray = projection.Ray(mouse);
        stack.AddRange(Objects(ray));
        stack.AddRange(Volumes(index, view, ray));
        foreach (var marker in glyphs)
        {
            if (marker.Item is Zone)
            {
                stack.Add(marker.Item);
            }
        }

        if (view.Zones)
        {
            stack.AddRange(Zones(projection, ray, mouse, index));
        }

        return new ScenePick(null, stack.Items);
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
        var (origin, direction) = InZone(zone, ray);
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

    /// <summary>
    /// The part of a ray in the pick shape of a nebula or a field (section 6.2 of the M22 design). A nebula with puffs
    /// and a sphere or ellipsoid zone is the ellipsoid of <see cref="NebulaPickRadii"/> in the frame of its zone. So a
    /// click on an outer puff selects it. Each other nebula and each field is its zone.
    /// </summary>
    internal static RaySpan VolumeSpan(VolumeItem volume, Ray ray)
    {
        if (volume.Item is Nebula nebula && NebulaPickRadii(nebula) is { } radii)
        {
            var (origin, direction) = InZone(volume.Zone, ray);
            return Ellipsoid(origin, direction, radii);
        }

        return ZoneSpan(volume.Zone, ray);
    }

    /// <summary>
    /// The radii of the pick shape of a nebula: the zone radii plus the largest puff radius on each axis. The renderer
    /// puts the center of each puff on the surface of the zone ellipsoid. A puff is up to
    /// <c>bit_radius * (1 + bit_radius_random_variation)</c> in size (NebulaRenderer.cs:385). Null for a nebula without
    /// puffs, and for a box, cylinder or ring zone, which draws no puffs. The pick shape is then the zone.
    /// </summary>
    internal static Vector3? NebulaPickRadii(Nebula nebula) =>
        nebula.HasExteriorBits && nebula.Zone != null && NebulaRenderer.ZoneRadii(nebula.Zone) is { } radii
            ? radii + new Vector3(nebula.ExteriorBitRadius * (1 + nebula.ExteriorBitRandomVariation))
            : null;

    /// <summary>
    /// The volume of a zone, to put the smallest volume first. A sphere or an ellipsoid gives 4/3 pi times the product
    /// of its radii, and a box the product of its sizes. A cylinder gives pi r^2 h, and a ring pi (R^2 - r^2) h. With
    /// <paramref name="radii"/>, the volume of the ellipsoid of these radii.
    /// </summary>
    internal static float VolumeSize(Zone zone, Vector3? radii = null)
    {
        var size = zone.Size;
        const float sphere = 4 * MathF.PI / 3;
        if (radii is { } r)
        {
            return sphere * r.X * r.Y * r.Z;
        }

        return zone.Shape switch
        {
            ShapeKind.Sphere => sphere * size.X * size.X * size.X,
            ShapeKind.Ellipsoid => sphere * size.X * size.Y * size.Z,
            ShapeKind.Box => size.X * size.Y * size.Z,
            ShapeKind.Cylinder => MathF.PI * size.X * size.X * size.Y,
            ShapeKind.Ring => MathF.PI * (size.X * size.X - size.Z * size.Z) * size.Y,
            _ => 0,
        };
    }

    /// <summary>
    /// True when a nebula or a field is a target of the pick, because the user can see it. Unity has the same rule
    /// (decision D157 of the M22 design). With the Zones toggle on, each volume shows, so each one is a target. Else a
    /// nebula is a target when the Nebulae toggle shows it and the renderer draws it. With the fog of <paramref name="fogNebula"/>, only that
    /// nebula draws (SystemRenderer.cs:236-252). Without the fog, a nebula draws a cloud only for a sphere or ellipsoid
    /// zone with <c>fill_shape</c> or puffs (NebulaRenderer.cs:548-552). A field is a target when its asteroids draw: the
    /// camera is within the largest zone size plus <c>fill_dist</c> of the zone center (AsteroidFieldRenderer.cs:45-57,
    /// :109). A field with a band and a sphere or ellipsoid zone draws its band from any distance (:66-80).
    /// </summary>
    /// <param name="fogNebula">The nebula whose fog the renderer shows (see <see cref="FogNebula"/>), or null.</param>
    internal static bool CanPick(VolumeItem volume, Nebula? fogNebula, PickView view)
    {
        if (view.Zones)
        {
            return true;
        }

        var zone = volume.Zone;
        var round = NebulaRenderer.ZoneRadii(zone) != null;
        switch (volume.Item)
        {
            case Nebula nebula:
                if (!view.Nebulae)
                {
                    return false;
                }

                return fogNebula != null
                    ? ReferenceEquals(nebula, fogNebula)
                    : round && (nebula.ExteriorFill != null || nebula.HasExteriorBits);
            case AsteroidField field:
                var reach = Math.Max(zone.Size.X, Math.Max(zone.Size.Y, zone.Size.Z)) + field.FillDist;
                return Vector3.DistanceSquared(view.Camera, zone.Position) <= reach * reach ||
                       field.Band != null && round;
            default:
                return false;
        }
    }

    /// <summary>
    /// The nebula whose fog the renderer shows, or null when no fog shows. With the Nebulae and Fog toggles on, it is the
    /// first nebula in file order that holds the camera (SystemRenderer.cs:389-402).
    /// </summary>
    internal static Nebula? FogNebula(SystemIndex index, PickView view) =>
        view.Nebulae && view.Fog
            ? index.System.Nebulae.FirstOrDefault(n => n.Zone?.ContainsPoint(view.Camera) == true)
            : null;

    /// <summary>Layer 2: the objects that the ray enters, the nearest entry first.</summary>
    private IEnumerable<object> Objects(Ray ray)
    {
        var hits = new List<(object Item, float Value)>();
        foreach (var (obj, gameObject) in objects)
        {
            if (Entry(ObjectSpan(obj, gameObject, ray)) is { } entry)
            {
                hits.Add((obj, entry));
            }
        }

        return Sorted(hits);
    }

    /// <summary>
    /// Layer 3: the nebulae and fields that are targets. First the volumes that the ray enters ahead, the nearest entry
    /// first. Then the volumes that hold the start of the ray: the nebulae in file order, then the fields, the smallest
    /// first. Decision D153 of the M22 design gives this order.
    /// </summary>
    private static IEnumerable<object> Volumes(SystemIndex index, PickView view, Ray ray)
    {
        var fog = FogNebula(index, view);
        var ahead = new List<(object Item, float Value)>();
        var nebulae = new List<(object Item, float Value)>();
        var fields = new List<(object Item, float Value)>();
        foreach (var volume in index.VolumeItems)
        {
            if (!CanPick(volume, fog, view))
            {
                continue;
            }

            var span = VolumeSpan(volume, ray);
            if (Entry(span) is { } entry)
            {
                ahead.Add((volume.Item, entry));
            }
            else if (span.HoldsOrigin && volume.Item is Nebula nebula)
            {
                nebulae.Add((nebula, index.System.Nebulae.IndexOf(nebula)));
            }
            else if (span.HoldsOrigin)
            {
                fields.Add((volume.Item, VolumeSize(volume.Zone)));
            }
        }

        return Sorted(ahead).Concat(Sorted(nebulae)).Concat(Sorted(fields));
    }

    /// <summary>
    /// Layer 4 without the diamonds. First come the patrol path lines and the zone volumes that the ray enters, by the
    /// distance from the start of the ray. Then come the zone volumes that hold the start of the ray, the smallest
    /// first.
    /// </summary>
    private static IEnumerable<object> Zones(SceneProjection projection, Ray ray, Vector2 mouse, SystemIndex index)
    {
        var ahead = new List<(object Item, float Value)>();
        var holders = new List<(object Item, float Value)>();
        foreach (var zone in index.VolumeZones)
        {
            var span = ZoneSpan(zone, ray);
            if (Entry(span) is { } entry)
            {
                ahead.Add((zone, entry));
            }
            else if (span.HoldsOrigin)
            {
                holders.Add((zone, VolumeSize(zone)));
            }
        }

        // A patrol path shows as a line, so the click must be near the line. Its cylinder is not a volume of the
        // Scene, and a camera in that cylinder does not stop the hit.
        var margin = PathMargin * projection.UiScale;
        foreach (var zone in index.PatrolPaths)
        {
            var (start, end) = SystemIndex.PathLine(zone);
            if (LinePoint(projection, start, end, mouse, margin) is { } hit)
            {
                ahead.Add((zone, Vector3.Distance(ray.Position, hit)));
            }
        }

        return Sorted(ahead).Concat(Sorted(holders));
    }

    /// <summary>The items by their value, the smallest first. Items with the same value keep their order.</summary>
    private static IEnumerable<object> Sorted(List<(object Item, float Value)> items) =>
        items.OrderBy(i => i.Value).Select(i => i.Item);

    /// <summary>The origin and the direction of a ray in the frame of a zone. The rotation keeps lengths, so a distance
    /// in this frame is a world distance.</summary>
    private static (Vector3 Origin, Vector3 Direction) InZone(Zone zone, Ray ray)
    {
        var inverse = Matrix4x4.Transpose(zone.RotationMatrix);
        return (Vector3.TransformNormal(ray.Position - zone.Position, inverse),
            Vector3.TransformNormal(ray.Direction, inverse));
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

    /// <summary>The pick stack: each item once, in the order of the first add.</summary>
    private sealed class Stack
    {
        private readonly HashSet<object> added = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> items = [];

        public IReadOnlyList<object> Items => items;

        public void Add(object item)
        {
            if (added.Add(item))
            {
                items.Add(item);
            }
        }

        public void AddRange(IEnumerable<object> range)
        {
            foreach (var item in range)
            {
                Add(item);
            }
        }
    }
}

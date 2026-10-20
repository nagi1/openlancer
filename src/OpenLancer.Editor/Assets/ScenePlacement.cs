using System;
using System.Numerics;

namespace OpenLancer.Editor.Assets;

/// <summary>
/// The point of a drop of an asset in the Scene (decision D275 of the M23 design): the point on the grid plane Y = 0
/// under the cursor, or the point on the ray at the distance of the orbit pivot. Unity first places an instance on the
/// surface under the cursor. The editor does not, because it knows no offset for the new model, and the new object would
/// go into the hull of the other object (review finding R22).
/// </summary>
internal static class ScenePlacement
{
    /// <summary>A hit on the plane counts only up to this many distances from the camera to the pivot. So a flat view
    /// angle does not put the point far away.</summary>
    public const float MaxPlaneDistance = 4;

    /// <summary>
    /// The point of a drop: the hit of the ray on the plane Y = 0 when it is in front of the camera and at most
    /// <see cref="MaxPlaneDistance"/> times the distance from the camera to the pivot. Else the point on the ray at the
    /// distance of the pivot. Each value rounds to a whole number (decision D234). The point never lies behind the camera.
    /// </summary>
    /// <param name="eye">The position of the camera.</param>
    /// <param name="direction">The unit direction of the ray under the cursor.</param>
    /// <param name="pivot">The orbit point of the camera.</param>
    public static Vector3 Point(Vector3 eye, Vector3 direction, Vector3 pivot)
    {
        var distance = Vector3.Distance(eye, pivot);
        var point = eye + direction * distance;
        if (direction.Y != 0)
        {
            var t = -eye.Y / direction.Y;
            if (t > 0 && t <= MaxPlaneDistance * distance)
                point = eye + direction * t;
        }
        return new Vector3(MathF.Round(point.X), MathF.Round(point.Y), MathF.Round(point.Z));
    }

    /// <summary>
    /// The point of "Pick in Scene" on empty space (section 8.9 of the M6b design): the hit of the ray on the horizontal
    /// plane of the camera pivot when it is in front of the camera and at most <see cref="MaxPlaneDistance"/> times the
    /// distance from the camera to the pivot. Else the point on the ray at the distance of the pivot. Each value rounds
    /// to a whole number.
    /// </summary>
    public static Vector3 PivotPlanePoint(Vector3 eye, Vector3 direction, Vector3 pivot)
    {
        var distance = Vector3.Distance(eye, pivot);
        var point = eye + direction * distance;
        if (direction.Y != 0)
        {
            var t = (pivot.Y - eye.Y) / direction.Y;
            if (t > 0 && t <= MaxPlaneDistance * distance)
                point = eye + direction * t;
        }
        return new Vector3(MathF.Round(point.X), MathF.Round(point.Y), MathF.Round(point.Z));
    }

    /// <summary>True when the point lies on the grid plane, so the ghost needs no line down to the plane.</summary>
    public static bool OnPlane(Vector3 point) => point.Y == 0;
}

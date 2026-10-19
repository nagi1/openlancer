using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Thn;
using LibreLancer.Thn.Events;

namespace OpenLancer.Runtime.Thn;

/// <summary>
/// The rules that put a space script into its star system (decisions D644 and D658 of the cutscene fixes plan). The
/// class has no engine objects, so the tests check the rules without a GPU.
/// </summary>
/// <remarks>
/// <para>In the game, the <c>main_object</c> marker of a space script takes the pose of the main object of the mission
/// line in each frame. The preview has no mission, so the main object stands still at its start in the mission data.
/// Many scripts build their shot around a marker at the origin. Some scripts put the marker at a system position, and
/// their cameras stand at system positions too. So only a marker within 1 m of the origin takes the start pose.</para>
/// <para>A static COMPOUND of such a local layout stands in for a mission object near the main object, for example the
/// ship of the player or an escort. It keeps its pose relative to the marker. The retail stand-ins of a local layout
/// stand at most 1.5 km from the origin, and the stand-ins at system positions stand 41 km or more from it.</para>
/// </remarks>
internal static class SpaceFrame
{
    /// <summary>A <c>main_object</c> marker within this distance of the origin takes the start pose of the main
    /// object.</summary>
    public const float OriginRadius = 1;

    /// <summary>A static COMPOUND nearer than this distance to the origin is part of the local layout.</summary>
    public const float LocalRadius = 5000;

    /// <summary>True when the marker stands within 1 m of the origin. A marker without a position stands at the
    /// origin.</summary>
    public static bool MovesMarker(Vector3? markerPosition) =>
        (markerPosition ?? Vector3.Zero).Length() <= OriginRadius;

    /// <summary>
    /// The names of the COMPOUND entities of a script that follow the main object (decision D658): no event moves them
    /// (they are not the first target of an ATTACH_ENTITY, START_PATH_ANIMATION or START_SPATIAL_PROP_ANIM event), so
    /// they follow no other entity, and they stand less than 5 km from the origin.
    /// </summary>
    public static IReadOnlyList<string> StaticStandIns(ThnScript script)
    {
        var moved = new HashSet<string>(script.Events
            .Where(e => e is AttachEntityEvent or StartPathAnimationEvent or StartSpatialPropAnimEvent &&
                        e.Targets.Length > 0)
            .Select(e => e.Targets[0]), StringComparer.OrdinalIgnoreCase);
        return script.Entities.Values
            .Where(e => e.Type == EntityTypes.Compound && !e.MainObject &&
                        (e.ObjectFlags & ThnObjectFlags.Reference) != ThnObjectFlags.Reference &&
                        !moved.Contains(e.Name) && (e.Position ?? Vector3.Zero).Length() < LocalRadius)
            .Select(e => e.Name)
            .ToList();
    }

    /// <summary>The pose of a stand-in that keeps its pose relative to the marker, when the marker moves to the
    /// start.</summary>
    public static Transform3D Place(Transform3D standIn, Transform3D marker, Transform3D start) =>
        standIn * marker.Inverse() * start;

    /// <summary>
    /// The banner about the main object of a space plan (section 3.3 of the cutscene fixes plan), or null when the
    /// script has no <c>main_object</c> marker.
    /// </summary>
    /// <param name="marker">The name of the <c>main_object</c> marker, or null.</param>
    /// <param name="markerPosition">The position of the marker in the script.</param>
    /// <param name="mainObject">The main object of the mission line.</param>
    /// <param name="hasStart">The mission data gives a start pose for the main object.</param>
    public static string? MainObjectNote(string? marker, Vector3? markerPosition, string mainObject, bool hasStart)
    {
        if (marker == null)
        {
            return null;
        }

        if (!MovesMarker(markerPosition))
        {
            return $"The marker {marker} stays at its position in the script. In the game, it follows the main object " +
                   $"{mainObject}.";
        }

        return hasStart
            ? $"The main object {mainObject} stands still at its start in the mission data. In the game, it moves with " +
              "its mission orders."
            : $"The mission data gives no start for the main object {mainObject}. Its marker stays at its position in " +
              "the script.";
    }
}

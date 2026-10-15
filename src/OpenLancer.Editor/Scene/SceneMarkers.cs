using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>A marker on the screen. A zone marker is a diamond, and all other markers are glyphs.</summary>
/// <param name="Item">The selectable item that the marker shows.</param>
/// <param name="Color">The ABGR color of the glyph or the diamond.</param>
/// <param name="Depth">The distance from the camera along the view direction.</param>
/// <param name="Alpha">The opacity after the distance fade.</param>
internal readonly record struct SceneMarker(object Item, ObjectKind Kind, uint Color, Vector2 Screen, float Depth,
    float Alpha);

/// <summary>
/// The markers of one Scene frame: a glyph for each object and light, and a diamond for each zone that has no volume or
/// path line. The overlay draws them, and the picker finds the marker under the mouse in them. A marker that fades out
/// with the distance does not draw and cannot be picked. This class has no ImGui call and no GPU call.
/// </summary>
internal sealed class SceneMarkers
{
    // Sizes in unscaled points.
    private const float PickMargin = 3;
    private const float ViewMargin = 16;

    // A marker with less alpha than this does not draw and cannot be picked.
    private const float MinAlpha = 0.02f;

    // A marker starts to fade at this part of its fade distance.
    private const float FadeStart = 0.6f;

    private readonly List<SceneMarker> markers = [];
    private float scale = 1;

    /// <summary>The markers of the frame, from far to near, so that near markers draw on top of far markers.</summary>
    public IReadOnlyList<SceneMarker> Items => markers;

    /// <summary>Forgets the markers. Call it when a system opens or closes.</summary>
    public void Clear() => markers.Clear();

    /// <summary>
    /// Projects the markers of the system for this frame. The Zones and Lights toggles show the zone and light markers.
    /// The selected zone or light has its marker also when its toggle is off.
    /// </summary>
    public void Update(SceneProjection projection, SystemIndex index, EditorSettings settings, object? selection)
    {
        markers.Clear();
        scale = projection.UiScale;
        if (!settings.ShowMarkers)
        {
            return;
        }

        foreach (var obj in index.System.Objects)
        {
            var kind = ObjectKinds.Of(obj);
            Add(projection, selection, obj, kind, kind.Abgr, obj.Position, (obj.Visit & VisitFlags.Hidden) != 0);
        }

        var selectionAdded = false;
        if (settings.ShowZones)
        {
            foreach (var zone in index.MarkerZones)
            {
                var kind = index.KindOf(zone)!;
                Add(projection, selection, zone, kind, kind.Abgr, zone.Position, false);
                selectionAdded |= ReferenceEquals(zone, selection);
            }
        }

        // A field, nebula or patrol path zone has no diamond. When it is selected, it gets one for its label.
        if (!selectionAdded && selection is Zone selectedZone && index.System.Zones.Contains(selectedZone))
        {
            var kind = index.KindOf(selectedZone)!;
            Add(projection, selection, selectedZone, kind, kind.Abgr, selectedZone.Position, false);
        }

        foreach (var light in index.System.LightSources)
        {
            if (settings.ShowLights || ReferenceEquals(light, selection))
            {
                Add(projection, selection, light, ObjectKinds.Light,
                    EditorTheme.ToAbgr(ObjectKinds.LightColor(light.Light.Color)), light.Light.Position, false);
            }
        }

        // Far markers draw first, so that near markers draw on top of them.
        markers.Sort((a, b) => b.Depth.CompareTo(a.Depth));
    }

    /// <summary>
    /// Projects the markers of other items for this frame, for example the entities of a cutscene. An item compares
    /// with the selection by its equality, so a value such as a cutscene element can be the selection.
    /// </summary>
    public void Update(SceneProjection projection, object? selection,
        IEnumerable<(object Item, ObjectKind Kind, uint Color, Vector3 Position)> items)
    {
        markers.Clear();
        scale = projection.UiScale;
        foreach (var (item, kind, color, position) in items)
        {
            Add(projection, selection, item, kind, color, position, false);
        }

        markers.Sort((a, b) => b.Depth.CompareTo(a.Depth));
    }

    /// <summary>
    /// Adds the marker of an item when it is in view and not faded out. The selected item never fades, so its marker
    /// stays in view. A hidden item draws at half opacity.
    /// </summary>
    private void Add(SceneProjection projection, object? selection, object item, ObjectKind kind, uint color,
        Vector3 position, bool hidden)
    {
        if (!projection.Project(position, out var screen, out var depth) ||
            !projection.InView(screen, ViewMargin * scale))
        {
            return;
        }

        var alpha = Equals(item, selection) ? 1 : Fade(depth, kind.FadeDistance);
        if (hidden)
        {
            alpha *= 0.5f;
        }

        if (alpha >= MinAlpha)
        {
            markers.Add(new SceneMarker(item, kind, color, screen, depth, alpha));
        }
    }

    /// <summary>
    /// The item of the marker nearest to <paramref name="mouse"/>, or null when no marker is under it.
    /// </summary>
    public object? HitTest(Vector2 mouse)
    {
        object? best = null;
        var bestDistance = float.MaxValue;
        // From near to far, so that the nearer marker wins when two markers are at the same place.
        for (var i = markers.Count - 1; i >= 0; i--)
        {
            var marker = markers[i];
            var distance = Vector2.Distance(marker.Screen, mouse);
            if (distance <= (marker.Kind.MarkerSize / 2 + PickMargin) * scale && distance < bestDistance)
            {
                best = marker.Item;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The opacity of a marker at a distance. A kind without a fade distance never fades.</summary>
    internal static float Fade(float depth, float fadeDistance)
    {
        if (float.IsPositiveInfinity(fadeDistance))
        {
            return 1;
        }

        var t = Math.Clamp((depth - FadeStart * fadeDistance) / ((1 - FadeStart) * fadeDistance), 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }
}

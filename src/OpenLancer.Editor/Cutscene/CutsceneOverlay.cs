using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The overlays of the Free mode of the Cutscene view (section 7.6 of the M8 design): a glyph for each entity of the
/// open script, the frustum of each camera, the motion paths, the names and the selection. The poses come from the
/// visible player at the shown frame. A click selects the entity of the glyph under the mouse.
/// </summary>
internal sealed class CutsceneOverlay
{
    // Sizes in unscaled points.
    private const float LabelGap = 4;
    private const float PillRounding = 3;
    private const float SelectionRadius = 10;
    private static readonly Vector2 PillPadding = new(5, 1);

    /// <summary>The length of a frustum, as a part of its distance from the free camera, between two limits in
    /// meters.</summary>
    private const float FrustumScale = 0.12f;

    private const float MinFrustum = 0.3f;
    private const float MaxFrustum = 1000;

    private static readonly uint PillColor = EditorTheme.ToAbgr(EditorTheme.Backdrop with { W = 180 / 255f });
    private static readonly uint LabelColor = EditorTheme.ToAbgr(EditorTheme.Text);
    private static readonly uint SelectedColor = EditorTheme.ToAbgr(EditorTheme.Accent);
    private static readonly uint BrightColor = EditorTheme.ToAbgr(EditorTheme.TextBright);
    private static readonly uint ActiveCameraColor = EditorTheme.ToAbgr(EditorTheme.Warning);

    private readonly List<(PreviewObject Object, ThnRef Entity, ObjectKind Kind)> objects = [];
    private readonly Dictionary<ThnRef, PreviewObject> byEntity = [];

    /// <summary>The markers of this frame. A click finds the entity under the mouse in them.</summary>
    public SceneMarkers Markers { get; } = new();

    /// <summary>
    /// Reads the poses of the entities of the open script from the player, and projects their glyphs. A monitor and the
    /// SCENE entity have no place in the scene. An earlier entity that a later one with the same name hides has the
    /// object of the later one, so it has no glyph.
    /// </summary>
    public void Update(SceneProjection projection, CutscenePlayer player, TimelineModel timeline, object? selection)
    {
        objects.Clear();
        byEntity.Clear();
        foreach (var group in timeline.Groups)
        {
            foreach (var row in group.Rows)
            {
                if (row.Entity is { } entity && row.Mark != TimelineModel.HiddenEntityMark &&
                    player.Find(row.Name) is { Type: not (EntityTypes.Monitor or EntityTypes.Scene) } obj)
                {
                    objects.Add((obj, entity, row.Kind));
                    byEntity[entity] = obj;
                }
            }
        }

        var markers = new List<(object, ObjectKind, uint, Vector3)>(objects.Count);
        foreach (var (obj, entity, kind) in objects)
        {
            var color = obj.LightColor is { } light ? EditorTheme.ToAbgr(ObjectKinds.LightColor(light)) : kind.Abgr;
            markers.Add((entity, kind, color, obj.Pose.Position));
        }

        Markers.Update(projection, selection, markers);
    }

    /// <summary>The pose of an entity of the last update, or null.</summary>
    public PreviewObject? Find(ThnRef entity) => byEntity.GetValueOrDefault(entity);

    /// <summary>Draws the frustums, the paths, the glyphs and the names, clipped to the view.</summary>
    /// <param name="activeCamera">The name of the camera that the shot looks through, or null.</param>
    /// <param name="screenAspect">The screen ratio of the shot, for the width of the frustums.</param>
    /// <param name="viewportRatio">The ratio of the shot viewport, for the height of the frustums.</param>
    /// <param name="names">True to show the names of all entities, not only of the selected and the hovered one.</param>
    public void Draw(SceneProjection projection, object? selection, object? hovered, string? activeCamera,
        float screenAspect, float viewportRatio, bool names)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(projection.Origin, projection.Origin + projection.Size, true);
        var scale = ImGuiHelper.Scale;
        foreach (var (obj, entity, kind) in objects)
        {
            var selected = entity.Equals(selection);
            var thickness = (selected ? 2 : 1) * scale;
            if (obj.Path is { } path)
            {
                var color = selected ? BrightColor : kind.Abgr;
                for (var i = 1; i < path.Count; i++)
                {
                    Segment(drawList, projection, path[i - 1], path[i], color, thickness);
                }
            }

            if (obj.FovH is { } fovH)
            {
                var active = string.Equals(obj.Name, activeCamera, StringComparison.OrdinalIgnoreCase);
                var color = selected ? BrightColor : active ? ActiveCameraColor : kind.Abgr;
                Frustum(drawList, projection, obj.Pose, fovH, screenAspect, viewportRatio, color, thickness);
            }
        }

        foreach (var marker in Markers.Items)
        {
            var pixelSize = MathF.Round(marker.Kind.MarkerSize * scale);
            if (marker.Item.Equals(selection))
            {
                drawList.AddCircle(marker.Screen, SelectionRadius * scale, SelectedColor, 0, 2 * scale);
            }

            Glyphs.DrawShadowed(drawList, marker.Kind.Icon, pixelSize, marker.Screen, marker.Color, marker.Alpha);
        }

        foreach (var marker in Markers.Items)
        {
            var highlight = marker.Item.Equals(selection) || marker.Item.Equals(hovered);
            var obj = Find((ThnRef)marker.Item);
            if (obj != null && (names || highlight ||
                                string.Equals(obj.Name, activeCamera, StringComparison.OrdinalIgnoreCase)))
            {
                Label(drawList, marker, obj.Name, highlight);
            }
        }

        drawList.PopClipRect();
    }

    /// <summary>
    /// The frustum of a cutscene camera: the lines from the lens to the corners of the view and the rectangle of the
    /// view, with a small triangle on the top edge. The width follows the field of view of the camera and the screen
    /// ratio, as the shot has it (ThnCamera.cs:68-75).
    /// </summary>
    private static void Frustum(ImDrawListPtr drawList, SceneProjection projection, Transform3D pose, float fovH,
        float screenAspect, float viewportRatio, uint color, float thickness)
    {
        var length = Math.Clamp(FrustumScale * Vector3.Distance(projection.Eye, pose.Position), MinFrustum, MaxFrustum);
        var tanX = MathF.Tan(MathHelper.DegreesToRadians(FOVUtil.CalcFovx(fovH, screenAspect)));
        var tanY = tanX / viewportRatio;
        var forward = Vector3.Transform(-Vector3.UnitZ, pose.Orientation) * length;
        var right = Vector3.Transform(Vector3.UnitX, pose.Orientation) * length * tanX;
        var up = Vector3.Transform(Vector3.UnitY, pose.Orientation) * length * tanY;
        var apex = pose.Position;
        Span<Vector3> corners =
        [
            apex + forward - right + up, apex + forward + right + up, apex + forward + right - up,
            apex + forward - right - up,
        ];
        for (var i = 0; i < 4; i++)
        {
            Segment(drawList, projection, apex, corners[i], color, thickness);
            Segment(drawList, projection, corners[i], corners[(i + 1) % 4], color, thickness);
        }

        var top = apex + forward + up;
        var peak = top + up * 0.5f;
        Segment(drawList, projection, top - right * 0.3f, peak, color, thickness);
        Segment(drawList, projection, peak, top + right * 0.3f, color, thickness);
    }

    /// <summary>
    /// A line between two points of the scene. The part behind the near plane does not draw: the clip position changes
    /// linearly along the line, so the cut is exact.
    /// </summary>
    internal static void Segment(ImDrawListPtr drawList, SceneProjection projection, Vector3 a, Vector3 b, uint color,
        float thickness)
    {
        if (Clip(projection, a, b) is { } line)
        {
            drawList.AddLine(line.A, line.B, color, thickness);
        }
    }

    /// <summary>The screen points of the part of a line in front of the near plane, or null when no part is.</summary>
    internal static (Vector2 A, Vector2 B)? Clip(SceneProjection projection, Vector3 a, Vector3 b)
    {
        var near = projection.Near;
        var ca = projection.Clip(a);
        var cb = projection.Clip(b);
        if (ca.W < near && cb.W < near)
        {
            return null;
        }

        if (ca.W < near)
        {
            ca = Vector4.Lerp(ca, cb, (near - ca.W) / (cb.W - ca.W));
        }
        else if (cb.W < near)
        {
            cb = Vector4.Lerp(cb, ca, (near - cb.W) / (ca.W - cb.W));
        }

        return (projection.Screen(ca), projection.Screen(cb));
    }

    /// <summary>A name on a dark pill to the right of a glyph.</summary>
    private static void Label(ImDrawListPtr drawList, SceneMarker marker, string text, bool highlight)
    {
        var scale = ImGuiHelper.Scale;
        var fontSize = MathF.Round(EditorTheme.CaptionSize * scale);
        var padding = PillPadding * scale;
        var size = ImGuiHelper.Roboto.CalcTextSizeA(fontSize, float.MaxValue, 0, text) + 2 * padding;
        var min = new Vector2(marker.Screen.X + MathF.Round(marker.Kind.MarkerSize * scale) / 2 + LabelGap * scale,
            MathF.Round(marker.Screen.Y - size.Y / 2));
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(PillColor, marker.Alpha), PillRounding * scale);
        drawList.AddText(ImGuiHelper.Roboto, fontSize, min + padding,
            ImGui.GetColorU32(highlight ? BrightColor : LabelColor, marker.Alpha), text);
    }
}

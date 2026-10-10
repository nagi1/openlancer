using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.World;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The 2D layer over the 3D scene: a glyph marker for each object and light, a diamond marker for each zone that has
/// no volume or path line, the labels, and the corner brackets around the selected and the hovered item. It also finds
/// the marker under the mouse.
/// </summary>
/// <param name="objects">The world object of each system object, for the model bounds of the brackets.</param>
internal sealed class SceneOverlay(EditorWindow window, IReadOnlyDictionary<SystemObject, GameObject> objects)
{
    // Sizes in unscaled points.
    private const float LabelGap = 4;
    private const float PillRounding = 3;
    private const float PickMargin = 3;
    private const float ViewMargin = 16;
    private const float BracketPadding = 4;
    private const float BracketMinSize = 16;
    private const float BracketArm = 10;
    private const float SelectionThickness = 2;
    private const float HoverThickness = 1.5f;
    private static readonly Vector2 PillPadding = new(5, 1);
    private static readonly Vector2 ShadowOffset = new(1, 1);

    // A marker with less alpha than this does not draw and cannot be picked.
    private const float MinAlpha = 0.02f;

    // A marker starts to fade at this part of its fade distance.
    private const float FadeStart = 0.6f;

    private static readonly uint ShadowColor = EditorTheme.ToAbgr(EditorTheme.Shadow);
    private static readonly uint PillColor = EditorTheme.ToAbgr(EditorTheme.Backdrop with { W = 180 / 255f });
    private static readonly uint LabelColor = EditorTheme.ToAbgr(EditorTheme.Text);
    private static readonly uint SelectedLabelColor = EditorTheme.ToAbgr(EditorTheme.TextBright);
    private static readonly uint SelectionColor = EditorTheme.ToAbgr(EditorTheme.Accent);
    private static readonly uint HoverColor = EditorTheme.ToAbgr(EditorTheme.Label);

    private readonly List<Marker> markers = [];
    private readonly List<Marker> labelOrder = [];
    private readonly List<(Vector2 Min, Vector2 Max)> placedLabels = [];

    // The glyphs of the kinds with labels. A label does not cover the glyph of a kind with the same or a higher label
    // priority.
    private readonly List<(Vector2 Min, Vector2 Max, object Item, int Priority)> labelledGlyphs = [];

    // The label text and its size of each item, at the scale in labelScale.
    private readonly Dictionary<object, (string Text, Vector2 Size)> labels = [];
    private float labelScale;

    // The offset from the text position of a glyph to the center of its visible shape, for each glyph and pixel size.
    private readonly Dictionary<(char Glyph, float Size), Vector2> glyphCenters = [];

    private Matrix4x4 viewProjection;
    private Matrix4x4 view;
    private Vector2 origin;
    private Vector2 size;
    private SystemIndex? index;

    /// <summary>A marker on the screen. A zone marker is a diamond, and all other markers are glyphs.</summary>
    /// <param name="Item">The selectable item that the marker shows.</param>
    /// <param name="Color">The ABGR color of the glyph or the diamond.</param>
    /// <param name="Depth">The distance from the camera along the view direction.</param>
    /// <param name="Alpha">The opacity after the distance fade.</param>
    private readonly record struct Marker(object Item, ObjectKind Kind, uint Color, Vector2 Screen, float Depth,
        float Alpha);

    /// <summary>Forgets the cached labels. Call it when a system opens or closes.</summary>
    public void Reset()
    {
        markers.Clear();
        labels.Clear();
    }

    /// <summary>
    /// Projects the markers of the system for this frame. Call it after the camera update and before
    /// <see cref="HitTest"/> and <see cref="Draw"/>. The Zones and Lights toggles show the zone and light markers.
    /// The selected zone or light has its marker also when its toggle is off.
    /// </summary>
    /// <param name="origin">The top-left corner of the scene on the screen, in pixels.</param>
    /// <param name="size">The size of the scene, in pixels.</param>
    public void Update(LookAtCamera camera, Vector2 origin, Vector2 size, SystemIndex systemIndex)
    {
        viewProjection = camera.ViewProjection;
        view = camera.View;
        this.origin = origin;
        this.size = size;
        index = systemIndex;
        markers.Clear();
        var settings = window.Settings;
        if (!settings.ShowMarkers)
        {
            return;
        }

        foreach (var obj in systemIndex.System.Objects)
        {
            var kind = ObjectKinds.Of(obj);
            AddMarker(obj, kind, kind.Abgr, obj.Position, (obj.Visit & VisitFlags.Hidden) != 0);
        }

        var selection = window.Selection;
        var selectionAdded = false;
        if (settings.ShowZones)
        {
            foreach (var zone in systemIndex.MarkerZones)
            {
                var kind = systemIndex.KindOf(zone)!;
                AddMarker(zone, kind, kind.Abgr, zone.Position, false);
                selectionAdded |= ReferenceEquals(zone, selection);
            }
        }

        // A field, nebula or patrol path zone has no diamond. When it is selected, it gets one for its label.
        if (!selectionAdded && selection is Zone selectedZone && systemIndex.System.Zones.Contains(selectedZone))
        {
            var kind = systemIndex.KindOf(selectedZone)!;
            AddMarker(selectedZone, kind, kind.Abgr, selectedZone.Position, false);
        }

        foreach (var light in systemIndex.System.LightSources)
        {
            if (settings.ShowLights || ReferenceEquals(light, selection))
            {
                AddMarker(light, ObjectKinds.Light, EditorTheme.ToAbgr(ObjectKinds.LightColor(light.Light.Color)),
                    light.Light.Position, false);
            }
        }

        // Far markers draw first, so that near markers draw on top of them.
        markers.Sort((a, b) => b.Depth.CompareTo(a.Depth));
    }

    /// <summary>
    /// Adds the marker of an item when it is in view and not faded out. The selected item never fades, so its marker
    /// stays in view. A hidden item draws at half opacity.
    /// </summary>
    private void AddMarker(object item, ObjectKind kind, uint color, Vector3 position, bool hidden)
    {
        if (!Project(position, out var screen, out var depth) || !InView(screen, ViewMargin * ImGuiHelper.Scale))
        {
            return;
        }

        var alpha = ReferenceEquals(item, window.Selection) ? 1 : Fade(depth, kind.FadeDistance);
        if (hidden)
        {
            alpha *= 0.5f;
        }

        if (alpha >= MinAlpha)
        {
            markers.Add(new Marker(item, kind, color, screen, depth, alpha));
        }
    }

    /// <summary>
    /// The item of the marker nearest to <paramref name="mouse"/>, or null when no marker is under it.
    /// </summary>
    public object? HitTest(Vector2 mouse)
    {
        var scale = ImGuiHelper.Scale;
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

    /// <summary>Draws the markers, the brackets and the labels into the current window, clipped to the scene.</summary>
    /// <param name="hovered">The item under the mouse, or null.</param>
    /// <param name="widget">The screen rectangle of the axis widget. Labels do not go there.</param>
    public void Draw(object? hovered, (Vector2 Min, Vector2 Max) widget)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(origin, origin + size, true);
        // The zone diamonds draw below the glyphs. A zone is often at the position of an object, for example the
        // exclusion zone of a station, and the glyph of the object must stay readable.
        foreach (var marker in markers)
        {
            if (marker.Item is Zone)
            {
                DrawDiamond(drawList, marker);
            }
        }

        foreach (var marker in markers)
        {
            if (marker.Item is not Zone)
            {
                DrawGlyph(drawList, marker);
            }
        }

        if (hovered != null && !ReferenceEquals(hovered, window.Selection))
        {
            DrawBrackets(drawList, hovered, HoverColor, HoverThickness);
        }

        if (window.Selection != null)
        {
            DrawBrackets(drawList, window.Selection, SelectionColor, SelectionThickness);
        }

        DrawLabels(drawList, hovered, widget);
        drawList.PopClipRect();
    }

    /// <summary>Projects a world position to the screen. Returns false for a position behind the camera.</summary>
    private bool Project(Vector3 position, out Vector2 screen, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), viewProjection);
        depth = clip.W;
        if (clip.W <= 0)
        {
            screen = default;
            return false;
        }

        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        screen = origin + new Vector2((ndc.X + 1) * 0.5f * size.X, (1 - ndc.Y) * 0.5f * size.Y);
        return true;
    }

    /// <summary>
    /// Projects points and adds them to the screen bounds. Returns false when a point is behind the camera, because
    /// the projection of such a point flips to the wrong side of the screen.
    /// </summary>
    private bool ProjectAll(ReadOnlySpan<Vector3> points, ref Vector2 min, ref Vector2 max)
    {
        foreach (var point in points)
        {
            if (!Project(point, out var screen, out _))
            {
                return false;
            }

            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }

        return true;
    }

    private bool InView(Vector2 screen, float margin) =>
        screen.X >= origin.X - margin && screen.Y >= origin.Y - margin &&
        screen.X <= origin.X + size.X + margin && screen.Y <= origin.Y + size.Y + margin;

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

    private void DrawGlyph(ImDrawListPtr drawList, Marker marker)
    {
        var pixelSize = Pixels(marker.Kind.MarkerSize);
        var position = marker.Screen - GlyphCenter(marker.Kind.Icon, pixelSize);
        var font = ImGuiHelper.Roboto;
        font.RenderChar(drawList, pixelSize, position + ShadowOffset * ImGuiHelper.Scale,
            ImGui.GetColorU32(ShadowColor, marker.Alpha), marker.Kind.Icon);
        font.RenderChar(drawList, pixelSize, position, ImGui.GetColorU32(marker.Color, marker.Alpha),
            marker.Kind.Icon);
    }

    /// <summary>A filled diamond as wide as the marker size, with a drop shadow.</summary>
    private static void DrawDiamond(ImDrawListPtr drawList, Marker marker)
    {
        var half = Pixels(marker.Kind.MarkerSize) / 2;
        var center = marker.Screen;
        var shadow = center + ShadowOffset * ImGuiHelper.Scale;
        drawList.AddQuadFilled(shadow + new Vector2(0, -half), shadow + new Vector2(half, 0),
            shadow + new Vector2(0, half), shadow + new Vector2(-half, 0),
            ImGui.GetColorU32(ShadowColor, marker.Alpha));
        drawList.AddQuadFilled(center + new Vector2(0, -half), center + new Vector2(half, 0),
            center + new Vector2(0, half), center + new Vector2(-half, 0),
            ImGui.GetColorU32(marker.Color, marker.Alpha));
    }

    /// <summary>
    /// Draws four L-shaped corners around an item. The arms are 10 points long, or a quarter of the side for a small
    /// bracket.
    /// </summary>
    private void DrawBrackets(ImDrawListPtr drawList, object item, uint color, float thickness)
    {
        if (!BracketBounds(item, out var min, out var max))
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var center = (min + max) / 2;
        var half = Vector2.Max((max - min) / 2, new Vector2(BracketMinSize * scale / 2));
        min = center - half;
        max = center + half;
        var arm = Vector2.Min(new Vector2(BracketArm * scale), 2 * half * 0.25f);
        thickness *= scale;
        Corner(drawList, min, new Vector2(arm.X, arm.Y), color, thickness);
        Corner(drawList, new Vector2(max.X, min.Y), new Vector2(-arm.X, arm.Y), color, thickness);
        Corner(drawList, max, new Vector2(-arm.X, -arm.Y), color, thickness);
        Corner(drawList, new Vector2(min.X, max.Y), new Vector2(arm.X, -arm.Y), color, thickness);
    }

    private static void Corner(ImDrawListPtr drawList, Vector2 corner, Vector2 arm, uint color, float thickness)
    {
        drawList.PathLineTo(corner + new Vector2(0, arm.Y));
        drawList.PathLineTo(corner);
        drawList.PathLineTo(corner + new Vector2(arm.X, 0));
        drawList.PathStroke(color, ImDrawFlags.None, thickness);
    }

    /// <summary>
    /// The screen bounds of an item for its brackets. A zone or a light has no shape on the screen, so its bounds are
    /// the box of its marker. Returns false when the item is behind the camera or has no position.
    /// </summary>
    private bool BracketBounds(object item, out Vector2 min, out Vector2 max)
    {
        if (item is SystemObject obj)
        {
            return BracketBounds(obj, out min, out max);
        }

        Vector3? position = item switch
        {
            Zone zone => zone.Position,
            LightSource light => light.Light.Position,
            _ => null,
        };
        min = max = default;
        if (position == null || index?.KindOf(item) is not { } kind || !Project(position.Value, out var screen, out _))
        {
            return false;
        }

        var half = new Vector2(GlyphBoxHalf(kind));
        min = screen - half;
        max = screen + half;
        return true;
    }

    /// <summary>
    /// The screen bounds of an object for its brackets: the projected box of its model, or the projected radius of a
    /// sun. When markers show, the bounds also hold the glyph, so that the brackets never cut through it. An object
    /// with no model, or with a box corner behind the camera, gets the glyph box. Returns false when the object is
    /// behind the camera.
    /// </summary>
    private bool BracketBounds(SystemObject obj, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue);
        max = new Vector2(float.MinValue);
        if (!Project(obj.Position, out var screen, out _))
        {
            return false;
        }

        var hasShape = false;
        Span<Vector3> points = stackalloc Vector3[BoundingBox.CornerCount];
        if (objects.TryGetValue(obj, out var gameObject) &&
            gameObject.RenderComponent is ModelRenderer { Model: { } model })
        {
            model.GetBoundingBox().GetCorners(points);
            var world = gameObject.WorldTransform.Matrix();
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = Vector3.Transform(points[i], world);
            }

            hasShape = ProjectAll(points, ref min, ref max);
        }
        else if (obj.Star is { } star)
        {
            // A sun has no model. The camera right and up vectors give the screen size of its sphere.
            var right = new Vector3(view.M11, view.M21, view.M31) * star.Radius;
            var up = new Vector3(view.M12, view.M22, view.M32) * star.Radius;
            points[0] = obj.Position - right - up;
            points[1] = obj.Position + right - up;
            points[2] = obj.Position + right + up;
            points[3] = obj.Position - right + up;
            hasShape = ProjectAll(points[..4], ref min, ref max);
        }

        if (!hasShape || window.Settings.ShowMarkers)
        {
            var half = new Vector2(GlyphBoxHalf(ObjectKinds.Of(obj)));
            min = hasShape ? Vector2.Min(min, screen - half) : screen - half;
            max = hasShape ? Vector2.Max(max, screen + half) : screen + half;
        }

        return true;
    }

    /// <summary>Half the size of the glyph box of a kind: the glyph and the bracket padding, in pixels.</summary>
    private static float GlyphBoxHalf(ObjectKind kind) =>
        Pixels(kind.MarkerSize) / 2 + BracketPadding * ImGuiHelper.Scale;

    /// <summary>
    /// Draws the labels on dark pills. The selected and hovered objects get their labels first, then the kinds with a
    /// higher label priority, then the nearer objects. A label goes to the right of its glyph, or to the left when the
    /// right side is not free. A side is not free when the label would cover a label that is already placed, the axis
    /// widget, or the glyph of a kind with the same or a higher label priority. A label with no free side does not
    /// draw, except for the selected and the hovered object.
    /// </summary>
    /// <param name="widget">The screen rectangle of the axis widget.</param>
    private void DrawLabels(ImDrawListPtr drawList, object? hovered, (Vector2 Min, Vector2 Max) widget)
    {
        var scale = ImGuiHelper.Scale;
        if (labelScale != scale)
        {
            labels.Clear();
            labelScale = scale;
        }

        var showLabels = window.Settings.ShowLabels;
        labelOrder.Clear();
        foreach (var marker in markers)
        {
            if (Highlight(marker.Item, hovered) > 0 || (showLabels && marker.Kind.LabelPriority > 0))
            {
                labelOrder.Add(marker);
            }
        }

        labelOrder.Sort((a, b) =>
        {
            var order = Highlight(b.Item, hovered).CompareTo(Highlight(a.Item, hovered));
            if (order == 0)
            {
                order = b.Kind.LabelPriority.CompareTo(a.Kind.LabelPriority);
            }

            return order != 0 ? order : a.Depth.CompareTo(b.Depth);
        });

        placedLabels.Clear();
        placedLabels.Add(widget);
        labelledGlyphs.Clear();
        foreach (var marker in markers)
        {
            if (marker.Kind.LabelPriority > 0 && marker.Item is not Zone)
            {
                var half = new Vector2(Pixels(marker.Kind.MarkerSize) / 2);
                labelledGlyphs.Add((marker.Screen - half, marker.Screen + half, marker.Item,
                    marker.Kind.LabelPriority));
            }
        }

        var fontSize = Pixels(EditorTheme.CaptionSize);
        var padding = PillPadding * scale;
        var gap = LabelGap * scale;
        foreach (var marker in labelOrder)
        {
            var (text, textSize) = Label(marker.Item, fontSize);
            var pillSize = textSize + 2 * padding;
            var highlight = Highlight(marker.Item, hovered);
            // A highlighted object has brackets around its glyph, so its label moves out of them.
            var offset = (highlight > 0 ? GlyphBoxHalf(marker.Kind) : Pixels(marker.Kind.MarkerSize) / 2) + gap;
            // The label is centered vertically on the glyph.
            var top = MathF.Round(marker.Screen.Y - pillSize.Y / 2);
            var right = new Vector2(marker.Screen.X + offset, top);
            var left = new Vector2(marker.Screen.X - offset - pillSize.X, top);
            var rightInView = right.X + pillSize.X <= origin.X + size.X;
            // A label that the scene edge would cut shows a wrong name, so the left side is used only when it is in
            // view, or when the right side is not in view either.
            var leftUsable = left.X >= origin.X || !rightInView;
            Vector2 min;
            if (highlight > 0)
            {
                min = rightInView ? right : left;
            }
            else if (rightInView && IsFree(right, right + pillSize, marker))
            {
                min = right;
            }
            else if (leftUsable && IsFree(left, left + pillSize, marker))
            {
                min = left;
            }
            else
            {
                continue;
            }

            var max = min + pillSize;
            placedLabels.Add((min, max));
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(PillColor, marker.Alpha), PillRounding * scale);
            var color = highlight == 2 ? SelectedLabelColor : LabelColor;
            drawList.AddText(ImGuiHelper.Roboto, fontSize, min + padding, ImGui.GetColorU32(color, marker.Alpha), text);
        }
    }

    /// <summary>2 for the selected item, 1 for the hovered item and 0 for all other items.</summary>
    private int Highlight(object item, object? hovered) =>
        ReferenceEquals(item, window.Selection) ? 2 : ReferenceEquals(item, hovered) ? 1 : 0;

    /// <summary>
    /// True when the label of a marker can go in a rectangle: it covers no placed label, not the axis widget, and no
    /// glyph of another item with the same or a higher label priority.
    /// </summary>
    private bool IsFree(Vector2 min, Vector2 max, Marker marker)
    {
        foreach (var placed in placedLabels)
        {
            if (Overlaps(min, max, placed.Min, placed.Max))
            {
                return false;
            }
        }

        foreach (var glyph in labelledGlyphs)
        {
            if (glyph.Priority >= marker.Kind.LabelPriority && !ReferenceEquals(glyph.Item, marker.Item) &&
                Overlaps(min, max, glyph.Min, glyph.Max))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Overlaps(Vector2 min, Vector2 max, Vector2 otherMin, Vector2 otherMax) =>
        min.X < otherMax.X && max.X > otherMin.X && min.Y < otherMax.Y && max.Y > otherMin.Y;

    /// <summary>The display name of an item, or its nickname when it has no name, and the size of the text.</summary>
    private (string Text, Vector2 Size) Label(object item, float fontSize)
    {
        if (!labels.TryGetValue(item, out var label))
        {
            var text = window.Session?.ItemName(item) is { Length: > 0 } name ? name
                : item is NicknameItem named ? named.Nickname
                : item.ToString() ?? "";
            label = (text, ImGuiHelper.Roboto.CalcTextSizeA(fontSize, float.MaxValue, 0, text));
            labels[item] = label;
        }

        return label;
    }

    /// <summary>
    /// The offset from the text position of a glyph to the center of its visible shape. The icon glyphs have empty
    /// space around them, so the center of the text box is not the center of the shape.
    /// </summary>
    private Vector2 GlyphCenter(char glyph, float pixelSize)
    {
        if (!glyphCenters.TryGetValue((glyph, pixelSize), out var center))
        {
            var info = ImGuiHelper.Roboto.GetFontBaked(pixelSize).FindGlyph(glyph);
            center = new Vector2(info.X0 + info.X1, info.Y0 + info.Y1) / 2;
            glyphCenters[(glyph, pixelSize)] = center;
        }

        return center;
    }

    /// <summary>A size in whole pixels at the current UI scale, so that text and glyphs stay sharp.</summary>
    private static float Pixels(float points) => MathF.Round(points * ImGuiHelper.Scale);
}

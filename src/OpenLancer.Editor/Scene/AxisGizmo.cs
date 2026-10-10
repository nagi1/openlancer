using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// The axis widget in the top-right corner of the Scene. It shows the world axes as the camera sees them. A click on an
/// axis end turns the camera to look at the orbit point from that side, for example from above for the Y end.
/// </summary>
internal sealed class AxisGizmo
{
    // Sizes in unscaled points.
    private const float Inset = 48;
    private const float AxisLength = 32;
    private const float PositiveRadius = 7;
    private const float NegativeRadius = 5;
    private const float LineThickness = 2;
    private const float OutlineThickness = 1.5f;
    private const float HitMargin = 2;
    private const float BackgroundRadius = AxisLength + PositiveRadius + 5;

    private static readonly (Vector3 Axis, uint Color, string Letter)[] Axes =
    [
        (Vector3.UnitX, EditorTheme.ToAbgr(EditorTheme.AxisX), "X"),
        (Vector3.UnitY, EditorTheme.ToAbgr(EditorTheme.AxisY), "Y"),
        (Vector3.UnitZ, EditorTheme.ToAbgr(EditorTheme.AxisZ), "Z"),
    ];

    private static readonly uint LetterColor = EditorTheme.ToAbgr(EditorTheme.Backdrop);
    private static readonly uint HoverColor = EditorTheme.ToAbgr(EditorTheme.TextBright);
    private static readonly uint BackgroundColor = EditorTheme.ToAbgr(EditorTheme.Backdrop with { W = 0.5f });

    // The six axis ends, sorted from far to near.
    private readonly End[] ends = new End[6];
    private Vector2 center;

    /// <summary>One end of an axis on the screen.</summary>
    /// <param name="Direction">The world direction from the center to this end.</param>
    /// <param name="Depth">The view-space Z of the direction. A larger value is nearer to the camera.</param>
    /// <param name="Axis">The index in <see cref="Axes"/>.</param>
    private readonly record struct End(Vector3 Direction, Vector2 Screen, float Depth, bool Positive, int Axis);

    /// <summary>
    /// The screen rectangle of the widget and its hover disc, in pixels. Scene labels do not go there.
    /// </summary>
    public (Vector2 Min, Vector2 Max) Bounds
    {
        get
        {
            var radius = new Vector2(BackgroundRadius * ImGuiHelper.Scale);
            return (center - radius, center + radius);
        }
    }

    /// <summary>Places the widget in the scene rectangle and turns its axes with the camera view.</summary>
    /// <param name="origin">The top-left corner of the scene on the screen, in pixels.</param>
    /// <param name="size">The size of the scene, in pixels.</param>
    public void Update(Matrix4x4 view, Vector2 origin, Vector2 size)
    {
        var scale = ImGuiHelper.Scale;
        var inset = MathF.Round(Inset * scale);
        center = new Vector2(origin.X + size.X - inset, origin.Y + inset);
        var length = AxisLength * scale;
        for (var i = 0; i < Axes.Length; i++)
        {
            var v = Vector3.TransformNormal(Axes[i].Axis, view);
            var offset = new Vector2(v.X, -v.Y) * length;
            ends[2 * i] = new End(Axes[i].Axis, center + offset, v.Z, true, i);
            ends[2 * i + 1] = new End(-Axes[i].Axis, center - offset, -v.Z, false, i);
        }

        // Far ends draw first, so near ends draw over them.
        Array.Sort(ends, (a, b) => a.Depth.CompareTo(b.Depth));
    }

    /// <summary>
    /// The direction of the axis end under the mouse, or null when no end is under it. The nearest end wins.
    /// </summary>
    public Vector3? HitTest(Vector2 mouse)
    {
        var scale = ImGuiHelper.Scale;
        for (var i = ends.Length - 1; i >= 0; i--)
        {
            var end = ends[i];
            var radius = (end.Positive ? PositiveRadius : NegativeRadius) + HitMargin;
            if (Vector2.Distance(end.Screen, mouse) <= radius * scale)
            {
                return end.Direction;
            }
        }

        return null;
    }

    /// <summary>
    /// Draws the widget. A positive end has a line, a filled circle and its letter. A negative end is a circle outline.
    /// </summary>
    /// <param name="mouse">The mouse position when the mouse is over the scene, or null.</param>
    public void Draw(ImDrawListPtr drawList, Vector2? mouse)
    {
        var scale = ImGuiHelper.Scale;
        var hovered = mouse is { } point ? HitTest(point) : null;
        // A dark disc behind the widget shows that it takes clicks while the mouse is near it.
        if (mouse is { } near && Vector2.Distance(near, center) <= BackgroundRadius * scale)
        {
            drawList.AddCircleFilled(center, BackgroundRadius * scale, BackgroundColor);
        }

        var font = ImGuiHelper.Roboto;
        var fontSize = MathF.Round(EditorTheme.CaptionSize * scale);
        foreach (var end in ends)
        {
            var color = Axes[end.Axis].Color;
            var isHovered = hovered == end.Direction;
            if (end.Positive)
            {
                var radius = PositiveRadius * scale;
                drawList.AddLine(center, end.Screen, color, LineThickness * scale);
                drawList.AddCircleFilled(end.Screen, radius, color);
                if (isHovered)
                {
                    drawList.AddCircle(end.Screen, radius, HoverColor, 0, OutlineThickness * scale);
                }

                var letter = Axes[end.Axis].Letter;
                var size = font.CalcTextSizeA(fontSize, float.MaxValue, 0, letter);
                drawList.AddText(font, fontSize, Round(end.Screen - size / 2), LetterColor, letter);
            }
            else
            {
                var radius = NegativeRadius * scale;
                if (isHovered)
                {
                    drawList.AddCircleFilled(end.Screen, radius, color);
                }

                drawList.AddCircle(end.Screen, radius, isHovered ? HoverColor : color, 0, OutlineThickness * scale);
            }
        }
    }

    private static Vector2 Round(Vector2 value) => new(MathF.Round(value.X), MathF.Round(value.Y));
}

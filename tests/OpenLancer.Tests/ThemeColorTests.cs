using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The color of the mission layer (decision D557 of the M6a design). Its hue differs by 25 degrees or more from each
/// color of <see cref="EditorTheme"/> and <see cref="ObjectKinds"/>. So no other meaning uses it. A grey has no hue, so
/// the test leaves out the colors with a saturation below 0.15.
/// </summary>
public sealed class ThemeColorTests
{
    private const float MinSaturation = 0.15f;

    /// <summary>The hue in degrees and the saturation of a color, as the HSV model gives them.</summary>
    internal static (float Hue, float Saturation) Hsv(Vector4 color)
    {
        var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var min = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        var delta = max - min;
        var saturation = max <= 0 ? 0 : delta / max;
        if (delta <= 0)
            return (0, saturation);
        var hue = max == color.X ? (color.Y - color.Z) / delta % 6
            : max == color.Y ? (color.Z - color.X) / delta + 2
            : (color.X - color.Y) / delta + 4;
        return ((hue * 60 + 360) % 360, saturation);
    }

    /// <summary>The colors of the theme and of all kinds, without the mission color.</summary>
    private static IEnumerable<(string Name, Vector4 Color)> OtherColors()
    {
        foreach (var field in typeof(EditorTheme).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(Vector4) && f.Name != nameof(EditorTheme.MissionItem)))
            yield return (field.Name, (Vector4)field.GetValue(null)!);
        foreach (var type in new[] { typeof(ObjectKinds) }.Concat(typeof(ObjectKinds).GetNestedTypes()))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(f => f.FieldType == typeof(ObjectKind)))
                yield return ($"{type.Name}.{field.Name}", ((ObjectKind)field.GetValue(null)!).Color);
        }
    }

    [Fact]
    public void TheMissionColorHasItsOwnHue()
    {
        var (hue, saturation) = Hsv(EditorTheme.MissionItem);
        var others = OtherColors().ToList();

        Assert.True(saturation >= MinSaturation);
        Assert.True(others.Count > 40, $"The test found only {others.Count} colors.");
        foreach (var (name, color) in others)
        {
            var (otherHue, otherSaturation) = Hsv(color);
            if (otherSaturation < MinSaturation)
                continue;
            var distance = MathF.Abs(hue - otherHue);
            distance = MathF.Min(distance, 360 - distance);
            Assert.True(distance >= 25, $"{name} has a hue of {otherHue:0} degrees, only {distance:0} degrees from the mission color.");
        }
    }

    [Theory]
    [InlineData(0xFF0000, 0)]
    [InlineData(0x00FF00, 120)]
    [InlineData(0x0000FF, 240)]
    [InlineData(0xF07AB4, 331)]
    public void TheHueOfAColor(uint rgb, float expected)
    {
        Assert.Equal(expected, MathF.Round(Hsv(EditorTheme.Rgb(rgb)).Hue));
    }

    /// <summary>Each kind of the mission layer has the mission color, so the user sees what belongs to the
    /// mission.</summary>
    [Fact]
    public void EachKindOfTheMissionLayerHasTheMissionColor()
    {
        foreach (var kind in Enum.GetValues<PlacementKind>())
            Assert.Equal(EditorTheme.MissionItem, MissionLayer.KindOf(kind).Color);
    }

    /// <summary>The highlight of the cross-links is a grey without a hue. So ten highlighted ships do not look
    /// selected. Blue means a selection or an unsaved edit (decision D537, UI rule 7).</summary>
    [Fact]
    public void TheHighlightHasNoStateColor()
    {
        Assert.True(Hsv(EditorTheme.Highlight).Saturation < MinSaturation);
        Assert.NotEqual(EditorTheme.Accent, EditorTheme.Highlight);
    }
}

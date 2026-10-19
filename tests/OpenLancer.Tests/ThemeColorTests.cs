using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The color of the mission layer (decision D557 of the M6a design): its hue differs by 25 degrees or more from each
/// color of <see cref="EditorTheme"/> and <see cref="ObjectKinds"/>, so no other meaning uses it. A grey has no hue, so
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

    /// <summary>A placement of a trigger line selects the line. A placement of an item selects its section.</summary>
    [Fact]
    public void ThePlacementOfALineSelectsTheLine()
    {
        var index = MissionTestData.Index(MissionTestData.Story.Replace("Cnd_True = no_params\nAct_ActTrig = t_end",
            "Cnd_True = no_params\nAct_ActTrig = t_end\nAct_StaticCam = 1, 2, 3") + "\n\n[MsnShip]\nnickname = s\nsystem = sys\nposition = 1, 1, 1\n");
        var start = index.Trigger("t_start")!;
        var placements = MissionPlacements.In(index, "sys", _ => null);

        Assert.DoesNotContain(placements, p => p.Trigger != null);
        var ship = MissionLayer.RefOf(MissionTestData.Path, placements.Single());
        Assert.Equal(MissionItemKind.Ship, ship.Kind);
        var line = new MissionPlacement(PlacementKind.FixedCamera, PlacementShape.Point, "sys", Vector3.Zero, start.Section,
            5, "Fixed camera") { Trigger = start };
        Assert.Equal(new MissionRef(MissionTestData.Path, MissionItemKind.Row, start.Section.Ref, Row: 5),
            MissionLayer.RefOf(MissionTestData.Path, line));
    }
}

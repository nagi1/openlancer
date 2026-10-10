using System.Numerics;
using OpenLancer.Editor;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

public sealed class EditorLayoutTests
{
    [Fact]
    public void UsesTheDefaultSizesAt100Percent()
    {
        var sizes = EditorLayout.Measure(new Vector2(1600, 856), 1, new EditorSettings());

        Assert.Equal(4, sizes.Gap);
        Assert.Equal(280, sizes.HierarchyWidth);
        Assert.Equal(360, sizes.InspectorWidth);
        Assert.Equal(260, sizes.BottomHeight);
        Assert.Equal(1600 - 360 - 4, sizes.LeftWidth);
        Assert.Equal(856 - 260 - 4, sizes.TopHeight);
    }

    [Fact]
    public void ScalesThePanelsButKeepsTheSavedSizesUnscaled()
    {
        var settings = new EditorSettings();

        var sizes = EditorLayout.Measure(new Vector2(2400, 1284), 1.5f, settings);

        Assert.Equal(6, sizes.Gap);
        Assert.Equal(420, sizes.HierarchyWidth);
        Assert.Equal(540, sizes.InspectorWidth);
        Assert.Equal(390, sizes.BottomHeight);
        Assert.Equal(EditorSettings.DefaultHierarchyWidth, settings.HierarchyWidth);
        Assert.Equal(EditorSettings.DefaultInspectorWidth, settings.InspectorWidth);
        Assert.Equal(EditorSettings.DefaultBottomHeight, settings.BottomHeight);
    }

    [Fact]
    public void LimitsASavedSizeForTheFrameOnly()
    {
        var settings = new EditorSettings { HierarchyWidth = 1000 };

        var sizes = EditorLayout.Measure(new Vector2(1600, 856), 1, settings);

        Assert.Equal(640, sizes.HierarchyWidth); // 40% of the workspace width
        Assert.Equal(1000, settings.HierarchyWidth);
    }

    [Fact]
    public void TakesTheSceneHeightFromTheBottomGroup()
    {
        var sizes = EditorLayout.Measure(new Vector2(1600, 400), 1, new EditorSettings());

        Assert.Equal(196, sizes.BottomHeight);
        Assert.Equal(200, sizes.TopHeight);
    }

    [Fact]
    public void TakesTheSceneWidthFromTheHierarchyFirst()
    {
        var sizes = EditorLayout.Measure(new Vector2(900, 856), 1, new EditorSettings());

        Assert.Equal(212, sizes.HierarchyWidth);
        Assert.Equal(360, sizes.InspectorWidth);
    }

    [Fact]
    public void TakesTheSceneWidthFromTheInspectorWhenTheHierarchyIsAtItsMinimum()
    {
        var sizes = EditorLayout.Measure(new Vector2(700, 856), 1, new EditorSettings());

        Assert.Equal(180, sizes.HierarchyWidth);
        Assert.Equal(280, sizes.InspectorWidth);
    }

    [Fact]
    public void KeepsTheSplitterUnderTheMouseAfterADragPastTheLimit()
    {
        // The drag starts at 280 points with the mouse at x = 500 and goes far past the maximum of 640.
        Assert.Equal(640, Splitter.Drag(280, 500, 1200, 1, 1, 180, 640));

        // The mouse comes back to 20 pixels right of the start, so the edge is 20 points right of its start.
        Assert.Equal(300, Splitter.Drag(280, 500, 520, 1, 1, 180, 640));
    }

    [Fact]
    public void DragsInUnscaledPointsAndInTheDirectionOfTheSign()
    {
        Assert.Equal(300, Splitter.Drag(280, 0, 30, 1, 1.5f, 180, 640));
        Assert.Equal(410, Splitter.Drag(360, 1000, 950, -1, 1, 280, 720));
    }
}

using OpenLancer.Editor.Inspector;
using Xunit;

namespace OpenLancer.Tests;

public sealed class PropertyGridTests
{
    [Theory]
    [InlineData(360, 1, 140)]
    [InlineData(800, 1, 320)]
    [InlineData(540, 1.5f, 210)]
    [InlineData(1200, 1.5f, 480)]
    public void GivesTheUnityLabelWidthWithAMinimum(float width, float scale, float expected)
    {
        Assert.Equal(expected, PropertyGrid.LabelWidth(width, scale));
    }

    [Fact]
    public void ScalesTheOffsetAndTheMinimumOnlyOnce()
    {
        // The width is in pixels, so it already has the scale. A second scale would make the column too wide.
        Assert.Equal(1.5f * PropertyGrid.LabelWidth(800, 1), PropertyGrid.LabelWidth(1200, 1.5f));
        Assert.Equal(1.5f * PropertyGrid.LabelWidth(360, 1), PropertyGrid.LabelWidth(540, 1.5f));
    }
}

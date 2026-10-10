using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

public sealed class GridRendererTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(10, 0.1f, 1)]
    [InlineData(-10, 0.1f, 1)]
    [InlineData(50_000, 0.0001f, 0.30103f)]
    public void TakesTheCoarseSpacingFromThePowerOfTenBelowTheHeight(float height, float scale, float fineAlpha)
    {
        var levels = GridRenderer.LevelsFor(height);

        Assert.Equal(scale, levels.Scale, 1e-6f);
        Assert.Equal(fineAlpha, levels.FineAlpha, 1e-4f);
    }

    [Fact]
    public void FadesTheFineLinesOutSoThatALevelChangeDoesNotJump()
    {
        // Just below a power of ten, only the coarse lines show. Just above it, the same lines are now the fine lines,
        // at full opacity.
        var below = GridRenderer.LevelsFor(999.9f);
        var above = GridRenderer.LevelsFor(1000.1f);

        Assert.Equal(0, below.FineAlpha, 1e-3f);
        Assert.Equal(1, above.FineAlpha, 1e-3f);
        Assert.Equal(1 / below.Scale, 1 / (10 * above.Scale), 1e-2f);
    }

    [Fact]
    public void EndsTheGridAtTwentyTimesTheHeight()
    {
        Assert.Equal(40_000, GridRenderer.LevelsFor(2_000).FadeDistance);
        Assert.Equal(20, GridRenderer.LevelsFor(0.5f).FadeDistance);
    }
}

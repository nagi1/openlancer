using System;
using LibreLancer;
using OpenLancer.Editor.Cutscene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rectangles of the Shot view (section 7.6 of the M8 design, test 13.1 item 12): S is the largest rectangle of the
/// screen ratio R in the panel, centered. A cutscene draws into S.width x (S.height / 1.39) with the integer steps of
/// RoomGameplay.cs:1574-1579. A room without a cutscene draws into S. The camera gets R.
/// </summary>
public sealed class ShotViewportTests
{
    /// <summary>A wide panel of 2000 x 800 at (10, 20): S has the height of the panel and is centered across.</summary>
    [Theory]
    [InlineData("SixteenNine", 299, 1422, 16f / 9f)]
    [InlineData("FourThree", 477, 1066, 4f / 3f)]
    public void AWidePanelGivesAScreenOfItsHeight(string name, int x, int width, float ratio)
    {
        var aspect = Enum.Parse<ShotAspect>(name);
        var panel = new Rectangle(10, 20, 2000, 800);

        var cutscene = ShotViewport.Of(panel, aspect, cutscene: true);
        var room = ShotViewport.Of(panel, aspect, cutscene: false);

        Assert.Equal(new Rectangle(x, 20, width, 800), cutscene.Screen);
        // 800 / 1.39 = 575.54: the bar above is (int)(224.46 / 2) = 112, and the viewport is (int)575.54 = 575 high.
        Assert.Equal(new Rectangle(x, 132, width, 575), cutscene.Viewport);
        Assert.Equal(ratio, cutscene.ScreenAspect);
        Assert.Equal(cutscene.Screen, room.Screen);
        Assert.Equal(room.Screen, room.Viewport);
        Assert.Equal(ratio, room.ScreenAspect);
    }

    /// <summary>A tall panel of 800 x 1200: S has the width of the panel and is centered down.</summary>
    [Theory]
    [InlineData("SixteenNine", 375, 450, 438, 323, 16f / 9f)]
    [InlineData("FourThree", 300, 600, 384, 431, 4f / 3f)]
    public void ATallPanelGivesAScreenOfItsWidth(string name, int y, int height, int viewportY, int viewportHeight,
        float ratio)
    {
        var aspect = Enum.Parse<ShotAspect>(name);
        var panel = new Rectangle(0, 0, 800, 1200);

        var cutscene = ShotViewport.Of(panel, aspect, cutscene: true);
        var room = ShotViewport.Of(panel, aspect, cutscene: false);

        Assert.Equal(new Rectangle(0, y, 800, height), cutscene.Screen);
        Assert.Equal(new Rectangle(0, viewportY, 800, viewportHeight), cutscene.Viewport);
        Assert.Equal(ratio, cutscene.ScreenAspect);
        Assert.Equal(new Rectangle(0, y, 800, height), room.Viewport);
    }

    /// <summary>
    /// With the ratio of the window, S is the panel, so the viewport is the letterbox of the game on a screen of that
    /// size: on 1920 x 1080, RoomGameplay gives vp.Y = (int)((1080 - 1080 / 1.39) / 2) = 151 and vp.Height = 776.
    /// </summary>
    [Fact]
    public void TheWindowRatioGivesTheLetterboxOfTheGameOnAScreenOfThePanelSize()
    {
        var shot = ShotViewport.Of(new Rectangle(0, 0, 1920, 1080), ShotAspect.Window, cutscene: true);

        Assert.Equal(new Rectangle(0, 0, 1920, 1080), shot.Screen);
        Assert.Equal(new Rectangle(0, 151, 1920, 776), shot.Viewport);
        Assert.Equal(1920f / 1080f, shot.ScreenAspect);
    }

    /// <summary>A panel without a width or a height has no view.</summary>
    [Fact]
    public void AnEmptyPanelThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShotViewport.Of(new Rectangle(0, 0, 0, 100), ShotAspect.Window, true));
    }
}

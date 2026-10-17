using Xunit;
using static OpenLancer.Editor.Scene.StarspherePass;

namespace OpenLancer.Tests;

/// <summary>The rule of the starsphere pass follows the starsphere block of the engine (SystemRenderer.Draw).</summary>
public sealed class StarspherePassTests
{
    [Fact]
    public void DrawsNothingWhenTheToggleIsOff()
    {
        Assert.Equal(new SkyPlan(false, false), Plan(false, true, true, false, false));
        Assert.Equal(new SkyPlan(false, false), Plan(false, true, true, true, false));
    }

    [Fact]
    public void DrawsTheStarsWithoutTintOutsideEveryNebula()
    {
        Assert.Equal(new SkyPlan(true, false), Plan(true, true, true, false, false));
    }

    [Fact]
    public void DrawsTheStarsAndTheFogTintInANebulaThatIsNotFullyFogged()
    {
        Assert.Equal(new SkyPlan(true, true), Plan(true, true, true, true, false));
    }

    [Fact]
    public void DrawsNothingInFullFogWhenTheNebulaeDraw()
    {
        // The renderer then clears the view with the fog color.
        Assert.Equal(new SkyPlan(false, false), Plan(true, true, true, true, true));
    }

    [Fact]
    public void DrawsTheStarsWithoutTintInFullFogWhenTheNebulaeDoNotDraw()
    {
        Assert.Equal(new SkyPlan(true, false), Plan(true, false, true, true, true));
    }

    [Fact]
    public void DrawsTheStarsWithoutTintInFullFogWhenTheFogIsOff()
    {
        // Without the fog, no nebula holds the camera (SystemRenderer.DrawFog), so the view shows the sky.
        Assert.Equal(new SkyPlan(true, false), Plan(true, true, false, true, true));
        Assert.Equal(new SkyPlan(true, false), Plan(true, true, false, true, false));
    }
}

using System.Numerics;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The click cycle of the Scene (section 6.4 of the M22 design). A click again at the same place, or an Alt+click,
/// selects the next item of the pick stack.
/// </summary>
public sealed class PickCycleTests
{
    private static readonly object A = new(), B = new(), C = new();
    private static readonly object[] Stack = [A, B, C];
    private static readonly Vector2 Place = new(200, 300);
    private static readonly CameraPose Pose = new(new Vector3(1, 2, 3), Quaternion.Identity);

    private readonly PickCycle cycle = new();

    [Fact]
    public void AClickSelectsTheFirstItem()
    {
        Assert.Equal((true, A), cycle.Click(Stack, Place, Pose, null, false));
    }

    [Fact]
    public void AClickAgainAtTheSamePlaceSelectsTheNext()
    {
        var (_, first) = cycle.Click(Stack, Place, Pose, null, false);

        // Within 4 pixels of the last pick is the same place.
        Assert.Equal((true, B), cycle.Click(Stack, Place + new Vector2(3, 2), Pose, first, false));
    }

    [Fact]
    public void TheCycleGoesBackToTheFirst()
    {
        object? selection = null;
        foreach (var expected in new[] { A, B, C, A })
        {
            (_, selection) = cycle.Click(Stack, Place, Pose, selection, false);
            Assert.Same(expected, selection);
        }
    }

    [Fact]
    public void AClickFivePixelsAwayStartsAgain()
    {
        var (_, first) = cycle.Click(Stack, Place, Pose, null, false);
        var (_, second) = cycle.Click(Stack, Place, Pose, first, false);

        Assert.Equal((true, A), cycle.Click(Stack, Place + new Vector2(5, 0), Pose, second, false));
    }

    [Fact]
    public void ACameraMoveStartsAgain()
    {
        var (_, first) = cycle.Click(Stack, Place, Pose, null, false);

        Assert.Equal((true, A), cycle.Click(Stack, Place, Pose with { Position = new Vector3(1, 2, 4) }, first, false));
        Assert.Equal((true, B), cycle.Click(Stack, Place, Pose with { Position = new Vector3(1, 2, 4) }, A, false));
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f);
        Assert.Equal((true, A), cycle.Click(Stack, Place, Pose with { Position = new Vector3(1, 2, 4), Orientation = turned },
            B, false));
    }

    [Fact]
    public void ASelectionFromTheHierarchyStartsAgain()
    {
        cycle.Click(Stack, Place, Pose, null, false);

        // The Hierarchy selected C, so the selection is not the item of the last pick.
        Assert.Equal((true, A), cycle.Click(Stack, Place, Pose, C, false));
    }

    [Fact]
    public void AltClickSelectsTheNextFromAnyPlace()
    {
        // The selection is in the stack under the mouse, at another place than the last pick.
        Assert.Equal((true, C), cycle.Click(Stack, new Vector2(600, 100), Pose, B, true));
        Assert.Equal((true, A), cycle.Click(Stack, new Vector2(10, 10), Pose, C, true));
        // A selection that is not in the stack gives the first item.
        Assert.Equal((true, A), cycle.Click(Stack, Place, Pose, new object(), true));
    }

    [Fact]
    public void AltClickOnEmptySpaceKeepsTheSelection()
    {
        Assert.Equal((false, B), cycle.Click([], Place, Pose, B, true));
    }

    [Fact]
    public void AClickOnEmptySpaceClearsTheSelection()
    {
        Assert.Equal((true, (object?)null), cycle.Click([], Place, Pose, B, false));
    }

    [Fact]
    public void TheHintGivesThePlaceAndTheCount()
    {
        Assert.Null(cycle.Hint(Place, null));

        var (_, first) = cycle.Click(Stack, Place, Pose, null, false);
        Assert.Equal("Item 1 of 3 under the mouse. Click again, or Alt+click, for the next item.",
            cycle.Hint(Place, first));
        var (_, second) = cycle.Click(Stack, Place, Pose, first, false);
        Assert.Equal("Item 2 of 3 under the mouse. Click again, or Alt+click, for the next item.",
            cycle.Hint(Place + new Vector2(0, 4), second));

        // A stack of one item has no hint, and a selection from another place has none.
        Assert.Null(cycle.Hint(Place, C));
        cycle.Click([A], Place, Pose, null, false);
        Assert.Null(cycle.Hint(Place, A));
    }

    [Fact]
    public void TheHintGoesWhenTheMouseMoves()
    {
        var (_, first) = cycle.Click(Stack, Place, Pose, null, false);

        Assert.Null(cycle.Hint(Place + new Vector2(0, 5), first));

        cycle.Reset();
        Assert.Null(cycle.Hint(Place, first));
    }
}

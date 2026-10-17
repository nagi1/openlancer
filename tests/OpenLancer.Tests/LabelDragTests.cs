using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The state machine of a label drag without ImGui (section 6.3 and 6.5 of the M20 design). Each test scripts the
/// frames of one or two labels: a press, the motion, the keys and the release.
/// </summary>
public sealed class LabelDragTests
{
    // The ImGui ids of the X and the Y label of a row.
    private const uint LabelX = 21;
    private const uint LabelY = 22;

    // ImGui starts its own drags after half of the default threshold of 6 pixels.
    private const float Threshold = 3;

    [Fact]
    public void AClickWithoutMotionIsNoEdit()
    {
        var drag = new Script();

        Assert.Equal(default, drag.Press(100));
        Assert.Equal(default, drag.Hold(100));
        Assert.Equal(default, drag.Release(100));
    }

    [Fact]
    public void TheDragStartsAtTheThresholdWith0Units()
    {
        var drag = new Script();
        drag.Press(100);

        Assert.Equal(default, drag.Hold(102));
        var start = drag.Hold(103);

        Assert.True(start.Events.Activated);
        Assert.True(start.Events.Active);
        Assert.False(start.Events.Changed);
        Assert.Equal(0, start.Units);
    }

    [Fact]
    public void EachPointOfMotionIsOneUnitAndAFrameThatMovesIsAChange()
    {
        var drag = Script.Started();

        var moved = drag.Hold(drag.X + 20);
        Assert.Equal((true, 20m), (moved.Events.Changed, moved.Units));
        var still = drag.Hold(drag.X);
        Assert.Equal((false, true, 20m), (still.Events.Changed, still.Events.Active, still.Units));
        var more = drag.Hold(drag.X + 30);
        Assert.Equal((true, 50m), (more.Events.Changed, more.Units));
        var back = drag.Hold(drag.X - 60);
        Assert.Equal((true, -10m), (back.Events.Changed, back.Units));
    }

    /// <summary>The drag divides the pixel total by the scale, so the frames add no rounding error.</summary>
    [Fact]
    public void TheScaleDividesThePixelTotal()
    {
        var one = Script.Started(scale: 1.5f);
        Assert.Equal(2m, one.Hold(one.X + 3).Units);

        var frames = Script.Started(scale: 1.5f);
        Assert.Equal(0m, frames.Hold(frames.X + 1).Units);
        Assert.Equal(1m, frames.Hold(frames.X + 1).Units);
        Assert.Equal(2m, frames.Hold(frames.X + 1).Units);

        var there = Script.Started();
        Assert.Equal(1m, there.Hold(there.X + 1).Units);
        var andBack = there.Hold(there.X - 1);
        Assert.Equal((true, 0m), (andBack.Events.Changed, andBack.Units));
    }

    /// <summary>Each point counts with the keys of its frame, so a key during the drag changes the speed from that
    /// point on, and the value does not jump.</summary>
    [Fact]
    public void ShiftAndCtrlChangeTheUnitsOfTheNextPoints()
    {
        var drag = Script.Started();
        drag.Hold(drag.X + 10);
        Assert.Equal(10.2m, drag.Hold(drag.X + 2, shift: true).Units);
        Assert.Equal(30.2m, drag.Hold(drag.X + 2, ctrl: true).Units);
        // Shift wins when both are down.
        Assert.Equal(30.3m, drag.Hold(drag.X + 1, shift: true, ctrl: true).Units);
        Assert.Equal(29.3m, drag.Hold(drag.X - 1).Units);
    }

    [Fact]
    public void EscapeCancelsTheDragAndTheButtonDoesNothingUntilItsRelease()
    {
        var drag = Script.Started();
        drag.Hold(drag.X + 10);

        var cancel = drag.Hold(drag.X + 5, escape: true);

        Assert.Equal(new FieldEvents(false, false, false, true, true), cancel.Events);
        Assert.Equal(default, drag.Hold(drag.X + 40).Events);
        Assert.Equal(default, drag.Release(drag.X + 40).Events);
        // The next press starts a new drag.
        drag.Press(drag.X);
        Assert.True(drag.Hold(drag.X + 5).Events.Activated);
    }

    [Fact]
    public void TheRightMouseButtonCancelsAsEscapeDoes()
    {
        var drag = Script.Started();
        drag.Hold(drag.X + 10);

        var cancel = drag.Hold(drag.X, right: true);

        Assert.Equal(new FieldEvents(false, false, false, true, true), cancel.Events);
        Assert.Equal(default, drag.Hold(drag.X + 40).Events);
        Assert.Equal(default, drag.Release(drag.X).Events);
    }

    [Fact]
    public void EscapeBeforeTheThresholdStartsNoDrag()
    {
        var drag = new Script();
        drag.Press(100);
        Assert.Equal(default, drag.Hold(101, escape: true).Events);

        Assert.Equal(default, drag.Hold(140).Events);
        Assert.Equal(default, drag.Hold(180).Events);
        Assert.Equal(default, drag.Release(180).Events);
    }

    [Fact]
    public void EscapeWinsOverAReleaseInTheSameFrame()
    {
        var drag = Script.Started();
        drag.Hold(drag.X + 10);

        var end = drag.Release(drag.X + 10, escape: true);

        Assert.True(end.Events.Deactivated);
        Assert.True(end.Events.Cancelled);
    }

    /// <summary>The motion of the release frame counts, and the release keeps the edit.</summary>
    [Fact]
    public void AReleaseWithMotionChangesAndEndsInOneFrame()
    {
        var drag = Script.Started();
        drag.Hold(drag.X + 10);

        var end = drag.Release(drag.X + 5);

        Assert.Equal(new FieldEvents(false, false, true, true, false), end.Events);
        Assert.Equal(15m, end.Units);
    }

    /// <summary>A label that does not draw for a frame lost its drag, and a press on another label starts a new
    /// drag. The other labels draw in the same frames and change nothing.</summary>
    [Fact]
    public void AGapOfFramesOrAPressOnAnotherLabelResetsTheDrag()
    {
        var drag = Script.Started();
        drag.Hold(drag.X + 10);
        // The Y label draws in each frame of the drag of X.
        Assert.Equal(default, drag.Other(LabelY));
        Assert.True(drag.Hold(drag.X + 1).Events.Changed);

        drag.Skip();
        drag.Press(drag.X);
        Assert.Equal(default, drag.Hold(drag.X + 2).Events);
        Assert.True(drag.Hold(drag.X + 1).Events.Activated);

        var other = new Script();
        other.Press(50);
        other.Hold(60);
        Assert.Equal(default, other.Other(LabelY, activated: true));
        // The press on Y took the drag, so X has no drag.
        Assert.Equal(default, other.Hold(80).Events);
    }

    /// <summary>The close prompt can end the edit of a drag, and an edit can fail to start. The drag then gives no
    /// events until the release.</summary>
    [Fact]
    public void ADragThatDoesNotOwnTheOpenEditEnds()
    {
        var drag = new Script();
        drag.Press(100);
        Assert.True(drag.Hold(103).Events.Activated);

        drag.Owned = false;
        Assert.Equal(default, drag.Hold(120).Events);
        drag.Owned = true;
        Assert.Equal(default, drag.Hold(140).Events);
        Assert.Equal(default, drag.Release(140).Events);
    }

    [Fact]
    public void AFrameWithoutAMousePositionAddsNoMotion()
    {
        var drag = Script.Started();
        drag.Hold(drag.X + 10);

        var lost = drag.Hold(float.NaN);

        Assert.Equal((false, 10m), (lost.Events.Changed, lost.Units));
        Assert.Equal(12m, drag.Hold(115).Units);
    }

    /// <summary>The frames of one label, with the state of the open edit as a router keeps it.</summary>
    private sealed class Script
    {
        private readonly LabelDrag drag = new();
        private readonly float scale;
        private int frame;

        public Script(float scale = 1) => this.scale = scale;

        /// <summary>The X position of the mouse after the last frame.</summary>
        public float X { get; private set; }

        /// <summary>True when the open edit belongs to the label, as after a successful start.</summary>
        public bool Owned { get; set; } = true;

        /// <summary>A drag that passed the threshold at X = 103.</summary>
        public static Script Started(float scale = 1)
        {
            var script = new Script(scale);
            script.Press(100);
            Assert.True(script.Hold(103).Events.Activated);
            return script;
        }

        public (FieldEvents Events, decimal Units) Press(float x) => Run(x, activated: true, held: true);

        public (FieldEvents Events, decimal Units) Hold(float x, bool escape = false, bool right = false,
            bool shift = false, bool ctrl = false) =>
            Run(x, held: true, escape: escape, right: right, shift: shift, ctrl: ctrl);

        public (FieldEvents Events, decimal Units) Release(float x, bool escape = false) =>
            Run(x, released: true, escape: escape);

        /// <summary>Another label draws in the frame of the last call.</summary>
        public FieldEvents Other(uint id, bool activated = false) =>
            drag.Frame(new LabelDragInput(id, frame, activated, activated, false, X, false, false, false, false, scale,
                Threshold, false)).Events;

        /// <summary>A frame in which no label draws.</summary>
        public void Skip() => frame++;

        private (FieldEvents Events, decimal Units) Run(float x, bool activated = false, bool held = false,
            bool released = false, bool escape = false, bool right = false, bool shift = false, bool ctrl = false)
        {
            frame++;
            if (!float.IsNaN(x))
                X = x;
            return drag.Frame(new LabelDragInput(LabelX, frame, activated, held, released, x, escape, right, shift,
                ctrl, scale, Threshold, Owned));
        }
    }
}

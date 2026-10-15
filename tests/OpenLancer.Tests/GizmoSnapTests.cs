using System;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The snap steps of a drag: a whole number of steps from the start, on the world axes or on the local axes of the
/// item.
/// </summary>
public sealed class GizmoSnapTests
{
    /// <summary><c>Li01_14_depot_water</c>.</summary>
    private static readonly Vector3 Depot = new(-38675, 0, -27705);

    /// <summary><c>Li01_to_Li05</c>, with <c>rotate = 0, 105, 0</c>.</summary>
    private static readonly Vector3 Gate = new(80632, 0, 12299);

    private static readonly Quaternion GateRotation = MathHelper.QuatFromEulerDegrees(0, 105, 0);

    [Fact]
    public void AWorldMoveSnapsToWholeStepsFromTheStart()
    {
        Assert.Equal(new Vector3(-37675, 0, -27705),
            TransformValues.SnapTranslation(Depot, Depot + new Vector3(1062.5f, 0.3f, -40), 1000, null));
        Assert.Equal(new Vector3(-36675, 0, -27705),
            TransformValues.SnapTranslation(Depot, Depot + new Vector3(1500, 0, 0), 1000, null));
        // Less than half a step gives the start, so the line keeps its value objects.
        Assert.Equal(Depot, TransformValues.SnapTranslation(Depot, Depot + new Vector3(499, -499, 499), 1000, null));
    }

    /// <summary>A step along the local X axis of a gate that is turned 105 degrees changes the world X and Z by
    /// fractions: cos 105 and -sin 105 times the step.</summary>
    [Fact]
    public void ALocalMoveSnapsAlongTheLocalAxesAndGivesFractions()
    {
        var current = Gate + Vector3.Transform(new Vector3(130, 20, -40), GateRotation);

        var snapped = TransformValues.SnapTranslation(Gate, current, 100, GateRotation);

        Assert.Equal(-25.882f, snapped.X - Gate.X, 0.01f);
        Assert.Equal(Gate.Y, snapped.Y);
        Assert.Equal(-96.593f, snapped.Z - Gate.Z, 0.01f);
        var line = TransformValues.Line([new Int32Value(80632), new Int32Value(0), new Int32Value(12299)],
            [snapped.X, snapped.Y, snapped.Z]);
        Assert.IsType<SingleValue>(line[0]);
        Assert.IsType<Int32Value>(line[1]);
        Assert.IsType<SingleValue>(line[2]);
        var lessThanHalf = Gate + Vector3.Transform(new Vector3(40, 0, 0), GateRotation);
        Assert.Equal(Gate, TransformValues.SnapTranslation(Gate, lessThanHalf, 100, GateRotation));
    }

    /// <summary>A world turn is <c>turn * start</c>: the matrix <c>start * Ry</c> of the engine.</summary>
    [Fact]
    public void AWorldTurnSnapsToWholeStepsOfTheAngle()
    {
        var current = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(17)) * GateRotation;

        var snapped = TransformValues.SnapRotation(GateRotation, current, 15);

        var angles = TransformValues.EulerNear(Matrix4x4.CreateFromQuaternion(snapped), new Vector3(0, 105, 0));
        Assert.Equal(new Vector3(0, 120, 0), angles);
        // Less than half a step gives the start.
        Assert.Equal(GateRotation, TransformValues.SnapRotation(GateRotation,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(7)) * GateRotation, 15));
    }

    /// <summary>
    /// A turn keeps its axis: a turn about a local axis of the item (<c>start * turn</c>) snaps about that local axis,
    /// and a turn about the world axis with the same direction snaps about the world axis. The two results are other
    /// rotations.
    /// </summary>
    [Fact]
    public void ATurnSnapsAboutItsOwnAxis()
    {
        var local = TransformValues.SnapRotation(GateRotation,
            GateRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(-38)), 15);
        var world = TransformValues.SnapRotation(GateRotation,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(-38)) * GateRotation, 15);

        var aboutLocalX = GateRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(-45));
        var aboutWorldX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(-45)) * GateRotation;
        Assert.True(MathF.Abs(Quaternion.Dot(aboutLocalX, local)) > 1 - 1e-6f, local.ToString());
        Assert.True(MathF.Abs(Quaternion.Dot(aboutWorldX, world)) > 1 - 1e-6f, world.ToString());
        Assert.False(MathF.Abs(Quaternion.Dot(aboutLocalX, world)) > 1 - 1e-6f);
    }

    /// <summary>
    /// A quaternion and its negation are the same rotation, and the engine can give either one. The snap uses the turn
    /// with the smaller angle, so both give the same result. With a step that does not divide 360, the other turn
    /// (350 degrees about the other axis) would snap to 10 degrees, not to 7.
    /// </summary>
    /// <param name="local">True for a turn about a local axis of the item, false for a turn about a world axis.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANegatedQuaternionSnapsAsTheSameRotation(bool local)
    {
        Quaternion Turn(float degrees) => local
            ? GateRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(degrees))
            : Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(degrees)) * GateRotation;
        var current = Turn(10);

        var snapped = TransformValues.SnapRotation(GateRotation, current, 7);

        Assert.Equal(snapped, TransformValues.SnapRotation(GateRotation, Quaternion.Negate(current), 7));
        Assert.True(MathF.Abs(Quaternion.Dot(Turn(7), snapped)) > 1 - 1e-6f, snapped.ToString());
    }

    [Fact]
    public void ASizeSnapsToWholeStepsFromTheStart()
    {
        Assert.Equal(6000, TransformValues.SnapSize(4100, 6049, 100));
        Assert.Equal(6100, TransformValues.SnapSize(4100, 6051, 100));
        Assert.Equal(4100, TransformValues.SnapSize(4100, 4140, 100));
    }

    /// <summary>A snap can give a size of 0 or less. The size line then gets the smallest size, 1.</summary>
    [Fact]
    public void ASnappedSizeNeverGoesBelow1()
    {
        var start = new ValueBase[] { new Int32Value(150), new Int32Value(200), new Int32Value(300) };
        var axes = ZoneSizeAxes.Of(ShapeKind.Box, start);
        var snapped = TransformValues.SnapSize(150, -420, 100);

        var line = axes.Line(start, new Vector3(snapped, 200, 300));

        Assert.True(snapped < 1);
        Assert.Equal(1, Assert.IsType<Int32Value>(line[0]).ToInt32());
        Assert.Same(start[1], line[1]);
    }

    /// <summary>Ctrl during a drag snaps when the snap toggle is off, and does not snap when it is on (decision D27).
    /// The steps stay.</summary>
    [Fact]
    public void CtrlReversesTheSnapToggle()
    {
        var off = new GizmoSnap(false, 100, 15, 100);
        var on = off with { On = true };

        Assert.Equal(on, off.WithCtrl(true));
        Assert.Equal(off, off.WithCtrl(false));
        Assert.Equal(off, on.WithCtrl(true));
        Assert.Equal(on, on.WithCtrl(false));
    }

    [Fact]
    public void AStepOf0OrLessThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransformValues.SnapTranslation(Depot, Depot, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransformValues.SnapRotation(GateRotation, GateRotation, -15));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransformValues.SnapSize(1, 2, 0));
    }

    private static float Radians(float degrees) => degrees * MathF.PI / 180;
}

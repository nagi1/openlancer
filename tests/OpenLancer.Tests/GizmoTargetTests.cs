using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A synthetic binary Li01 with the kinds of sections that the gizmo changes, and the ones it refuses. Li01_moved has
/// two <c>pos</c> lines, and the engine reads the last one. Light_main has a <c>rotate</c> line, which the engine does
/// not read for a light.
/// </summary>
internal static class GizmoSystem
{
    public const string Li01 = """
        [SystemInfo]
        space_color = 0, 0, 0

        [Object]
        nickname = Li01_depot
        archetype = depot
        pos = -38675, 0, -27705

        [Object]
        nickname = Li01_gate
        archetype = jumpgate
        pos = 80632, 0, 12299
        rotate = 0, 105, 0

        [Object]
        nickname = Li01_bad
        archetype = depot
        pos = abc, 0, 0

        [Object]
        nickname = Li01_moved
        archetype = depot
        pos = 1, 2, 3
        pos = 100, 0, 200

        [Zone]
        nickname = Zone_sphere
        pos = -33270, 0, -33039
        shape = SPHERE
        size = 4100

        [Zone]
        nickname = Zone_cylinder
        pos = 0, 0, 0
        rotate = -29, 16, 3
        shape = CYLINDER
        size = 750, 13088

        [Zone]
        nickname = Zone_flat_box
        shape = BOX
        size = 100, 200

        [LightSource]
        nickname = Light_main
        pos = -33520, 0, -30946
        rotate = 0, 30, 0
        type = POINT

        [LightSource]
        nickname = Light_twice
        pos = 0, 0, 0

        [LightSource]
        nickname = Light_twice
        pos = 1, 0, 0
        """;

    /// <summary>A workspace with <see cref="Li01"/> as a binary file, as retail stores it.</summary>
    public static TestWorkspace Create() => SystemWorkspace.Create(Li01, binary: true);

    public static SectionRef Section(string type, string? nickname) => SystemWorkspace.Li01Section(type, nickname);

    /// <summary>The target of a section that the gizmo can change.</summary>
    public static GizmoTarget Target(EditSession session, SectionRef section)
    {
        var target = GizmoTarget.Of(session, section, null, out var reason);
        Assert.True(target != null, reason);
        Assert.Null(reason);
        return target;
    }
}

/// <summary>The sections that the gizmo changes, the tools of each kind, and the matrix that a drag starts from.</summary>
public sealed class GizmoTargetTests
{
    private const string NoObjectSize = "The engine reads no size for an object.";
    private const string NoLightRotation = "The engine reads no rotation for a light.";
    private const string NoLightSize = "The engine reads no size for a light.";

    /// <summary>An object takes move and rotate, a zone takes all three tools, and a light takes only move (section 10.3
    /// of the design). A zone whose size line cannot scale has the reason of <see cref="ZoneSizeAxes"/>.</summary>
    [Fact]
    public void EachKindTakesItsTools()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");

        AssertTools(session, "Object", "Li01_depot", SceneSection.Object, null, null, null, NoObjectSize);
        AssertTools(session, "Zone", "Zone_sphere", SceneSection.Zone, ShapeKind.Sphere, null, null, null);
        AssertTools(session, "Zone", "Zone_cylinder", SceneSection.Zone, ShapeKind.Cylinder, null, null, null);
        AssertTools(session, "Zone", "Zone_flat_box", SceneSection.Zone, ShapeKind.Box, null, null,
            "The size has two values, so the engine gives Z the size 0. Give it three values first.");
        AssertTools(session, "LightSource", "Light_main", SceneSection.LightSource, null, null, NoLightRotation, NoLightSize);
    }

    /// <summary>
    /// The gizmo shows no target when the editor takes no edits, when the section is read-only or missing, when it is
    /// not an object, a zone or a light, or when it has a check error. The reason tells why.
    /// </summary>
    [Fact]
    public void ASectionThatTheGizmoCannotChangeHasNoTargetAndAReason()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");

        AssertNoTarget(session, GizmoSystem.Section("Object", "Li01_depot"), "The editor waits for a confirmation.",
            "The editor waits for a confirmation.");
        // A patch finds a section by its type and key, so a section with a repeated key is read-only.
        AssertNoTarget(session, GizmoSystem.Section("LightSource", "Light_twice"), null,
            "another [LightSource] section also has nickname = Light_twice.");
        AssertNoTarget(session, GizmoSystem.Section("SystemInfo", null), null,
            "The gizmo changes only an object, a zone or a light.");
        AssertNoTarget(session, GizmoSystem.Section("Object", "Li01_none"), null,
            "The edit view has no section [Object] nickname = Li01_none.");
        // A fast value is the engine value only for a line without a check error, and the scene keeps the last state
        // of such a section.
        AssertNoTarget(session, GizmoSystem.Section("Object", "Li01_bad"), null,
            "[Object] nickname = Li01_bad: 'abc' in a 'pos' line is not a number. The game reads 0 in its place. Correct it, then " +
            "use the gizmo.");

        using var withoutMod = SystemWorkspace.Create(GizmoSystem.Li01, binary: true);
        AssertNoTarget(withoutMod.Session(null), GizmoSystem.Section("Object", "Li01_depot"), null,
            "There is no active mod. Select a mod to edit.");
    }

    /// <summary>
    /// A check error stops the gizmo only on a line that the target reads: <c>pos</c> and <c>rotate</c> of an object,
    /// also <c>shape</c> and <c>size</c> of a zone, and only <c>pos</c> of a light. The fast values of other lines are
    /// not used, so an error there does not stop a move.
    /// </summary>
    [Fact]
    public void ACheckErrorStopsTheGizmoOnlyOnALineThatTheTargetReads()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var depot = GizmoSystem.Section("Object", "Li01_depot");
        var gate = GizmoSystem.Section("Object", "Li01_gate");
        var light = GizmoSystem.Section("LightSource", "Light_main");
        var sphere = GizmoSystem.Section("Zone", "Zone_sphere");
        var cylinder = GizmoSystem.Section("Zone", "Zone_cylinder");

        // The save refuses a text placeholder without text, but the pos line of the depot is valid.
        session.Execute(EditCommand.SetLines(depot, "ids_name", [[new StringValue("$str:missing")]]));
        session.Execute(EditCommand.SetLinesFromText(light, "rotate", ["abc, 0, 0"]));
        session.Execute(EditCommand.SetLinesFromText(gate, "rotate", ["abc, 0, 0"]));
        session.Execute(EditCommand.SetLinesFromText(sphere, "shape", ["CUBE"]));
        session.Execute(EditCommand.SetLinesFromText(cylinder, "size", ["abc, 13088"]));

        Assert.Contains(session.Check(depot), p => p.Severity == DiagnosticSeverity.Error && p.Key == "ids_name");
        GizmoSystem.Target(session, depot);
        Assert.Contains(session.Check(light), p => p.Severity == DiagnosticSeverity.Error && p.Key == "rotate");
        GizmoSystem.Target(session, light);
        AssertNoTarget(session, gate, null,
            "[Object] nickname = Li01_gate: 'abc' in a 'rotate' line is not a number. The game reads 0 in its place. Correct it, " +
            "then use the gizmo.");
        Assert.Null(GizmoTarget.Of(session, sphere, null, out var shapeReason));
        Assert.StartsWith("[Zone] nickname = Zone_sphere: 'CUBE' in a 'shape' line is not one of", shapeReason);
        AssertNoTarget(session, cylinder, null,
            "[Zone] nickname = Zone_cylinder: 'abc' in a 'size' line is not a number. The game reads 0 in its place. Correct it, " +
            "then use the gizmo.");
    }

    /// <summary>
    /// The matrix of a tool is the rotation and the position that the engine reads from W. An object has the matrix of
    /// the quaternion of its world object. The scale tool of a zone puts the scale of the zone first (section 10.5). A
    /// light has no rotation, also with a <c>rotate</c> line.
    /// </summary>
    [Fact]
    public void TheMatrixOfAToolIsTheTransformOfW()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var gate = GizmoSystem.Target(session, GizmoSystem.Section("Object", "Li01_gate"));
        var cylinder = GizmoSystem.Target(session, GizmoSystem.Section("Zone", "Zone_cylinder"));
        var light = GizmoSystem.Target(session, GizmoSystem.Section("LightSource", "Light_main"));

        var gateMatrix = Matrix4x4.CreateFromQuaternion(MathHelper.MatrixFromEulerDegrees(0, 105, 0).ExtractRotation()) *
                         Matrix4x4.CreateTranslation(80632, 0, 12299);
        Assert.Equal(gateMatrix, gate.Matrix(GizmoTool.Move));
        Assert.Equal(gateMatrix, gate.Matrix(GizmoTool.Rotate));
        var zoneRotation = MathHelper.MatrixFromEulerDegrees(-29, 16, 3);
        Assert.Equal(zoneRotation, cylinder.Matrix(GizmoTool.Rotate));
        Assert.Equal(Matrix4x4.CreateScale(750, 13088, 1) * zoneRotation, cylinder.Matrix(GizmoTool.Scale));
        Assert.Equal(Matrix4x4.CreateTranslation(-33520, 0, -30946), light.Matrix(GizmoTool.Move));
    }

    /// <summary>The start values come from the last line of a key, which is the line that the engine reads. A repeated
    /// key is only a warning, so the section has a target.</summary>
    [Fact]
    public void TheMatrixComesFromTheLastLineOfAKey()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");

        var moved = GizmoSystem.Target(session, GizmoSystem.Section("Object", "Li01_moved"));

        Assert.Equal(Matrix4x4.CreateTranslation(100, 0, 200), moved.Matrix(GizmoTool.Move));
    }

    /// <summary>
    /// A tool that the section does not take has no matrix. The scale of a 2-value box is 0 on Z, and ImGuizmo inverts
    /// the matrix that it gets (ImGuizmo.cpp:1059), so such a matrix would give NaN.
    /// </summary>
    [Fact]
    public void AToolThatTheSectionDoesNotTakeHasNoMatrix()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flatBox = GizmoSystem.Target(session, GizmoSystem.Section("Zone", "Zone_flat_box"));
        var depot = GizmoSystem.Target(session, GizmoSystem.Section("Object", "Li01_depot"));
        var light = GizmoSystem.Target(session, GizmoSystem.Section("LightSource", "Light_main"));

        AssertNoMatrix(flatBox, GizmoTool.Scale,
            "The size has two values, so the engine gives Z the size 0. Give it three values first.");
        AssertNoMatrix(depot, GizmoTool.Scale, NoObjectSize);
        AssertNoMatrix(light, GizmoTool.Rotate, NoLightRotation);
        AssertNoMatrix(light, GizmoTool.Scale, NoLightSize);
        // The box still moves and turns.
        Assert.Equal(Matrix4x4.Identity, flatBox.Matrix(GizmoTool.Move));
        Assert.Equal(Matrix4x4.Identity, flatBox.Matrix(GizmoTool.Rotate));
    }

    private static void AssertNoMatrix(GizmoTarget target, GizmoTool tool, string reason) =>
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => target.Matrix(tool)).Message);

    private static void AssertTools(EditSession session, string type, string nickname, SceneSection kind,
        ShapeKind? shape, string? move, string? rotate, string? scale)
    {
        var section = GizmoSystem.Section(type, nickname);
        var target = GizmoSystem.Target(session, section);
        Assert.Equal(section, target.Section);
        Assert.Equal(kind, target.Kind);
        Assert.Equal(shape, target.Shape);
        Assert.Equal(new Dictionary<GizmoTool, string?>
        {
            [GizmoTool.Move] = move,
            [GizmoTool.Rotate] = rotate,
            [GizmoTool.Scale] = scale,
        }, target.Tools);
    }

    private static void AssertNoTarget(EditSession session, SectionRef section, string? noEditReason, string expected)
    {
        Assert.Null(GizmoTarget.Of(session, section, noEditReason, out var reason));
        Assert.Equal(expected, reason);
    }
}

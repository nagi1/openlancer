using System;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using LibreLancer.Render.Cameras;
using OpenLancer.Editor;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the pick stack and the Hierarchy rows of the nebulae and asteroid fields (section 16.3 of the M22
/// design).
/// </summary>
public sealed class PickRetailTests(ITestOutputHelper output)
{
    private const string BuffaloBase = "Li01_12";
    private const string BadlandsNebula = "Zone_Li01_Badlands_Nebula";
    private const string BadlandsField = "Zone_Li01_Badlands_Asteroids";

    /// <summary>
    /// Li01, with the camera 2 km above Buffalo Base and a ray up. The base is inside the Badlands nebula and its field,
    /// and the ray crosses no other volume. So the nebulae and fields layer holds the nebula, then the field: both hold
    /// the camera, and the nebula comes first. The zones layer holds the field zone, then the nebula zone, the smaller
    /// first. With the Zones toggle off, the fog of the nebula and the asteroids of the field still show, so both stay
    /// in the stack.
    /// </summary>
    [Fact]
    public void FromBuffaloBaseTheBadlandsNebulaComesBeforeItsField()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m22pick");
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        var index = new SystemIndex(scene.System, scene.Source);
        var nebula = scene.System.Nebulae.Single(n => n.Zone?.Nickname == BadlandsNebula);
        var field = scene.System.AsteroidFields.Single(f => f.Zone?.Nickname == BadlandsField);
        var eye = scene.Object(BuffaloBase).Position + new Vector3(0, 2_000, 0);

        var items = Pick(scene, index, eye, zones: true);
        output.WriteLine(string.Join(", ", items.Select(Describe)));

        Assert.Equal([nebula, field, field.Zone!, nebula.Zone!], items);
        Assert.Equal([nebula, field], Pick(scene, index, eye, zones: false));
    }

    /// <summary>
    /// Each of the 58 nebulae and of the 201 fields of the 53 retail systems has a row in the Hierarchy of its system.
    /// This includes the 7 nebulae whose zone is the zone of a field. The folder of the intro system holds one more
    /// system file with 2 nebulae and 9 fields. But universe.ini does not name that file, so the engine does not load
    /// it.
    /// </summary>
    [Fact]
    public void EachRetailNebulaIsInTheHierarchy()
    {
        GameDataPaths.RequireBase();
        var systems = RetailGame.Instance.Data.Items.Systems.ToList();
        var (nebulae, fields, shared) = (0, 0, 0);
        foreach (var system in systems)
        {
            var index = new SystemIndex(system, system);
            var model = new HierarchyModel(index, (_, _) => "");
            foreach (var nebula in system.Nebulae)
            {
                Assert.True(model.NodeOf(nebula)?.Parent?.Id == "nebulae", $"{system.Nickname}: {nebula.SourceFile}");
                nebulae++;
                shared += system.AsteroidFields.Any(f => ReferenceEquals(f.Zone, nebula.Zone)) ? 1 : 0;
            }

            foreach (var field in system.AsteroidFields)
            {
                Assert.True(model.NodeOf(field)?.Parent?.Id == "fields", $"{system.Nickname}: {field.SourceFile}");
                fields++;
            }

            var counts = HierarchyModel.TypeCounts(system);
            Assert.Equal((system.Nebulae.Count, system.AsteroidFields.Count), (counts["nebula"], counts["field"]));
        }

        output.WriteLine($"{systems.Count} systems, {nebulae} nebulae, {fields} fields, {shared} shared zones");
        Assert.Equal((58, 201, 7), (nebulae, fields, shared));
    }

    /// <summary>The pick stack of a click at the center of the view, with the camera at a point and looking up
    /// (+Y).</summary>
    private static object[] Pick(HeadlessScene scene, SystemIndex index, Vector3 eye, bool zones)
    {
        var size = new Vector2(800, 600);
        var turn = Matrix4x4.CreateRotationX(MathF.PI / 2);
        var camera = new LookAtCamera { ZRange = new Vector2(1, 10_000_000) };
        camera.Update(size.X, size.Y, eye, eye + Vector3.TransformNormal(-Vector3.UnitZ, turn), turn);
        var projection = new SceneProjection(camera, Vector2.Zero, size, 1);
        var axes = new AxisGizmo();
        axes.Update(projection);
        var markers = new SceneMarkers();
        markers.Update(projection, index, new EditorSettings { ShowMarkers = false }, null);
        var picker = new ScenePicker(axes, markers, scene.Objects);
        return picker.Pick(projection, size / 2, index, new PickView(eye, zones, Nebulae: true, Fog: true)).Items
            .ToArray();
    }

    private static string Describe(object item) => item switch
    {
        Nebula nebula => $"nebula {nebula.Zone?.Nickname}",
        AsteroidField field => $"field {field.Zone?.Nickname}",
        Zone zone => $"zone {zone.Nickname}",
        SystemObject obj => $"object {obj.Nickname}",
        _ => item.ToString() ?? "",
    };
}

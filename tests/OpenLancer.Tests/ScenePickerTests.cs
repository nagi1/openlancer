using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Archetypes;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.World;
using OpenLancer.Editor;
using OpenLancer.Editor.Scene;
using Xunit;
using Encounter = LibreLancer.Data.Schema.Universe.Encounter;

namespace OpenLancer.Tests;

/// <summary>
/// The picks of the Scene with synthetic shapes. The camera is at the world origin and looks along -Z with Y up, unless
/// a test moves or turns it. Its near plane is at 1 unit, and the UI scale is 1. The scene rectangle starts at
/// (100, 50) and is 800 by 600 pixels.
/// </summary>
public sealed class ScenePickerTests
{
    private static readonly Vector2 Origin = new(100, 50);
    private static readonly Vector2 Size = new(800, 600);
    private static readonly Vector2 Center = Origin + Size / 2;

    private readonly Dictionary<SystemObject, GameObject> objects = [];
    private readonly List<SystemObject> systemObjects = [];
    private readonly List<Zone> zones = [];
    private readonly List<AsteroidField> fields = [];

    // Markers are off by default, so that a test of the ray sees no marker.
    private readonly EditorSettings settings = new() { ShowMarkers = false };
    private object? selection;
    private Vector3 eye = Vector3.Zero;

    // The turn of the camera, as SceneCamera turns it: the pitch about X, then the yaw about Y.
    private Matrix4x4 turn = Matrix4x4.Identity;
    private float nearPlane = 1;
    private float uiScale = 1;

    [Fact]
    public void HitsTheBoxOfAModelAndTheSphereOfAPlanet()
    {
        var station = Add("station", new Vector3(0, 0, -5_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        var planet = Add("planet", new Vector3(3_000, 0, -10_000), ArchetypeType.planet, SphereModel(1_000));

        Assert.Same(station, Item(Center));
        // The ray enters the box at a depth of 4,900, where the point (90, 0, -5000) is at X 88.2.
        Assert.Same(station, Item(Screen(new Vector3(90, 0, -5_000))));
        Assert.Null(Item(Screen(new Vector3(150, 0, -5_000))));
        // From the camera, the sphere has an angular radius of 5.50 degrees. These points are 4.60 and 6.08 degrees
        // from its center.
        Assert.Same(planet, Item(Screen(new Vector3(3_900, 0, -10_000))));
        Assert.Null(Item(Screen(new Vector3(4_200, 0, -10_000))));
    }

    [Fact]
    public void GivesAPlanetItsSphereAndNotTheBoxOfItsModel()
    {
        // A point on the near face of the box of the sphere model, near a corner. The ray to it passes 1,616 units from
        // the center, so it misses the sphere of radius 1,000.
        var corner = new Vector3(3_950, 950, -9_000);
        var planet = Add("planet", new Vector3(3_000, 0, -10_000), ArchetypeType.planet, SphereModel(1_000));

        Assert.Null(Item(Screen(corner)));

        Remove(planet);
        var station = Add("station", new Vector3(3_000, 0, -10_000), ArchetypeType.station, SphereModel(1_000));

        Assert.Same(station, Item(Screen(corner)));
    }

    [Fact]
    public void TurnsTheBoxWithTheObject()
    {
        // A box 2,000 long on X and 200 thick, turned 90 degrees about Y, lies along Z.
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var lane = Add("lane", new Vector3(500, 0, -10_000), ArchetypeType.station,
            BoxModel(new Vector3(1_000, 100, 100)), turned);

        Assert.Same(lane, Item(Screen(new Vector3(500, 0, -10_900))));
        Assert.Null(Item(Screen(new Vector3(1_400, 0, -10_000))));
    }

    [Fact]
    public void HitsABoxFromATurnedCamera()
    {
        // The camera is away from the origin, with a yaw of 30 degrees and a pitch of -15 degrees. The box is about 7
        // degrees from the center of the view. The camera sees the +X, +Y and +Z faces of the box. So the -X and -Y
        // edges of the +Z face are edges of the box on the screen. 50 units at the box are about 2.7 pixels.
        eye = new Vector3(5_000, 3_000, 2_000);
        turn = Matrix4x4.CreateRotationX(Degrees(-15)) * Matrix4x4.CreateRotationY(Degrees(30));
        var box = Add("box", new Vector3(-2_000, 1_000, -8_000), ArchetypeType.station, BoxModel(new Vector3(500)));
        const float face = -7_500;

        Assert.Same(box, Item(Screen(new Vector3(-2_000, 1_000, face))));
        Assert.Same(box, Item(Screen(new Vector3(-2_450, 1_000, face))));
        Assert.Null(Item(Screen(new Vector3(-2_550, 1_000, face))));
        Assert.Same(box, Item(Screen(new Vector3(-2_000, 550, face))));
        Assert.Null(Item(Screen(new Vector3(-2_000, 450, face))));
    }

    [Fact]
    public void HitsAModelBoxThatIsNotCenteredOnItsObject()
    {
        // A retail model box is often not centered on its object. This box is from (0, 0, 0) to (400, 400, 400) in
        // the model. The object turns 90 degrees about Z, so the box is from (-400, 0, 0) to (0, 400, 400) from the
        // object, and its +Z face is at a depth of 4,600.
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var ring = Add("ring", new Vector3(0, 0, -5_000), ArchetypeType.docking_ring,
            Model(new BoundingBox(Vector3.Zero, new Vector3(400)), 700, RigidModelSource.SinglePart), turned);

        Assert.Same(ring, Item(Screen(new Vector3(-300, 300, -4_600))));
        // A box centered on the object would hold the ray to this point.
        Assert.Null(Item(Screen(new Vector3(100, -100, -4_600))));
    }

    [Fact]
    public void GivesAModelWithoutAMeshTheSphereOfItsRadius()
    {
        // The box of a model without a mesh is empty, with its minimum above its maximum. The pick shape is then the
        // sphere of ScenePicker.Radius, which is 50 here. At 2,000 units, these points are 40 and 60 units from the
        // center.
        var part = new RigidModelPart();
        var model = new RigidModel { Root = part, AllParts = [part], Source = RigidModelSource.SinglePart };
        model.UpdateTransform();
        var buoy = Add("buoy", new Vector3(0, 0, -2_000), ArchetypeType.NONE, model);

        Assert.Equal(50, ScenePicker.Radius(model));
        Assert.Same(buoy, Item(Screen(new Vector3(40, 0, -2_000))));
        Assert.Null(Item(Screen(new Vector3(60, 0, -2_000))));
    }

    [Fact]
    public void TheRayStartsAtTheNearPlane()
    {
        // Nothing nearer to the camera than the near plane shows, so it is not hit. The hidden box and the hidden zone
        // are from a depth of 300 to 700, in front of the visible station and field. The ray to the field misses the
        // hidden box.
        nearPlane = 1_000;
        Add("hidden", new Vector3(0, 0, -500), ArchetypeType.station, BoxModel(new Vector3(50, 50, 200)));
        AddField(Volume("hidden field", ShapeKind.Sphere, new Vector3(0, 150, -500), new Vector3(200)));
        var station = Add("station", new Vector3(0, 0, -5_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 1_500, -5_000), new Vector3(1_000)));

        Assert.Same(station, Item(Center));
        Assert.Same(field, Item(Screen(new Vector3(0, 1_500, -5_000))));

        // The near plane cuts open a box that it crosses, as a box that holds the camera is open around it. This box
        // is from a depth of 900 to 1,100.
        Add("cut", new Vector3(0, 0, -1_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        Assert.Same(station, Item(Center));
    }

    [Fact]
    public void TheNearestHitWins()
    {
        Add("far", new Vector3(0, 0, -9_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        var near = Add("near", new Vector3(0, 0, -3_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        Add("middle", new Vector3(0, 0, -6_000), ArchetypeType.station, BoxModel(new Vector3(100)));

        Assert.Same(near, Item(Center));

        // The hit distance counts, not the distance of the center: this long box starts at a depth of 2,000, and its
        // center is at 20,000.
        var longBox = Add("long", new Vector3(0, 0, -20_000), ArchetypeType.station,
            BoxModel(new Vector3(100, 100, 18_000)));

        Assert.Same(longBox, Item(Center));
    }

    [Fact]
    public void ASmallBoxInFrontOfABigSphereWins()
    {
        // As Dock_Ring_Li01_01 in front of Manhattan: the ring is 4,229 units from the planet center, and the planet
        // has a radius of 4,000.
        var planet = Add("planet", new Vector3(0, 0, -30_000), ArchetypeType.planet, SphereModel(4_000));
        var ring = Add("ring", new Vector3(0, 0, -25_771), ArchetypeType.docking_ring,
            BoxModel(new Vector3(150, 150, 50)));

        Assert.Same(ring, Item(Center));
        Assert.Same(planet, Item(Screen(new Vector3(1_000, 0, -30_000))));
    }

    [Fact]
    public void HitsABoxFarBeyondTheOldDistanceLimit()
    {
        var far = Add("far", new Vector3(0, 0, -200_000), ArchetypeType.station, BoxModel(new Vector3(500)));

        Assert.Same(far, Item(Center));
    }

    [Fact]
    public void SkipsAShapeThatHoldsTheCameraAndHitsTheObjectBehindIt()
    {
        var station = Add("station", new Vector3(0, 0, -1_000), ArchetypeType.station, BoxModel(new Vector3(5_000)));
        Add("planet", new Vector3(0, 0, 500), ArchetypeType.planet, SphereModel(2_000));

        Assert.Null(Item(Center));

        var behind = Add("behind", new Vector3(0, 0, -30_000), ArchetypeType.satellite, BoxModel(new Vector3(100)));

        Assert.Same(behind, Item(Center));

        // From outside, the station box is hit again.
        eye = new Vector3(0, 0, 10_000);
        Assert.Same(station, Item(Center));
    }

    [Fact]
    public void HitsAnObjectWithoutAModelByItsFallbackSphere()
    {
        // The fallback sphere has a radius of 500: an angular radius of 2.87 degrees at 10,000 units. These points are
        // 2.29 and 3.43 degrees from its center.
        var buoy = Add("buoy", new Vector3(0, 0, -10_000), ArchetypeType.NONE, model: null);

        Assert.Same(buoy, Item(Screen(new Vector3(400, 0, -10_000))));
        Assert.Null(Item(Screen(new Vector3(600, 0, -10_000))));
        Assert.Equal(500, ScenePicker.Radius(null));
    }

    [Fact]
    public void HitsASunByTheRadiusOfItsStar()
    {
        // The star radius 2,000 gives 5.74 degrees at 20,000 units. These points are 4.29 and 7.13 degrees away.
        var sun = Add("sun", new Vector3(0, 0, -20_000), ArchetypeType.sun, model: null, star: new Sun { Radius = 2_000 });

        Assert.Same(sun, Item(Screen(new Vector3(1_500, 0, -20_000))));
        Assert.Null(Item(Screen(new Vector3(2_500, 0, -20_000))));
    }

    [Fact]
    public void AMarkerWinsOverAModel()
    {
        settings.ShowMarkers = true;
        // The box covers the center of the screen, but its marker is far to the right of it.
        var box = Add("box", new Vector3(3_000, 0, -5_000), ArchetypeType.station,
            BoxModel(new Vector3(4_000, 500, 500)));
        var station = Add("station", new Vector3(0, 0, -20_000), ArchetypeType.station, model: null);

        Assert.Same(station, Item(Center));

        settings.ShowMarkers = false;
        Assert.Same(box, Item(Center));
    }

    [Fact]
    public void AFadedMarkerCannotBePicked()
    {
        settings.ShowMarkers = true;
        // Markers only: these objects have no world object, so the ray cannot hit them.
        var satellite = Add("satellite", new Vector3(0, 0, -20_000), ArchetypeType.satellite, model: null, world: false);

        Assert.Same(satellite, Item(Center));

        // A satellite marker is gone at 30,000 units.
        eye = new Vector3(0, 0, 20_000);
        Assert.Null(Item(Center));

        // The selected item never fades.
        selection = satellite;
        Assert.Same(satellite, Item(Center));

        // A jump gate never fades: its marker is picked as far away as Li01_to_Li05 is from Manhattan.
        selection = null;
        var gate = Add("gate", new Vector3(0, 0, -102_600), ArchetypeType.jump_gate, model: null, world: false);
        Assert.Same(gate, Item(Center));
    }

    [Fact]
    public void TheAxisWidgetWinsOverAMarker()
    {
        settings.ShowMarkers = true;
        // The X end of the axis widget: 48 points from the top-right corner, then 32 points to the right.
        var end = new Vector2(Origin.X + Size.X - 48 + 32, Origin.Y + 48);
        var ray = Projection().Ray(end);
        Add("station", ray.Position + ray.Direction * 10_000, ArchetypeType.station, model: null);

        var pick = Pick(end);

        Assert.Equal(Vector3.UnitX, pick.Axis);
        Assert.Null(pick.Item);
    }

    [Fact]
    public void PicksAZoneOnlyWhenNoObjectIsHit()
    {
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 0, -5_000), new Vector3(1_000)));
        var station = Add("station", new Vector3(0, 0, -20_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        var besideStation = Screen(new Vector3(700, 0, -5_000));

        Assert.Same(station, Item(Center));
        Assert.Same(field, Item(besideStation));

        // The Zones toggle shows the volumes. Without it, a volume is not hit.
        settings.ShowZones = false;
        Assert.Null(Item(besideStation));
    }

    [Fact]
    public void SkipsAZoneThatHoldsTheCamera()
    {
        AddField(Volume("around", ShapeKind.Sphere, Vector3.Zero, new Vector3(10_000)));

        Assert.Null(Item(Center));

        var ahead = AddField(Volume("ahead", ShapeKind.Sphere, new Vector3(0, 0, -30_000), new Vector3(1_000)));

        Assert.Same(ahead, Item(Center));
    }

    [Fact]
    public void HitsAPatrolPathNearItsLineOnly()
    {
        // The path runs along X through (0, 0, -10000). Its cylinder of radius 500 covers about 32 pixels on each
        // side of the line, but only the line counts.
        var path = AddPath(new Vector3(0, 0, -10_000), Matrix4x4.CreateRotationZ(MathF.PI / 2), 6_000);

        Assert.Same(path, Item(Center + new Vector2(0, 3)));
        Assert.Null(Item(Center + new Vector2(0, 6)));
        Assert.Null(Item(Screen(new Vector3(3_200, 0, -10_000))));

        // The camera in the cylinder does not stop the hit, because the path shows as a line.
        eye = new Vector3(0, 50, -9_700);
        Assert.Same(path, Item(Screen(new Vector3(0, 0, -10_000))));
    }

    [Fact]
    public void TheNearestOfAPathAndAVolumeWins()
    {
        // The path runs along Z, 100 units below the camera, from a depth of 1,000 to 100,000.
        var path = AddPath(new Vector3(0, -100, -50_500), Matrix4x4.CreateRotationX(MathF.PI / 2), 99_000);
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, -100, -50_000), new Vector3(1_000)));
        // The point of the path at a depth of 20,000. On the screen it is 4% of the way from the far end of the path,
        // so a distance that is linear on the screen would give 96,000 and lose to the field at 49,000.
        var click = Screen(new Vector3(0, -100, -20_000));

        Assert.Same(path, Item(click));

        field.Position = new Vector3(0, -100, -10_000);
        Assert.Same(field, Item(click));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void HitsAPatrolPathThatCrossesTheNearPlane(float degrees)
    {
        // The path runs along Z, 100 units below the camera, from 10,000 in front of the camera to 10,000 behind it.
        // With 90 degrees about X, its line ends behind the camera. With -90 degrees, its line starts there.
        var path = AddPath(new Vector3(0, -100, 0), Matrix4x4.CreateRotationX(Degrees(degrees)), 20_000);
        Assert.Equal(degrees < 0, SystemIndex.PathLine(path).Start.Z > 0);

        Assert.Same(path, Item(Screen(new Vector3(0, -100, -5_000))));
        Assert.Null(Item(Screen(new Vector3(0, 100, -5_000))));
    }

    [Fact]
    public void IgnoresAPatrolPathThatIsWhollyBehindTheCamera()
    {
        // The path runs along Z, 100 units above the camera, from 2,000 to 20,000 behind it, so it does not show. A cut
        // at the near plane would move its start to the front of the camera. The line on the screen would then run
        // through these points.
        AddPath(new Vector3(0, 100, 11_000), Matrix4x4.CreateRotationX(MathF.PI / 2), 18_000);

        Assert.Null(Item(Screen(new Vector3(0, 100, -2_000))));
        Assert.Null(Item(Screen(new Vector3(0, 100, -10_000))));
    }

    [Fact]
    public void TheUiScaleScalesThePickSizes()
    {
        uiScale = 2;
        settings.ShowMarkers = true;

        // A patrol path is hit within 4 points of its line, which is 8 pixels here.
        var path = AddPath(new Vector3(0, 0, -10_000), Matrix4x4.CreateRotationZ(MathF.PI / 2), 6_000);
        Assert.Same(path, Item(Center + new Vector2(0, 6)));
        Assert.Null(Item(Center + new Vector2(0, 10)));

        // A station marker is 14 points. It is hit within half its size plus 3 points, which is 20 pixels here. The
        // station has no world object, so the ray cannot hit it.
        var station = Add("station", new Vector3(0, 3_000, -10_000), ArchetypeType.station, model: null, world: false);
        var marker = Screen(station.Position);
        Assert.Same(station, Item(marker + new Vector2(19.5f, 0)));
        Assert.Null(Item(marker + new Vector2(20.5f, 0)));

        // The X end of the axis widget: 96 pixels from the top-right corner, then 64 pixels to the right.
        Assert.Equal(Vector3.UnitX, Pick(new Vector2(Origin.X + Size.X - 96 + 64, Origin.Y + 96)).Axis);
    }

    [Fact]
    public void EmptySpaceGivesNothing()
    {
        Add("station", new Vector3(5_000, 0, -5_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 5_000, -5_000), new Vector3(1_000)));
        Add("behind", new Vector3(0, 0, 5_000), ArchetypeType.station, BoxModel(new Vector3(100)));

        Assert.Equal(default, Pick(Center));
    }

    [Fact]
    public void FindsTheEntryOfEachZoneShape()
    {
        var forward = new Ray(Vector3.Zero, -Vector3.UnitZ);

        Assert.Equal(4_000, Entry(Volume("sphere", ShapeKind.Sphere, new Vector3(0, 0, -5_000), new Vector3(1_000)),
            forward), 0.01f);
        Assert.Null(ScenePicker.ZoneEntry(
            Volume("sphere", ShapeKind.Sphere, new Vector3(0, 0, 5_000), new Vector3(1_000)), forward));

        var ellipsoid = Volume("ellipsoid", ShapeKind.Ellipsoid, new Vector3(0, 0, -10_000), new Vector3(1_000, 2_000,
            3_000));
        Assert.Equal(7_000, Entry(ellipsoid, forward), 0.01f);
        // At Y 1,500 the ellipsoid reaches 3,000 * sqrt(1 - 0.75^2) = 1,984.3 along Z.
        Assert.Equal(8_015.686f, Entry(ellipsoid, new Ray(new Vector3(0, 1_500, 0), -Vector3.UnitZ)), 0.01f);

        // A box of 2,000 turned 45 degrees about Y shows its edge to the ray.
        var box = Volume("box", ShapeKind.Box, new Vector3(0, 0, -10_000), new Vector3(2_000),
            Matrix4x4.CreateRotationY(MathF.PI / 4));
        Assert.Equal(10_000 - 1_000 * MathF.Sqrt(2), Entry(box, forward), 0.01f);

        var cylinder = Volume("cylinder", ShapeKind.Cylinder, new Vector3(0, 0, -10_000), new Vector3(500, 4_000, 0));
        Assert.Equal(9_500, Entry(cylinder, forward), 0.01f);
        Assert.Equal(3_000, Entry(cylinder, new Ray(new Vector3(0, 5_000, -10_000), -Vector3.UnitY)), 0.01f);
        Assert.Null(ScenePicker.ZoneEntry(cylinder, new Ray(new Vector3(0, 2_500, -10_000), -Vector3.UnitZ)));

        // A ring stores (outer radius, height, inner radius).
        var ring = Volume("ring", ShapeKind.Ring, new Vector3(0, 0, -10_000), new Vector3(1_000, 200, 600));
        Assert.Equal(9_000, Entry(ring, forward), 0.01f);
        // Down the axis, the ray passes through the hole.
        Assert.Null(ScenePicker.ZoneEntry(ring, new Ray(new Vector3(0, 1_000, -10_000), -Vector3.UnitY)));
        // From the center of the hole, the ray enters the ring at its inner wall.
        Assert.Equal(600, Entry(ring, new Ray(new Vector3(0, 0, -10_000), -Vector3.UnitZ)), 0.01f);
        // From in the ring, the ring is not hit.
        Assert.Null(ScenePicker.ZoneEntry(ring, new Ray(new Vector3(0, 0, -10_800), -Vector3.UnitZ)));
    }

    private static float Entry(Zone zone, Ray ray) =>
        ScenePicker.ZoneEntry(zone, ray) ?? throw new InvalidOperationException($"The ray misses {zone.Nickname}.");

    private SystemObject Add(string nickname, Vector3 position, ArchetypeType type, RigidModel? model,
        Quaternion? rotation = null, Sun? star = null, bool world = true)
    {
        var obj = new SystemObject
        {
            Nickname = nickname,
            Position = position,
            Rotation = rotation ?? Quaternion.Identity,
            Archetype = new Archetype { Nickname = nickname, Type = type },
            Star = star,
        };
        systemObjects.Add(obj);
        if (world)
        {
            var gameObject = new GameObject { RenderComponent = model == null ? null : new ModelRenderer(model) };
            gameObject.SetTransform(new Transform3D(position, obj.Rotation));
            objects[obj] = gameObject;
        }

        return obj;
    }

    private void Remove(SystemObject obj)
    {
        systemObjects.Remove(obj);
        objects.Remove(obj);
    }

    /// <summary>A model with one part whose mesh has a box from -half to half.</summary>
    private static RigidModel BoxModel(Vector3 half) => Model(new BoundingBox(-half, half), half.Length(),
        RigidModelSource.SinglePart);

    /// <summary>A model as a planet .sph file gives it: a sphere, with the box around the sphere.</summary>
    private static RigidModel SphereModel(float radius) =>
        Model(BoundingBox.CreateFromSphere(new BoundingSphere(Vector3.Zero, radius)), radius, RigidModelSource.Sphere);

    private static RigidModel Model(BoundingBox box, float radius, RigidModelSource source)
    {
        var part = new RigidModelPart { Mesh = new VisualMesh { BoundingBox = box, Radius = radius } };
        var model = new RigidModel { Root = part, AllParts = [part], Source = source };
        model.UpdateTransform();
        return model;
    }

    private static Zone Volume(string nickname, ShapeKind shape, Vector3 position, Vector3 size,
        Matrix4x4? rotation = null) => new()
    {
        Nickname = nickname,
        Shape = shape,
        RotationMatrix = rotation ?? Matrix4x4.Identity,
        Size = size,
        Position = position,
    };

    /// <summary>Adds the zone of an asteroid field. The Scene draws its volume.</summary>
    private Zone AddField(Zone zone)
    {
        zones.Add(zone);
        fields.Add(new AsteroidField { SourceFile = "", Zone = zone });
        return zone;
    }

    /// <summary>Adds a patrol path: a cylinder of radius 500 whose axis is its line.</summary>
    private Zone AddPath(Vector3 center, Matrix4x4 rotation, float length)
    {
        var zone = Volume("path", ShapeKind.Cylinder, center, new Vector3(500, length, 0), rotation);
        zone.Encounters = [new Encounter()];
        zone.PathLabel = ["path", "1"];
        zones.Add(zone);
        return zone;
    }

    private SceneProjection Projection()
    {
        var camera = new LookAtCamera { ZRange = new Vector2(nearPlane, 10_000_000) };
        camera.Update(Size.X, Size.Y, eye, eye + Vector3.TransformNormal(-Vector3.UnitZ, turn), turn);
        return new SceneProjection(camera, Origin, Size, uiScale);
    }

    private static float Degrees(float degrees) => degrees * MathF.PI / 180;

    /// <summary>The screen point of a world point.</summary>
    private Vector2 Screen(Vector3 position)
    {
        Assert.True(Projection().Project(position, out var screen, out _), $"{position} is behind the camera.");
        return screen;
    }

    private object? Item(Vector2 mouse) => Pick(mouse).Item;

    private ScenePick Pick(Vector2 mouse)
    {
        var projection = Projection();
        var system = new StarSystem
        {
            Nickname = "li01", SourceFile = "", Objects = [.. systemObjects], Zones = [.. zones],
            AsteroidFields = [.. fields],
        };
        var index = new SystemIndex(system, system);
        var axes = new AxisGizmo();
        axes.Update(projection);
        var markers = new SceneMarkers();
        markers.Update(projection, index, settings, selection);
        return new ScenePicker(axes, markers, objects).Pick(projection, mouse, index, settings.ShowZones);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
using OpenLancer.Editor.Ui;
using Xunit;
using Encounter = LibreLancer.Data.Schema.Universe.Encounter;

namespace OpenLancer.Tests;

/// <summary>
/// The picks of the Scene with synthetic shapes. The camera is at the world origin and looks along -Z with Y up, unless
/// a test moves or turns it. Its near plane is at 1 unit, and the UI scale is 1. The scene rectangle starts at
/// (100, 50) and is 800 by 600 pixels. The pick stack of milestone M22 has four layers: glyphs, objects, nebulae and
/// fields, then zones (section 6.1 of the M22 design).
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
    private readonly List<Nebula> nebulae = [];

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
        Assert.Single(Pick(Center).Items);
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
    public void PicksAFieldOnlyWhenNoObjectIsHit()
    {
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 0, -5_000), new Vector3(1_000)));
        var station = Add("station", new Vector3(0, 0, -20_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        var besideStation = Screen(new Vector3(700, 0, -5_000));

        Assert.Equal([station, field, field.Zone!], Pick(Center).Items);
        // The field item comes first, and its zone second.
        Assert.Equal([field, field.Zone!], Pick(besideStation).Items);

        // The Zones toggle shows the volumes. Without it, the field is a target only when its asteroids draw, and the
        // camera is 5,000 from its center, beyond its size of 1,000.
        settings.ShowZones = false;
        Assert.Null(Item(besideStation));
    }

    [Fact]
    public void AVolumeAheadComesBeforeAVolumeThatHoldsTheCamera()
    {
        var around = AddField(Volume("around", ShapeKind.Sphere, Vector3.Zero, new Vector3(10_000)));

        // A volume that holds the camera is in the stack: inside it, each click is a click on it.
        Assert.Equal([around, around.Zone!], Pick(Center).Items);

        var ahead = AddField(Volume("ahead", ShapeKind.Sphere, new Vector3(0, 0, -30_000), new Vector3(1_000)));

        Assert.Equal([ahead, around, ahead.Zone!, around.Zone!], Pick(Center).Items);
    }

    [Fact]
    public void ANebulaGlyphComesBeforeAPlanetBehindIt()
    {
        settings.ShowMarkers = true;
        // The planet covers the center of the screen, but its glyph is far to the right. The glyph of the nebula is at
        // the center, in front of the planet.
        var planet = Add("planet", new Vector3(5_000, 0, -30_000), ArchetypeType.planet, SphereModel(8_000));
        var nebula = AddNebula(Volume("nebula", ShapeKind.Sphere, new Vector3(0, 0, -60_000), new Vector3(2_000)));

        Assert.Equal([nebula, planet, nebula.Zone!], Pick(Center).Items);
    }

    [Fact]
    public void AnObjectOnTheRayComesBeforeANebulaInFrontOfIt()
    {
        var nebula = AddNebula(Volume("nebula", ShapeKind.Sphere, new Vector3(0, 0, -5_000), new Vector3(1_000)));
        var station = Add("station", new Vector3(0, 0, -20_000), ArchetypeType.station, BoxModel(new Vector3(100)));

        Assert.Equal([station, nebula, nebula.Zone!], Pick(Center).Items);
    }

    [Fact]
    public void ANebulaThatHoldsTheCameraComesBeforeAFieldThatHoldsIt()
    {
        var field = AddField(Volume("field", ShapeKind.Sphere, Vector3.Zero, new Vector3(5_000)));
        var nebula = AddNebula(Volume("nebula", ShapeKind.Sphere, Vector3.Zero, new Vector3(20_000)));

        // Inside a field, only the asteroids cover pixels, so the nebula comes first. The zones give the smaller first.
        Assert.Equal([nebula, field, field.Zone!, nebula.Zone!], Pick(Center).Items);
    }

    [Fact]
    public void TwoNebulaeThatHoldTheCameraKeepTheirFileOrder()
    {
        // The renderer draws the first nebula in file order that holds the camera (SystemRenderer.cs:243-249).
        var large = AddNebula(Volume("large", ShapeKind.Sphere, Vector3.Zero, new Vector3(20_000)));
        var small = AddNebula(Volume("small", ShapeKind.Sphere, Vector3.Zero, new Vector3(5_000)));

        Assert.Equal([large, small, small.Zone!, large.Zone!], Pick(Center).Items);
    }

    [Fact]
    public void TwoFieldsThatHoldTheCameraGiveTheSmallerFirst()
    {
        var large = AddField(Volume("large", ShapeKind.Sphere, Vector3.Zero, new Vector3(20_000)));
        var small = AddField(Volume("small", ShapeKind.Box, Vector3.Zero, new Vector3(5_000)));

        Assert.Equal([small, large, small.Zone!, large.Zone!], Pick(Center).Items);
    }

    [Fact]
    public void ZonesComeLastAndOnlyWithTheZonesToggle()
    {
        // The camera is within the size plus fill_dist of the field center, so the asteroids draw.
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 0, -1_500), new Vector3(1_000)));
        field.FillDist = 1_000;
        var path = AddPath(new Vector3(0, -10, -1_500), Matrix4x4.CreateRotationX(MathF.PI / 2), 1_000);

        // The ray enters the zone at 500, and the click is at the far end of the path line, at 2,000.
        Assert.Equal([field, field.Zone!, path], Pick(Center + new Vector2(0, 2)).Items);

        settings.ShowZones = false;
        Assert.Equal([field], Pick(Center + new Vector2(0, 2)).Items);
    }

    /// <summary>
    /// The selected zone keeps its diamond when the Zones toggle is off, and a diamond that shows can be picked. A zone
    /// diamond comes after the glyphs and the volumes.
    /// </summary>
    [Fact]
    public void AZoneDiamondThatShowsIsInTheStack()
    {
        settings.ShowMarkers = true;
        var population = Volume("population", ShapeKind.Sphere, new Vector3(0, 0, -10_000), new Vector3(500));
        population.Encounters = [new Encounter()];
        zones.Add(population);
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 0, -30_000), new Vector3(2_000)));

        Assert.Equal([field, population, field.Zone!], Pick(Center).Items);

        // Without the Zones toggle, the field keeps its glyph, and only the selected zone has a diamond.
        settings.ShowZones = false;
        Assert.Equal([field], Pick(Center).Items);
        selection = population;
        Assert.Equal([field, population], Pick(Center).Items);
    }

    [Fact]
    public void AZoneThatHoldsTheCameraIsInTheStack()
    {
        var field = AddField(Volume("field", ShapeKind.Ellipsoid, new Vector3(100, 0, 0), new Vector3(3_000, 1_000, 2_000)));

        Assert.Equal([field, field.Zone!], Pick(Center).Items);
    }

    [Fact]
    public void EachItemIsInTheStackOnce()
    {
        // A field and a nebula that share a zone: the field, the nebula and the zone, each once.
        var zone = Volume("shared", ShapeKind.Sphere, new Vector3(0, 0, -10_000), new Vector3(1_000));
        var field = AddField(zone);
        var nebula = AddNebula(zone, addZone: false);

        Assert.Equal([field, nebula, zone], Pick(Center).Items);

        // With the markers, the glyphs come first, and the volumes do not come again.
        settings.ShowMarkers = true;
        var items = Pick(Center).Items;
        Assert.Equal(3, items.Count);
        Assert.Equal([field, nebula], items.Take(2).OrderBy(i => i is Nebula));
        Assert.Same(zone, items[2]);
    }

    [Fact]
    public void TheOuterPuffsOfANebulaArePickable()
    {
        // The puff radius of 200 with a variation of 0.5 gives up to 300 on each axis (NebulaRenderer.cs:385).
        var nebula = AddNebula(Volume("nebula", ShapeKind.Sphere, new Vector3(0, 0, -10_000), new Vector3(1_000)));
        nebula.HasExteriorBits = true;
        nebula.ExteriorBitRadius = 200;
        nebula.ExteriorBitRandomVariation = 0.5f;

        Assert.Equal(new Vector3(1_300), ScenePicker.NebulaPickRadii(nebula));
        // At 10,000 units, these points are 1,200 and 1,400 units from the center, near the plane of the center.
        Assert.Equal([nebula], Pick(Screen(new Vector3(1_200, 0, -10_000))).Items);
        Assert.Empty(Pick(Screen(new Vector3(1_400, 0, -10_000))).Items);
    }

    [Fact]
    public void ABoxNebulaUsesItsZone()
    {
        // A box nebula draws no puffs, so its pick shape is its zone: from -1,000 to 1,000 on each axis.
        var nebula = AddNebula(Volume("nebula", ShapeKind.Box, new Vector3(0, 0, -10_000), new Vector3(2_000)));
        nebula.HasExteriorBits = true;
        nebula.ExteriorBitRadius = 500;

        Assert.Null(ScenePicker.NebulaPickRadii(nebula));
        Assert.Equal([nebula, nebula.Zone!], Pick(Screen(new Vector3(900, 0, -9_000))).Items);
        Assert.Empty(Pick(Screen(new Vector3(1_100, 0, -9_000))).Items);
    }

    [Fact]
    public void AHiddenNebulaIsNoTarget()
    {
        var nebula = AddNebula(Volume("nebula", ShapeKind.Sphere, new Vector3(0, 0, -10_000), new Vector3(1_000)));
        nebula.ExteriorFill = "fill";
        settings.ShowZones = false;
        settings.ShowNebulae = false;

        Assert.Empty(Pick(Center).Items);

        // The Nebulae toggle shows its cloud, so it is a target.
        settings.ShowNebulae = true;
        Assert.Equal([nebula], Pick(Center).Items);

        // A nebula without fill_shape and without puffs draws no cloud from outside.
        nebula.ExteriorFill = null;
        Assert.Empty(Pick(Center).Items);
    }

    [Fact]
    public void WithTheFogOnlyTheFogNebulaIsATarget()
    {
        settings.ShowZones = false;
        var around = AddNebula(Volume("around", ShapeKind.Sphere, Vector3.Zero, new Vector3(5_000)));
        var ahead = AddNebula(Volume("ahead", ShapeKind.Sphere, new Vector3(0, 0, -20_000), new Vector3(1_000)));
        around.ExteriorFill = ahead.ExteriorFill = "fill";

        // With the fog, the renderer draws only the nebula that holds the camera.
        Assert.Equal([around], Pick(Center).Items);

        // Without the fog, each nebula draws as from outside.
        settings.ShowFog = false;
        Assert.Equal([ahead, around], Pick(Center).Items);
    }

    [Fact]
    public void AFarFieldWithoutABandIsNoTargetWithTheZonesOff()
    {
        settings.ShowZones = false;
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 0, -10_000), new Vector3(1_000)));
        field.FillDist = 1_000;

        // The camera is 10,000 from the center, beyond the size plus fill_dist of 2,000.
        Assert.Empty(Pick(Center).Items);

        // Within that distance, the asteroids draw.
        eye = new Vector3(0, 0, -8_000);
        Assert.Equal([field], Pick(Center).Items);
    }

    [Fact]
    public void AFieldWithABandIsATargetFromFar()
    {
        settings.ShowZones = false;
        var field = AddField(Volume("field", ShapeKind.Sphere, new Vector3(0, 0, -100_000), new Vector3(1_000)));
        field.Band = new AsteroidBand();

        Assert.Equal([field], Pick(Center).Items);

        // A band draws only around a sphere or an ellipsoid zone (AsteroidFieldRenderer.cs:66-80).
        field.Zone!.Shape = ShapeKind.Box;
        Assert.Empty(Pick(Center).Items);
    }

    [Fact]
    public void TheVolumeSizeOfEachShape()
    {
        Assert.Equal(4 * MathF.PI / 3 * 8, ScenePicker.VolumeSize(Volume("s", ShapeKind.Sphere, default, new Vector3(2))),
            0.01f);
        Assert.Equal(4 * MathF.PI / 3 * 24,
            ScenePicker.VolumeSize(Volume("e", ShapeKind.Ellipsoid, default, new Vector3(2, 3, 4))), 0.01f);
        Assert.Equal(24, ScenePicker.VolumeSize(Volume("b", ShapeKind.Box, default, new Vector3(2, 3, 4))), 0.01f);
        // A cylinder of radius 2 and height 3, and a ring of outer radius 2, height 3 and inner radius 1.
        Assert.Equal(MathF.PI * 12, ScenePicker.VolumeSize(Volume("c", ShapeKind.Cylinder, default, new Vector3(2, 3, 0))),
            0.01f);
        Assert.Equal(MathF.PI * 9, ScenePicker.VolumeSize(Volume("r", ShapeKind.Ring, default, new Vector3(2, 3, 1))), 0.01f);
        // The pick radii of a nebula give the volume of their ellipsoid.
        Assert.Equal(4 * MathF.PI / 3 * 6,
            ScenePicker.VolumeSize(Volume("b", ShapeKind.Box, default, new Vector3(9)), new Vector3(1, 2, 3)), 0.01f);
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
        var zone = field.Zone!;
        // The point of the path at a depth of 20,000. On the screen it is 4% of the way from the far end of the path,
        // so a distance that is linear on the screen would give 96,000 and lose to the zone at 49,000. The field item
        // comes first, and the path and the zone volume compare in the zone layer.
        var click = Screen(new Vector3(0, -100, -20_000));

        Assert.Equal([field, path, zone], Pick(click).Items);

        zone.Position = new Vector3(0, -100, -10_000);
        Assert.Equal([field, zone, path], Pick(click).Items);
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

        var pick = Pick(Center);
        Assert.Null(pick.Axis);
        Assert.Empty(pick.Items);
        Assert.Null(pick.Item);
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

    /// <summary>Adds an asteroid field with its zone. The Scene draws its volume.</summary>
    private AsteroidField AddField(Zone zone)
    {
        zones.Add(zone);
        var field = new AsteroidField { SourceFile = "", Zone = zone };
        fields.Add(field);
        return field;
    }

    /// <summary>Adds a nebula with its zone, after the nebulae before it in file order.</summary>
    /// <param name="addZone">False for a zone that the system has already, for example the zone of a field.</param>
    private Nebula AddNebula(Zone zone, bool addZone = true)
    {
        if (addZone)
        {
            zones.Add(zone);
        }

        var nebula = new Nebula { SourceFile = "", Zone = zone };
        nebulae.Add(nebula);
        return nebula;
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

    // The markers of other items that the frame appends, for example the items of the open mission.
    private readonly List<(object Item, ObjectKind Kind, uint Color, Vector3 Position)> appended = [];

    /// <summary>The overlay finds the marker of an item by its equality. So the brackets of a highlighted item of the
    /// mission find its glyph with one lookup.</summary>
    [Fact]
    public void TheMarkersFindTheMarkerOfAnItem()
    {
        var markers = new SceneMarkers();
        var item = new OpenLancer.Data.Missions.MissionRef(MissionTestData.Path,
            OpenLancer.Data.Missions.MissionItemKind.Label, Key: "l_one");
        var kind = MissionLayer.KindOf(OpenLancer.Data.Missions.PlacementKind.ShipStart);

        markers.Update(Projection(), null, [(item, kind, 0u, new Vector3(0, 0, -1_000))]);

        Assert.Equal(item, markers.Find(item with { })?.Item);
        Assert.Null(markers.Find(item with { Key = "l_two" }));
        markers.Clear();
        Assert.Null(markers.Find(item));
    }

    /// <summary>
    /// A position in front of the camera gives its projected point. A position behind the camera gives a point far
    /// outside the scene, on the side where the position is (section 7.3 of the M6a design). So a connector line to an
    /// item behind the camera ends at the edge of the scene with an arrow.
    /// </summary>
    [Fact]
    public void TowardGivesThePointOrTheSideOfAPositionBehindTheCamera()
    {
        var projection = Projection();
        var front = new Vector3(100, 0, -5_000);

        Assert.Equal(Screen(front), projection.Toward(front));
        // The camera looks along -Z, so +Z is behind it. A position behind and to the left is left of the scene.
        var left = projection.Toward(new Vector3(-1_000, 0, 5_000));
        Assert.True(left.X < Origin.X, $"{left} is not left of the scene.");
        var below = projection.Toward(new Vector3(0, -1_000, 5_000));
        Assert.True(below.Y > Origin.Y + Size.Y, $"{below} is not below the scene.");
    }

    /// <summary>A marker of the mission layer is a glyph. So it is in the first layer of the pick stack, before the
    /// object under it (section 7.2 of the M6a design). It shows also when the markers of the system are off.</summary>
    [Fact]
    public void AMarkerOfTheMissionLayerComesBeforeTheObjectUnderIt()
    {
        var station = Add("station", new Vector3(0, 0, -5_000), ArchetypeType.station, BoxModel(new Vector3(100)));
        var item = new OpenLancer.Data.Missions.MissionRef(@"DATA\MISSIONS\TEST\test.ini",
            OpenLancer.Data.Missions.MissionItemKind.Ship, Key: "s_one");
        appended.Add((item, MissionLayer.KindOf(OpenLancer.Data.Missions.PlacementKind.ShipStart),
            EditorTheme.ToAbgr(EditorTheme.MissionItem), new Vector3(0, 0, -5_000)));

        var stack = Pick(Center).Items;

        Assert.Equal(item, stack[0]);
        Assert.Same(station, stack[1]);
    }

    private ScenePick Pick(Vector2 mouse)
    {
        var projection = Projection();
        var system = new StarSystem
        {
            Nickname = "li01", SourceFile = "", Objects = [.. systemObjects], Zones = [.. zones],
            AsteroidFields = [.. fields], Nebulae = [.. nebulae],
        };
        var index = new SystemIndex(system, system);
        var axes = new AxisGizmo();
        axes.Update(projection);
        var markers = new SceneMarkers();
        markers.Update(projection, index, settings, selection);
        markers.Append(projection, selection, appended);
        return new ScenePicker(axes, markers, objects).Pick(projection, mouse, index,
            new PickView(eye, settings.ShowZones, settings.ShowNebulae, settings.ShowFog));
    }
}

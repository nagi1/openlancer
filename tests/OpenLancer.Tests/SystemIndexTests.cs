using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;
using Encounter = LibreLancer.Data.Schema.Universe.Encounter;

namespace OpenLancer.Tests;

public sealed class SystemIndexTests
{
    private static SystemObject Ring(string nickname, string? next, string? previous) => new()
    {
        Nickname = nickname,
        Dock = new DockAction { Kind = DockKinds.Tradelane, Target = next, TargetLeft = previous },
    };

    private static string[][] Lanes(params SystemObject[] objects) =>
        SystemIndex.BuildLanes(objects).Select(l => l.Rings.Select(r => r.Nickname).ToArray()).ToArray();

    [Fact]
    public void FollowsEachLaneFromItsStartRing()
    {
        var lanes = Lanes(Ring("b2", "b3", "b1"), Ring("a1", "a2", null), Ring("b3", null, "b2"),
            Ring("a2", null, "a1"), Ring("b1", "b2", null));

        Assert.Equal([["a1", "a2"], ["b1", "b2", "b3"]], lanes);
    }

    [Fact]
    public void IgnoresObjectsWithoutATradeLaneDock()
    {
        var gate = new SystemObject
        {
            Nickname = "gate", Dock = new DockAction { Kind = DockKinds.Jump, Target = "li02" },
        };

        Assert.Equal([["a1"]], Lanes(gate, new SystemObject { Nickname = "planet" }, Ring("a1", null, null)));
    }

    [Fact]
    public void StopsAtAClosedLoopAndKeepsEachRingOnce()
    {
        var lanes = Lanes(Ring("a", "b", "c"), Ring("b", "c", "a"), Ring("c", "a", "b"));

        Assert.Equal([["a", "b", "c"]], lanes);
    }

    [Fact]
    public void StopsAtAMissingNextRing()
    {
        Assert.Equal([["a", "b"]], Lanes(Ring("a", "b", null), Ring("b", "missing", "a")));
    }

    [Fact]
    public void StopsAtARingThatPointsBackToItsPreviousRing()
    {
        Assert.Equal([["a", "b"]], Lanes(Ring("a", "b", null), Ring("b", "a", "a")));
    }

    [Fact]
    public void GivesTwoStartsThatJoinOneRingOnlyOnce()
    {
        Assert.Equal([["a", "c"], ["b"]], Lanes(Ring("a", "c", null), Ring("b", "c", null), Ring("c", null, "a")));
    }

    [Fact]
    public void StartsALaneAtARingWhosePreviousRingIsMissing()
    {
        Assert.Equal([["b", "c"]], Lanes(Ring("b", "c", "missing"), Ring("c", null, "b")));
    }

    [Fact]
    public void GivesEachZoneTheFirstRoleInTheRoleOrder()
    {
        var field = Zone("field", laneId: 3);
        var nebula = Zone("nebula");
        var fieldExclusion = Zone("field_exclusion", laneId: 4);
        var nebulaExclusion = Zone("nebula_exclusion");
        var population = Zone("population", encounters: true);
        var patrol = Zone("patrol", ShapeKind.Cylinder, encounters: true, pathLabel: ["path", "1"]);
        var lane = Zone("lane", laneId: 5);
        var other = Zone("other", ShapeKind.Box, encounters: true);
        var system = new StarSystem
        {
            Nickname = "li01",
            SourceFile = "",
            Zones = [field, nebula, fieldExclusion, nebulaExclusion, population, patrol, lane, other],
            AsteroidFields =
            [
                new AsteroidField
                {
                    SourceFile = "",
                    Zone = field,
                    ExclusionZones = [new AsteroidExclusionZone { Zone = fieldExclusion }],
                },
            ],
            Nebulae =
            [
                new Nebula { SourceFile = "", Zone = nebula, ExclusionZones = [new() { Zone = nebulaExclusion }] },
            ],
        };

        var index = new SystemIndex(system, system);

        Assert.Equal(ZoneRole.AsteroidField, index.ZoneRole(field));
        Assert.Equal(ZoneRole.Nebula, index.ZoneRole(nebula));
        Assert.Equal(ZoneRole.Exclusion, index.ZoneRole(fieldExclusion));
        Assert.Equal(ZoneRole.Exclusion, index.ZoneRole(nebulaExclusion));
        Assert.Equal(ZoneRole.Population, index.ZoneRole(population));
        Assert.Equal(ZoneRole.PatrolPath, index.ZoneRole(patrol));
        Assert.Equal(ZoneRole.TradeLane, index.ZoneRole(lane));
        Assert.Equal(ZoneRole.Other, index.ZoneRole(other));
        Assert.Equal(ZoneRole.Other, index.ZoneRole(Zone("not_in_system")));
        Assert.Same(ObjectKinds.Of(ZoneRole.Nebula), index.KindOf(nebula));
    }

    [Fact]
    public void ListsTheItemsThatUseAZone()
    {
        var field = Zone("field");
        var nebula = Zone("nebula");
        var exclusion = Zone("exclusion");
        var ring = Zone("Ring");
        var unused = Zone("unused");
        var asteroidField = new AsteroidField
        {
            SourceFile = "field.ini", Zone = field, ExclusionZones = [new AsteroidExclusionZone { Zone = exclusion }],
        };
        var cloud = new Nebula
        {
            SourceFile = "nebula.ini", Zone = nebula, ExclusionZones = [new() { Zone = exclusion }],
        };
        var planet = new SystemObject { Nickname = "planet", RingZone = "ring" };
        var system = new StarSystem
        {
            Nickname = "li01",
            SourceFile = "",
            Zones = [field, nebula, exclusion, ring, unused],
            AsteroidFields = [asteroidField],
            Nebulae = [cloud],
            Objects = [planet, new SystemObject { Nickname = "other", RingZone = "missing" }],
        };

        var index = new SystemIndex(system, system);

        Assert.Equal([new ZoneUse(ZoneUseKind.AsteroidField, asteroidField)], index.UsesOf(field));
        Assert.Equal([new ZoneUse(ZoneUseKind.Nebula, cloud)], index.UsesOf(nebula));
        Assert.Equal([new ZoneUse(ZoneUseKind.Exclusion, field), new ZoneUse(ZoneUseKind.Exclusion, nebula)],
            index.UsesOf(exclusion));
        Assert.Equal([new ZoneUse(ZoneUseKind.Ring, planet)], index.UsesOf(ring));
        Assert.Empty(index.UsesOf(unused));
    }

    [Fact]
    public void SplitsTheZonesByHowTheSceneShowsThem()
    {
        var field = Zone("field");
        var nebula = Zone("nebula");
        var cylinderPath = Zone("cylinder_path", ShapeKind.Cylinder, encounters: true, pathLabel: ["path", "1"]);
        var boxPath = Zone("box_path", ShapeKind.Box, encounters: true, pathLabel: ["path", "2"]);
        var population = Zone("population", encounters: true);
        var other = Zone("other");
        var system = new StarSystem
        {
            Nickname = "li01",
            SourceFile = "",
            Zones = [field, nebula, cylinderPath, boxPath, population, other],
            AsteroidFields = [new AsteroidField { SourceFile = "", Zone = field }],
            Nebulae = [new Nebula { SourceFile = "", Zone = nebula }],
        };

        var index = new SystemIndex(system, system);

        Assert.Equal([field, nebula], index.VolumeZones);
        // Only a cylinder has an axis for the path line. A patrol path with another shape gets a marker.
        Assert.Equal([cylinderPath], index.PatrolPaths);
        Assert.Equal([boxPath, population, other], index.MarkerZones);
    }

    [Fact]
    public void FindsObjectsByNicknameWithoutCase()
    {
        var planet = new SystemObject { Nickname = "Li01_01" };
        var system = new StarSystem { Nickname = "li01", SourceFile = "", Objects = [planet] };

        Assert.Same(planet, new SystemIndex(system, system).ObjectsByNickname["LI01_01"]);
    }

    internal static Zone Zone(string nickname, ShapeKind shape = ShapeKind.Sphere, bool encounters = false,
        string[]? pathLabel = null, int laneId = 0)
    {
        var zone = new Zone
        {
            Nickname = nickname,
            Shape = shape,
            Encounters = encounters ? [new Encounter()] : null,
            PathLabel = pathLabel,
        };
        zone.LaneId = laneId;
        return zone;
    }
}

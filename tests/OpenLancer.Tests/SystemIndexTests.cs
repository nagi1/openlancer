using System.Linq;
using System.Numerics;
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
        // The field and its exclusion zone also have the form of a trade lane zone, but they keep their first role.
        var field = LaneBox("field_Tradelane");
        var nebula = Zone("nebula");
        var fieldExclusion = LaneBox("field_exclusion_Tradelane");
        var nebulaExclusion = Zone("nebula_exclusion");
        var population = Zone("population", encounters: true);
        var patrol = Zone("patrol", ShapeKind.Cylinder, encounters: true, pathLabel: ["path", "1"]);
        var lane = LaneBox("Zone_Xx01_Tradelane_1");
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

    /// <summary>
    /// The game makes a zone a trade lane zone by its nickname and its form, and it never reads <c>lane_id</c> (decision
    /// D513). Most retail trade lane zones have a text such as a lane name in <c>lane_id</c>, and the engine reads such a
    /// text in a text file as 0. Such a zone still gets the trade lane role (fix S2).
    /// </summary>
    [Fact]
    public void ALongBoxWithTradelaneInItsNicknameIsATradeLaneZoneWithoutALaneId()
    {
        var lane = LaneBox("Zone_Xx01_Tradelane_1");
        var system = new StarSystem { Nickname = "xx01", SourceFile = "", Zones = [lane] };

        Assert.Equal(0, lane.LaneId);
        Assert.Equal(ZoneRole.TradeLane, new SystemIndex(system, system).ZoneRole(lane));
    }

    /// <summary>A lane id alone gives no role, because the game does not read it.</summary>
    [Fact]
    public void ALaneIdWithoutTheTradeLaneFormGivesNoRole()
    {
        var zone = Zone("Zone_Xx01_lane", ShapeKind.Box, laneId: 3);
        zone.Size = new Vector3(500, 500, 10_000);
        var system = new StarSystem { Nickname = "xx01", SourceFile = "", Zones = [zone] };

        Assert.Equal(ZoneRole.Other, new SystemIndex(system, system).ZoneRole(zone));
    }

    /// <summary>
    /// The rule of the game: the nickname has "Tradelane" with this letter case, the shape is a box, and the z size is
    /// more than 10 times the x size and the y size.
    /// </summary>
    [Theory]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 500, 500, 10_000, true)]
    [InlineData("Xx01_Tradelane", ShapeKind.Box, 999, 999, 10_000, true)]
    [InlineData("Zone_Xx01_tradelane_1", ShapeKind.Box, 500, 500, 10_000, false)]
    [InlineData("Zone_Xx01_TRADELANE_1", ShapeKind.Box, 500, 500, 10_000, false)]
    [InlineData("Zone_Xx01_Trade_Lane_1", ShapeKind.Box, 500, 500, 10_000, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 1_000, 500, 10_000, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 500, 1_000, 10_000, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 10_000, 500, 500, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Ellipsoid, 500, 500, 10_000, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Cylinder, 500, 10_000, 500, false)]
    public void ATradeLaneZoneHasTheNameAndTheFormThatTheGameNeeds(string nickname, ShapeKind shape, float x, float y,
        float z, bool lane)
    {
        var zone = Zone(nickname, shape);
        zone.Size = new Vector3(x, y, z);

        Assert.Equal(lane, SystemIndex.IsTradeLaneZone(zone));
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

    /// <summary>A system with two fields and three nebulae. One field and one nebula share a zone. One nebula has no
    /// zone, as in a clone of a nebula whose zone the system does not have.</summary>
    private static (StarSystem System, AsteroidField Debris, AsteroidField Smog, Nebula Cloud, Nebula Lost) Volumes()
    {
        var debris = Zone("debris");
        var shared = Zone("smog");
        var cloudZone = Zone("cloud");
        var debrisField = new AsteroidField { SourceFile = "debris.ini", Zone = debris };
        var smogField = new AsteroidField { SourceFile = "smog_asteroids.ini", Zone = shared };
        var smogCloud = new Nebula { SourceFile = "smog_nebula.ini", Zone = shared };
        var cloud = new Nebula { SourceFile = "cloud.ini", Zone = cloudZone };
        var lost = new Nebula { SourceFile = "lost.ini" };
        var system = new StarSystem
        {
            Nickname = "br04",
            SourceFile = "",
            Zones = [debris, shared, cloudZone],
            AsteroidFields = [debrisField, smogField],
            Nebulae = [cloud, lost, smogCloud],
        };
        return (system, debrisField, smogField, smogCloud, lost);
    }

    [Fact]
    public void VolumeItemsHoldEachFieldAndNebulaWithAZone()
    {
        var (system, debris, smog, smogCloud, _) = Volumes();

        var index = new SystemIndex(system, system);

        // The fields first, then the nebulae, each in file order.
        Assert.Equal([debris, smog, system.Nebulae[0], smogCloud], index.VolumeItems.Select(v => v.Item));
        Assert.Equal(["debris", "smog", "cloud", "smog"], index.VolumeItems.Select(v => v.Zone.Nickname));
    }

    [Fact]
    public void ASharedZoneGivesTwoItems()
    {
        var (system, _, smog, smogCloud, _) = Volumes();

        var index = new SystemIndex(system, system);

        Assert.Same(smog.Zone, index.VolumeOf(smog)!.Zone);
        Assert.Same(smog.Zone, index.VolumeOf(smogCloud)!.Zone);
        Assert.NotSame(index.VolumeOf(smog), index.VolumeOf(smogCloud));
        // The zone keeps the role of the field, and both items use it.
        Assert.Equal(ZoneRole.AsteroidField, index.ZoneRole(smog.Zone!));
        Assert.Equal([ZoneUseKind.AsteroidField, ZoneUseKind.Nebula], index.UsesOf(smog.Zone!).Select(u => u.Kind));
    }

    [Fact]
    public void AnItemWithoutAZoneIsNotAVolumeItem()
    {
        var (system, _, _, _, lost) = Volumes();

        var index = new SystemIndex(system, system);

        Assert.Null(index.VolumeOf(lost));
        Assert.DoesNotContain(index.VolumeItems, v => ReferenceEquals(v.Item, lost));
        Assert.Null(index.VolumeOf(system.Zones[0]));
        Assert.Null(index.VolumeOf(null));
    }

    [Fact]
    public void KindOfAnItemIsItsKind()
    {
        var (system, debris, _, smogCloud, _) = Volumes();

        var index = new SystemIndex(system, system);

        Assert.Same(ObjectKinds.AsteroidField, index.KindOf(debris));
        Assert.Same(ObjectKinds.Nebula, index.KindOf(smogCloud));
        Assert.Same(ObjectKinds.AsteroidField, index.VolumeOf(debris)!.Kind);
        Assert.Same(ObjectKinds.Nebula, index.VolumeOf(smogCloud)!.Kind);
        // The zone keeps the kind of its role.
        Assert.Same(ObjectKinds.Of(ZoneRole.AsteroidField), index.KindOf(debris.Zone));
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

    /// <summary>A box zone with the form of a trade lane zone: 20 times longer on its z axis than on its x and y
    /// axes.</summary>
    private static Zone LaneBox(string nickname)
    {
        var zone = Zone(nickname, ShapeKind.Box);
        zone.Size = new Vector3(500, 500, 10_000);
        return zone;
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The placeable items of a mission in a system (section 7.2 of the M6a design) on a synthetic script: the starts of the
/// cast, the anchors, the lines of the triggers with a place, the objective markers and the order paths, each in its
/// system.
/// </summary>
public sealed class MissionPlacementsTests
{
    private const string Script = """
        [Mission]

        [MsnShip]
        nickname = s_one
        system = sys_a
        position = 10, 0, 20
        arrival_obj = gate_a

        [MsnShip]
        nickname = s_near
        system = sys_a
        rel_pos = 90, station_a, 1000

        [MsnShip]
        nickname = s_player
        system = sys_a
        rel_pos = 0, Player, 500

        [MsnShip]
        nickname = s_other
        system = sys_b
        position = 1, 2, 3

        [MsnShip]
        nickname = s_nowhere
        position = 1, 2, 3

        [MsnFormation]
        nickname = f_one
        system = sys_a
        position = 100, 0, 0
        formation = shape_a
        ship = s_one

        [MsnLoot]
        nickname = l_one
        system = sys_a
        rel_pos_obj = station_a
        rel_pos_offset = 5, 5, 5

        [ObjList]
        nickname = ol_path
        GotoVec = goto, 0, 0, 0, 100
        GotoVec = goto, 1000, 0, 0, 100
        StayInRange = 500, 0, 500, 250

        [NNObjective]
        nickname = o_nav
        type = navmarker, sys_a, 100, 101, 7, 8, 9

        [NNObjective]
        nickname = o_rep
        type = rep_inst, sys_a, 100, 101, 1, 2, 3, station_a

        [NNObjective]
        nickname = o_far
        type = navmarker, sys_b, 100, 101, 7, 8, 9

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        system = sys_a
        Cnd_True = no_params
        Act_SpawnShip = s_one, ol_path, 50, 60, 70
        Act_GiveObjList = s_one, ol_path
        Act_RelocateShip = s_one, 1, 1, 1
        Act_RandomPopSphere = 0, 0, 0, 3000, on
        Act_StaticCam = 5, 5, 5, 1, 0, 0, 0
        Act_CallThorn = scripts\a.thn, s_one

        [Trigger]
        nickname = t_near
        system = sys_a
        Cnd_DistVec = inside, Player, 100, 200, 300, 400
        Act_ActTrig = t_out

        [Trigger]
        nickname = t_out
        system = sys_a
        Cnd_DistShip = outside, Player, s_one, 750
        Act_ActTrig = t_ring

        [Trigger]
        nickname = t_ring
        system = sys_a
        Cnd_DistCircle = Player, ring_a
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_elsewhere
        system = sys_b
        Cnd_DistVec = inside, Player, 1, 2, 3, 4
        """;

    private static readonly Dictionary<string, Vector3> Objects = new()
    {
        ["station_a"] = new Vector3(5000, 0, 0), ["gate_a"] = new Vector3(0, 0, -9000), ["ring_a"] = new Vector3(0, 0, 777),
    };

    private static IReadOnlyList<MissionPlacement> In(string system, string text = Script) =>
        MissionPlacements.In(Index(text), system, name => Objects.TryGetValue(name, out var p) ? p : null,
            shape => shape == "shape_a" ? [new Vector3(0, 0, 0), new Vector3(30, 0, 0)] : null);

    [Fact]
    public void TheStartsOfTheCastAreInTheirSystem()
    {
        var placements = In("sys_a");

        var ship = placements.Single(p => p.Kind == PlacementKind.ShipStart);
        Assert.Equal(("s_one", new Vector3(10, 0, 20)), (ship.Label, ship.Position));
        var formation = placements.Single(p => p.Kind == PlacementKind.FormationStart);
        Assert.Equal(new Vector3(100, 0, 0), formation.Position);
        // The formation takes one ship, so one member shows at the first slot.
        Assert.Equal([new Vector3(100, 0, 0)], placements.Single(p => p.Kind == PlacementKind.FormationMember).Points);
        Assert.Equal(new Vector3(5005, 5, 5), placements.Single(p => p.Kind == PlacementKind.LootStart).Position);
        Assert.DoesNotContain(placements, p => p.Label is "s_other" or "s_nowhere" or "s_player");
        Assert.Equal(["s_other"], In("sys_b").Where(p => p.Kind == PlacementKind.ShipStart).Select(p => p.Label));
    }

    [Fact]
    public void AnAnchorGivesARingAndADotAtTheHeading()
    {
        var near = In("sys_a").Single(p => p.Kind == PlacementKind.NearAnchor);

        Assert.Equal((PlacementShape.Ring, new Vector3(5000, 0, 0), 1000f), (near.Shape, near.Position, near.Radius));
        Assert.Equal(6000, near.To!.Value.X, 1);
        Assert.Equal(["station_a"], near.Names);
    }

    [Fact]
    public void TheArrivalObjectIsALineFromTheStart()
    {
        var arrival = In("sys_a").Single(p => p.Kind == PlacementKind.Arrival);

        Assert.Equal((new Vector3(10, 0, 20), new Vector3(0, 0, -9000)), (arrival.Position, arrival.To!.Value));
    }

    [Fact]
    public void TheLinesOfTheTriggersHaveTheirPlaceAndTheirTrigger()
    {
        var placements = In("sys_a");
        PlacementKind[] kinds =
        [
            PlacementKind.Spawn, PlacementKind.Move, PlacementKind.TrafficArea, PlacementKind.FixedCamera,
            PlacementKind.CutsceneAnchor, PlacementKind.DistanceVolume, PlacementKind.DistanceVolume,
            PlacementKind.RingPass,
        ];

        var lines = placements.Where(p => p.Trigger != null).ToList();
        Assert.Equal(kinds, lines.Select(p => p.Kind));
        Assert.Equal(new Vector3(50, 60, 70), lines[0].Position);
        Assert.Equal(("t_start", 4), (lines[0].Trigger!.Nickname, lines[0].Entry));
        Assert.Equal((3000f, PlacementShape.Sphere), (lines[2].Radius, lines[2].Shape));
        Assert.Equal(new Vector3(10, 0, 20), lines[4].Position);
        Assert.Equal((new Vector3(100, 200, 300), 400f, false), (lines[5].Position, lines[5].Radius, lines[5].Outside));
        // The sphere of Cnd_DistShip is around the start of the other ship, and it is an outside sphere.
        Assert.Equal((new Vector3(10, 0, 20), 750f, true), (lines[6].Position, lines[6].Radius, lines[6].Outside));
        Assert.Equal(new Vector3(0, 0, 777), lines[7].Position);
        Assert.Single(In("sys_b"), p => p.Kind == PlacementKind.DistanceVolume);
    }

    [Fact]
    public void TheObjectivesAndTheOrderPathsAreInTheirSystem()
    {
        var placements = In("sys_a");

        var markers = placements.Where(p => p.Kind == PlacementKind.ObjectiveMarker).ToList();
        Assert.Equal(["o_nav", "o_rep"], markers.Select(p => p.Label));
        Assert.Equal((new Vector3(7, 8, 9), PlacementShape.Point), (markers[0].Position, markers[0].Shape));
        Assert.Equal((PlacementShape.Line, new Vector3(5000, 0, 0)), (markers[1].Shape, markers[1].To!.Value));
        // The order list gets the system of the trigger that gives it.
        var path = placements.Where(p => p.Kind == PlacementKind.OrderPath).ToList();
        Assert.Equal(["ol_path 1", "ol_path 2", "ol_path", "ol_path"], path.Select(p => p.Label));
        Assert.Equal((PlacementShape.Sphere, 250f), (path[2].Shape, path[2].Radius));
        Assert.Equal(2, path[3].Points.Count);
        Assert.Equal(["o_far"], In("sys_b").Where(p => p.Kind == PlacementKind.ObjectiveMarker).Select(p => p.Label));
    }

    [Fact]
    public void TheItemsNearThePlayerAndWithoutASystemAreListed()
    {
        var index = Index(Script);

        Assert.Equal(["s_player"], MissionPlacements.NearPlayer(index).Select(s => s.Nickname));
        Assert.Equal(["s_nowhere"], MissionPlacements.SystemUnknown(index).Select(s => s.Nickname));
    }

    [Fact]
    public void TwoItemsThatAnchorEachOtherHaveNoPlace()
    {
        const string text = """
            [MsnShip]
            nickname = a
            system = sys_a
            rel_pos = 0, b, 100

            [MsnShip]
            nickname = b
            system = sys_a
            rel_pos = 0, a, 100
            """;

        Assert.Empty(In("sys_a", text));
    }
}

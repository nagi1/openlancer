using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The helper lines of the mission layer of the Scene (section 7.2 of the M6a design) on a synthetic script. The volumes
/// use the zone outline of the Scene, and an outside volume is dashed.
/// </summary>
public sealed class MissionLayerTests
{
    private const string Script = """
        [Mission]

        [MsnShip]
        nickname = s_one
        system = sys_a
        position = 10, 0, 20

        [MsnShip]
        nickname = s_near
        system = sys_a
        rel_pos = 90, station_a, 1000

        [Trigger]
        nickname = t_near
        system = sys_a
        Cnd_DistVec = inside, Player, 100, 200, 300, 400
        Act_ActTrig = t_out

        [Trigger]
        nickname = t_out
        system = sys_a
        Cnd_DistShip = outside, Player, s_one, 750
        """;

    private static IReadOnlyList<MissionPlacement> Placements() =>
        MissionPlacements.In(MissionTestData.Index(Script), "sys_a",
            name => name == "station_a" ? new Vector3(5000, 0, 0) : null);

    private static IEnumerable<Vector3> Points(IEnumerable<(Vector3 A, Vector3 B)> lines) =>
        lines.SelectMany(l => new[] { l.A, l.B });

    [Fact]
    public void AnOutsideSphereDrawsHalfTheSegmentsOfAnInsideSphere()
    {
        var placements = Placements();
        var inside = MissionLayer.Outline(placements.Single(p => p.Shape == PlacementShape.Sphere && !p.Outside));
        var outside = MissionLayer.Outline(placements.Single(p => p.Shape == PlacementShape.Sphere && p.Outside));

        Assert.Equal(inside.Count / 2, outside.Count);
        Assert.All(Points(inside), p => Assert.Equal(400, Vector3.Distance(p, new Vector3(100, 200, 300)), 1));
        // The sphere of the outside condition is around the start of the ship.
        Assert.All(Points(outside), p => Assert.Equal(750, Vector3.Distance(p, new Vector3(10, 0, 20)), 1));
    }

    [Fact]
    public void ARingAroundAnAnchorEndsWithTheLineToItsDot()
    {
        var ring = Placements().Single(p => p.Shape == PlacementShape.Ring);
        var lines = MissionLayer.Outline(ring);

        Assert.Equal((ring.Position, ring.To!.Value), lines[^1]);
        Assert.All(Points(lines.Take(lines.Count - 1)), p => Assert.Equal(1000, Vector3.Distance(p, ring.Position), 1));
    }

    [Fact]
    public void AStartHasNoHelperLines()
    {
        var start = Placements().Single(p => p.Kind == PlacementKind.ShipStart);

        Assert.Empty(MissionLayer.Outline(start));
    }

    /// <summary>A placement of a trigger line selects the line. A placement of an item selects its section.</summary>
    [Fact]
    public void ThePlacementOfALineSelectsTheLine()
    {
        var index = MissionTestData.Index(MissionTestData.Story.Replace("Cnd_True = no_params\nAct_ActTrig = t_end",
            "Cnd_True = no_params\nAct_ActTrig = t_end\nAct_StaticCam = 1, 2, 3") + "\n\n[MsnShip]\nnickname = s\nsystem = sys\nposition = 1, 1, 1\n");
        var start = index.Trigger("t_start")!;
        var placements = MissionPlacements.In(index, "sys", _ => null);

        Assert.DoesNotContain(placements, p => p.Trigger != null);
        var ship = MissionRef.OfPlacement(MissionTestData.Path, placements.Single());
        Assert.Equal(MissionItemKind.Ship, ship.Kind);
        var line = new MissionPlacement(PlacementKind.FixedCamera, PlacementShape.Point, "sys", Vector3.Zero, start.Section,
            5, "Fixed camera") { Trigger = start };
        Assert.Equal(new MissionRef(MissionTestData.Path, MissionItemKind.Row, start.Section.Ref, Row: 5),
            MissionRef.OfPlacement(MissionTestData.Path, line));
    }
}

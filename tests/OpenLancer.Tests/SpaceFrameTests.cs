using System;
using System.Numerics;
using LibreLancer;
using LibreLancer.Thn;
using LibreLancer.Thn.Events;
using LibreLancer.Thorn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rules that put a space script into its system (test 5.2 item 5 of the cutscene fixes plan): the marker at the
/// origin, the static stand-ins of a local layout and their new pose. The scripts are synthetic.
/// </summary>
public sealed class SpaceFrameTests
{
    private static ThnEntity Entity(string name, EntityTypes type, Vector3 position) =>
        new(new ThornTable { ["entity_name"] = name, ["type"] = type }) { Position = position };

    [Fact]
    public void OnlyAMarkerWithin1MOfTheOriginMoves()
    {
        Assert.True(SpaceFrame.MovesMarker(Vector3.Zero));
        Assert.True(SpaceFrame.MovesMarker(null));
        Assert.True(SpaceFrame.MovesMarker(new Vector3(0.6f, 0, -0.6f)));
        Assert.False(SpaceFrame.MovesMarker(new Vector3(0.8f, 0, -0.8f)));
        Assert.False(SpaceFrame.MovesMarker(new Vector3(3600, 1000, -21000)));
    }

    /// <summary>A static COMPOUND near the origin follows the marker. A COMPOUND that an event moves, a COMPOUND 5 km
    /// or more from the origin, and the cameras and markers stay.</summary>
    [Fact]
    public void OnlyStaticCompoundsNearTheOriginAreStandIns()
    {
        var script = new ThnScript { Duration = 10 };
        foreach (var entity in new[]
                 {
                     Entity("ship", EntityTypes.Compound, new Vector3(30, 0, 40)),
                     Entity("escort", EntityTypes.Compound, new Vector3(-1400, 0, 0)),
                     Entity("attached", EntityTypes.Compound, new Vector3(10, 0, 0)),
                     Entity("pathed", EntityTypes.Compound, new Vector3(10, 0, 0)),
                     Entity("animated", EntityTypes.Compound, new Vector3(10, 0, 0)),
                     Entity("station", EntityTypes.Compound, new Vector3(3000, 0, 4000)),
                     Entity("camera", EntityTypes.Camera, new Vector3(5, 0, 0)),
                     Entity("marker", EntityTypes.Marker, new Vector3(5, 0, 0)),
                 })
        {
            script.Entities[entity.Name] = entity;
        }

        script.Events.Add(new AttachEntityEvent { Targets = ["attached", "marker"] });
        script.Events.Add(new StartPathAnimationEvent { Targets = ["pathed", "path"] });
        script.Events.Add(new StartSpatialPropAnimEvent { Targets = ["Animated"] });
        // The second target of an attach does not move.
        script.Events.Add(new AttachEntityEvent { Targets = ["camera", "ship"] });

        Assert.Equal(["ship", "escort"], SpaceFrame.StaticStandIns(script));
    }

    /// <summary>A stand-in keeps its pose relative to the marker when the marker moves to the start.</summary>
    [Fact]
    public void AStandInKeepsItsPoseRelativeToTheMarker()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var start = new Transform3D(new Vector3(1000, 0, 0), turn);
        var marker = new Transform3D(Vector3.Zero, Quaternion.Identity);
        var standIn = new Transform3D(new Vector3(0, 0, -10), Quaternion.Identity);

        var placed = SpaceFrame.Place(standIn, marker, start);

        // 10 m in front of the marker is 10 m in front of the start, along the turned front axis.
        var expected = start.Position + Vector3.Transform(new Vector3(0, 0, -10), turn);
        Assert.True(Vector3.Distance(expected, placed.Position) < 1e-3f);
        Assert.True(MathF.Abs(Quaternion.Dot(turn, placed.Orientation)) > 0.9999f);

        // A marker that stands a little off the origin moves with its offset.
        var offMarker = new Transform3D(new Vector3(0.5f, 0, 0), Quaternion.Identity);
        var atMarker = SpaceFrame.Place(offMarker, offMarker, start);
        Assert.True(Vector3.Distance(start.Position, atMarker.Position) < 1e-3f);
    }

    [Fact]
    public void TheNoteOfTheMainObjectTellsWhatThePreviewDoes()
    {
        Assert.Null(SpaceFrame.MainObjectNote(null, null, "zz_ship", true));
        Assert.Equal("The main object zz_ship stands still at its start in the mission data. In the game, it moves with " +
                     "its mission orders.", SpaceFrame.MainObjectNote("Mk", Vector3.Zero, "zz_ship", true));
        Assert.Equal("The mission data gives no start for the main object zz_ship. Its marker stays at its position in " +
                     "the script.", SpaceFrame.MainObjectNote("Mk", Vector3.Zero, "zz_ship", false));
        Assert.Equal("The marker Mk stays at its position in the script. In the game, it follows the main object " +
                     "zz_ship.", SpaceFrame.MainObjectNote("Mk", new Vector3(500, 0, 0), "zz_ship", true));
    }
}

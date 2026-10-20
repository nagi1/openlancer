using System;
using System.IO;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Runtime.Play;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>The read of a start save in the play process (section 5.6 of the M6c design): the place and the mission
/// part.</summary>
public sealed class PlayStartSaveTests : IDisposable
{
    private readonly string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openlancer-tests",
        Guid.NewGuid().ToString("N"));

    public PlayStartSaveTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private string Write(StartSavePlace place, string trigger = "t_chosen")
    {
        var plan = StartPlan.Make(Index(StartPlanTests.Script), StartPlanTests.Slot, MissionStartPoint.At(trigger));
        var file = System.IO.Path.Combine(folder, "start.fl");
        File.WriteAllText(file, StartSaveText.OpenLancer(plan, place, DateTime.Now));
        return file;
    }

    [Fact]
    public void ASpaceStartGivesThePointWithTheHeadingOfRotate()
    {
        var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 3);
        var file = Write(new StartSavePlace("ol_sys_a", null, new Vector3(1, 2, 3), heading));

        var start = PlayStartSave.Read(file);

        var space = Assert.IsType<PlayStart.InSpace>(start.Place);
        Assert.Equal(("ol_sys_a", new Vector3(1, 2, 3)), (space.System, space.Position));
        Assert.True(MathF.Abs(Quaternion.Dot(heading, space.Orientation)) > 0.9999f);
        Assert.Equal("Mission_99", start.Save.StoryInfo!.Mission);
        Assert.Single(start.Save.TriggerSave);
    }

    [Fact]
    public void ABaseStartGivesTheBase()
    {
        var file = Write(StartSavePlace.AtBase("ol_sys_a", "ol_base_a"));

        Assert.Equal(new PlayStart.AtBase("ol_base_a"), PlayStartSave.Read(file).Place);
    }

    [Fact]
    public void AFileWithoutAMissionOrAPlaceIsNoStartSave()
    {
        var noMission = System.IO.Path.Combine(folder, "a.fl");
        File.WriteAllText(noMission, "[Player]\r\nsystem = ol_sys_a\r\npos = 0, 0, 0\r\n");
        var noPlace = System.IO.Path.Combine(folder, "b.fl");
        File.WriteAllText(noPlace, "[Player]\r\ndescription = 0041\r\n\r\n[StoryInfo]\r\nMission = Mission_99\r\n");

        Assert.Contains("names no mission", Assert.Throws<InvalidDataException>(() => PlayStartSave.Read(noMission)).Message);
        Assert.Contains("names no system and no base",
            Assert.Throws<InvalidDataException>(() => PlayStartSave.Read(noPlace)).Message);
        Assert.Null(PlayStartSave.TryRead(System.IO.Path.Combine(folder, "missing.fl"), out var problem));
        Assert.Contains("cannot be read", problem);
    }
}

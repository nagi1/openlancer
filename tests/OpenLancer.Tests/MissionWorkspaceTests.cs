using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The rules of the mission workspace that need no editor window (section 4 of the M6a design): the selection of a line
/// of a problem, and the check of a kept selection of the Mission tab.
/// </summary>
public sealed class MissionWorkspaceTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    [Fact]
    public void AProblemSelectsItsLineOrItsSection()
    {
        var trigger = new SectionRef(Script, new SectionIdentity("Trigger", "t1"));
        var ship = new SectionRef(Script, new SectionIdentity("MsnShip", "s1"));
        var other = new SectionRef(Script, new SectionIdentity("Constants", null));
        var mission = new SectionRef(Script, new SectionIdentity("Mission", null));

        Assert.Equal(new MissionRef(Script, MissionItemKind.Row, trigger, Row: 3), MissionWorkspace.RefOf(trigger, 3));
        Assert.Equal(new MissionRef(Script, MissionItemKind.Trigger, trigger), MissionWorkspace.RefOf(trigger, -1));
        // A line of another section selects the section.
        Assert.Equal(new MissionRef(Script, MissionItemKind.Ship, ship), MissionWorkspace.RefOf(ship, 2));
        Assert.Equal(MissionItemKind.OtherSection, MissionWorkspace.RefOf(other, -1).Kind);
        Assert.Equal(MissionRef.Of(Script), MissionWorkspace.RefOf(mission, 0));
    }

    [Fact]
    public void AKeptSelectionStaysWhileTheRunHasItsSection()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        var result = MissionRun.Execute(folder.Inputs());
        var start = result.Of(Script)!.Index.Trigger("t_start")!.Section.Ref;
        var kept = new MissionRef(Script, MissionItemKind.Trigger, start);
        var gone = new MissionRef(Script, MissionItemKind.Trigger, new SectionRef(Script, new SectionIdentity("Trigger", "t_gone")));

        Assert.Equal(kept, MissionWorkspace.Valid(result, kept));
        // A selection whose section is gone becomes its mission.
        Assert.Equal(MissionRef.Of(Script), MissionWorkspace.Valid(result, gone));
        // A selection of a script that the run does not have, and a selection of another kind, are not valid.
        Assert.Null(MissionWorkspace.Valid(result, MissionRef.Of(@"DATA\MISSIONS\M02\m02.ini")));
        Assert.Null(MissionWorkspace.Valid(result, new object()));
        // Before the first run, each mission selection stays.
        Assert.Equal(gone, MissionWorkspace.Valid(null, gone));
    }
}

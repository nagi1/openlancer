using System;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The guard of the mission files (section 4.1 of the M6b design, decision D660): only the mission editor
/// changes a mission script, the ship build file of a script and the editor files of the mod.</summary>
public sealed class MissionGuardTests
{
    [Fact]
    public void AnEditCommandOnAMissionScriptOrItsShipBuildFileOutsideTheLayerThrows()
    {
        using var m = Create();
        var wave = new SectionRef(Script, new SectionIdentity("Trigger", "ol_wave"));
        var build = new SectionRef(Ships, new SectionIdentity("NPCShipArch", "ol_build_1"));
        var before = m.State();

        var error = Assert.Throws<InvalidOperationException>(() =>
            m.Session.Execute(EditCommand.SetLines(wave, "Cnd_Timer", [[new Int32Value(5)]])));
        Assert.Throws<InvalidOperationException>(() =>
            m.Session.Execute(EditCommand.SetLinesFromText(build, "loadout", ["ol_loadout_2"])));
        Assert.Throws<InvalidOperationException>(() =>
            m.Session.Execute(EditCommand.SetModFile($@"graphs\{Script}", "[Graph]")));

        Assert.Equal($"{Script} is a mission file. Change it with the mission commands, so that the checks, the references " +
                     "and the undo of the missions follow the edit.", error.Message, ignoreCase: true);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void TheGuardDoesNotRefuseTheSharedFilesOfTheMissions()
    {
        using var m = Create(setup: ws => ws.WriteBase(@"DATA\MISSIONS\pilots_population.ini",
            "[Pilot]\nnickname = ol_pilot\ngun_id = ol_gun\n"));
        var shared = new SectionRef(SharedShips, new SectionIdentity("NPCShipArch", "ol_shared_build"));
        var pilot = new SectionRef(@"DATA\MISSIONS\pilots_population.ini", new SectionIdentity("Pilot", "ol_pilot"));

        m.Session.Execute(EditCommand.SetLinesFromText(shared, "loadout", ["ol_loadout_2"]));
        m.Session.Execute(EditCommand.SetLinesFromText(pilot, "gun_id", ["ol_gun_2"]));

        Assert.Equal("loadout = ol_loadout_2", m.Lines(new ItemRef(SharedShips, shared.Id))![2]);
        Assert.Equal(2, m.Session.EditedPaths.Count);
    }

    [Fact]
    public void TheRestoreOfARecoveryFileWithAMissionPatchWorks()
    {
        using var m = Create();
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 9));
        m.Apply(new AddItem(TestScript, "ship_build") { Nickname = "ol_build_2" });
        Assert.Empty(m.Session.WriteRecovery());
        m.Session.Abandon();

        var again = m.Ws.Session("act");
        Assert.True(again.HasRecovery);
        Assert.Empty(again.RestoreRecovery());

        var wave = again.OpenDocument(Script)!.Working.First(s => s.Name == "Trigger" && s.Any(e => e.Name == "nickname" && e[0].ToString() == "ol_wave"));
        Assert.Equal("Cnd_Timer = 9", MissionEdits.Line(wave[2]));
        Assert.Contains(again.OpenDocument(Ships)!.Working, s => SectionIdentity.KeyOf(s) == "ol_build_2");
        Assert.Equal("Restore unsaved edits", again.UndoLabel);
    }

    [Fact]
    public void TheMissionEditorIsTheWritePathOfTheSession()
    {
        using var m = Create();

        Assert.Same(m.Missions, m.Session.Missions);
        Assert.True(m.Session.IsGuardedMissionFile(Script));
        Assert.True(m.Session.IsGuardedMissionFile(Ships));
        Assert.False(m.Session.IsGuardedMissionFile(SharedShips));
    }
}

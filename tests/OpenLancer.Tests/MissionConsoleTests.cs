using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Logs;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The Console entries of the mission checks (section 12.4 of the M6a design, decision D552): each run replaces the
/// entries of the source "Mission", and an old problem does not count as new again.
/// </summary>
public sealed class MissionConsoleTests
{
    private static readonly SectionRef Target = new(Path, new SectionIdentity("Trigger", "t1"));

    private static Diagnostic Problem(string message, int entry = 1) =>
        Diagnostic.Warning(MissionChecks.Source, message) with { Target = Target, EntryIndex = entry, Rule = "M05" };

    [Fact]
    public void ARunReplacesTheEntriesOfItsSourceAndKeepsTheOthers()
    {
        var log = new ConsoleLog();
        var other = log.AddStatus(ConsoleSeverity.Warning, "Editor", "Another message.");

        log.ReplaceSource(MissionChecks.Source, [Problem("A."), Problem("B.")]);
        log.ReplaceSource(MissionChecks.Source, [Problem("B."), Problem("C.", 2)]);

        Assert.Equal(["B.", "C."], log.EntriesOf(MissionChecks.Source).Select(e => e.Message));
        Assert.Equal(2, log.EntriesOf(MissionChecks.Source).Last().EntryIndex);
        Assert.Equal(Target, log.EntriesOf(MissionChecks.Source).First().Target);
        Assert.Contains(other, log.EntriesOf("Editor"));
        Assert.Equal(3, log.Count(ConsoleSeverity.Warning));
    }

    [Fact]
    public void AnOldProblemDoesNotCountAsNewAgain()
    {
        var log = new ConsoleLog();
        log.ReplaceSource(MissionChecks.Source, [Problem("A.")]);
        log.MarkSeen();

        log.ReplaceSource(MissionChecks.Source, [Problem("A.")]);
        Assert.Equal(0, log.UnseenWarnings);

        log.ReplaceSource(MissionChecks.Source, [Problem("A."), Problem("New.")]);
        Assert.Equal(1, log.UnseenWarnings);
        Assert.Equal("New.", log.Last!.Message);
    }

    [Fact]
    public void AnEmptyRunRemovesTheEntriesAndTheLastEntry()
    {
        var log = new ConsoleLog();
        var older = log.AddStatus(ConsoleSeverity.Warning, "Editor", "Older.");
        log.ReplaceSource(MissionChecks.Source, [Problem("A.")]);

        log.ReplaceSource(MissionChecks.Source, []);

        Assert.Empty(log.EntriesOf(MissionChecks.Source));
        Assert.Same(older, log.Last);
        Assert.Equal(1, log.Count(ConsoleSeverity.Warning));
    }

    [Fact]
    public void EachProblemOfARunHasTheSourceMission()
    {
        var problems = Check(Story + "\n[Trigger]\nnickname = t_x\nCnd_True = no_params\nAct_ActTrig = t_none\n");

        Assert.NotEmpty(problems);
        Assert.All(problems, p => Assert.Equal("Mission", p.Source));
    }
}
